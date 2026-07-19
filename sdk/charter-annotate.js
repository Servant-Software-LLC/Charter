/*!
 * charter-annotate.js — Charter's in-browser annotation SDK.
 * The browser half of Charter's comment-in-place review loop.
 *
 * Adapted lean from Lavish (https://github.com/kunchenguid/lavish-axi) by Kun Chen,
 * which is distributed under the MIT License. Only the comment-in-place review loop is
 * reproduced here — anchoring a human note to an element, a text-range, or a diagram-node,
 * carried over a narrow postMessage/HTTP boundary. This is deliberately NOT a full Lavish
 * port (see plan decision D2): keeping the surface minimal is what keeps the re-port
 * manageable.
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

  // The three annotation kinds Charter supports. The value each maps to is the wire
  // token sent to the server (kept stable and human-readable).
  var KIND = Object.freeze({
    element: 'element',          // (a) a whole rendered block, keyed by its stable block id
    textRange: 'text-range',     // (b) a selection within a block
    diagramNode: 'diagram-node'  // (c) a node inside a :::diagram Mermaid render, by node identity
  });

  var state = {
    started: false,
    key: null,        // capability key, read from the page URL's ?key= query string
    origin: null,     // postMessage target origin (same-origin by default)
    events: null,     // EventSource for /events live reload
    handlers: []      // local subscribers registered via on()
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
  // the SDK is doing.
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
  function onMessage(ev) {
    var data = ev && ev.data;
    if (!data || data.channel !== CHANNEL) return;
    if (data.type === 'annotate' && data.detail) {
      submit(data.detail);
    }
  }

  // ---- anchoring: the three kinds -----------------------------------------------------

  // Walk up to the nearest ancestor that carries a stable anchor: the renderer stamps each
  // block's content-derived stable id on its root element (and may also expose an explicit
  // data-charter-anchor / data-anchor attribute). Text nodes resolve to their parent.
  function closestAnchored(node) {
    var el = (node && node.nodeType === 3) ? node.parentElement : node;
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
    if (!node) return null;
    var block = closestAnchored(node);
    return {
      kind: KIND.diagramNode,
      anchorId: block ? anchorIdOf(block) : null,
      nodeId: node.getAttribute('data-node-id') || node.id || null
    };
  }

  // ---- submit: POST the annotation to /api/{key}/prompts + emit over the boundary ------
  function submit(annotation) {
    if (!annotation || !annotation.anchorId) {
      emit('error', { reason: 'no-anchor', annotation: annotation });
      return Promise.resolve(null);
    }
    // Each annotation carries the block/anchor id, the kind, and the note text.
    var payload = {
      anchorId: annotation.anchorId,
      kind: annotation.kind || KIND.element,
      note: annotation.note || '',
      quote: annotation.quote || null,
      nodeId: annotation.nodeId || null
    };
    emit('submitting', payload);
    var url = '/api/' + encodeURIComponent(state.key || '') + '/prompts';
    return fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }).then(function (res) {
      emit(res.ok ? 'submitted' : 'error', { status: res.status, payload: payload });
      return res.ok ? res : null;
    }).catch(function (err) {
      emit('error', { reason: 'network', message: String(err), payload: payload });
      return null;
    });
  }

  // ---- capture UI: Alt+click to anchor an element / diagram-node; select text to anchor
  // a range. Kept deliberately minimal (a native prompt) — the review panel UI is not part
  // of this lean SDK.
  function promptAndSubmit(anchor) {
    emit('anchor', anchor);
    var label = anchor.kind + (anchor.anchorId ? ' ' + anchor.anchorId : '');
    var note = (typeof window.prompt === 'function')
      ? window.prompt('Note for ' + label + ':')
      : '';
    if (!note) { emit('cancelled', anchor); return; }
    anchor.note = note;
    submit(anchor);
  }

  function onClick(ev) {
    if (!ev.altKey) return;           // Alt+click is the annotate affordance
    var dn = diagramNodeAnchor(ev.target);
    if (dn) { ev.preventDefault(); promptAndSubmit(dn); return; }
    var el = elementAnchor(ev.target);
    if (el) { ev.preventDefault(); promptAndSubmit(el); }
  }

  function onMouseUp() {
    var sel = (typeof window.getSelection === 'function') ? window.getSelection() : null;
    var tr = textRangeAnchor(sel);
    if (tr) promptAndSubmit(tr);
  }

  // ---- live reload: listen for server-sent events on /events (SSE) ---------------------
  function eventsUrl() {
    // /events is capability-gated like every other route, so ride the key on the query
    // string when we have one. The route itself stays literally /events.
    return state.key ? ('/events?key=' + encodeURIComponent(state.key)) : '/events';
  }

  function openEvents() {
    if (typeof EventSource === 'undefined') return;   // SSE unavailable — non-fatal
    try {
      var es = new EventSource(eventsUrl());
      es.addEventListener('reload', function () {
        emit('reload', {});
        try { window.location.reload(); } catch (e) { /* ignore */ }
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
      document.addEventListener('click', onClick, true);
      document.addEventListener('mouseup', onMouseUp, false);
    }
    openEvents();

    state.started = true;
    emit('ready', { hasKey: !!state.key });
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
      document.removeEventListener('click', onClick, true);
      document.removeEventListener('mouseup', onMouseUp, false);
    }
    if (state.events) {
      try { state.events.close(); } catch (e) { /* ignore */ }
      state.events = null;
    }
    state.handlers.length = 0;
    state.started = false;
  }

  var api = {
    KIND: KIND,
    CHANNEL: CHANNEL,
    init: init,        // entry point
    on: on,            // subscribe to boundary events locally
    annotate: submit,  // submit an annotation programmatically
    dispose: dispose
  };
  return api;
})();
