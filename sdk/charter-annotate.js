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
    // The FOLDED review log of every author, from GET /api/review-log — this is what makes teammates'
    // comments visible. The server reads and folds `<plan>.review/*.jsonl` itself; the browser only ever
    // sees this projection, never a file.
    log: { comments: [], diagnostics: [], unreadable: [], selfEmail: null },
    ui: null,            // the SDK-owned chrome: { style, panel, toggle, overlay, ... }
    // The review ROUND's hand-off state, mirrored from GET /api/review. `submitted` is true while the
    // reviewer's "Send to agent" click is pending (the agent has not been told yet); `pending` is the live
    // server-side queue depth. Both come from the server rather than being counted locally, so they survive a
    // live reload — which is a full navigation that would otherwise reset a local tally.
    round: { submitted: false, pending: { annotations: 0, answers: 0 } },
    // The replaced-plan quarantine, mirrored from GET /api/review: { count, fileName, durabilityDisabled } or
    // null. `charter review` is frequently launched BY an agent, so the stderr notice it also writes may reach
    // no human at all — this is what puts "your earlier notes were set aside, here is how to get them back"
    // where the reviewer actually is. Runtime-only DOM, like every other piece of SDK chrome.
    staleQueue: null,
    staleQueueShown: false,
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
    // `{ channel, type: 'resolve', detail: { id } }` closes a comment in the review log — the
    // programmatic twin of the panel's Resolve button. Distinct from 'send' below: resolve settles
    // ONE comment durably in the log, send hands the whole ROUND to the agent.
    if (data.type === 'resolve' && data.detail && data.detail.id) {
      resolveNote(data.detail.id);
    }
    // `{ channel, type: 'send' }` hands the round off to the agent — the programmatic twin of the
    // panel's "Send to agent" button.
    if (data.type === 'send') {
      sendRound();
    }
  }

  // ---- anchoring: the three kinds -----------------------------------------------------

  // Nodes that must never resolve to an annotation anchor: the SDK's own chrome (composer,
  // panel, markers, overlay) and the native controls of a rendered :::question form. The guard
  // lives at the ANCHORING layer rather than in the event handlers, so every path that could
  // produce an anchor — click, selection, or a future one — is covered by construction.
  var UNANCHORABLE = '[' + UI_ATTR + '], input, textarea, select, button, option, form.question';

  // A rendered :::diagram block. The renderer stamps the block's content-derived stable Charter id on the
  // <pre class="mermaid"> root; the Mermaid runtime then REPLACES that element's content with an <svg> and
  // stamps ITS OWN generated ids on the svg and on every node inside it. Those ids are not Charter anchors:
  // SourceMap.LineForAnchor cannot map one to a markdown line (so the agent is handed no sourceLine), and
  // they are regenerated on every render (so the annotation orphans). Charter #48.
  var DIAGRAM_BLOCK = 'pre.mermaid';

  // The Mermaid node selectors — the ONLY sub-part of a diagram Charter addresses.
  var DIAGRAM_NODE = '.node, [data-node-id], g.node';

  // The enclosing :::diagram block of `node`, or null. Text nodes resolve through their parent.
  function diagramBlock(node) {
    var el = (node && node.nodeType === 3) ? node.parentElement : node;
    if (!el || el.nodeType !== 1 || typeof el.closest !== 'function') return null;
    return el.closest(DIAGRAM_BLOCK);
  }

  function isDiagramBlock(el) {
    return !!(el && el.nodeType === 1 && typeof el.matches === 'function' && el.matches(DIAGRAM_BLOCK));
  }

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
    // The same guard, one layer down: INSIDE a rendered diagram the only Charter anchor is the block
    // itself, so resolve it explicitly rather than letting the generic id walk stop on whichever Mermaid
    // id it meets first. Doing it here rather than in each caller means no path — click, selection, or a
    // future one — can walk out carrying a Mermaid id, by construction (Charter #48).
    var diagram = diagramBlock(el);
    if (diagram) return diagram;
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

  // (a) element: anchor a note to a whole rendered block by its stable block id. `block` is the element
  // closestAnchored already resolved, so this is the same shape for every block type — a :::diagram
  // commented on as a whole included (Charter #60).
  function elementAnchor(block) {
    return { kind: KIND.element, anchorId: anchorIdOf(block) };
  }

  // The block's own text nodes in document order, with every [data-charter-ui] subtree skipped, plus their
  // values. Concatenating `texts` gives the block's text content — the SINGLE reference frame that both the
  // recorded start/end offsets and the panel's quote lookup are expressed in. SDK chrome injected into a
  // block (a marker, a count badge) can therefore never shift an offset or contribute to a quote.
  function blockTextNodes(block) {
    var nodes = [];
    var texts = [];
    (function walk(node) {
      if (!node) return;
      if (node.nodeType === 1) {
        if (node.hasAttribute && node.hasAttribute(UI_ATTR)) return;
        for (var i = 0; i < node.childNodes.length; i++) walk(node.childNodes[i]);
        return;
      }
      if (node.nodeType === 3) { nodes.push(node); texts.push(node.nodeValue || ''); }
    })(block);
    return { nodes: nodes, texts: texts };
  }

  // (b) text-range: anchor a note to a selection within a block.
  //
  // `gestureTarget` is what the reviewer's pointer was actually over when the gesture ended. A selection is
  // only INTENT when it includes that: double-clicking a spot the browser cannot select — a rendered
  // diagram's background — makes Chromium fall back to selecting the nearest word instead, which is real,
  // non-empty and inside a perfectly good block, and is still not what the reviewer pointed at. Turning
  // that into a text-range annotation is how a diagram double-click opened a composer over unrelated text
  // elsewhere on the page (Charter #61).
  function textRangeAnchor(selection, gestureTarget) {
    if (!selection || selection.isCollapsed || selection.rangeCount === 0) return null;
    var quote = String(selection).trim();
    if (!quote) return null;
    var range = null;
    try { range = selection.getRangeAt(0); } catch (e) { range = null; }
    if (!coversGesture(range, gestureTarget)) return null;
    var block = closestAnchored(selection.anchorNode);
    if (!block) return null;
    // A rendered diagram carries no annotatable prose — its granularities are the NODE and the WHOLE block
    // — so a selection landing inside one is never a text range, however it got there. This is the half of
    // the #61 fix that does not depend on the stylesheet reaching the browser.
    if (isDiagramBlock(block)) return null;
    var span = blockSpan(block, range);
    return {
      kind: KIND.textRange,
      anchorId: anchorIdOf(block),
      quote: quote,
      // Offsets into the ANCHORED BLOCK's own text, or nulls. Never a pair from two frames (#56).
      start: span ? span.start : null,
      end: span ? span.end : null
    };
  }

  // Does `range` include the element the reviewer's gesture ended on? An UNKNOWN gesture (a programmatic
  // mouseup dispatched on the document, an engine without intersectsNode) is ACCEPTED: this guard exists to
  // reject a demonstrably unrelated selection, not to demand proof of a related one — over-rejecting would
  // cost legitimate prose selections, which is the worse failure.
  function coversGesture(range, target) {
    if (!range || !target || typeof range.intersectsNode !== 'function') return true;
    try {
      return range.intersectsNode(target);
    } catch (e) {
      return true;
    }
  }

  // Map a selection Range onto a [start, end) pair of offsets into the anchored BLOCK's text — the one
  // reference frame blockTextNodes defines, and the same one findQuoteRange searches — so the two numbers
  // are comparable and `end > start` always holds for a selection with visible text.
  //
  // Charter #56: this used to record `selection.anchorOffset` / `selection.focusOffset`, which are offsets
  // WITHIN their own text nodes. Across a multi-node selection they are not in the same frame at all (the
  // focus node's offset 0 is the start of the LAST node), and a real multi-line selection drained as
  // `start: 146, end: 0` over a ~150-character quote.
  //
  // A Range's boundaries are always in document order — unlike anchor/focus, which follow the drag direction
  // — so start <= end by construction. A selection that spills outside the block clamps to the block, and
  // leading/trailing whitespace is trimmed so the span measures the same text the trimmed `quote` names.
  // When no honest offset can be computed the result is null and the caller emits NULLS: a wrong range is
  // worse than an absent one, and `quote` already carries the human-readable target.
  function blockSpan(block, range) {
    if (!block || !range || typeof document.createRange !== 'function') return null;
    var walked = blockTextNodes(block);
    var text = walked.texts.join('');
    if (!text) return null;

    var start = boundaryOffset(walked, range.startContainer, range.startOffset);
    var end = boundaryOffset(walked, range.endContainer, range.endOffset);
    if (start === null || end === null) return null;
    if (end < start) { var swap = start; start = end; end = swap; }

    while (start < end && /\s/.test(text.charAt(start))) start++;
    while (end > start && /\s/.test(text.charAt(end - 1))) end--;
    return end > start ? { start: start, end: end } : null;
  }

  // Where one Range boundary falls in the block's concatenated text. A collapsed probe Range plus
  // comparePoint is what makes an ELEMENT container work: such a boundary sits BETWEEN child nodes and
  // carries no text offset of its own, so it cannot simply be looked up in the walked node list.
  function boundaryOffset(walked, container, offset) {
    if (!container) return null;

    var probe;
    try {
      probe = document.createRange();
      probe.setStart(container, offset);
      probe.setEnd(container, offset);
    } catch (e) {
      return null;
    }

    var total = 0;
    for (var i = 0; i < walked.nodes.length; i++) {
      var node = walked.nodes[i];
      var len = walked.texts[i].length;
      var atEnd;
      try { atEnd = probe.comparePoint(node, len); } catch (e) { return null; }

      // -1: this node ENDS before the boundary, so all of it precedes the boundary.
      if (atEnd < 0) { total += len; continue; }
      // 0: the node's end IS the boundary.
      if (atEnd === 0) return total + len;
      // 1: the boundary is at or before this node's end. A text container gives the offset directly; an
      // element container can only be sitting at this node's start.
      if (container === node) return total + Math.min(Math.max(offset, 0), len);
      return total;
    }

    // The boundary lies after every text node the block contributes (or inside SDK chrome the walk skips).
    return total;
  }

  // The Mermaid node under `target` within `block`, or null when the pointer is on the diagram's
  // BACKGROUND (the svg's empty space, the block's padding, an edge) — or when `block` is not a diagram at
  // all. This is the ONE place node-vs-background is decided, so the two can never both fire or both miss.
  function diagramNodeOf(target, block) {
    if (!isDiagramBlock(block) || !target || typeof target.closest !== 'function') return null;
    var node = target.closest(DIAGRAM_NODE);
    return (node && !isSdkUi(node)) ? node : null;
  }

  // (c) diagram-node: anchor a note to a node inside a :::diagram Mermaid render. `anchorId` is the BLOCK
  // (source-mappable and stable across a re-render) and the Mermaid node's own identifier stays in
  // `nodeId`, which is exactly what that field is for — Charter #48, where the anchor used to be the
  // Mermaid node id and the agent therefore received no sourceLine at all.
  function diagramNodeAnchor(block, node) {
    return {
      kind: KIND.diagramNode,
      anchorId: anchorIdOf(block),
      nodeId: node.getAttribute('data-node-id') || node.id || null
    };
  }

  // ---- human-readable labels ----------------------------------------------------------

  // Elements whose text nodes are MACHINERY, not words a human reads — a <style> or <script> inside a block
  // (Mermaid ships its theme CSS in a <style> INSIDE the rendered <svg>; :::custom-html may carry either).
  // Matched case-insensitively because an SVG element's tagName keeps its lower-case local name while an
  // HTML element's is upper-cased.
  var NON_VISIBLE_TAGS = { STYLE: true, SCRIPT: true };

  function isNonVisible(el) {
    return NON_VISIBLE_TAGS[String(el.tagName || '').toUpperCase()] === true;
  }

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
        if (isNonVisible(node)) return;
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

  // A whole-diagram note and a diagram-node note anchor to the SAME block id, so the words are the only
  // thing telling the reviewer which of the two they are about to write. They must never read alike.
  var WHOLE_DIAGRAM = 'the whole diagram \u2014 not a single node';

  // The composer's context line: what the reviewer is about to comment on, in words.
  function contextLine(anchor, targetEl) {
    if (anchor.kind === KIND.textRange && anchor.quote) {
      return 'Commenting on selected text: \u201C' + truncate(anchor.quote, 64) + '\u201D';
    }
    if (anchor.kind === KIND.diagramNode) {
      var label = visibleText(targetEl) || anchor.nodeId || 'an unnamed node';
      return 'Commenting on diagram node: ' + truncate(label, 64);
    }
    var el = targetEl || anchorElement(anchor.anchorId);
    if (isDiagramBlock(el)) return 'Commenting on ' + WHOLE_DIAGRAM;
    return 'Commenting on: ' + targetLabel(el);
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
    if (isDiagramBlock(el)) return WHOLE_DIAGRAM;
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
          refreshRound();   // there is now something to hand off
          // The same submission also became a durable `create` record in this author's log; re-read the fold
          // so the entry the panel shows is the COMMITTED one (with its author, actor and status), not just
          // the pending copy.
          hydrateLog();
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

  // Re-read the FOLDED review log — every author's committed comments, not only this machine's pending
  // queue. Called on load, after every write, and whenever the server reports that `.review/` changed
  // (a `git pull` landing a teammate's log mid-session).
  function hydrateLog() {
    var url = '/api/review-log?key=' + encodeURIComponent(state.key || '');
    return fetch(url, { headers: { 'Accept': 'application/json' } }).then(function (res) {
      if (!res.ok) {
        emit('review-log-error', { status: res.status });
        return null;
      }
      return res.json().then(function (view) {
        state.log = {
          comments: (view && view.comments) || [],
          diagnostics: (view && view.diagnostics) || [],
          unreadable: (view && view.unreadable) || [],
          selfEmail: (view && view.selfEmail) || null
        };
        render();
        emit('review-log-loaded', {
          count: state.log.comments.length,
          diagnostics: state.log.diagnostics.length,
          unreadable: state.log.unreadable.length
        });
        return state.log;
      }, function () {
        emit('review-log-error', { reason: 'malformed' });
        return null;
      });
    }).catch(function () {
      emit('review-log-error', { reason: 'network' });
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
        hydrateLog();
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
        refreshRound();   // retracting the last note leaves nothing to hand off
        // In the log a retract HIDES the body and KEEPS the thread — replies are other people's words and
        // are never removed by someone else's retract — so re-read rather than assume the entry is gone.
        hydrateLog();
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

  // Close a comment in the review log. Open to ANYONE (review is collaborative) and always attributed —
  // unlike retract, which only the comment's own author may write.
  function resolveNote(id) {
    return fetch(annotationUrl(id, 'resolve'), { method: 'POST' }).then(function (res) {
      if (res.ok) {
        setStatus('');
        hydrateLog();
        emit('annotation-resolved', { id: id });
        return true;
      }
      setStatus('Could not resolve that comment (' + res.status + ').');
      emit('annotation-resolve-error', { id: id, status: res.status });
      return false;
    }).catch(function () {
      setStatus('Could not reach the review server.');
      emit('annotation-resolve-error', { id: id, reason: 'network' });
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

  // ---- the round hand-off: "Send to agent" ---------------------------------------------------
  // The reviewer's way to say "I am done with this round" without leaving the page. It SIGNALS ONLY:
  // POST /api/{key}/review/submit records the hand-off and wakes the agent's long-poll. The agent still
  // does every drain and remains the ONLY writer of the plan file — the server never touches it.
  //
  // The button's state is derived from the SERVER (GET /api/review), never from a local tally: a live
  // reload is a full navigation, so anything counted in this page's memory is lost exactly when the loop
  // is working. `submitted` stays true until the agent acks the hand-off, which is what keeps a reviewer
  // from queueing the same round twice.

  var SENT_MESSAGE = 'Sent — the agent is revising…';

  function applyRound(status) {
    var pending = (status && status.pending) || {};
    state.round = {
      submitted: !!(status && status.submitted),
      pending: {
        annotations: pending.annotations || 0,
        answers: pending.answers || 0
      }
    };
    applyStaleQueue(status && status.staleQueue);
    syncSendButton();
  }

  // ---- the replaced-plan quarantine notice (#75 item 2) -------------------------------------------
  // The server set an earlier annotation queue aside because none of its anchors resolve in the plan now at
  // this path. Nothing was destroyed, and the recovery is one flag — but only stderr used to say so, and a
  // review an agent started shows a human none of it. Said here, once, in the panel.

  function applyStaleQueue(notice) {
    if (!notice || !notice.count) return;
    state.staleQueue = {
      count: notice.count,
      fileName: notice.fileName || '',
      durabilityDisabled: !!notice.durabilityDisabled
    };
    renderStaleQueue();
    // Open the panel the first time, and only the first time: a reviewer whose earlier notes are missing must
    // not have to go looking for the explanation, but re-reading /api/review must not keep reopening a panel
    // they deliberately hid.
    if (!state.staleQueueShown) {
      state.staleQueueShown = true;
      showPanel();
      emit('stale-queue', {
        count: state.staleQueue.count,
        durabilityDisabled: state.staleQueue.durabilityDisabled
      });
    }
  }

  function staleQueueText(stale) {
    var notes = stale.count + (stale.count === 1 ? ' earlier note' : ' earlier notes');
    return notes + ' from a previous review at this path were set aside: none of them still point at a block '
      + 'in this plan, so Charter did not hand them to the agent. Nothing was deleted'
      + (stale.fileName ? ' — they are kept in ' + stale.fileName : '')
      + '. Re-run charter review with --keep-annotations to restore them.'
      + (stale.durabilityDisabled
        ? ' Charter could not copy them aside, so this session is not saving new notes across a restart.'
        : '');
  }

  function renderStaleQueue() {
    if (!state.ui || !state.staleQueue) return;
    if (!state.ui.stale) {
      var stale = make('div', 'charter-panel-stale', 'stale-queue');
      stale.setAttribute('role', 'status');
      state.ui.panel.insertBefore(stale, state.ui.list);
      state.ui.stale = stale;
    }
    state.ui.stale.setAttribute('data-charter-stale-count', String(state.staleQueue.count));
    state.ui.stale.textContent = staleQueueText(state.staleQueue);
  }

  function pendingCount() {
    return state.round.pending.annotations + state.round.pending.answers;
  }

  function refreshRound() {
    var url = '/api/review?key=' + encodeURIComponent(state.key || '');
    return fetch(url, { headers: { 'Accept': 'application/json' } }).then(function (res) {
      if (!res.ok) {
        emit('round-error', { status: res.status });
        return null;
      }
      return res.json().then(function (status) {
        applyRound(status);
        emit('round-loaded', {
          submitted: state.round.submitted,
          annotations: state.round.pending.annotations,
          answers: state.round.pending.answers
        });
        return state.round;
      }, function () {
        emit('round-error', { reason: 'malformed' });
        return null;
      });
    }).catch(function () {
      emit('round-error', { reason: 'network' });
      return null;
    });
  }

  function sendRound() {
    var url = '/api/' + encodeURIComponent(state.key || '') + '/review/submit';
    emit('round-sending', {});
    return fetch(url, { method: 'POST' }).then(function (res) {
      if (!res.ok) {
        setStatus('Could not send this round to the agent (' + res.status + ').');
        emit('round-error', { status: res.status });
        return null;
      }
      // Reflect the hand-off immediately — the reviewer just clicked and needs to see that it landed —
      // then re-read the authoritative state behind that confirmation.
      state.round.submitted = true;
      syncSendButton();
      showPanel();
      setStatus(SENT_MESSAGE);
      emit('round-sent', {});
      refreshRound();
      return true;
    }).catch(function () {
      setStatus('Could not reach the review server.');
      emit('round-error', { reason: 'network' });
      return null;
    });
  }

  // Enabled EXACTLY when there is queued feedback the agent has not been handed yet. Disabled while the
  // queue is empty ("nothing to send") and while a hand-off is already pending ("the agent is coming").
  function syncSendButton() {
    if (!state.ui || !state.ui.send) return;
    var send = state.ui.send;
    var nothingToSend = pendingCount() === 0;
    send.disabled = state.round.submitted || nothingToSend;
    send.setAttribute('data-charter-sent', state.round.submitted ? 'true' : 'false');
    send.title = state.round.submitted
      ? 'Sent — the agent is revising this round.'
      : (nothingToSend
        ? 'Nothing to send yet — add a note or answer a question first.'
        : 'Hand this round of feedback to the agent');
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
      if (res.ok) {
        refreshRound();   // a saved decision is feedback the reviewer can now hand off
      }
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
  //
  // Charter #63 adds ONE case to that rule rather than an exception to it: an emptied form. On an open
  // question the emptied form equals the (empty) baseline, so Save simply returns to disabled — nothing to
  // save. On a RESOLVED one the emptied form differs from the recorded answer, so Save enables and posting
  // it clears that answer (values: [], which charter-format reads as open again). The control renames
  // itself for that case so a retraction is never pressed by mistake.

  var SUBMIT_SELECTOR = 'button[type="submit"]';

  // The signature of "no answer at all" — collectValues' empty array, as answerSignature stringifies it.
  var EMPTY_ANSWER = '[]';

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
    if (!button.charterSaveLabel) button.charterSaveLabel = button.textContent;

    var current = answerSignature(form, root);
    var changed = current !== ensureWired(form, root);
    // A submit that would EMPTY a recorded answer is a retraction, not a save, and the button says so
    // before it is pressed — the reviewer must not discover which one they did afterwards.
    var clearing = changed && current === EMPTY_ANSWER;

    button.disabled = !changed;
    button.textContent = clearing ? 'Clear answer' : button.charterSaveLabel;
    button.title = clearing
      ? 'Clear the recorded answer \u2014 this question goes back to unanswered'
      : (changed
        ? 'Save this answer to the Charter review session'
        : 'Choose or change an answer to enable saving');
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

  // ---- clearing an accidental answer (Charter #63) --------------------------------------
  // A native radio cannot be deselected, so one mis-click leaves a decision the reviewer never made with no
  // way back to "unanswered" — which for a :::question is a real, distinct state, not the absence of one
  // (charter-format: a question with no non-empty `answer` IS open). Clicking the ALREADY-SELECTED option
  // therefore clears it. Applies to `bool` too, which renders as two radios (Charter #43).
  //
  // The radio's state has to be sampled BEFORE the browser's activation behaviour runs: by `click` time an
  // already-checked radio and a just-checked one are indistinguishable. `armedRadio` is that sample, taken
  // on mousedown and consumed by the very next click. Every keydown clears it, so arrow-key navigation
  // (which also fires a click, on the option it moves ONTO) can never be read as a clear.
  var armedRadio = null;

  // The <input type=radio> in `target`, if it belongs to a rendered :::question form; else null.
  function questionRadio(target) {
    if (!target || target.nodeType !== 1 || target.tagName !== 'INPUT') return null;
    if (String(target.type).toLowerCase() !== 'radio') return null;
    return questionRoot(target.form) ? target : null;
  }

  function armRadio(target) {
    var radio = questionRadio(target);
    armedRadio = (radio && radio.checked) ? radio : null;
  }

  function onQuestionPointerDown(ev) {
    armRadio(ev && ev.target);
  }

  function onQuestionClick(ev) {
    var armed = armedRadio;
    armedRadio = null;
    var radio = questionRadio(ev && ev.target);
    if (radio && radio === armed) clearRadio(radio);
  }

  // The KEYBOARD's own path to the same rule. Chromium deliberately dispatches NO click when Space is
  // pressed on an ALREADY-CHECKED radio (Blink's RadioInputType::HandleKeyupEvent returns early for a
  // checked control), so the click rule above is unreachable from the keyboard and Space would silently do
  // nothing — leaving a keyboard reviewer with the very dead end this fix exists to remove. Keyup is where
  // the browser would have activated the control, so it is where the clear belongs.
  function onQuestionKeyup(ev) {
    if (!ev || (ev.key !== ' ' && ev.key !== 'Spacebar')) return;
    var radio = questionRadio(ev.target);
    if (!radio || !radio.checked) return;
    // Take the key's default action over: Blink decides whether to simulate a click by re-reading
    // `checked` AFTER this listener has run, so clearing without also preventing the default lets it
    // observe the now-unchecked control and re-check it — the clear would undo itself.
    ev.preventDefault();
    clearRadio(radio);
  }

  function clearRadio(radio) {
    radio.checked = false;
    var root = questionRoot(radio.form);
    syncSubmitState(radio.form, root);
    emit('answer-cleared', { questionId: root ? root.getAttribute('data-question-id') : null });
  }

  // Enter submits where the control makes that natural: a radio/checkbox/number/text control triggers
  // the form's IMPLICIT submission natively — and because the default button is the disabled-until-
  // changed Save button, that path obeys the same rule for free. A <textarea> must keep Enter as a
  // NEWLINE (a free-text answer is prose), so free-text submits on Ctrl/Cmd+Enter.
  function onQuestionKeydown(ev) {
    if (!ev) return;
    // Any key DISARMS the pending mouse gesture. Arrow-key navigation within a radio group fires a `click`
    // on the option it moves ONTO, so a stale sample would clear the very option the reviewer just chose.
    armedRadio = null;
    if (ev.key !== 'Enter' || !(ev.ctrlKey || ev.metaKey)) return;
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
        answerStatus(
          form,
          answer.values.length === 0
            ? 'Answer cleared \u2014 this question is unanswered again.'
            : 'Answer saved.',
          false);
      } else {
        answerStatus(form, 'Could not save this answer.', true);
      }
      syncSubmitState(form, root);
      // The decision is no longer unsaved, so a reload deferred to protect it can now proceed.
      maybeReload();
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
      if (button) {
        button.disabled = true;
        button.removeAttribute('title');
        // Return the label the renderer emitted, so a disposed SDK leaves the artifact's own markup behind
        // rather than a button still offering to clear an answer nothing can now post.
        if (button.charterSaveLabel) button.textContent = button.charterSaveLabel;
        button.charterSaveLabel = null;
      }

      form.charterAnswerBaseline = null;
    }

    armedRadio = null;
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
    '.charter-panel-stale { padding: 8px 12px; border-bottom: 1px solid var(--charter-warn-border);',
    '  background: var(--charter-warn-bg); color: var(--charter-fg); font-size: 12px; line-height: 1.45; }',
    '.charter-panel-list { flex: 1 1 auto; overflow-y: auto; padding: 8px; }',
    '.charter-panel-empty { color: var(--charter-muted); padding: 10px 4px; }',
    '.charter-panel-actions { display: flex; align-items: center; gap: 8px; padding: 8px 12px;',
    '  border-top: 1px solid var(--charter-border); }',
    '.charter-panel-hint { flex: 1 1 auto; color: var(--charter-muted); font-size: 11px; }',
    '.charter-send { flex: 0 0 auto; font-size: 12px; padding: 5px 12px; }',
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
    '.charter-item-actions { display: flex; gap: 6px; flex-wrap: wrap; }',
    '.charter-item[data-charter-orphan="true"] .charter-item-label { font-style: italic; }',

    '.charter-item-meta { display: flex; align-items: center; flex-wrap: wrap; gap: 6px;',
    '  margin-bottom: 6px; font-size: 11px; color: var(--charter-muted); }',
    '.charter-item-author { font-weight: 600; color: var(--charter-fg); }',
    '.charter-chip { border: 1px solid var(--charter-border); border-radius: 999px; padding: 0 6px;',
    '  font-size: 11px; line-height: 1.6; }',
    '.charter-chip-contested { border-color: var(--charter-warn-border); background: var(--charter-warn-bg); }',
    '.charter-chip-orphaned { font-style: italic; }',
    '.charter-item[data-charter-status="retracted"] .charter-item-note { font-style: italic;',
    '  color: var(--charter-muted); }',
    '.charter-item-orphan { font-size: 11px; color: var(--charter-muted); margin-bottom: 6px; }',
    '.charter-item-quote { font-style: italic; overflow-wrap: break-word; }',
    '.charter-item-sides { font-size: 11px; color: var(--charter-muted); margin-bottom: 6px; }',
    '.charter-item-reply { font-size: 12px; border-left: 2px solid var(--charter-border);',
    '  padding-left: 8px; margin: 0 0 6px 6px; overflow-wrap: break-word; }',

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

    // Suppress the native selection gesture where it has nothing to act on: a rendered :::diagram is an SVG
    // with no prose, so a double-click there means "select a word" to the browser and Chromium answers by
    // grabbing whatever text is NEAREST — often somewhere else entirely (Charter #61). Declared in the SDK's
    // serve-time style rather than in charter.css so the SAVED artifact stays byte-identical (invariant 1)
    // and a reader of the offline file can still select a diagram's labels; the accident this prevents only
    // exists where the annotation gestures do.
    '.mermaid { -webkit-user-select: none; user-select: none; }',

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

    // The round hand-off. Disabled until there is queued feedback to send (and again once sent), so the
    // control can never post an empty round or double-hand-off the same one.
    var actions = make('div', 'charter-panel-actions', 'panel-actions');
    actions.appendChild(make('span', 'charter-panel-hint', 'panel-hint',
      'The agent sees your feedback as you save it.'));
    var send = button('charter-btn charter-btn-primary charter-send', 'send-to-agent', 'Send to agent');
    send.disabled = true;
    send.setAttribute('data-charter-sent', 'false');
    send.addEventListener('click', function () { sendRound(); }, false);
    actions.appendChild(send);

    panel.appendChild(header);
    panel.appendChild(list);
    panel.appendChild(actions);
    panel.appendChild(status);

    var toggle = button('charter-panel-toggle', 'panel-toggle', 'Notes 0');
    toggle.setAttribute('aria-label', 'Show Charter review notes');
    toggle.addEventListener('click', togglePanel, false);

    var overlay = make('div', 'charter-overlay', 'overlay');

    document.body.appendChild(overlay);
    document.body.appendChild(panel);
    document.body.appendChild(toggle);

    state.ui = {
      style: style, panel: panel, title: title, list: list, send: send,
      status: status, toggle: toggle, overlay: overlay, banner: null,
      // The quarantine notice is built on demand (renderStaleQueue) and lives inside the panel, so disposing
      // the panel disposes it. It is never in the saved artifact — invariant 1 — like the rest of this chrome.
      stale: null
    };
    syncSendButton();
    renderStaleQueue();
    return state.ui;
  }

  function setStatus(text) {
    if (!state.ui) return;
    state.ui.status.textContent = text || '';
    state.ui.status.className = text ? 'charter-panel-status' : 'charter-panel-status charter-hidden';
  }

  // The floating toggle is fixed to the viewport's bottom-right corner — which is INSIDE the open panel,
  // over its footer. It exists only to open the panel (the header carries Hide), so while the panel is open
  // it is both redundant and an occluder: leaving it there swallows clicks meant for the panel's own
  // controls, which is how "Send to agent" became unclickable for a real mouse.
  function setToggleVisible(visible) {
    if (!state.ui) return;
    state.ui.toggle.className = visible ? 'charter-panel-toggle' : 'charter-panel-toggle charter-hidden';
  }

  function showPanel() {
    if (!ensureUi()) return;
    state.ui.panel.className = 'charter-panel';
    setToggleVisible(false);
    emit('panel-opened', {});
  }

  function hidePanel() {
    if (!state.ui) return;
    state.ui.panel.className = 'charter-panel charter-hidden';
    setToggleVisible(true);
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

  // ---- one list from two sources -------------------------------------------------------------------
  // The panel shows the FOLDED review log (every author's committed comments, with their status) plus any
  // pending annotation the log does not already carry. The log record's id IS the annotation's id, so a
  // comment this session just wrote appears exactly once — as its committed self. The pending fallback is
  // what keeps a Charter with no author identity (no writer configured) working exactly as before.

  function logRecord(comment) {
    return {
      id: comment.id,
      anchorId: comment.anchorId,
      kind: comment.kind || KIND.element,
      quote: comment.quote || null,
      note: comment.body || '',
      sourceLine: comment.sourceLine || null,
      authorName: comment.authorName || null,
      authorEmail: comment.authorEmail || null,
      actor: comment.actor || null,
      status: comment.status || 'open',
      anchorStatus: comment.anchorStatus || null,
      // Whether the plan is still the text this comment was written against: 'current' / 'different' /
      // 'unknown' (§4.3.1). It is deliberately NOT rendered as a per-comment badge — 'different' is the modal
      // state of nearly every comment in a living document, so badging it would train the reviewer to ignore
      // the one badge that matters. It is used only to keep the orphan line an EARNED claim.
      baseStatus: comment.baseStatus || null,
      mine: !!comment.mine,
      sides: comment.sides || [],
      replies: comment.replies || [],
      committed: true
    };
  }

  function pendingRecord(annotation) {
    return {
      id: annotation.id,
      anchorId: annotation.anchorId,
      kind: annotation.kind || KIND.element,
      quote: annotation.quote || null,
      note: annotation.note || '',
      sourceLine: annotation.sourceLine || null,
      authorName: null,
      authorEmail: null,
      actor: null,
      status: 'open',
      anchorStatus: null,
      baseStatus: null,
      mine: true,
      sides: [],
      replies: [],
      committed: false
    };
  }

  function mergedRecords() {
    var records = [];
    var seen = Object.create(null);
    var i;
    for (i = 0; i < state.log.comments.length; i++) {
      var committed = logRecord(state.log.comments[i]);
      seen[committed.id] = true;
      records.push(committed);
    }
    for (i = 0; i < state.annotations.length; i++) {
      if (!seen[state.annotations[i].id]) records.push(pendingRecord(state.annotations[i]));
    }
    return records;
  }

  // Document order, so the list reads top-to-bottom like the plan. Orphans (their block is gone
  // from the re-rendered plan) sort last and keep submit order — they are never dropped.
  function orderedEntries() {
    var entries = [];
    var records = mergedRecords();
    for (var i = 0; i < records.length; i++) {
      entries.push({ record: records[i], el: anchorElement(records[i].anchorId), index: i });
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

  // How each settlement reads in the panel. A CONTESTED comment shows BOTH sides with their authors —
  // the fold hands over both precisely so the disagreement is visible instead of being ordered away.
  function sideLine(side) {
    var verb = side.op === 'reopen' ? 'reopened' : 'resolved';
    return verb + ' by ' + (side.authorName || side.authorEmail || 'someone');
  }

  function buildItem(entry) {
    var record = entry.record;
    // An anchor resolves by EXACT block-id match or it is orphaned (§4.3) — there is no fuzzy re-binding.
    // The server's verdict wins when it has one; a pending-only note falls back to the live DOM.
    var orphaned = record.anchorStatus ? (record.anchorStatus === 'orphaned') : !entry.el;
    var retracted = record.status === 'retracted';

    var item = make('div', 'charter-item', 'item');
    item.setAttribute('data-annotation-id', record.id || '');
    item.setAttribute('data-anchor-id', record.anchorId || '');
    item.setAttribute('data-charter-orphan', orphaned ? 'true' : 'false');
    item.setAttribute('data-charter-anchor-status', orphaned ? 'orphaned' : 'resolved');
    item.setAttribute('data-charter-status', record.status || 'open');
    item.setAttribute('data-charter-committed', record.committed ? 'true' : 'false');
    if (record.baseStatus) item.setAttribute('data-charter-base-status', record.baseStatus);
    if (record.authorEmail) item.setAttribute('data-charter-author-email', record.authorEmail);
    if (record.actor) item.setAttribute('data-charter-actor', record.actor);

    var target = make('div', 'charter-item-target', 'item-target');
    target.appendChild(make('span', 'charter-item-label', 'item-label', recordLabel(record)));
    if (record.sourceLine) {
      target.appendChild(make('span', 'charter-item-line', 'item-line', 'line ' + record.sourceLine));
    }
    item.appendChild(target);

    // Who said it, whether a human or an agent said it, and what state it settled into. Only committed
    // (review-log) entries carry attribution; a pending-only note is by definition this reviewer's own.
    if (record.committed) {
      var meta = make('div', 'charter-item-meta', 'item-meta');
      meta.appendChild(make('span', 'charter-item-author', 'item-author', record.authorName || record.authorEmail || ''));
      if (record.actor && record.actor !== 'human') {
        meta.appendChild(make('span', 'charter-chip', 'item-actor', record.actor));
      }
      meta.appendChild(make('span', 'charter-chip charter-chip-' + record.status, 'item-status', record.status));
      if (orphaned) {
        meta.appendChild(make('span', 'charter-chip charter-chip-orphaned', 'item-orphan-chip', 'orphaned'));
      }
      item.appendChild(meta);
    }

    item.appendChild(make('div', 'charter-item-note', 'item-note',
      retracted ? '(comment withdrawn by author)' : (record.note || '')));

    // An orphan is never blind: the quote it was written against, plus the neutral FACT that its block is
    // gone. Deliberately not "addressed" — folding a :::question answer rewrites that block and orphans every
    // comment on it though nobody addressed anything.
    //
    // The stronger sentence — "the plan has CHANGED since this comment was written" — is a claim about the
    // whole document, and it is made only when `baseStatus` backs it (§4.3.1). It used to be asserted on every
    // orphan, including the ones where the plan is byte-identical to what the reviewer saw and the anchor
    // simply never resolved.
    if (orphaned) {
      var orphan = make('div', 'charter-item-orphan', 'item-orphan');
      orphan.appendChild(make('div', null, 'item-orphan-note',
        record.baseStatus === 'different'
          ? 'The plan has changed since this comment was written.'
          : 'The block this comment was written on is not in the plan.'));
      if (record.quote) {
        orphan.appendChild(make('div', 'charter-item-quote', 'item-quote',
          '“' + truncate(record.quote, 160) + '”'));
      }
      item.appendChild(orphan);
    }

    if (record.sides && record.sides.length) {
      var sides = make('div', 'charter-item-sides', 'item-sides');
      for (var s = 0; s < record.sides.length; s++) {
        sides.appendChild(make('div', 'charter-item-side', 'item-side', sideLine(record.sides[s])));
      }
      item.appendChild(sides);
    }

    for (var r = 0; r < record.replies.length; r++) {
      var reply = record.replies[r];
      var replyEl = make('div', 'charter-item-reply', 'item-reply',
        (reply.authorName || reply.authorEmail || '') + ': ' +
        (reply.retracted ? '(reply withdrawn by author)' : (reply.body || '')));
      item.appendChild(replyEl);
    }

    var actions = make('div', 'charter-item-actions', 'item-actions');
    var jump = button('charter-btn', 'item-jump', 'Jump');
    jump.disabled = !entry.el || orphaned;
    jump.addEventListener('click', function () { jumpTo(record); }, false);
    actions.appendChild(jump);

    // Edit and Delete are the AUTHOR's own: a retract by anyone else is retained and reported by the fold
    // but never applied, so offering the button would only promise something the model refuses.
    if (record.mine && !retracted) {
      var edit = button('charter-btn', 'item-edit', 'Edit');
      edit.addEventListener('click', function () { openComposerForEdit(record, item); }, false);
      var remove = button('charter-btn', 'item-delete', 'Delete');
      remove.addEventListener('click', function () { deleteNote(record.id); }, false);
      actions.appendChild(edit);
      actions.appendChild(remove);
    }

    // Resolve is open to anyone — review is collaborative — but only for a committed comment that is not
    // already settled closed or withdrawn.
    if (record.committed && !retracted && record.status !== 'resolved') {
      var resolve = button('charter-btn', 'item-resolve', 'Resolve');
      resolve.addEventListener('click', function () { resolveNote(record.id); }, false);
      actions.appendChild(resolve);
    }

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
      // A withdrawn comment must not keep badging its block — the thread survives in the panel, but the
      // block no longer carries an open note.
      if (!id || !entries[i].el || entries[i].record.status === 'retracted') continue;
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
    syncSendButton();
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
    var walked = blockTextNodes(block);
    var nodes = walked.nodes;
    var texts = walked.texts;

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

    // One walk decides the block, and the block is always what the note anchors to.
    var block = closestAnchored(ev.target);
    if (!block || !anchorIdOf(block)) return;
    ev.preventDefault();

    // A rendered :::diagram is ONE block with TWO annotatable granularities, and which one the reviewer
    // gets is decided here, once: pointer on a Mermaid node ⇒ a `diagram-node` note carrying that node's
    // id; pointer anywhere else in the block (the svg background, the padding, an edge) ⇒ the same plain
    // `element` note every other block produces (Charter #60). Both anchor to the BLOCK (Charter #48), so
    // only the composer's context line distinguishes them for the reviewer — see contextLine.
    var node = diagramNodeOf(ev.target, block);
    if (node) {
      openComposerForAnchor(diagramNodeAnchor(block, node), node);
      return;
    }

    openComposerForAnchor(elementAnchor(block), block);
  }

  function onMouseUp(ev) {
    if (ev && isSdkUi(ev.target)) return;
    if (hasDraft()) return;   // never clobber a note in progress
    var sel = (typeof window.getSelection === 'function') ? window.getSelection() : null;
    var tr = textRangeAnchor(sel, ev && ev.target);
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

  // Is the reviewer mid-DECISION? A question form whose Save button is enabled holds an answer that
  // differs from the one the markup records — a choice made but not yet saved. Reloading over it loses
  // it exactly as reloading over a half-typed note would, and the loop's whole point is that the agent
  // revises WHILE the human reviews, so this case is now the common one rather than the rare one.
  // Deliberately NOT folded into hasDraft(): that guard also suppresses opening a composer, and a
  // half-answered question must not stop the reviewer annotating something else.
  function hasDirtyAnswer() {
    var buttons = document.querySelectorAll('form[data-question-id] ' + SUBMIT_SELECTOR);
    for (var i = 0; i < buttons.length; i++) {
      if (!buttons[i].disabled) return true;
    }
    return false;
  }

  // Everything a navigation would silently discard: a half-typed note, or a half-made decision.
  function wouldLoseWork() {
    return hasDraft() || hasDirtyAnswer();
  }

  // Reload once the reviewer is no longer mid-note or mid-decision — called after a save or a cancel.
  function maybeReload() {
    if (state.reloadPending && !wouldLoseWork()) navigate();
  }

  // The agent edits the plan while the reviewer is typing. A blocking window.prompt used to make
  // that impossible; a non-modal composer does not, so a reload arriving over a live draft is
  // DEFERRED and offered as a banner instead of silently discarding what was typed.
  function onReload() {
    emit('reload', {});
    if (wouldLoseWork()) {
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
      'The plan changed on disk. Your unsaved work is safe \u2014 reload when you are ready.'));
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
      // A teammate's log landing in `.review/` (a `git pull` mid-session) refreshes the PANEL only — never
      // a page navigation, which would discard a half-typed note for someone else's comment.
      es.addEventListener('review-log', function () { emit('review-log-changed', {}); hydrateLog(); });
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
      // The radio-deselect pair (Charter #63). mousedown samples the pre-activation state; the click that
      // follows acts on it. Registered AFTER onClick so the annotation gesture still sees the click first.
      document.addEventListener('mousedown', onQuestionPointerDown, true);
      document.addEventListener('click', onQuestionClick, true);
      document.addEventListener('mouseup', onMouseUp, false);
      document.addEventListener('submit', onSubmit, true);
      document.addEventListener('input', onQuestionInput, true);
      document.addEventListener('change', onQuestionInput, true);
      document.addEventListener('keydown', onQuestionKeydown, true);
      document.addEventListener('keyup', onQuestionKeyup, true);
    }
    ensureUi();
    wireQuestionForms();
    openEvents();

    state.started = true;
    emit('ready', { hasKey: !!state.key });

    // Hydrate the pending queue, the folded review log, and the round state ONCE, here. A live reload is a
    // full navigation, so init() runs again on the new document — re-fetching inside the reload handler would
    // only race it. The round state comes from the server for the same reason: after a reload there is no
    // local tally left.
    hydrate();
    hydrateLog();
    refreshRound();
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
      document.removeEventListener('mousedown', onQuestionPointerDown, true);
      document.removeEventListener('click', onQuestionClick, true);
      document.removeEventListener('mouseup', onMouseUp, false);
      document.removeEventListener('submit', onSubmit, true);
      document.removeEventListener('input', onQuestionInput, true);
      document.removeEventListener('change', onQuestionInput, true);
      document.removeEventListener('keydown', onQuestionKeydown, true);
      document.removeEventListener('keyup', onQuestionKeyup, true);
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
    state.log = { comments: [], diagnostics: [], unreadable: [], selfEmail: null };
    state.round = { submitted: false, pending: { annotations: 0, answers: 0 } };
    state.staleQueue = null;
    state.staleQueueShown = false;
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
    reviewLog: hydrateLog, // re-read the folded review log (every author's committed comments)
    update: updateNote,  // edit a note
    remove: deleteNote,  // retract a note (own author only, once committed)
    resolve: resolveNote, // close a committed comment
    panel: togglePanel,  // show/hide the review panel
    send: sendRound,     // hand this round of feedback to the agent
    round: refreshRound, // re-read the round's hand-off state from the server
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
