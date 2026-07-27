/*!
 * charter-annotate.js — Charter's in-browser annotation SDK.
 * The browser half of Charter's comment-in-place review loop.
 *
 * Adapted lean from Lavish (https://github.com/kunchenguid/lavish-axi) by Kun Chen,
 * which is distributed under the MIT License. Only the comment-in-place review loop is
 * reproduced here — anchoring a human note to an element, a text-range, or a diagram-node,
 * carried over a narrow postMessage/HTTP boundary. This is deliberately NOT a full Lavish
 * port (see plan decision D2): keeping the surface minimal is what keeps the re-port
 * manageable. The near-target composer card, the transient getClientRects text highlight,
 * and the "the SDK's own UI is never annotatable" self-guard are adapted from Lavish's
 * artifact-sdk.js (showAnnotationCard / highlightTextRange / isLavishUi).
 *
 * MIT License.
 *   Original comment-in-place review loop © Kun Chen and the Lavish contributors.
 *   Lean C#-native-Charter adaptation © the Charter contributors.
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this
 * software and associated documentation files (the "Software"), to deal in the Software
 * without restriction. The Software is provided "as is", without warranty of any kind.
 *
 * This is the ONLY JavaScript in Charter. It is injected into the served HTML at serve
 * time only (the on-disk artifact stays SDK-free — invariant 1). It never reaches into
 * server internals: every crossing of the C#<->JS boundary is either a `postMessage`
 * (page side) or an HTTP request to a defined route (server side) — invariant 6.
 */
