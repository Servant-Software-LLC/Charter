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
    agent: null,
    statusIsSent: false,
    // The reviewer handed off a round and has not yet seen the plan change. Deliberately CLIENT-side and
    // deliberately not `round.submitted`: the server clears `submitted` the moment the agent ACKS the
    // hand-off, which happens on the drain — before any revision exists — so it is already false by the time
    // the rewrite lands. This flag spans the whole wait, which is the interval the reviewer is living in.
    awaitingRevision: false,
    agentSeenAt: 0,      // when this page last learned the presence facts, for honest elapsed time
    ageTicker: 0,        // the display-only timer that keeps that elapsed reading from freezing
    staleQueue: null,
    staleQueueShown: false,
    composer: null,      // the open composer, or null
    // The pan/zoom views for the rendered :::diagram blocks that are shown SMALLER than they were drawn
    // (Charter #51), plus the MutationObservers waiting on Mermaid to produce each block's <svg>. A diagram
    // that fits gets neither, so it keeps exactly the behaviour the exported artifact has.
    diagrams: [],
    diagramObservers: [],
    pan: null,           // the in-flight pan drag, or null
    panLatch: 0,         // the bounded timer that retires a pan's click-swallowing latch
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

  // WAS a constant reading 'Sent — the agent is revising…'. That asserted two things the page cannot
  // know: that an agent received the round, and that it is revising. Charter never invokes an agent —
  // the button records the hand-off and wakes whatever is already long-polling — so with nothing
  // listening the old text was simply false, and it was the LOUD surface (the panel status line) while
  // #107's honest wording only reached the tooltip. Derived from the same presence facts now, and
  // re-derived when the authoritative read lands (see applyRound).
  // The reviewer's own next step, said as an INSTRUCTION. It is the one thing here that is unconditionally
  // true: Charter cannot see an agent thinking — a drain only means bytes left the queue, and `poll --watch`
  // re-arms its long poll the instant a cycle returns, so presence reads "listening" while the agent revises
  // AND while it sits idle. Rather than infer a state it cannot observe, the panel tells the human where the
  // rest of the conversation happens. That is what a reviewer actually needed, and it costs no claim at all.
  function sentMessage() {
    return 'Sent.' + agentHint() + ' The conversation continues in your agent’s terminal.';
  }

  function applyRound(status) {
    var pending = (status && status.pending) || {};
    state.round = {
      submitted: !!(status && status.submitted),
      pending: {
        annotations: pending.annotations || 0,
        answers: pending.answers || 0
      }
    };
    state.agent = (status && status.agent) || null;
    state.agentSeenAt = state.agent ? Date.now() : 0;
    applyStaleQueue(status && status.staleQueue);
    syncSendButton();
    syncAgeTicker();

    // The click reflects the hand-off instantly from whatever presence was last known, which may be
    // stale by a poll interval. This is the authoritative answer arriving, so correct the wording —
    // but only while the status line is still OUR line, and only while the round is still outstanding.
    if (state.statusIsSent) {
      if (state.round.submitted) setSentStatus();
      else setStatus('');
    }
  }

  // ---- #107: is anything actually listening? ------------------------------------------------------
  // The panel knew `submitted` and the pending counts but nothing about whether an agent exists, so a
  // reviewer who clicked Send to agent could not tell a working agent from no agent — both are silence, and
  // the second is indistinguishable from patience until far too much of it has passed.
  //
  // Said ONLY after a round has been handed over, and only as a plain statement of fact. A solo reviewer
  // running `charter resolve` is a fully supported workflow, not a degraded one: "no agent connected" as a
  // standing warning would be wrong for them, and alarming for everyone else. The question "did anyone
  // receive this?" only exists once you have actually sent something.
  function agentHint() {
    if (!state.round.submitted && !state.awaitingRevision) return '';
    var agent = state.agent;
    if (agent && agent.waiting) return ' An agent is listening.';
    if (agent && typeof agent.lastSeenSecondsAgo === 'number') {
      return ' An agent last checked ' + describeAgo(agentSeenSecondsAgo(agent)) + '.';
    }
    return ' No agent has checked this session yet — run `charter poll <plan> --wait --apply`, ' +
           'or fold the answers in yourself with `charter resolve <plan>`.';
  }

  // How long ago the agent was last seen, AS OF NOW — the server's number plus the time since we were told
  // it. Without this the line freezes at whatever it said when it was fetched: `refreshRound()` runs only on
  // discrete events, and a reviewer who has sent a round and is waiting generates none, so "an agent last
  // checked 3s ago" would still read 3s an hour later, including long after that agent died. Ticking the
  // browser's own clock keeps it a claim about CHARTER'S OBSERVATION, which is the only thing it ever was.
  function agentSeenSecondsAgo(agent) {
    var base = agent.lastSeenSecondsAgo;
    if (!state.agentSeenAt) return base;
    return base + Math.max(0, Math.round((Date.now() - state.agentSeenAt) / 1000));
  }

  // Re-render the standing status line on a slow tick so the elapsed reading above stays true. Text only —
  // it issues no request, and it is the display that moves, never the facts. Started when there is something
  // time-dependent to show and stopped when there is not, so an idle page runs no timer at all.
  function syncAgeTicker() {
    var wanted = !!(state.statusIsSent && state.agent && !state.agent.waiting);
    if (wanted && !state.ageTicker) {
      state.ageTicker = window.setInterval(function () {
        if (state.statusIsSent) setSentStatus();
      }, 5000);
    } else if (!wanted && state.ageTicker) {
      window.clearInterval(state.ageTicker);
      state.ageTicker = 0;
    }
  }

  function describeAgo(seconds) {
    if (seconds < 10) return 'just now';
    if (seconds < 90) return seconds + 's ago';
    return Math.round(seconds / 60) + 'm ago';
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
      state.awaitingRevision = true;
      syncSendButton();
      showPanel();
      setSentStatus();
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
    var alreadyDelivered = nothingToSend && deliveredCount() > 0;
    send.disabled = state.round.submitted || nothingToSend;
    send.setAttribute('data-charter-sent', state.round.submitted ? 'true' : 'false');
    send.setAttribute('data-charter-delivered', alreadyDelivered ? 'true' : 'false');

    // The empty queue has TWO causes and they mean opposite things (#124). "Nothing to send yet — add a
    // note" is correct before the reviewer has written anything, and flatly false once an attached
    // `poll --watch` has drained what they wrote: it contradicts the notes visible on screen, and the
    // reasonable reading is "my note didn't register".
    send.title = state.round.submitted
      ? 'Sent.' + agentHint()
      : (alreadyDelivered
        ? 'Nothing new to send — your notes have already gone to the agent as you saved them.'
        : (nothingToSend
          ? 'Nothing to send yet — add a note or answer a question first.'
          : 'Hand this round of feedback to the agent'));

    syncPanelHint(alreadyDelivered);
  }

  // The standing line under the button. It used to claim "The agent sees your feedback as you save it"
  // unconditionally — which is true only when something is actually listening, and the reviewer had no way
  // to tell. Each branch below now says only what is KNOWN:
  //
  //   1. notes have already been drained — the strongest possible evidence a listener exists, because
  //      something took them;
  //   2. presence says an agent is waiting (#107) — evidence, not proof, so it describes the agent rather
  //      than promising what will happen;
  //   3. otherwise — the one statement that is true in every case, including a solo review with nothing
  //      attached. It does NOT say "nobody is listening": presence is observational, and #107 settled that
  //      the panel must not report absence as a fault. Solo review is supported, not degraded.
  function syncPanelHint(alreadyDelivered) {
    var hint = state.ui && state.ui.hint;
    if (!hint) return;
    var listening = state.agent && state.agent.waiting;
    var text = alreadyDelivered
      ? 'Your notes have gone to the agent.'
      : (listening
        ? 'An agent is listening — a note goes over as you save it.'
        : 'Save your notes, then hand them to the agent.');
    if (hint.textContent !== text) hint.textContent = text;
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
  // The "Something else" escape hatch (#109). Its control carries value="" and is paired with a text
  // input, so the ANSWER is whatever the reviewer typed, not the control's own value. An Other that is
  // checked but empty yields NOTHING — deliberately: the rule above ("an empty string is not an answer")
  // is what keeps the Save button honest, and a blank Other reads as resolved while saying less than
  // leaving the question open.
  function effectiveValue(control) {
    if (!control.getAttribute || control.getAttribute('data-answer-other') !== '1') {
      return control.value;
    }
    var label = control.closest ? control.closest('label') : null;
    var text = label ? label.querySelector('[data-answer-other-text]') : null;
    return text ? String(text.value).trim() : '';
  }

  function collectValues(form, mode) {
    if (mode === 'multi' || mode === 'multi-select') {
      var picked = [];
      var boxes = form.querySelectorAll('input[type="checkbox"][name="answer"]:checked');
      for (var i = 0; i < boxes.length; i++) {
        var boxValue = effectiveValue(boxes[i]);
        // Other COMBINES with declared options — "these two, plus this other thing" is a real answer.
        if (boxValue !== '') picked.push(boxValue);
      }
      return picked;
    }
    // bool shares the single-select shape: two mutually-exclusive radios valued "true"/"false"
    // (Charter #43), NOT the lone checkbox the SDK used to look for. Explicit, not incidental.
    if (mode === 'single' || mode === 'single-select' || mode === 'bool') {
      var radio = form.querySelector('input[type="radio"][name="answer"]:checked');
      if (!radio) return [];
      var radioValue = effectiveValue(radio);
      return radioValue === '' ? [] : [radioValue];
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

    var label = clearing ? 'Clear answer' : button.charterSaveLabel;
    var title = clearing
      ? 'Clear the recorded answer \u2014 this question goes back to unanswered'
      : (changed
        ? 'Save this answer to the Charter review session'
        : 'Choose or change an answer to enable saving');

    // WRITE ONLY WHAT ACTUALLY CHANGED (#111). Assigning `textContent` destroys the button's text node and
    // builds a new one even when the label is identical \u2014 and this runs on every `input`/`change`, including
    // the `change` a text field fires ON BLUR. Clicking Save from a textarea therefore lands that mutation
    // BETWEEN the button's mousedown and its mouseup, and WebKit then declines to synthesize the `click` at
    // all: no click, no submit, no POST, and the reviewer's answer is silently lost. Chromium tolerated the
    // swap, which is why this survived until the engine was actually tested.
    //
    // Guarding each write is the fix, and it is right independently of the bug: re-rendering a label to the
    // same string is gratuitous DOM churn that also resets any in-progress selection or IME composition.
    if (button.disabled !== !changed) button.disabled = !changed;
    if (button.textContent !== label) button.textContent = label;
    if (button.title !== title) button.title = title;
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
    // The delivery chip (#124). Deliberately quiet — it is reassurance, not an alert; it must read at a
    // glance without competing with `contested`, which is the chip that wants the reviewer to stop.
    '.charter-chip-sent { border-style: dashed; }',
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

    // The :::diagram pan/zoom chrome (Charter #51). Serve-time only, for the same reason as the selection
    // guard above: the SAVED artifact keeps the diagram exactly as the exporter wrote it (invariant 1),
    // and every rule here is scoped to a class the SDK adds at runtime and removes on dispose().
    '.charter-zoomable { position: relative; }',
    // A horizontal gesture that runs out of diagram must not become browser back-navigation; vertical
    // chaining is deliberately LEFT alone so the page still scrolls once the diagram reaches its edge.
    '.charter-zoomed { overscroll-behavior-x: contain; }',
    '.charter-zoomed:not(.charter-panning) { cursor: grab; }',
    '.charter-panning { cursor: grabbing; }',
    '.charter-zoomable:focus-visible { outline: 2px solid var(--charter-accent); outline-offset: 2px; }',
    // The same persistent-scrollbar affordance a wide table gets (#68): a silently scrollable region is
    // nearly as bad as a clipped one. It costs an unzoomed diagram nothing — with no overflow, no bar is
    // drawn and no gutter is taken.
    '.charter-zoomable::-webkit-scrollbar { height: 10px; width: 10px; }',
    '.charter-zoomable::-webkit-scrollbar-track { background: var(--charter-code-bg); border-radius: 999px; }',
    '.charter-zoomable::-webkit-scrollbar-thumb { background: var(--charter-scroll-thumb); border-radius: 999px; }',
    '.charter-zoom-bar { position: absolute; top: 6px; left: 6px; z-index: 3; display: flex;',
    '  align-items: center; gap: 4px; padding: 3px 6px; border-radius: 999px; white-space: nowrap;',
    '  background: var(--charter-bg); border: 1px solid var(--charter-border); opacity: 0.94; }',
    '.charter-zoom-btn { min-width: 24px; padding: 1px 7px; }',
    '.charter-zoom-level { font-size: 11px; color: var(--charter-muted); min-width: 36px;',
    '  text-align: center; }',
    '.charter-zoom-hint { font-size: 11px; color: var(--charter-muted); }',

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
    var hint = make('span', 'charter-panel-hint', 'panel-hint',
      'Save your notes, then hand them to the agent.');
    actions.appendChild(hint);
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
      style: style, panel: panel, title: title, list: list, send: send, hint: hint,
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
    // Any other message replaces the hand-off line and takes ownership of it — so a later presence
    // refresh cannot clobber an error the reviewer needs to see.
    state.statusIsSent = false;
    state.ui.status.textContent = text || '';
    state.ui.status.className = text ? 'charter-panel-status' : 'charter-panel-status charter-hidden';
  }

  function setSentStatus() {
    setStatus(sentMessage());
    state.statusIsSent = true;
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
      committed: true,
      delivered: false      // computed in mergedRecords() against the live queue (#124)
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
      committed: false,
      delivered: false      // it is IN the queue by construction, so it has not left for the agent
    };
  }

  // ---- delivery is its own axis (#124) --------------------------------------------------------------
  // open/resolved says whether a note is SETTLED. It says nothing about whether the agent has it, and the
  // panel used to render only the first — so with `charter poll --watch` draining on save, a note that had
  // already been handed over sat there badged `open`, looking untouched, while "Send to agent" said
  // "Nothing to send yet — add a note". The reviewer's rational conclusion was "my note didn't take", which
  // is the opposite of the truth.
  //
  // The signal needs no new endpoint: `/api/annotations` is the QUEUE, so a note the server no longer holds
  // there has left for the agent. A batch that is in flight but unacked (#117) is already excluded from that
  // snapshot deliberately — the reviewer must not see a note as still pending once it is the agent's — and
  // "sent" is the honest word for it either way.
  //
  // "Sent" is also the honest CEILING. Charter knows a note was delivered; it cannot know the agent read it,
  // agreed with it, or acted on it. `reply` and `resolve` are how those get said, by whoever actually knows.
  function queuedIds() {
    var ids = Object.create(null);
    for (var i = 0; i < state.annotations.length; i++) ids[state.annotations[i].id] = true;
    return ids;
  }

  function mergedRecords() {
    var records = [];
    var seen = Object.create(null);
    var queued = queuedIds();
    var i;
    for (i = 0; i < state.log.comments.length; i++) {
      var committed = logRecord(state.log.comments[i]);
      // Only THIS reviewer's own notes can be reported as delivered-or-not: a teammate's committed comment
      // arrived through git, never through this session's queue, so its absence from the queue says nothing.
      committed.delivered = committed.mine && !queued[committed.id];
      seen[committed.id] = true;
      records.push(committed);
    }
    for (i = 0; i < state.annotations.length; i++) {
      if (!seen[state.annotations[i].id]) records.push(pendingRecord(state.annotations[i]));
    }
    return records;
  }

  // Notes of this reviewer's that the agent has been handed. Drives the honest wording on the Send control.
  function deliveredCount() {
    var records = mergedRecords();
    var n = 0;
    for (var i = 0; i < records.length; i++) {
      if (records[i].delivered && records[i].status !== 'retracted') n++;
    }
    return n;
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
    // What the note is anchored to. The panel already renders the kind as a human label; exposing it as data
    // too keeps the card self-describing, and lets a test say "this note is not a text range" at the moment
    // that becomes true rather than 30s later at whatever downstream step assumed it was.
    item.setAttribute('data-charter-kind', record.kind || KIND.element);
    // Delivery is a SEPARATE axis from open/resolved (#124): a note can be open-and-sent, open-and-queued,
    // or settled. Rendering only the first pair is what made a delivered note look unprocessed.
    item.setAttribute('data-charter-delivery', record.delivered ? 'sent' : 'queued');
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

      // The delivery chip sits beside the status chip rather than replacing it, because they answer different
      // questions: `open` means nobody has settled it, `sent` means the agent has it. Only said for a note
      // still in play — on a resolved or retracted one, delivery is history and the extra chip is noise.
      if (record.delivered && record.status === 'open') {
        var sent = make('span', 'charter-chip charter-chip-sent', 'item-delivery', 'sent');
        sent.title = 'Handed to the agent. Charter knows it was delivered, not whether it has been acted on.';
        meta.appendChild(sent);
      }
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

    // A badge inside a PANNED diagram is a fresh element with no scroll compensation on it yet, so it
    // would render at the content's offset instead of pinned to the block's corner (Charter #51).
    for (var v = 0; v < state.diagrams.length; v++) pinDiagramChrome(state.diagrams[v]);

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

  // ---- :::diagram pan/zoom (Charter #51) ------------------------------------------------
  //
  // A real architecture diagram routinely exceeds the review column, and Mermaid renders with
  // `useMaxWidth`: the <svg> is scaled DOWN to fit its container, so nothing is clipped and nothing is
  // legible either. A reviewer cannot read a node's label — let alone decide whether to annotate it.
  //
  // This is a REVIEW-TIME affordance and lives entirely in the SDK (invariant 1): the saved/exported
  // artifact renders the same diagram statically, with none of this markup and none of these styles.
  //
  // The mechanism is deliberately the one Charter already uses for a wide table (#68) rather than a CSS
  // transform: zooming WIDENS the <svg> itself (`width: <base x scale>px; max-width: none`) and the block
  // becomes an ordinary scroll container. That buys, with no coordinate frame of our own:
  //   * crisp VECTOR text at every zoom level — `transform: scale()` rasterizes, and a blurry label is
  //     exactly the thing this feature exists to fix;
  //   * hit-testing and getBoundingClientRect() that are simply CORRECT, so Alt+click still resolves the
  //     Mermaid node under the pointer and the annotation overlay needs no new maths;
  //   * arrow-key panning for free — a focusable scroll container already does it (the #68 shape);
  //   * the overlay following a PAN through the SDK's EXISTING capture-phase 'scroll' listener, since a
  //     pan here is a real element scroll rather than a transform the listener cannot see.
  //
  // The gesture set, chosen so nothing collides with a gesture the reviewer already has:
  //   * the control bar (-, %, +, Reset) — the discoverable, keyboard-reachable, touch-usable path, and
  //     the only one a reviewer has to find;
  //   * Ctrl/Cmd + wheel zooms about the pointer (a trackpad pinch reaches Chromium as exactly that). A
  //     PLAIN wheel is never intercepted: hijacking page scroll is hostile;
  //   * a primary-button DRAG pans, but only once there is something to pan to. A drag is not a click —
  //     past a small threshold the gesture swallows the click that follows it, so panning can never open
  //     a composer;
  //   * Alt stays the ANNOTATE modifier at every zoom level, unchanged. Alt+drag pans and annotates
  //     nothing, which is the safe reading of an ambiguous gesture.
  //
  // Touch keeps its native gestures (page scroll, pinch): the bar is reachable by tap, and stealing a
  // touch-drag would take page scrolling away from the one input that has nothing else.
  var DIAGRAM_ZOOM = Object.freeze({
    min: 1,               // 1 == "fit", the resting state the exported artifact also shows
    step: 1.25,
    ceiling: 8,
    // How much wider than its rendered box a diagram must be before it is worth any chrome at all.
    slack: 8,
    // Movement (CSS px) past which a press-drag-release is a PAN and not a click.
    dragThreshold: 4,
    // A zoomed diagram's reading window, never shorter than its own resting height.
    window: 640,
    windowFraction: 0.75,
    // How long the pan's click-swallowing latch may stand if no click ever arrives (a drag released
    // outside the window). Without this bound a stray latch would eat the reviewer's NEXT Alt+click.
    latchMs: 400
  });

  // The intrinsic width Mermaid drew the diagram at, in CSS px. The viewBox is authoritative (Mermaid
  // always emits one); getBBox is the fallback for a build that ever stops.
  function intrinsicWidth(svg) {
    var box = svg.viewBox && svg.viewBox.baseVal;
    if (box && box.width > 0) return box.width;
    try {
      var bbox = svg.getBBox();
      return (bbox && bbox.width > 0) ? bbox.width : 0;
    } catch (e) {
      return 0;
    }
  }

  // Is this diagram being SHOWN SMALLER than it was drawn? That — not "does it overflow" — is the real
  // condition: useMaxWidth means an oversized diagram never overflows, it just shrinks until unreadable.
  // A diagram that fits gains no chrome, no tab stop and no behaviour change.
  function isZoomable(svg) {
    var rendered = svg.getBoundingClientRect().width;
    return rendered > 0 && intrinsicWidth(svg) > rendered + DIAGRAM_ZOOM.slack;
  }

  function viewFor(block) {
    for (var i = 0; i < state.diagrams.length; i++) {
      if (state.diagrams[i].el === block) return state.diagrams[i];
    }
    return null;
  }

  // Watch every rendered :::diagram until Mermaid has replaced its source text with an <svg> —
  // `mermaid.run()` is asynchronous and normally finishes AFTER the SDK's init(). The observer stays
  // connected so a re-render (a FRESH <svg>) re-evaluates rather than leaving a view pointing at a
  // detached element; it ignores the childList mutations the SDK makes itself (this bar, the annotation
  // count badge) because those do not change which <svg> the block holds.
  function scanDiagrams() {
    if (typeof document.querySelectorAll !== 'function') return;
    var blocks = document.querySelectorAll(DIAGRAM_BLOCK);
    for (var i = 0; i < blocks.length; i++) watchDiagram(blocks[i]);
  }

  function watchDiagram(block) {
    syncDiagram(block);
    if (typeof MutationObserver !== 'function' || block.charterDiagramObserver) return;
    var observer = new MutationObserver(function () { syncDiagram(block); });
    try { observer.observe(block, { childList: true }); } catch (e) { return; }
    block.charterDiagramObserver = observer;
    state.diagramObservers.push(observer);
  }

  // Create, refresh or tear down the view for ONE :::diagram. Idempotent: the initial scan, the
  // MutationObserver and the resize handler all call exactly this.
  function syncDiagram(block) {
    var svg = block.querySelector('svg');
    var view = viewFor(block);

    if (!svg) { if (view) releaseDiagram(view); return; }
    if (view && view.svg !== svg) { releaseDiagram(view); view = null; }

    if (view) {
      // A LIVE zoom is the reviewer's, not ours to revoke because the window changed size — and the
      // zoomed <svg> is deliberately wider than intrinsic, so isZoomable() would say "no" and tear down
      // the very view being used.
      if (view.scale > 1) return;
      view.baseWidth = svg.getBoundingClientRect().width || view.baseWidth;
      view.restingHeight = block.getBoundingClientRect().height || view.restingHeight;
      if (isZoomable(svg)) { view.maxScale = ceilingFor(svg, view.baseWidth); syncZoomBar(view); return; }
      releaseDiagram(view);
      return;
    }

    if (isZoomable(svg)) activateDiagram(block, svg);
  }

  // Zooming past 1:1 buys nothing, so the ceiling is whatever makes the diagram life-size — with a floor
  // of 2 (a barely-oversized diagram still deserves a usable step) and a hard cap.
  function ceilingFor(svg, baseWidth) {
    if (!(baseWidth > 0)) return 2;
    return Math.min(
      DIAGRAM_ZOOM.ceiling,
      Math.max(2, Math.ceil((intrinsicWidth(svg) / baseWidth) * 100) / 100));
  }

  function activateDiagram(block, svg) {
    var view = {
      el: block,
      svg: svg,
      scale: 1,
      baseWidth: svg.getBoundingClientRect().width,
      restingHeight: block.getBoundingClientRect().height,
      ownsTabIndex: false,
      bar: null, level: null, hint: null, zoomOut: null, zoomIn: null, reset: null,
      onWheel: null, onKeyDown: null, onPointerDown: null, onScroll: null
    };
    view.maxScale = ceilingFor(svg, view.baseWidth);

    block.classList.add('charter-zoomable');
    // Keyboard reach, exactly the shape #68 gave a wide table: a region only a mouse can enter hides half
    // the diagram from a keyboard-only reviewer just as effectively as shrinking it did.
    if (!block.hasAttribute('tabindex')) {
      block.setAttribute('tabindex', '0');
      view.ownsTabIndex = true;
    }
    block.setAttribute('role', 'group');
    block.setAttribute(
      'aria-label', 'Diagram, zoomable — use the zoom controls, arrow keys to pan');

    buildZoomBar(view);

    view.onWheel = function (ev) { onDiagramWheel(view, ev); };
    view.onKeyDown = function (ev) { onDiagramKeyDown(view, ev); };
    view.onPointerDown = function (ev) { onDiagramPointerDown(view, ev); };
    // Its own chrome has to ride along when the reviewer pans, or the zoom controls scroll off the block.
    view.onScroll = function () { pinDiagramChrome(view); };
    block.addEventListener('wheel', view.onWheel, { passive: false });
    block.addEventListener('keydown', view.onKeyDown, false);
    block.addEventListener('pointerdown', view.onPointerDown, false);
    block.addEventListener('scroll', view.onScroll, false);

    state.diagrams.push(view);
    syncZoomBar(view);
    emit('diagram-zoomable', { anchorId: anchorIdOf(block), maxScale: view.maxScale });
  }

  // The bar lives INSIDE the <pre> (so it travels with the diagram) at the TOP-LEFT — the top-RIGHT
  // corner already belongs to the annotation count badge. It is ordinary SDK chrome: `data-charter-ui`,
  // no ids, so closestAnchored refuses it as a target and blockTextNodes / visibleText skip it whole.
  function buildZoomBar(view) {
    var bar = make('div', 'charter-zoom-bar', 'diagram-zoom');

    // U+2212 MINUS SIGN (not a hyphen), so it optically balances the '+'. This file reaches the browser as
    // an EMBEDDED RESOURCE, so the glyph makes an encoding hop the source never sees — the browser test
    // asserts the character that arrives, rather than trusting the pipeline.
    view.zoomOut = button('charter-btn charter-zoom-btn', 'diagram-zoom-out', '−');
    view.zoomOut.setAttribute('aria-label', 'Zoom the diagram out');
    view.level = make('span', 'charter-zoom-level', 'diagram-zoom-level', '100%');
    view.zoomIn = button('charter-btn charter-zoom-btn', 'diagram-zoom-in', '+');
    view.zoomIn.setAttribute('aria-label', 'Zoom the diagram in');
    view.reset = button('charter-btn charter-zoom-btn', 'diagram-zoom-reset', 'Reset');
    view.reset.setAttribute('aria-label', 'Reset the diagram to fit');
    view.hint = make('span', 'charter-zoom-hint', 'diagram-zoom-hint', '');

    view.zoomOut.addEventListener('click', function (ev) {
      ev.preventDefault(); zoomBy(view, 1 / DIAGRAM_ZOOM.step);
    }, false);
    view.zoomIn.addEventListener('click', function (ev) {
      ev.preventDefault(); zoomBy(view, DIAGRAM_ZOOM.step);
    }, false);
    view.reset.addEventListener('click', function (ev) {
      ev.preventDefault(); resetZoom(view);
    }, false);

    bar.appendChild(view.zoomOut);
    bar.appendChild(view.level);
    bar.appendChild(view.zoomIn);
    bar.appendChild(view.reset);
    bar.appendChild(view.hint);

    view.el.appendChild(bar);
    view.bar = bar;
  }

  function syncZoomBar(view) {
    if (!view.bar) return;
    var atFit = view.scale <= DIAGRAM_ZOOM.min + 0.001;
    view.level.textContent = Math.round(view.scale * 100) + '%';
    view.zoomOut.disabled = atFit;
    view.reset.disabled = atFit;
    view.zoomIn.disabled = view.scale >= view.maxScale - 0.001;
    // Progressive disclosure: name the gesture that is USEFUL right now, not the whole vocabulary.
    view.hint.textContent = atFit ? 'Ctrl+scroll to zoom' : 'drag or arrow keys to pan';
    pinDiagramChrome(view);
  }

  // An absolutely-positioned child of a scroll container scrolls WITH the content, so the bar (and the
  // annotation count badge beside it) have to be pushed back by the scroll offset to stay pinned.
  function pinDiagramChrome(view) {
    var offset = 'translate(' + view.el.scrollLeft + 'px, ' + view.el.scrollTop + 'px)';
    if (view.bar) view.bar.style.transform = offset;
    var badges = view.el.querySelectorAll('.charter-annotation-badge');
    for (var i = 0; i < badges.length; i++) badges[i].style.transform = offset;
  }

  function clampScale(view, next) {
    if (!isFinite(next)) return view.scale;
    return Math.min(view.maxScale, Math.max(DIAGRAM_ZOOM.min, next));
  }

  function zoomBy(view, factor, focusX, focusY) {
    setZoom(view, view.scale * factor, focusX, focusY);
  }

  function resetZoom(view) {
    if (view.scale === DIAGRAM_ZOOM.min) return;
    view.scale = DIAGRAM_ZOOM.min;
    applyZoom(view);
  }

  // Zoom about a focal point, keeping whatever sits under it under it. Expressed as the focal point's
  // FRACTION of the <svg>'s own box, so it needs no coordinate frame beyond two rect reads and the
  // browser clamps the resulting scroll offsets for us.
  function setZoom(view, next, focusX, focusY) {
    next = clampScale(view, next);
    if (Math.abs(next - view.scale) < 0.0005) return;

    var before = view.svg.getBoundingClientRect();
    if (focusX === undefined || focusX === null) {
      var box = view.el.getBoundingClientRect();
      focusX = box.left + (box.width / 2);
      focusY = box.top + (box.height / 2);
    }
    var ratioX = before.width > 0 ? (focusX - before.left) / before.width : 0.5;
    var ratioY = before.height > 0 ? (focusY - before.top) / before.height : 0.5;

    view.scale = next;
    applyZoom(view);

    var after = view.svg.getBoundingClientRect();
    view.el.scrollLeft += after.left - (focusX - (ratioX * after.width));
    view.el.scrollTop += after.top - (focusY - (ratioY * after.height));
    pinDiagramChrome(view);
  }

  // Write the zoom to the DOM. At fit (scale 1) EVERY property this touches is removed rather than set to
  // a computed equivalent, so a reset leaves the block exactly as the renderer emitted it.
  function applyZoom(view) {
    var block = view.el;
    var svg = view.svg;

    if (view.scale <= DIAGRAM_ZOOM.min) {
      view.scale = DIAGRAM_ZOOM.min;
      svg.style.width = '';
      svg.style.maxWidth = '';
      block.style.maxHeight = '';
      block.style.overflow = '';
      block.classList.remove('charter-zoomed');
      block.scrollLeft = 0;
      block.scrollTop = 0;
      view.baseWidth = svg.getBoundingClientRect().width || view.baseWidth;
    } else {
      // The reading window: never shorter than the diagram's own resting height (shrinking it on the
      // first zoom would be a step backwards), never taller than most of the viewport.
      var reading = Math.max(
        view.restingHeight,
        Math.min(
          Math.round((window.innerHeight || 800) * DIAGRAM_ZOOM.windowFraction),
          DIAGRAM_ZOOM.window));
      svg.style.maxWidth = 'none';
      svg.style.width = (view.baseWidth * view.scale) + 'px';
      block.style.maxHeight = reading + 'px';
      block.style.overflow = 'auto';
      block.classList.add('charter-zoomed');
    }

    syncZoomBar(view);
    // A zoom changes the block's own height, so everything below it moves. The transient text highlight is
    // painted in viewport coordinates from a Range and has to be repainted for exactly the reason a scroll
    // or a resize repaints it.
    onViewportChange();
    emit('diagram-zoom', { anchorId: anchorIdOf(block), scale: view.scale });
  }

  function onDiagramWheel(view, ev) {
    // ONLY the zoom gesture is intercepted. A plain wheel keeps doing whatever the browser does with it:
    // over a diagram at fit that is the page, over a zoomed one it is the diagram's own scroll.
    if (!ev.ctrlKey && !ev.metaKey) return;
    ev.preventDefault();
    var mode = ev.deltaMode === 1 ? 16 : (ev.deltaMode === 2 ? 400 : 1);
    var factor = Math.exp(-(ev.deltaY || 0) * mode * 0.0025);
    zoomBy(view, Math.min(2, Math.max(0.5, factor)), ev.clientX, ev.clientY);
  }

  function onDiagramKeyDown(view, ev) {
    if (ev.altKey || ev.ctrlKey || ev.metaKey) return;
    // The bar's own buttons keep their native Enter/Space activation.
    if (ev.key === 'Enter' || ev.key === ' ' || ev.key === 'Spacebar') return;
    if (ev.key === '+' || ev.key === '=') { ev.preventDefault(); zoomBy(view, DIAGRAM_ZOOM.step); return; }
    if (ev.key === '-' || ev.key === '_') { ev.preventDefault(); zoomBy(view, 1 / DIAGRAM_ZOOM.step); return; }
    if (ev.key === '0') { ev.preventDefault(); resetZoom(view); }
    // Arrow keys are deliberately NOT handled: the block is a focusable scroll container, so the browser
    // already pans it, with the platform's own key repeat and reduced-motion behaviour (#68's shape).
  }

  // Is there anything to pan to? Asked of the live scroll geometry rather than of the scale, so a drag on
  // a diagram at fit stays completely inert — and cannot swallow the click that follows it.
  function canPan(view) {
    var el = view.el;
    return el.scrollWidth > el.clientWidth + 1 || el.scrollHeight > el.clientHeight + 1;
  }

  function onDiagramPointerDown(view, ev) {
    if (ev.button !== 0 || ev.pointerType === 'touch') return;
    if (isSdkUi(ev.target)) return;
    if (!canPan(view)) return;

    // Deliberately NO setPointerCapture: capture retargets the compatibility `click` at the captured
    // element, which is precisely how a diagram-NODE annotation would silently decay into a whole-block
    // one (Charter #48's failure, reintroduced by the back door).
    state.pan = {
      view: view, id: ev.pointerId,
      startX: ev.clientX, startY: ev.clientY,
      scrollLeft: view.el.scrollLeft, scrollTop: view.el.scrollTop,
      moved: false
    };
    document.addEventListener('pointermove', onDiagramPointerMove, true);
    document.addEventListener('pointerup', onDiagramPointerUp, true);
    document.addEventListener('pointercancel', onDiagramPointerUp, true);
  }

  function onDiagramPointerMove(ev) {
    var pan = state.pan;
    if (!pan || ev.pointerId !== pan.id) return;

    var dx = ev.clientX - pan.startX;
    var dy = ev.clientY - pan.startY;
    if (!pan.moved &&
        Math.abs(dx) < DIAGRAM_ZOOM.dragThreshold &&
        Math.abs(dy) < DIAGRAM_ZOOM.dragThreshold) {
      return;
    }
    if (!pan.moved) {
      pan.moved = true;
      pan.view.el.classList.add('charter-panning');
    }

    ev.preventDefault();
    pan.view.el.scrollLeft = pan.scrollLeft - dx;
    pan.view.el.scrollTop = pan.scrollTop - dy;

    // Re-pin SYNCHRONOUSLY, in the same turn as the scroll we just caused (#113). The `scroll` listener that
    // normally does this is dispatched ASYNCHRONOUSLY — the spec fires scroll at the next rendering
    // opportunity, not at the assignment above — so between these two lines and that event the zoom bar and
    // the annotation badges are still sitting at the OLD offset, riding away with the content. It reads as
    // jitter that trails the cursor during a drag, and how visible it is comes down to how the engine
    // schedules that event: Linux WebKit leaves a wide enough window to catch the chrome 7px adrift, while
    // Chromium and Windows WebKit hid it. The scroll listener stays as the backstop for every scroll this
    // handler does NOT cause — keyboard, wheel, scrollbar.
    pinDiagramChrome(pan.view);
  }

  function onDiagramPointerUp(ev) {
    var pan = state.pan;
    if (!pan || (ev && ev.pointerId !== undefined && ev.pointerId !== pan.id)) return;
    endPan();
  }

  function endPan() {
    var pan = state.pan;
    state.pan = null;
    document.removeEventListener('pointermove', onDiagramPointerMove, true);
    document.removeEventListener('pointerup', onDiagramPointerUp, true);
    document.removeEventListener('pointercancel', onDiagramPointerUp, true);
    if (!pan) return;

    pan.view.el.classList.remove('charter-panning');
    if (!pan.moved) return;

    // A PAN is not a click. Swallow the click Chromium synthesizes at the end of the drag, using the same
    // one-shot latch a text-selection drag already uses — and bound it, because a drag released outside
    // the window produces no click at all and a standing latch would eat the reviewer's next Alt+click.
    state.ignoreNextClick = true;
    if (state.panLatch) window.clearTimeout(state.panLatch);
    state.panLatch = window.setTimeout(function () {
      state.ignoreNextClick = false;
      state.panLatch = 0;
    }, DIAGRAM_ZOOM.latchMs);

    emit('diagram-panned', {
      anchorId: anchorIdOf(pan.view.el),
      scrollLeft: pan.view.el.scrollLeft,
      scrollTop: pan.view.el.scrollTop
    });
  }

  // A resize can make a diagram fit that did not, or the reverse. Kept off onViewportChange, which is also
  // the scroll handler and must stay cheap.
  function onDiagramResize() {
    scanDiagrams();
  }

  function releaseDiagram(view) {
    var at = state.diagrams.indexOf(view);
    if (at >= 0) state.diagrams.splice(at, 1);
    if (state.pan && state.pan.view === view) endPan();

    view.el.removeEventListener('wheel', view.onWheel, false);
    view.el.removeEventListener('keydown', view.onKeyDown, false);
    view.el.removeEventListener('pointerdown', view.onPointerDown, false);
    view.el.removeEventListener('scroll', view.onScroll, false);

    view.scale = DIAGRAM_ZOOM.min;
    view.svg.style.width = '';
    view.svg.style.maxWidth = '';
    view.el.style.maxHeight = '';
    view.el.style.overflow = '';
    view.el.scrollLeft = 0;
    view.el.scrollTop = 0;
    view.el.classList.remove('charter-zoomable');
    view.el.classList.remove('charter-zoomed');
    view.el.classList.remove('charter-panning');
    if (view.ownsTabIndex) view.el.removeAttribute('tabindex');
    view.el.removeAttribute('role');
    view.el.removeAttribute('aria-label');
    if (view.bar && view.bar.parentNode) view.bar.parentNode.removeChild(view.bar);
    view.bar = null;

    var badges = view.el.querySelectorAll('.charter-annotation-badge');
    for (var i = 0; i < badges.length; i++) badges[i].style.transform = '';
  }

  function disposeDiagrams() {
    while (state.diagrams.length) releaseDiagram(state.diagrams[state.diagrams.length - 1]);
    for (var i = 0; i < state.diagramObservers.length; i++) {
      try { state.diagramObservers[i].disconnect(); } catch (e) { /* ignore */ }
    }
    state.diagramObservers = [];
    var blocks = document.querySelectorAll(DIAGRAM_BLOCK);
    for (var j = 0; j < blocks.length; j++) blocks[j].charterDiagramObserver = null;
    if (state.panLatch) { window.clearTimeout(state.panLatch); state.panLatch = 0; }
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

    // A revision the reviewer ASKED FOR is offered, not imposed. Navigating drops them at the top of a
    // document that has just been rewritten underneath them — the further into a long plan they had read,
    // the more it costs, and it is the one reload they were expecting anyway. The banner says the same thing
    // and lets them arrive when they are ready. `awaitingRevision` is cleared either way: the wait is over
    // the moment the change exists, whether or not they have looked at it yet.
    var expected = state.awaitingRevision;
    state.awaitingRevision = false;

    // `state.reloadPending` keeps the offer STICKY. Once the banner is up, the reviewer has been promised
    // they choose when to move; a second change arriving while they are still reading must not quietly take
    // that back and navigate. An agent revising in several passes produces exactly that burst.
    if (wouldLoseWork() || expected || state.reloadPending) {
      state.reloadPending = true;
      showReloadBanner(expected);
      emit('reload-deferred', { expected: expected });
      return;
    }
    navigate();
  }

  function showReloadBanner(expected) {
    if (!ensureUi() || state.ui.banner) return;
    var banner = make('div', 'charter-reload-banner', 'reload-banner');
    banner.appendChild(make('span', null, 'reload-banner-text',
      expected
        ? 'The agent revised the plan. Load it when you are ready \u2014 you will not lose your place until you do.'
        : 'The plan changed on disk. Your unsaved work is safe \u2014 reload when you are ready.'));
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
      // The agent drained the queue (#124). Panel-only, like review-log — nothing about the document
      // changed, so a navigation here would discard a half-typed note to report good news. Re-reading the
      // queue is what flips a delivered note from "queued" to "sent" and stops the Send control claiming
      // there is nothing to send because the reviewer has not written anything.
      // The agent ACKED the round — it is now the one holding it. Panel-only; nothing about the document
      // changed. This is the moment a waiting reviewer most wants reported, and the ack clears `submitted`
      // server-side, so without this push the panel would keep showing the pre-ack wording indefinitely.
      es.addEventListener('round', function () { emit('round-changed', {}); refreshRound(); });
      es.addEventListener('queue', function () {
        emit('queue-changed', {});
        hydrate().then(function () { refreshRound(); });
      });
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
      // Separate from onViewportChange, which is also the (hot) scroll handler: a resize can make a
      // diagram fit that did not, or the reverse, and that re-evaluation must not run on every scroll.
      window.addEventListener('resize', onDiagramResize, false);
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
    // Mermaid renders asynchronously and normally has not finished yet, so this installs the watchers and
    // the views appear as each <svg> lands.
    scanDiagrams();
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
      window.removeEventListener('resize', onDiagramResize, false);
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
    if (state.ageTicker) { window.clearInterval(state.ageTicker); state.ageTicker = 0; }
    // Every pan/zoom view is torn down to the markup the renderer emitted — inline styles cleared, classes,
    // tab stop, role and label removed — so a disposed SDK leaves the block indistinguishable from the
    // exported artifact's.
    disposeDiagrams();
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
    state.diagrams = [];
    state.pan = null;
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