window.CharterAnnotate = (function () {
  'use strict';

  // The postMessage channel tag. Every message the SDK emits or accepts across the
  // C#<->JS boundary carries { channel: CHANNEL, type, detail } so host frames, an
  // embedded review panel, or a headless (Playwright) driver can observe/command the
  // SDK without touching server internals.
  var CHANNEL = 'charter-annotate';

  // Every element the SDK builds carries this attribute. It is the anchoring layer's self-guard
  // (see closestAnchored) AND the marker that keeps SDK chrome out of derived context labels.
  // Deliberately an ATTRIBUTE, never an `id`: the renderer anchors blocks by `id`, so an SDK
  // element with an id could be resolved as an annotation target. The SDK's UI has no ids at all,
  // so even a guard bug degrades to "no anchor", never "the wrong anchor".
  var UI_ATTR = 'data-charter-ui';

  // The three annotation kinds Charter supports. The value each maps to is the wire
  // token sent to the server (kept stable and human-readable).
  var KIND = Object.freeze({
    element: 'element',          // (a) a whole rendered block, keyed by its stable block id
    textRange: 'text-range',     // (b) a selection within a block
    diagramNode: 'diagram-node'  // (c) a node inside a :::diagram Mermaid render, by node identity
  });

  var state = {
    started: false,
    key: null,           // capability key, read from the page URL's ?key= query string
    origin: null,        // postMessage target origin (same-origin by default)
    events: null,        // EventSource for /events live reload
    handlers: [],        // local subscribers registered via on()
    annotations: [],     // the PENDING (pre-handoff) annotations, from GET /api/annotations
    ui: null,            // the SDK-owned chrome: { style, panel, toggle, overlay, ... }
    composer: null,      // the open composer, or null
    ignoreNextClick: false,
    reloadPending: false, // a reload arrived while a draft was open (see onReload)
    overlayRange: null,   // the Range the transient text highlight is drawn from
    overlayTimer: 0,
    flashTimer: 0,
    flashed: null
  };

  // ---- capability key: read from the page URL's ?key= query string --------------------
  function readKey() {
    try {
      return new URLSearchParams(window.location.search).get('key');
    } catch (e) {
      return null;
    }
  }

  // ---- postMessage boundary -----------------------------------------------------------
  // Emit an SDK event across the boundary: broadcast it as a window postMessage AND fan it
  // out to any local on() subscribers. This is the ONLY way page-side observers learn what
  // the SDK is doing. Details must stay structured-cloneable and must NEVER carry a request
  // URL — the capability key rides the path, and postMessage is a broadcast.
  function emit(type, detail) {
    var msg = { channel: CHANNEL, type: type, detail: detail || null };
    try {
      window.postMessage(msg, state.origin || (window.location && window.location.origin) || '*');
    } catch (e) { /* postMessage unavailable — non-fatal */ }
    for (var i = 0; i < state.handlers.length; i++) {
      try { state.handlers[i](msg); } catch (e) { /* isolate a bad subscriber */ }
    }
  }

  // Accept commands FROM the boundary (e.g. a host frame or a headless test driving the
  // SDK): `{ channel, type: 'annotate', detail: <annotation> }` submits programmatically.
  // Only messages this window posted to itself are honoured — the SDK's own emit() targets
  // `window`, so requiring ev.source === window costs nothing and keeps a framing page (or
  // any other window) from driving the DESTRUCTIVE commands below.
  function onMessage(ev) {
    if (!ev || ev.source !== window) return;
    var data = ev.data;
    if (!data || data.channel !== CHANNEL) return;
    if (data.type === 'annotate' && data.detail) {
      submit(data.detail);
    }
    // `{ channel, type: 'answer', detail: <answer> }` submits a :::question answer
    // programmatically — lets a host frame / headless driver drive the answer path too.
    if (data.type === 'answer' && data.detail) {
      postAnswer(data.detail);
    }
    // Pre-handoff management, the programmatic twin of the review panel's Edit/Delete.
    if (data.type === 'update' && data.detail && data.detail.id) {
      updateNote(data.detail.id, String(data.detail.note === undefined ? '' : data.detail.note));
    }
    if (data.type === 'delete' && data.detail && data.detail.id) {
      deleteNote(data.detail.id);
    }
  }

  // ---- anchoring: the three kinds -----------------------------------------------------

  // Nodes that must never resolve to an annotation anchor: the SDK's own chrome (composer,
  // panel, markers, overlay) and the native controls of a rendered :::question form. The guard
  // lives at the ANCHORING layer rather than in the event handlers, so every path that could
  // produce an anchor — click, selection, or a future one — is covered by construction.
  var UNANCHORABLE = '[' + UI_ATTR + '], input, textarea, select, button, option, form.question';

  // Walk up to the nearest ancestor that carries a stable anchor: the renderer stamps each
  // block's content-derived stable id on its root element (and may also expose an explicit
  // data-charter-anchor / data-anchor attribute). Text nodes resolve to their parent.
  function closestAnchored(node) {
    var el = (node && node.nodeType === 3) ? node.parentElement : node;
    if (!el || el.nodeType !== 1 || typeof el.closest !== 'function') return null;
    // The self-guard: anything inside SDK chrome (or a native control) has NO anchor, full stop.
    // Without it a selection that ends inside the panel would anchor to the panel and post a
    // bogus annotation — carrying a quote copied out of another reviewer's note — to the agent.
    if (el.closest(UNANCHORABLE)) return null;
    while (el && el.nodeType === 1) {
      if (el.id ||
          el.hasAttribute('data-charter-anchor') ||
          el.hasAttribute('data-anchor')) {
        return el;
      }
      el = el.parentElement;
    }
    return null;
  }

  function anchorIdOf(el) {
    return el.getAttribute('data-charter-anchor') ||
           el.getAttribute('data-anchor') ||
           el.id ||
           null;
  }

  // Resolve an anchor id back to its live element, or null when the block is gone (the plan was
  // re-rendered and that block no longer exists — an ORPHANED annotation, which the panel still
  // lists rather than dropping). SDK chrome can never satisfy this: it carries no ids.
  function anchorElement(anchorId) {
    if (!anchorId) return null;
    var found = null;
    try { found = document.getElementById(anchorId); } catch (e) { found = null; }
    if (found && !isSdkUi(found)) return found;
    try {
      var quoted = String(anchorId).replace(/["\\]/g, '\\$&');
      found = document.querySelector(
        '[data-charter-anchor="' + quoted + '"], [data-anchor="' + quoted + '"]');
    } catch (e) {
      found = null;
    }
    return (found && !isSdkUi(found)) ? found : null;
  }

  function isSdkUi(node) {
    var el = (node && node.nodeType === 3) ? node.parentElement : node;
    return !!(el && el.nodeType === 1 && typeof el.closest === 'function' && el.closest('[' + UI_ATTR + ']'));
  }

  // (a) element: anchor a note to a whole rendered block by its stable block id.
  function elementAnchor(target) {
    var el = closestAnchored(target);
    if (!el) return null;
    return { kind: KIND.element, anchorId: anchorIdOf(el) };
  }

  // (b) text-range: anchor a note to a selection within a block.
  function textRangeAnchor(selection) {
    if (!selection || selection.isCollapsed || selection.rangeCount === 0) return null;
    var quote = String(selection).trim();
    if (!quote) return null;
    var block = closestAnchored(selection.anchorNode);
    if (!block) return null;
    return {
      kind: KIND.textRange,
      anchorId: anchorIdOf(block),
      quote: quote,
      start: selection.anchorOffset,
      end: selection.focusOffset
    };
  }

  // (c) diagram-node: anchor a note to a node inside a :::diagram Mermaid render, keyed by
  // the node's own identity, plus the enclosing diagram block's stable id.
  function diagramNodeAnchor(target) {
    var node = (target && target.closest)
      ? target.closest('.node, [data-node-id], g.node')
      : null;
    if (!node || isSdkUi(node)) return null;
    var block = closestAnchored(node);
    return {
      kind: KIND.diagramNode,
      anchorId: block ? anchorIdOf(block) : null,
      nodeId: node.getAttribute('data-node-id') || node.id || null
    };
  }

  // ---- human-readable labels ----------------------------------------------------------

  // The text a human sees INSIDE a block, with every SDK-owned subtree excluded — otherwise a
  // count badge injected into the block would pollute the composer's "what am I annotating" line
  // and the panel entry's target label.
  function visibleText(root) {
    if (!root) return '';
    var out = [];
    (function walk(node) {
      if (!node) return;
      if (node.nodeType === 1) {
        if (node.hasAttribute && node.hasAttribute(UI_ATTR)) return;
        for (var i = 0; i < node.childNodes.length; i++) walk(node.childNodes[i]);
        return;
      }
      if (node.nodeType === 3) out.push(node.nodeValue);
    })(root);
    return out.join('').replace(/\s+/g, ' ').trim();
  }

  function truncate(text, max) {
    var s = String(text === null || text === undefined ? '' : text);
    return s.length > max ? (s.slice(0, max - 1) + '\u2026') : s;
  }

  // A short, human-readable name for whatever a note is attached to — a heading/first words of
  // the block, never a raw anchor id (which means nothing to a reviewer).
  function targetLabel(el) {
    if (!el) return 'a block that is no longer in the plan';
    var text = visibleText(el);
    if (text) return truncate(text, 72);
    var tag = (el.tagName || '').toLowerCase();
    return tag ? ('the ' + tag + ' block') : 'this block';
  }

  // The composer's context line: what the reviewer is about to comment on, in words.
  function contextLine(anchor, targetEl) {
    if (anchor.kind === KIND.textRange && anchor.quote) {
      return 'Commenting on selected text: \u201C' + truncate(anchor.quote, 64) + '\u201D';
    }
    if (anchor.kind === KIND.diagramNode) {
      var label = visibleText(targetEl) || anchor.nodeId || 'an unnamed node';
      return 'Commenting on diagram node: ' + truncate(label, 64);
    }
    return 'Commenting on: ' + targetLabel(targetEl || anchorElement(anchor.anchorId));
  }

  // The same label for a stored annotation, used by the panel.
  function recordLabel(record) {
    if (record.kind === KIND.textRange && record.quote) {
      return '\u201C' + truncate(record.quote, 64) + '\u201D';
    }
    var el = anchorElement(record.anchorId);
    if (record.kind === KIND.diagramNode && record.nodeId) {
      return 'diagram node ' + truncate(record.nodeId, 40);
    }
    return targetLabel(el);
  }

  // ---- submit: POST the annotation to /api/{key}/prompts + emit over the boundary ------
  function submit(annotation) {
    if (!annotation || !annotation.anchorId) {
      emit('error', { reason: 'no-anchor', annotation: annotation });
      return Promise.resolve(null);
    }
    // Each annotation carries the block/anchor id, the kind, and the note text, plus the optional sub-part
    // fidelity payload telling the draining agent WHICH part of the block was flagged: quote/start/end for a
    // text-range selection (from textRangeAnchor), nodeId for a diagram node (from diagramNodeAnchor). All are
    // null for a whole-block element annotation. start/end are selection offsets that can legitimately be 0, so
    // they are guarded with an explicit undefined check rather than `|| null` (which would clobber a real 0).
    var payload = {
      anchorId: annotation.anchorId,
      kind: annotation.kind || KIND.element,
      note: annotation.note || '',
      quote: annotation.quote || null,
      start: (annotation.start === undefined ? null : annotation.start),
      end: (annotation.end === undefined ? null : annotation.end),
      nodeId: annotation.nodeId || null
    };
    emit('submitting', payload);
    var url = '/api/' + encodeURIComponent(state.key || '') + '/prompts';
    return fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }).then(function (res) {
      if (!res.ok) {
        setStatus('Could not save that note (' + res.status + ').');
        emit('error', { status: res.status, payload: payload });
        return null;
      }
      // Read back the CREATED annotation (server-assigned id + resolved sourceLine) so the panel and
      // the on-page markers can show it immediately, with no extra round trip.
      return res.json().then(function (created) {
        if (created && created.id) {
          state.annotations.push(created);
          setStatus('');
          showPanel();
          render();
        }
        emit('submitted', { status: res.status, payload: payload, id: created ? created.id : null });
        return created;
      }, function () {
        emit('submitted', { status: res.status, payload: payload, id: null });
        return null;
      });
    }).catch(function (err) {
      setStatus('Could not reach the review server.');
      emit('error', { reason: 'network', message: String(err), payload: payload });
      return null;
    });
  }

  // ---- pre-handoff management: list / edit / retract -----------------------------------
  // These act on the server's PENDING queue only. An annotation the agent has already drained
  // answers 404 — which is not an error but a fact ("already handed off"), surfaced in the panel.

  function hydrate() {
    var url = '/api/annotations?key=' + encodeURIComponent(state.key || '');
    return fetch(url, { headers: { 'Accept': 'application/json' } }).then(function (res) {
      if (!res.ok) {
        setStatus('Could not load your notes (' + res.status + ').');
        emit('list-error', { status: res.status });
        return null;
      }
      return res.json().then(function (list) {
        state.annotations = (list && typeof list.length === 'number') ? list : [];
        render();
        emit('list-loaded', { count: state.annotations.length });
        return state.annotations;
      }, function () {
        emit('list-error', { reason: 'malformed' });
        return null;
      });
    }).catch(function () {
      setStatus('Could not reach the review server.');
      emit('list-error', { reason: 'network' });
      return null;
    });
  }

  function annotationUrl(id, action) {
    return '/api/' + encodeURIComponent(state.key || '') + '/annotations/' + encodeURIComponent(id) +
      (action ? ('/' + action) : '');
  }

  function handedOff(id, operation) {
    dropLocal(id);
    setStatus('That note has already been handed off to the agent.');
    render();
    emit('annotation-handed-off', { id: id, operation: operation });
  }

  function updateNote(id, note) {
    return fetch(annotationUrl(id, null), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ note: note })
    }).then(function (res) {
      if (res.ok) {
        for (var i = 0; i < state.annotations.length; i++) {
          if (state.annotations[i].id === id) state.annotations[i].note = note;
        }
        setStatus('');
        render();
        emit('annotation-updated', { id: id });
        return true;
      }
      if (res.status === 404) { handedOff(id, 'update'); return false; }
      setStatus('Could not save that edit (' + res.status + ').');
      emit('annotation-update-error', { id: id, status: res.status });
      return false;
    }).catch(function () {
      setStatus('Could not reach the review server.');
      emit('annotation-update-error', { id: id, reason: 'network' });
      return false;
    });
  }

  function deleteNote(id) {
    return fetch(annotationUrl(id, 'delete'), { method: 'POST' }).then(function (res) {
      if (res.ok) {
        dropLocal(id);
        setStatus('');
        render();
        emit('annotation-deleted', { id: id });
        return true;
      }
      if (res.status === 404) { handedOff(id, 'delete'); return false; }
      setStatus('Could not delete that note (' + res.status + ').');
      emit('annotation-delete-error', { id: id, status: res.status });
      return false;
    }).catch(function () {
      setStatus('Could not reach the review server.');
      emit('annotation-delete-error', { id: id, reason: 'network' });
      return false;
    });
  }

  function dropLocal(id) {
    var kept = [];
    for (var i = 0; i < state.annotations.length; i++) {
      if (state.annotations[i].id !== id) kept.push(state.annotations[i]);
    }
    state.annotations = kept;
  }

  // ---- :::question answer submit: POST the answer to /api/{key}/answers + emit over the
  // boundary. This is the elicitation half of the loop (parallel to the annotation submit()
  // above): a rendered :::question <form> is intercepted on submit, its structured answer
  // collected from the native controls, and POSTed to the wave-4 /answers route. Same narrow
  // boundary as everything else (invariant 6): the ONLY crossings are the postMessage channel
  // (page side) and the HTTP POST to the defined route (server side).

  // Resolve the question mode. Prefer the explicit attribute the renderer stamps on the block
  // root / form (data-question-mode | data-mode); fall back to inferring it from the controls
  // present so the SDK still works against a form emitted without a mode hint.
  //
  // Inference can no longer produce 'bool': since Charter #43 a bool renders as two Yes/No RADIOS,
  // indistinguishable by shape from a single-select. That is precisely why the renderer now stamps
  // data-question-mode (Charter #56) — without it every bool answer was collected by the single
  // branch and reported to the agent with mode "single".
  function resolveMode(root, form) {
    var m = root.getAttribute('data-question-mode') ||
            root.getAttribute('data-mode') ||
            form.getAttribute('data-question-mode') ||
            form.getAttribute('data-mode');
    if (m) return m.trim();
    if (form.querySelector('input[type="radio"]')) return 'single';
    if (form.querySelector('input[type="checkbox"]')) return 'multi';
    if (form.querySelector('input[type="number"]')) return 'number';
    return 'free-text';
  }

  // Collect the reviewer's answer from the native controls. ALWAYS an array of strings — the
  // server's answer contract is `values: [...]` (Answer.Values is a list), so anything else is
  // rejected with a 400 the reviewer sees as an unexplained in-page failure. This used to return a
  // bare boolean for bool and a bare string for free-text/number, which is exactly how those three
  // modes became unanswerable (Charter #56 / P1).
  //
  //   single | bool -> the checked radio's value      (0- or 1-element array)
  //   multi         -> every checked checkbox's value (0..n)
  //   free-text | number (and any unknown mode) -> the field's value (0- or 1-element array)
  //
  // An UNANSWERED control yields an EMPTY array, never ['']: an empty string is not an answer, and
  // the submit-enabled rule below is built on being able to tell those apart.
  function collectValues(form, mode) {
    if (mode === 'multi' || mode === 'multi-select') {
      var picked = [];
      var boxes = form.querySelectorAll('input[type="checkbox"][name="answer"]:checked');
      for (var i = 0; i < boxes.length; i++) picked.push(boxes[i].value);
      return picked;
    }
    // bool shares the single-select shape: two mutually-exclusive radios valued "true"/"false"
    // (Charter #43), NOT the lone checkbox the SDK used to look for. Explicit, not incidental.
    if (mode === 'single' || mode === 'single-select' || mode === 'bool') {
      var radio = form.querySelector('input[type="radio"][name="answer"]:checked');
      return radio ? [radio.value] : [];
    }
    var field = form.querySelector(
      'textarea, input[type="number"], input[type="text"], ' +
      'input:not([type="radio"]):not([type="checkbox"]):not([type="submit"])' +
      ':not([type="button"]):not([type="hidden"])'
    );
    var value = field ? String(field.value) : '';
    return value === '' ? [] : [value];
  }

  // Build the structured answer from a rendered :::question <form> and its block root (the
  // element carrying the stable block id + the question id — usually the form itself, else its
  // nearest [data-question-id] ancestor).
  function collectAnswer(form, root) {
    var mode = resolveMode(root, form);
    return {
      questionId: root.getAttribute('data-question-id') ||
                  root.getAttribute('data-question') || null,
      anchorId: anchorIdOf(root),   // the block's stable id, parallel to an annotation anchor
      mode: mode,
      values: collectValues(form, mode),
      target: root.getAttribute('data-target') ||
              root.getAttribute('data-question-target') ||
              form.getAttribute('data-target') || null
    };
  }

  // POST the answer to /api/{key}/answers and emit answer-submitting / answer-submitted /
  // answer-error over the boundary, exactly as submit() does for annotations. The key rides
  // the path segment (URL-encoded) from the same ?key= capability key the SDK already reads.
  function postAnswer(answer) {
    if (!answer || !answer.questionId) {
      emit('answer-error', { reason: 'no-question-id', answer: answer });
      return Promise.resolve(null);
    }
    var payload = {
      questionId: answer.questionId,
      anchorId: answer.anchorId || null,
      mode: answer.mode || null,
      values: (answer.values === undefined ? null : answer.values),
      target: answer.target || null
    };
    emit('answer-submitting', payload);
    var url = '/api/' + encodeURIComponent(state.key || '') + '/answers';
    return fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }).then(function (res) {
      emit(res.ok ? 'answer-submitted' : 'answer-error', { status: res.status, payload: payload });
      return res.ok ? res : null;
    }).catch(function (err) {
      emit('answer-error', { reason: 'network', message: String(err), payload: payload });
      return null;
    });
  }

  // The block root of a rendered :::question form — the element carrying the question id. Usually
  // the form itself; a form nested under an annotated block resolves to that ancestor. Returns null
  // for any form Charter does not own, which is what keeps the SDK from claiming a foreign submit.
  function questionRoot(form) {
    if (!form || form.nodeType !== 1 || form.tagName !== 'FORM') return null;
    if (form.hasAttribute('data-question-id')) return form;
    return form.closest ? form.closest('[data-question-id]') : null;
  }

  // Intercept the native submit of a rendered :::question <form>. Non-question forms (none of
  // which Charter emits today) are left to submit normally — we only claim a form that carries
  // (or sits under) a data-question-id.
  function onSubmit(ev) {
    var form = ev && ev.target;
    var root = questionRoot(form);
    if (!root) return;
    ev.preventDefault();
    submitQuestionForm(form, root);
  }

  // ---- the Save control's enabled state -------------------------------------------------
  // The renderer emits the Save button DISABLED (Charter #56): "nothing to submit yet" is the right
  // state for an open question AND for a resolved one, and it keeps the SDK-free saved artifact from
  // firing a native form navigation. From here the SDK owns it, against ONE rule:
  //
  //   Save is enabled exactly when the answer in the form DIFFERS from the answer the markup records.
  //
  // For an open question the markup records nothing, so that reduces to "something is selected". For a
  // RESOLVED question the markup records the settled answer (pre-selected — Charter #48), so Save
  // enables the moment the reviewer picks something else: revising a decision is the whole point of a
  // second review round, and it is never blocked. After a successful submit the baseline moves to what
  // was just saved, so Save settles back to disabled rather than inviting a duplicate post.

  var SUBMIT_SELECTOR = 'button[type="submit"]';

  // The reviewer's current answer as a comparable string. Built from collectValues, so the comparison
  // and the payload can never disagree about what counts as an answer.
  function answerSignature(form, root) {
    return JSON.stringify(collectValues(form, resolveMode(root || form, form)));
  }

  // Record what the MARKUP says is already answered, once per form per document load. A live reload is
  // a full navigation, so this re-runs against the freshly rendered (possibly now-resolved) markup.
  function ensureWired(form, root) {
    if (!form.charterAnswerBaseline) {
      form.charterAnswerBaseline = answerSignature(form, root);
    }
    return form.charterAnswerBaseline;
  }

  function syncSubmitState(form, root) {
    var button = form.querySelector(SUBMIT_SELECTOR);
    if (!button) return;
    var changed = answerSignature(form, root) !== ensureWired(form, root);
    button.disabled = !changed;
    button.title = changed
      ? 'Save this answer to the Charter review session'
      : 'Choose or change an answer to enable saving';
  }

  function wireQuestionForms() {
    var forms = document.querySelectorAll('form[data-question-id]');
    for (var i = 0; i < forms.length; i++) {
      ensureWired(forms[i], forms[i]);
      syncSubmitState(forms[i], forms[i]);
    }
  }

  // Re-sync on every edit of a question control. Capture phase and delegated from the document, like
  // the SDK's other listeners, so it needs no per-control bookkeeping.
  function onQuestionInput(ev) {
    var form = ev && ev.target && ev.target.form;
    var root = questionRoot(form);
    if (root) syncSubmitState(form, root);
  }

  // Enter submits where the control makes that natural: a radio/checkbox/number/text control triggers
  // the form's IMPLICIT submission natively — and because the default button is the disabled-until-
  // changed Save button, that path obeys the same rule for free. A <textarea> must keep Enter as a
  // NEWLINE (a free-text answer is prose), so free-text submits on Ctrl/Cmd+Enter.
  function onQuestionKeydown(ev) {
    if (!ev || ev.key !== 'Enter' || !(ev.ctrlKey || ev.metaKey)) return;
    var form = ev.target && ev.target.form;
    var root = questionRoot(form);
    if (!root) return;
    var button = form.querySelector(SUBMIT_SELECTOR);
    if (button && button.disabled) return;   // nothing to submit — same rule as the button
    ev.preventDefault();
    if (form.requestSubmit) form.requestSubmit(); else submitQuestionForm(form, root);
  }

  // A short SDK-owned status line beside Save. Without it a submit is silent, and "did that save?" is
  // the first thing a reviewer asks. Runtime-only DOM carrying UI_ATTR like the rest of the chrome, so
  // it never reaches the artifact (invariant 1) and can never become an annotation target.
  function answerStatus(form, text, isError) {
    var host = form.querySelector('.question-actions') || form;
    var el = host.querySelector('[' + UI_ATTR + '="answer-status"]');
    if (!el) {
      el = make('span', null, 'answer-status');
      el.setAttribute('role', 'status');
      host.appendChild(el);
    }
    el.className = 'charter-answer-status' + (isError ? ' charter-answer-status-error' : '');
    el.textContent = text;
  }

  // Submit one rendered :::question form, then move the baseline to what was saved so Save settles.
  function submitQuestionForm(form, root) {
    var answer = collectAnswer(form, root);
    answerStatus(form, 'Saving…', false);
    return postAnswer(answer).then(function (res) {
      if (res) {
        form.charterAnswerBaseline = JSON.stringify(answer.values);
        answerStatus(form, 'Answer saved.', false);
      } else {
        answerStatus(form, 'Could not save this answer.', true);
      }
      syncSubmitState(form, root);
      return res;
    });
  }

  // Undo the wiring: drop the status chrome and return every Save button to the DISABLED state the
  // renderer emits, so a disposed SDK leaves an inert (non-navigating) page behind.
  function unwireQuestionForms() {
    var forms = document.querySelectorAll('form[data-question-id]');
    for (var i = 0; i < forms.length; i++) {
      var form = forms[i];
      var status = form.querySelector('[' + UI_ATTR + '="answer-status"]');
      if (status && status.parentNode) status.parentNode.removeChild(status);
      var button = form.querySelector(SUBMIT_SELECTOR);
      if (button) { button.disabled = true; button.removeAttribute('title'); }
      form.charterAnswerBaseline = null;
    }
  }

  // ---- SDK chrome: one inline <style> + the panel + the transient highlight overlay -----
  // All of it is runtime-only DOM built here and never serialized into the artifact (invariant 1),
  // styled entirely from the bundled charter.css design tokens so it inherits light/dark.

  var STYLE = [
    '[' + UI_ATTR + '] { box-sizing: border-box; font-family: var(--charter-font); }',
    '[' + UI_ATTR + '] * { box-sizing: border-box; }',
    '.charter-hidden { display: none !important; }',

    '.charter-btn { font: inherit; font-size: 12px; line-height: 1.4; padding: 3px 9px;',
    '  border: 1px solid var(--charter-border); border-radius: 6px; cursor: pointer;',
    '  background: var(--charter-code-bg); color: var(--charter-fg); }',
    '.charter-btn:disabled { opacity: 0.45; cursor: default; }',
    '.charter-btn-primary { background: var(--charter-accent); border-color: var(--charter-accent); color: #fff; }',

    '.charter-answer-status { font-size: 12px; color: var(--charter-muted); }',
    '.charter-answer-status-error { color: var(--charter-diff-del-fg); }',

    '.charter-composer { position: fixed; z-index: 2147483000; width: 340px; max-width: calc(100vw - 24px);',
    '  background: var(--charter-bg); color: var(--charter-fg); border: 1px solid var(--charter-border);',
    '  border-radius: 8px; box-shadow: 0 8px 28px rgba(0, 0, 0, 0.3); padding: 10px; font-size: 13px; }',
    '.charter-composer-context { color: var(--charter-muted); font-size: 12px; margin-bottom: 6px;',
    '  overflow: hidden; text-overflow: ellipsis; }',
    '.charter-composer-input { display: block; width: 100%; min-height: 76px; resize: vertical;',
    '  font: inherit; font-size: 13px; padding: 6px 8px; border-radius: 6px;',
    '  border: 1px solid var(--charter-border); background: var(--charter-code-bg); color: var(--charter-fg); }',
    '.charter-composer-hint { color: var(--charter-muted); font-size: 11px; margin: 6px 0; }',
    '.charter-composer-actions { display: flex; gap: 8px; justify-content: flex-end; }',

    '.charter-panel { position: fixed; top: 0; right: 0; bottom: 0; width: 340px; max-width: 100vw;',
    '  z-index: 2147482000; display: flex; flex-direction: column; font-size: 13px;',
    '  background: var(--charter-bg); color: var(--charter-fg);',
    '  border-left: 1px solid var(--charter-border); box-shadow: -4px 0 18px rgba(0, 0, 0, 0.14); }',
    '.charter-panel-header { display: flex; align-items: center; justify-content: space-between; gap: 8px;',
    '  padding: 10px 12px; border-bottom: 1px solid var(--charter-border); font-weight: 600; }',
    '.charter-panel-list { flex: 1 1 auto; overflow-y: auto; padding: 8px; }',
    '.charter-panel-empty { color: var(--charter-muted); padding: 10px 4px; }',
    '.charter-panel-status { padding: 8px 12px; border-top: 1px solid var(--charter-border);',
    '  color: var(--charter-muted); font-size: 12px; }',

    '.charter-item { border: 1px solid var(--charter-border); border-radius: 6px; padding: 8px;',
    '  margin-bottom: 8px; background: var(--charter-code-bg); }',
    '.charter-item-target { display: flex; align-items: baseline; gap: 6px; margin-bottom: 4px;',
    '  color: var(--charter-muted); font-size: 11px; }',
    '.charter-item-label { flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }',
    '.charter-item-line { flex: 0 0 auto; border: 1px solid var(--charter-border); border-radius: 999px;',
    '  padding: 0 6px; }',
    '.charter-item-note { white-space: pre-wrap; overflow-wrap: break-word; margin-bottom: 6px; }',
    '.charter-item-actions { display: flex; gap: 6px; }',
    '.charter-item[data-charter-orphan="true"] .charter-item-label { font-style: italic; }',

    '.charter-panel-toggle { position: fixed; right: 14px; bottom: 14px; z-index: 2147482500;',
    '  font: inherit; font-size: 12px; padding: 6px 12px; border-radius: 999px; cursor: pointer;',
    '  background: var(--charter-accent); color: #fff; border: 1px solid var(--charter-accent);',
    '  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.24); }',

    '.charter-reload-banner { position: fixed; top: 12px; left: 50%; transform: translateX(-50%);',
    '  z-index: 2147483001; display: flex; align-items: center; gap: 10px; font-size: 13px;',
    '  padding: 8px 12px; border-radius: 8px; background: var(--charter-warn-bg); color: var(--charter-fg);',
    '  border: 1px solid var(--charter-warn-border); box-shadow: 0 4px 16px rgba(0, 0, 0, 0.2); }',

    '.charter-overlay { position: fixed; top: 0; left: 0; right: 0; bottom: 0; z-index: 2147481000;',
    '  pointer-events: none; }',
    '.charter-overlay-rect { position: fixed; border-radius: 2px; background: var(--charter-accent);',
    '  opacity: 0.24; pointer-events: none; }',

    '.charter-has-annotations { position: relative; box-shadow: inset 3px 0 0 0 var(--charter-accent); }',
    '.charter-annotation-badge { position: absolute; top: 2px; right: 2px; z-index: 3; font: inherit;',
    '  font-size: 11px; line-height: 1; min-width: 18px; padding: 3px 6px; border-radius: 999px;',
    '  cursor: pointer; background: var(--charter-accent); color: #fff;',
    '  border: 1px solid var(--charter-accent); }',
    '.charter-annotate-target { outline: 2px solid var(--charter-accent); outline-offset: 2px; }',
    '.charter-anchor-flash { outline: 2px dashed var(--charter-accent); outline-offset: 3px; }'
  ].join('\n');

  function make(tag, className, uiName, text) {
    var el = document.createElement(tag);
    if (className) el.className = className;
    if (uiName) el.setAttribute(UI_ATTR, uiName);
    if (text !== undefined && text !== null) el.textContent = text;
    return el;
  }

  function button(className, uiName, text) {
    var b = make('button', className, uiName, text);
    b.type = 'button';   // never a form submit — the SDK's chrome must not trigger navigation
    return b;
  }

  // Build the SDK chrome once. Idempotent; safe to call from any entry point.
  function ensureUi() {
    if (state.ui || !document.body) return state.ui;

    var style = make('style', null, 'style');
    style.textContent = STYLE;
    (document.head || document.documentElement).appendChild(style);

    var panel = make('div', 'charter-panel charter-hidden', 'panel');
    panel.setAttribute('role', 'complementary');
    panel.setAttribute('aria-label', 'Charter review notes');

    var header = make('div', 'charter-panel-header', 'panel-header');
    var title = make('span', 'charter-panel-title', 'panel-title', 'Review notes (0)');
    var close = button('charter-btn', 'panel-close', 'Hide');
    close.addEventListener('click', hidePanel, false);
    header.appendChild(title);
    header.appendChild(close);

    var list = make('div', 'charter-panel-list', 'panel-list');
    var status = make('div', 'charter-panel-status charter-hidden', 'panel-status');

    panel.appendChild(header);
    panel.appendChild(list);
    panel.appendChild(status);

    var toggle = button('charter-panel-toggle', 'panel-toggle', 'Notes 0');
    toggle.setAttribute('aria-label', 'Show Charter review notes');
    toggle.addEventListener('click', togglePanel, false);

    var overlay = make('div', 'charter-overlay', 'overlay');

    document.body.appendChild(overlay);
    document.body.appendChild(panel);
    document.body.appendChild(toggle);

    state.ui = {
      style: style, panel: panel, title: title, list: list,
      status: status, toggle: toggle, overlay: overlay, banner: null
    };
    return state.ui;
  }

  function setStatus(text) {
    if (!state.ui) return;
    state.ui.status.textContent = text || '';
    state.ui.status.className = text ? 'charter-panel-status' : 'charter-panel-status charter-hidden';
  }

  function showPanel() {
    if (!ensureUi()) return;
    state.ui.panel.className = 'charter-panel';
    emit('panel-opened', {});
  }

  function hidePanel() {
    if (!state.ui) return;
    state.ui.panel.className = 'charter-panel charter-hidden';
    emit('panel-closed', {});
  }

  function togglePanel() {
    if (!ensureUi()) return;
    if (state.ui.panel.className.indexOf('charter-hidden') >= 0) showPanel(); else hidePanel();
  }

  // ---- the composer: a near-target, dismissible popover (replaces window.prompt, #41) ---

  function closeComposer(reason) {
    var open = state.composer;
    if (!open) return;
    state.composer = null;
    if (open.root && open.root.parentNode) open.root.parentNode.removeChild(open.root);
    if (open.outlined && open.outlined.classList) open.outlined.classList.remove('charter-annotate-target');
    clearOverlay();
    if (reason) emit(reason, { });
  }

  function hasDraft() {
    return !!(state.composer && state.composer.input && state.composer.input.value.trim());
  }

  /**
   * Show the composer. `cfg` = { context, note, target, range, saveLabel, onSave(text) }.
   * Both the "new note" and the "edit an existing note" flows go through here, so the two
   * always look and behave identically.
   */
  function showComposer(cfg) {
    if (!ensureUi()) return null;
    closeComposer(null);

    var root = make('div', 'charter-composer', 'composer');
    root.setAttribute('role', 'dialog');
    root.setAttribute('aria-label', 'Add a review note');

    root.appendChild(make('div', 'charter-composer-context', 'composer-context', cfg.context));

    var input = make('textarea', 'charter-composer-input', 'composer-input');
    input.setAttribute('placeholder', 'Describe the change you want\u2026');
    input.value = cfg.note || '';
    root.appendChild(input);

    root.appendChild(make('div', 'charter-composer-hint', 'composer-hint',
      'Ctrl/\u2318+Enter to save \u00b7 Esc to cancel'));

    var actions = make('div', 'charter-composer-actions', 'composer-actions');
    var cancel = button('charter-btn', 'composer-cancel', 'Cancel');
    var save = button('charter-btn charter-btn-primary', 'composer-save', cfg.saveLabel || 'Save');
    actions.appendChild(cancel);
    actions.appendChild(save);
    root.appendChild(actions);

    document.body.appendChild(root);

    function syncEnabled() { save.disabled = input.value.trim().length === 0; }
    syncEnabled();

    function commit() {
      var text = input.value.trim();
      if (!text) return;
      closeComposer(null);
      var result = cfg.onSave(text);
      if (result && typeof result.then === 'function') result.then(maybeReload, maybeReload);
      else maybeReload();
    }

    function dismiss() {
      closeComposer('composer-cancelled');
      maybeReload();
    }

    input.addEventListener('input', syncEnabled, false);
    save.addEventListener('click', commit, false);
    cancel.addEventListener('click', dismiss, false);
    root.addEventListener('keydown', function (ev) {
      if (ev.key === 'Escape') { ev.stopPropagation(); dismiss(); return; }
      if (ev.key === 'Enter' && (ev.ctrlKey || ev.metaKey)) { ev.preventDefault(); commit(); }
    }, false);

    state.composer = { root: root, input: input, outlined: null };

    // Highlight what is being annotated, exactly as Lavish does: an outline for an element or a
    // diagram node, and transient overlay rectangles for a text range. The text-range highlight
    // deliberately does NOT wrap the quote in a <mark>: splitting the block's text nodes would
    // corrupt the selection offsets every later text-range annotation in that block is measured
    // against (and double-wrap a repeated quote).
    if (cfg.range) {
      drawOverlay(cfg.range);
    } else if (cfg.target && cfg.target.classList) {
      cfg.target.classList.add('charter-annotate-target');
      state.composer.outlined = cfg.target;
    }

    positionComposer(root, cfg.target);
    try { input.focus(); } catch (e) { /* focus is best-effort */ }
    emit('composer-opened', { context: cfg.context, editing: !!cfg.editing });
    return state.composer;
  }

  // Place the popover near its target, clamped inside the viewport, preferring below (Lavish's
  // proven placement). Falls back to the top-right corner when there is no live target.
  function positionComposer(root, target) {
    var margin = 12;
    var w = root.offsetWidth || 340;
    var h = root.offsetHeight || 180;
    var vw = window.innerWidth || 1024;
    var vh = window.innerHeight || 768;
    var top = margin;
    var left = Math.max(margin, vw - w - margin);

    var rect = (target && target.getBoundingClientRect) ? target.getBoundingClientRect() : null;
    if (rect && (rect.width || rect.height)) {
      top = rect.bottom + 8;
      if (top + h > vh - margin) top = rect.top - h - 8;
      left = rect.left;
    }

    root.style.top = Math.max(margin, Math.min(top, vh - h - margin)) + 'px';
    root.style.left = Math.max(margin, Math.min(left, vw - w - margin)) + 'px';
  }

  function openComposerForAnchor(anchor, target, range) {
    emit('anchor', anchor);
    showComposer({
      context: contextLine(anchor, target),
      target: target,
      range: range || null,
      onSave: function (text) {
        anchor.note = text;
        return submit(anchor);
      }
    });
  }

  function openComposerForEdit(record, target) {
    showComposer({
      context: 'Editing your note on: ' + recordLabel(record),
      note: record.note || '',
      target: target,
      editing: true,
      saveLabel: 'Save edit',
      onSave: function (text) { return updateNote(record.id, text); }
    });
  }

  // ---- the review panel: list / jump / edit / delete the PENDING notes (#42) ------------

  // Document order, so the list reads top-to-bottom like the plan. Orphans (their block is gone
  // from the re-rendered plan) sort last and keep submit order — they are never dropped.
  function orderedEntries() {
    var entries = [];
    for (var i = 0; i < state.annotations.length; i++) {
      entries.push({ record: state.annotations[i], el: anchorElement(state.annotations[i].anchorId), index: i });
    }
    entries.sort(function (a, b) {
      if (!a.el && !b.el) return a.index - b.index;
      if (!a.el) return 1;
      if (!b.el) return -1;
      if (a.el === b.el) return a.index - b.index;
      var mask = a.el.compareDocumentPosition(b.el);
      if (mask & 4 /* DOCUMENT_POSITION_FOLLOWING */) return -1;
      if (mask & 2 /* DOCUMENT_POSITION_PRECEDING */) return 1;
      return a.index - b.index;
    });
    return entries;
  }

  function buildItem(entry) {
    var record = entry.record;
    var item = make('div', 'charter-item', 'item');
    item.setAttribute('data-annotation-id', record.id || '');
    item.setAttribute('data-anchor-id', record.anchorId || '');
    item.setAttribute('data-charter-orphan', entry.el ? 'false' : 'true');

    var target = make('div', 'charter-item-target', 'item-target');
    target.appendChild(make('span', 'charter-item-label', 'item-label', recordLabel(record)));
    if (record.sourceLine) {
      target.appendChild(make('span', 'charter-item-line', 'item-line', 'line ' + record.sourceLine));
    }
    item.appendChild(target);

    item.appendChild(make('div', 'charter-item-note', 'item-note', record.note || ''));

    var actions = make('div', 'charter-item-actions', 'item-actions');
    var jump = button('charter-btn', 'item-jump', 'Jump');
    jump.disabled = !entry.el;
    jump.addEventListener('click', function () { jumpTo(record); }, false);
    var edit = button('charter-btn', 'item-edit', 'Edit');
    edit.addEventListener('click', function () { openComposerForEdit(record, item); }, false);
    var remove = button('charter-btn', 'item-delete', 'Delete');
    remove.addEventListener('click', function () { deleteNote(record.id); }, false);
    actions.appendChild(jump);
    actions.appendChild(edit);
    actions.appendChild(remove);
    item.appendChild(actions);

    return item;
  }

  function renderPanel(entries) {
    var ui = state.ui;
    var list = ui.list;
    while (list.firstChild) list.removeChild(list.firstChild);

    if (entries.length === 0) {
      list.appendChild(make('div', 'charter-panel-empty', 'panel-empty',
        'No notes yet. Alt+click a block \u2014 or select some text \u2014 to comment on it.'));
    } else {
      for (var i = 0; i < entries.length; i++) list.appendChild(buildItem(entries[i]));
    }

    ui.title.textContent = 'Review notes (' + entries.length + ')';
    ui.toggle.textContent = 'Notes ' + entries.length;
  }

  // ---- on-page markers: which blocks already carry a note ------------------------------
  // Elements that must not host an appended badge (the browser would relocate it, or it would
  // break the element's content model). The highlight class still carries the signal there.
  var BADGE_DENIED = ['TABLE', 'THEAD', 'TBODY', 'TFOOT', 'TR', 'UL', 'OL', 'DL', 'HR', 'IMG', 'BR'];

  function clearMarkers() {
    var marked = document.querySelectorAll('.charter-has-annotations');
    for (var i = 0; i < marked.length; i++) {
      marked[i].classList.remove('charter-has-annotations');
      marked[i].removeAttribute('data-charter-annotation-count');
    }
    var badges = document.querySelectorAll('.charter-annotation-badge');
    for (var j = 0; j < badges.length; j++) {
      if (badges[j].parentNode) badges[j].parentNode.removeChild(badges[j]);
    }
  }

  function renderMarkers(entries) {
    clearMarkers();
    var order = [];
    var counts = Object.create(null);
    for (var i = 0; i < entries.length; i++) {
      var id = entries[i].record.anchorId;
      if (!id || !entries[i].el) continue;
      if (counts[id] === undefined) { counts[id] = 0; order.push(id); }
      counts[id]++;
    }

    for (var k = 0; k < order.length; k++) {
      var anchorId = order[k];
      var el = anchorElement(anchorId);
      if (!el) continue;
      el.classList.add('charter-has-annotations');
      el.setAttribute('data-charter-annotation-count', String(counts[anchorId]));
      if (BADGE_DENIED.indexOf(el.tagName) < 0) el.appendChild(makeBadge(anchorId, counts[anchorId]));
    }

    emit('markers-rendered', { blocks: order.length });
  }

  function makeBadge(anchorId, count) {
    var badge = button('charter-annotation-badge', 'badge', String(count));
    badge.setAttribute('data-anchor-id', anchorId);
    badge.setAttribute('aria-label', count + ' review note(s) on this block');
    badge.addEventListener('click', function (ev) {
      ev.preventDefault();
      ev.stopPropagation();
      showPanel();
      focusPanelEntry(anchorId);
    }, false);
    return badge;
  }

  function focusPanelEntry(anchorId) {
    if (!state.ui) return;
    var quoted = String(anchorId).replace(/["\\]/g, '\\$&');
    var item = null;
    try { item = state.ui.list.querySelector('[data-anchor-id="' + quoted + '"]'); } catch (e) { item = null; }
    if (item && item.scrollIntoView) item.scrollIntoView({ block: 'nearest' });
  }

  function render() {
    if (!ensureUi()) return;
    var entries = orderedEntries();
    renderPanel(entries);
    renderMarkers(entries);
  }

  // ---- jump + transient highlight (never mutates the plan's DOM) -----------------------

  function jumpTo(record) {
    var el = anchorElement(record.anchorId);
    if (!el) return;
    if (el.scrollIntoView) el.scrollIntoView({ block: 'center' });
    flash(el);
    if (record.kind === KIND.textRange && record.quote) {
      var range = findQuoteRange(el, record.quote);
      if (range) drawOverlay(range, 1600);
    }
    emit('annotation-jumped', { id: record.id });
  }

  function flash(el) {
    if (state.flashTimer) window.clearTimeout(state.flashTimer);
    if (state.flashed && state.flashed.classList) state.flashed.classList.remove('charter-anchor-flash');
    el.classList.add('charter-anchor-flash');
    state.flashed = el;
    state.flashTimer = window.setTimeout(function () {
      if (state.flashed && state.flashed.classList) state.flashed.classList.remove('charter-anchor-flash');
      state.flashed = null;
      state.flashTimer = 0;
    }, 1400);
  }

  // Locate `quote` inside `block` as a DOM Range, walking the block's own text nodes (SDK subtrees
  // excluded). Building a Range READS the DOM — unlike wrapping the quote in a <mark>, it never
  // splits a text node, so the offsets later text-range annotations record stay valid.
  function findQuoteRange(block, quote) {
    if (!block || !quote || typeof document.createRange !== 'function') return null;
    var nodes = [];
    var texts = [];
    (function walk(node) {
      if (node.nodeType === 1) {
        if (node.hasAttribute && node.hasAttribute(UI_ATTR)) return;
        for (var i = 0; i < node.childNodes.length; i++) walk(node.childNodes[i]);
        return;
      }
      if (node.nodeType === 3) { nodes.push(node); texts.push(node.nodeValue || ''); }
    })(block);

    var at = texts.join('').indexOf(quote);
    if (at < 0) return null;

    var end = at + quote.length;
    var range = document.createRange();
    var pos = 0;
    var started = false;
    for (var i = 0; i < nodes.length; i++) {
      var len = texts[i].length;
      if (!started && at < pos + len) { range.setStart(nodes[i], at - pos); started = true; }
      if (started && end <= pos + len) { range.setEnd(nodes[i], end - pos); return range; }
      pos += len;
    }
    return null;
  }

  // Paint the range's client rectangles as pointer-transparent overlay divs in the SDK's own
  // fixed layer (Lavish's highlightTextRange pattern) — zero mutation of the annotated content.
  function drawOverlay(range, ms) {
    if (!ensureUi() || !range || typeof range.getClientRects !== 'function') return;
    clearOverlayRects();
    state.overlayRange = range;

    var rects = range.getClientRects();
    for (var i = 0; i < rects.length; i++) {
      var r = rects[i];
      if (!r || r.width <= 0 || r.height <= 0) continue;
      var box = make('div', 'charter-overlay-rect', 'overlay-rect');
      box.style.top = r.top + 'px';
      box.style.left = r.left + 'px';
      box.style.width = r.width + 'px';
      box.style.height = r.height + 'px';
      state.ui.overlay.appendChild(box);
    }

    if (ms) {
      if (state.overlayTimer) window.clearTimeout(state.overlayTimer);
      state.overlayTimer = window.setTimeout(clearOverlay, ms);
    }
  }

  function clearOverlayRects() {
    if (!state.ui) return;
    var layer = state.ui.overlay;
    while (layer.firstChild) layer.removeChild(layer.firstChild);
  }

  function clearOverlay() {
    if (state.overlayTimer) { window.clearTimeout(state.overlayTimer); state.overlayTimer = 0; }
    state.overlayRange = null;
    clearOverlayRects();
  }

  // The overlay is drawn in viewport coordinates, so it must follow scroll/resize.
  function onViewportChange() {
    if (state.overlayRange) drawOverlay(state.overlayRange);
  }

  // ---- capture UI: Alt+click to anchor an element / diagram-node; select text to anchor a
  // range. Both open the in-page composer — never a native window.prompt (#41).

  function onClick(ev) {
    if (state.ignoreNextClick) { state.ignoreNextClick = false; return; }

    if (!ev.altKey) {
      // Clicking away closes an EMPTY composer. A composer holding a draft stays put: losing a
      // half-written note to a stray click is the same failure mode as losing it to a reload.
      if (state.composer && !isSdkUi(ev.target) && !hasDraft()) closeComposer('composer-cancelled');
      return;
    }

    var dn = diagramNodeAnchor(ev.target);
    if (dn && dn.anchorId) {
      ev.preventDefault();
      openComposerForAnchor(dn, ev.target.closest('.node, [data-node-id], g.node'));
      return;
    }

    var el = elementAnchor(ev.target);
    if (el) {
      ev.preventDefault();
      openComposerForAnchor(el, anchorElement(el.anchorId));
    }
  }

  function onMouseUp(ev) {
    if (ev && isSdkUi(ev.target)) return;
    if (hasDraft()) return;   // never clobber a note in progress
    var sel = (typeof window.getSelection === 'function') ? window.getSelection() : null;
    var tr = textRangeAnchor(sel);
    if (!tr) return;

    // Snapshot the live selection BEFORE focusing the composer's textarea collapses it — the
    // Range is what the transient highlight is drawn from. It never leaves the SDK (it is not
    // part of the submitted payload and never rides a postMessage).
    var range = null;
    try { range = sel.getRangeAt(0).cloneRange(); } catch (e) { range = null; }

    state.ignoreNextClick = true;
    openComposerForAnchor(tr, anchorElement(tr.anchorId), range);
  }

  // ---- live reload: listen for server-sent events on /events (SSE) ---------------------
  function eventsUrl() {
    // /events is capability-gated like every other route, so ride the key on the query
    // string when we have one. The route itself stays literally /events.
    return state.key ? ('/events?key=' + encodeURIComponent(state.key)) : '/events';
  }

  function navigate() {
    state.reloadPending = false;
    try { window.location.reload(); } catch (e) { /* ignore */ }
  }

  // Reload once the reviewer is no longer mid-note — called after a save or a cancel.
  function maybeReload() {
    if (state.reloadPending && !hasDraft()) navigate();
  }

  // The agent edits the plan while the reviewer is typing. A blocking window.prompt used to make
  // that impossible; a non-modal composer does not, so a reload arriving over a live draft is
  // DEFERRED and offered as a banner instead of silently discarding what was typed.
  function onReload() {
    emit('reload', {});
    if (hasDraft()) {
      state.reloadPending = true;
      showReloadBanner();
      emit('reload-deferred', {});
      return;
    }
    navigate();
  }

  function showReloadBanner() {
    if (!ensureUi() || state.ui.banner) return;
    var banner = make('div', 'charter-reload-banner', 'reload-banner');
    banner.appendChild(make('span', null, 'reload-banner-text',
      'The plan changed on disk. Your note is safe \u2014 reload when you are ready.'));
    var now = button('charter-btn', 'reload-now', 'Reload now');
    now.addEventListener('click', navigate, false);
    banner.appendChild(now);
    document.body.appendChild(banner);
    state.ui.banner = banner;
  }

  function openEvents() {
    if (typeof EventSource === 'undefined') return;   // SSE unavailable — non-fatal
    try {
      var es = new EventSource(eventsUrl());
      es.addEventListener('reload', onReload);
      es.onmessage = function (m) { emit('event', { data: m && m.data }); };
      es.onerror = function () { emit('events-error', {}); };
      state.events = es;
    } catch (e) { /* SSE could not open — non-fatal, review still works pull-side */ }
  }

  // ---- public API ---------------------------------------------------------------------
  function init(options) {
    if (state.started) return api;
    options = options || {};
    state.key = options.key || readKey();
    state.origin = options.origin || (window.location && window.location.origin) || null;

    if (window.addEventListener) {
      window.addEventListener('message', onMessage, false);
      window.addEventListener('scroll', onViewportChange, true);
      window.addEventListener('resize', onViewportChange, false);
      document.addEventListener('click', onClick, true);
      document.addEventListener('mouseup', onMouseUp, false);
      document.addEventListener('submit', onSubmit, true);
      document.addEventListener('input', onQuestionInput, true);
      document.addEventListener('change', onQuestionInput, true);
      document.addEventListener('keydown', onQuestionKeydown, true);
    }
    ensureUi();
    wireQuestionForms();
    openEvents();

    state.started = true;
    emit('ready', { hasKey: !!state.key });

    // Hydrate the pending queue ONCE, here. A live reload is a full navigation, so init() runs
    // again on the new document — re-fetching inside the reload handler would only race it.
    hydrate();
    return api;
  }

  function on(handler) {
    if (typeof handler === 'function') state.handlers.push(handler);
    return api;
  }

  function dispose() {
    if (!state.started) return;
    if (window.removeEventListener) {
      window.removeEventListener('message', onMessage, false);
      window.removeEventListener('scroll', onViewportChange, true);
      window.removeEventListener('resize', onViewportChange, false);
      document.removeEventListener('click', onClick, true);
      document.removeEventListener('mouseup', onMouseUp, false);
      document.removeEventListener('submit', onSubmit, true);
      document.removeEventListener('input', onQuestionInput, true);
      document.removeEventListener('change', onQuestionInput, true);
      document.removeEventListener('keydown', onQuestionKeydown, true);
    }
    unwireQuestionForms();
    if (state.events) {
      try { state.events.close(); } catch (e) { /* ignore */ }
      state.events = null;
    }
    closeComposer(null);
    clearMarkers();
    if (state.ui) {
      var owned = [state.ui.style, state.ui.panel, state.ui.toggle, state.ui.overlay, state.ui.banner];
      for (var i = 0; i < owned.length; i++) {
        if (owned[i] && owned[i].parentNode) owned[i].parentNode.removeChild(owned[i]);
      }
      state.ui = null;
    }
    state.handlers.length = 0;
    state.annotations = [];
    state.started = false;
  }

  var api = {
    KIND: KIND,
    CHANNEL: CHANNEL,
    init: init,          // entry point
    on: on,              // subscribe to boundary events locally
    annotate: submit,    // submit an annotation programmatically
    answer: postAnswer,  // submit a :::question answer programmatically
    list: hydrate,       // re-read the pending queue from the server
    update: updateNote,  // edit a still-pending annotation's note
    remove: deleteNote,  // retract a still-pending annotation
    panel: togglePanel,  // show/hide the review panel
    dispose: dispose
  };

  // ---- serve-time auto-init ------------------------------------------------------------
  // The SDK is injected ONLY into the served review page (never the saved artifact — invariant 1), so wiring the
  // review loop up automatically here is safe and is what makes the in-place-annotation UI live on load. Guarded
  // to run once the DOM is ready and idempotent (init() no-ops if already started), so a host that prefers to
  // drive init() itself still can. Kept in the SDK (invariant 6: browser logic lives here, not in C#).
  function autoInit() {
    try { api.init(); } catch (e) { /* non-fatal — a host can still drive init() manually */ }
  }
  if (typeof document !== 'undefined' && typeof window !== 'undefined') {
    if (document.readyState === 'loading' && window.addEventListener) {
      document.addEventListener('DOMContentLoaded', autoInit, { once: true });
    } else {
      autoInit();
    }
  }

  return api;
})();
