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

  // Every element the SDK builds carries this attribute. It NAMES that element, for the SDK's own
  // stylesheet and for the browser tests, which is all it does.
  // Deliberately an ATTRIBUTE, never an `id`: the renderer anchors blocks by `id`, so an SDK
  // element with an id could be resolved as an annotation target. The SDK's UI has no ids at all,
  // so even a guard bug degrades to "no anchor", never "the wrong anchor".
  var UI_ATTR = 'data-charter-ui';

  // It is NOT proof of ownership, and it used to be treated as such — the anchoring layer's self-guard
  // (closestAnchored) and the block-text walker both matched on it. :::custom-html passes an author's
  // attributes through verbatim (that is the whole point of the escape hatch), so a body carrying
  // data-charter-ui made the SDK treat that subtree as its own chrome: Alt+click anywhere inside it resolved
  // to null, and blockTextNodes dropped its text out of the offset frame, shifting the start/end of every
  // note taken lower down that block (Charter #176).
  //
  // Ownership is therefore a JS PROPERTY, set by make() on every element the SDK constructs. HTML cannot
  // express one, so an author's markup can never claim it, and no name in the document is trusted. This is
  // the same move clearMarkers makes with its ledger and the two are the same rule: CHARTER IDENTIFIES ITS
  // OWN CHROME BY CONSTRUCTION, NEVER BY PATTERN-MATCHING A DOCUMENT IT DOES NOT OWN.
  var OWNED = 'charterOwned';

  // What one piece of REBUILDABLE chrome is, across the rebuild that replaces it (Charter #200).
  //
  // render() does not update its chrome in place, it destroys and rebuilds it: renderPanel empties the list,
  // and renderMarkers opens with clearMarkers. Both are deliberate and load-bearing — the ownership ledger
  // can only undo exactly what it did (#176), and the sweep-and-rebuild has to complete in ONE synchronous
  // turn so no frame is ever painted without a badge (#198). The cost is that an element holding keyboard
  // focus is REMOVED, and the browser drops focus to <body>: the same end state as #168, reached by a route
  // the reviewer did not initiate — a teammate's note arriving over SSE, or hydrateLog() after a local save.
  //
  // The value is { key, fallback, gone }: `key` names the same control across renders, `fallback` names the
  // enclosing chrome to land on when the control itself is legitimately gone (Resolve disappears the moment
  // it is used, and the card that held it is still there), and `gone` is the sentence the reviewer is told
  // when neither survives.
  //
  // A JS PROPERTY, set at construction, for exactly the reason OWNED is: a :::custom-html body can carry any
  // class or attribute Charter uses, so the counterpart is looked up in a ledger the SDK populates AS IT
  // BUILDS — never by querying the document for a name it does not own (#176).
  var FOCUS = 'charterFocus';

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
    // The plan's own path, read once from GET /api/sessions. It is what makes a copyable command a REAL
    // command rather than a template the reviewer has to finish (#116, #126). Fetched at init so the string
    // is already composed when the copy gesture arrives — WebKit rejects a clipboard write reached after an
    // intervening await.
    sourcePath: null,
    // Can this machine resolve the /charter-drain invocation the panel hands over (#116)? Defaults to TRUE
    // and is only ever lowered by a server that positively looked and did not find it. Optimistic on
    // purpose: the check cannot enumerate every place an agent might have skills installed
    // (`skills install --target <dir>` puts them anywhere), so a wrong "missing" must cost a reviewer one
    // extra true sentence, never a withheld instruction.
    drainSkillInstalled: true,
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
    flashed: null,
    // The selected note (#137). Held as an ID rather than a card, because render() rebuilds every card
    // — the selection is re-projected onto the new DOM instead of pointing at a detached node.
    selectedId: '',
    selectedAnchorEl: null,
    // Exactly what the last renderMarkers() pass put on the page, so clearMarkers can undo THAT and nothing
    // else. See clearMarkers for why a document-wide sweep by class name was the wrong shape.
    marks: newMarks(),
    // The rebuildable chrome THIS render pass built, keyed by the name that survives a rebuild (see FOCUS).
    // Populated by keyChrome as each element is constructed and reset at the top of every render, so a
    // reviewer's focus is put back on the counterpart of what they were on rather than on whatever the
    // document happens to be carrying that name (#200).
    focusIndex: Object.create(null),
    // How many times THIS page has changed the server's pending queue — one per save, per retract, per
    // edit. It is a clock, not a count: hydrate() reads it before it asks for a snapshot and again when
    // the snapshot arrives, and a difference means the snapshot is older than what the page already
    // knows. See hydrate() for why that has to be checked rather than waited out (#209).
    queueWrites: 0
  };

  function newMarks() {
    return { classed: [], counted: [], created: [] };
  }

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

  // Nodes that must never resolve to an annotation anchor: the native controls of a rendered :::question
  // form, and the form itself. The guard lives at the ANCHORING layer rather than in the event handlers, so
  // every path that could produce an anchor — click, selection, or a future one — is covered by construction.
  //
  // The SDK's own chrome is refused too, but it is NOT in this selector: chrome is recognised by OWNERSHIP
  // (isSdkUi), because `[data-charter-ui]` is a name an author's :::custom-html body can carry and a name is
  // not proof (Charter #176). See OWNED.
  var UNANCHORABLE = 'input, textarea, select, button, option, form.question';

  // The annotate modifier's NAME, per platform. The mechanic is identical everywhere — macOS's ⌥ key sets
  // `event.altKey`, so nothing about the handling changes — but its KEYCAP does not read "Alt" on a Mac, and
  // telling a reviewer to press a key their keyboard does not have is the same as telling them nothing.
  // Reported by a reviewer on a MacBook who could not find the gesture at all.
  var IS_MAC = (function () {
    try {
      var platform = (navigator.userAgentData && navigator.userAgentData.platform) ||
                     navigator.platform || navigator.userAgent || '';
      return /mac|iphone|ipad|ipod/i.test(platform);
    } catch (e) {
      return false;
    }
  })();

  var MODIFIER = IS_MAC ? '⌥ Option' : 'Alt';

  // The width the review panel occupies, and the viewport below which reserving it would squeeze the plan
  // harder than the panel ever covered it. Kept next to the panel's own width so the two cannot drift.
  // The standing caveat under the breakdown command. Held apart so the open-notes warning can be prepended
  // to it without either half drifting from the other.
  // The standing caveat under the drain command. Held apart so the skill-missing warning can be prepended to
  // it without either half drifting from the other — the same discipline BREAKDOWN_NOTE follows.
  var DRAIN_NOTE =
    'It keeps listening for the rest of the review, so this is the only time you need to send it.';

  var BREAKDOWN_NOTE =
    'Have your agent stop draining first — otherwise this queues behind that and looks like nothing ' +
    'happened. This starts a breakdown you review, never a run.';

  var PANEL_WIDTH = 340;

  // Reserve only where the plan can still be read at ESSENTIALLY ITS FULL MEASURE. The document wants
  // 52rem plus 1.5rem of padding either side — about 880px — so taking the panel's 340px needs ~1220px of
  // viewport before the reading column starts to suffer. Set at 1200: the squeeze there is ~3% of the
  // measure, and below it the panel overlays exactly as it did.
  //
  // The first value tried was 1000, which was wrong in an instructive way: it left a 612px column, narrower
  // than the 660px the layout suite treats as a SMALL SCREEN. Reserving had quietly forced every
  // 1000px-wide reviewer into the narrow layout to avoid an occlusion that costs them far less.
  var RESERVE_MIN_VIEWPORT = 1200;

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

  // ---- opaque regions: the two boxes Charter does not own the insides of ----------------
  //
  // A rendered plan contains exactly two regions whose CONTENT is markup Charter did not author, and whose
  // ids therefore mean nothing to the anchor model:
  //
  //   .custom-html-scroll  the :::custom-html escape hatch's body, emitted VERBATIM. An author's own
  //                        `id` survives into the render — that is what an escape hatch is for.
  //   pre.mermaid          a rendered :::diagram, whose <svg> and every g.node carry ids Mermaid
  //                        generated and regenerates on the next render (Charter #48).
  //
  // Neither kind of id is ever produced by `AnchorAssignment`, so `SourceMap` cannot map one to a markdown
  // line: a note anchored to one reaches the agent with `sourceLine: null`, orphaned (Charter #166). Worse
  // for an author id, which is not unique by construction: `document.getElementById` answers with the FIRST
  // element carrying it, so two copy-pasted escape hatches made a note taken in the second one resolve to the
  // first — misattribution, which is precisely what the duplicate discriminator in `AnchorAssignment` exists
  // to make impossible.
  var OPAQUE_REGION = '.custom-html-scroll, ' + DIAGRAM_BLOCK;

  // Is `el` INSIDE an opaque region? Strictly inside: a region is not its own ancestor, so `pre.mermaid`
  // itself stays the anchor for a top-level :::diagram exactly as it was before.
  function insideOpaqueRegion(el) {
    var parent = el ? el.parentElement : null;
    if (!parent || typeof parent.closest !== 'function') return false;
    return !!parent.closest(OPAQUE_REGION);
  }

  // THE PREDICATE. An element is an anchor iff it carries an id / data-anchor / data-charter-anchor AND no
  // ancestor of it is an opaque region. It gates the WHOLE acceptance test, uniformly: :::custom-html passes
  // all three attributes through verbatim, so accepting `data-anchor` from inside a region while refusing
  // `id` would leave the same hole under a different name.
  //
  // WHY FORGERY CANNOT DEFEAT IT. An author can write class="custom-html-scroll" or class="mermaid" inside
  // their own body, but the REAL region is an ancestor of everything in that body — so a forged inner region
  // only ever ADDS an ancestor match. The predicate is MONOTONE: forgery can make more things unanchorable,
  // never fewer. And "unanchorable" is not "null" — the walk below continues outward and lands on the
  // enclosing real block. So no "outermost region" computation is needed, and there is nothing to win by
  // faking one.
  //
  // The residue it CANNOT see is markup that breaks out of both wrappers (`</div></div><div id="pwn">`),
  // which the HTML parser hoists clear of the region. Balancing an author's body means parsing it. That case
  // is covered from the other side instead: the review panel reads the server's `anchorStatus` for a pending
  // note, so an unmappable anchor is STATED as an orphan rather than drawn as healthy (see buildItem).
  //
  // The attribute test is `anchorIdOf`, not `hasAttribute`, and the difference is load-bearing: an element
  // carrying `data-anchor=""` with no id SATISFIED hasAttribute and yielded a null id, so the walk stopped on
  // a block whose anchor could never be recorded. That is the state Charter #178 is about, and answering
  // "not an anchor" here is strictly better than guarding downstream, because the walk then CONTINUES
  // OUTWARD to a real anchor — the same degradation the opaque-region half already has, instead of null.
  function isAnchorElement(el) {
    if (!el || el.nodeType !== 1) return false;
    if (!anchorIdOf(el)) return false;
    return !insideOpaqueRegion(el);
  }

  // Walk up to the nearest ancestor that IS an anchor by the predicate above: the renderer stamps each
  // block's content-derived stable id on its root element (and may also expose an explicit
  // data-charter-anchor / data-anchor attribute). Text nodes resolve to their parent.
  //
  // The walk CONTINUES OUTWARD past an element the predicate refuses — it does not stop on the region and it
  // does not give up. That is the whole design, and an early return of the region itself would be a new bug:
  // `RenderBody`'s anchor pass iterates TOP-LEVEL nodes only, so a :::custom-html or :::diagram nested inside
  // a ::::note or a list item renders with NO id, and returning it would yield `anchorId: null` — which
  // `textRangeAnchor` does not guard, so the composer would open, take the reviewer's note, and `submit()`
  // would drop it. Continuing outward lands on the callout or the <li> instead: a real anchor, with a real
  // sourceLine.
  //
  // This subsumes the explicit `pre.mermaid` short-circuit that used to sit here for Charter #48 — every id
  // inside a rendered diagram now fails the predicate on the same rule, and the walk stops on the block. It
  // also repairs what that short-circuit got wrong: it returned `pre.mermaid` UNCONDITIONALLY, so a :::diagram
  // nested inside a ::::note (id-less, as above) hit `onClick`'s `!anchorIdOf(block)` guard and was
  // un-annotatable — no composer, no error, nothing at all.
  function closestAnchored(node) {
    var el = (node && node.nodeType === 3) ? node.parentElement : node;
    if (!el || el.nodeType !== 1 || typeof el.closest !== 'function') return null;
    // The self-guard: anything inside SDK chrome (or a native control) has NO anchor, full stop.
    // Without it a selection that ends inside the panel would anchor to the panel and post a
    // bogus annotation — carrying a quote copied out of another reviewer's note — to the agent.
    if (isSdkUi(el) || el.closest(UNANCHORABLE)) return null;
    while (el && el.nodeType === 1) {
      if (isAnchorElement(el)) return el;
      el = el.parentElement;
    }
    return null;
  }

  // A block a reviewer can SEE and POINT AT that still refuses notes — as opposed to a gesture that landed on
  // nothing (the page's margin) or on the SDK's own chrome, where there is nothing to explain because the
  // reviewer did not point at plan content. Exactly one block qualifies: a rendered :::question.
  function refusesNotes(node) {
    var el = (node && node.nodeType === 3) ? node.parentElement : node;
    if (!el || el.nodeType !== 1 || typeof el.closest !== 'function') return false;
    if (isSdkUi(el)) return false;
    return !!el.closest('form.question');
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
  //
  // The READ path is gated by the SAME predicate as the write path, and it is the more serious half. The
  // write path only decides where NEW notes go; this decides where every note ALREADY TAKEN points — every
  // committed note on an author id included, which no fix to the write path can reach. Left ungated, two
  // escape hatches that both contain <table id="raw-t"> (a copy-paste — what an escape hatch is for) made a
  // note taken in the second one jump to, scroll to, mark and quote the FIRST, while the agent was handed
  // `sourceLine: null`. That is misattribution, not orphaning: the reviewer sees a confident, wrong answer.
  //
  // `getElementById` answers with the first element in document order, so a rejection has to keep LOOKING
  // rather than give up — a forged id inside a region must not shadow a real Charter anchor further down.
  function anchorElement(anchorId) {
    if (!anchorId) return null;
    var found = null;
    try { found = document.getElementById(anchorId); } catch (e) { found = null; }
    if (usableAnchor(found)) return found;
    var matches = null;
    try {
      var quoted = String(anchorId).replace(/["\\]/g, '\\$&');
      matches = document.querySelectorAll(
        '[id="' + quoted + '"], [data-charter-anchor="' + quoted + '"], [data-anchor="' + quoted + '"]');
    } catch (e) {
      return null;
    }
    for (var i = 0; i < matches.length; i++) {
      if (usableAnchor(matches[i])) return matches[i];
    }
    return null;
  }

  // An element this anchor id may legitimately resolve TO: not SDK chrome, and not inside an opaque region.
  function usableAnchor(el) {
    return !!el && !isSdkUi(el) && !insideOpaqueRegion(el);
  }

  // Is this node the SDK's own chrome, or inside it? Answered by the ownership property make() sets, walked
  // up by hand because `closest` can only ask about attributes — and an attribute is exactly what an
  // author's verbatim body is free to carry (Charter #176).
  function isSdkUi(node) {
    var el = (node && node.nodeType === 3) ? node.parentElement : node;
    while (el && el.nodeType === 1) {
      if (el[OWNED] === true) return true;
      el = el.parentElement;
    }
    return false;
  }

  // (a) element: anchor a note to a whole rendered block by its stable block id. `block` is the element
  // closestAnchored already resolved, so this is the same shape for every block type — a :::diagram
  // commented on as a whole included (Charter #60).
  function elementAnchor(block) {
    return { kind: KIND.element, anchorId: anchorIdOf(block) };
  }

  // ---- what counts as the block's TEXT --------------------------------------------------
  //
  // Elements whose text nodes are MACHINERY, not words a human reads: a <style> or <script> inside a block.
  // Mermaid ships its theme CSS in a <style> INSIDE the rendered <svg>, and :::custom-html may carry either
  // — a plan that documents a widget, or pastes in a fragment of a real page, routinely does.
  //
  // Matched case-insensitively because an SVG element's tagName keeps its lower-case local name while an
  // HTML element's is upper-cased.
  //
  // ONE definition, TWO readers, deliberately. The derived label (visibleText) has skipped these since #48;
  // the OFFSET FRAME never did, so a note taken below an author's inline CSS was recorded against a span
  // shifted by the length of that CSS source, and the agent was handed the wrong text (Charter #179). Same
  // question, so it must not be answered twice.
  var NON_VISIBLE_TAGS = { STYLE: true, SCRIPT: true };

  function isNonVisible(el) {
    return NON_VISIBLE_TAGS[String(el.tagName || '').toUpperCase()] === true;
  }

  // Is this subtree excluded from the block's text? The SDK's own chrome (a marker, a count badge, a zoom
  // bar) is not the author's words, and neither is machinery. Ownership, not `[data-charter-ui]`: an
  // author's body carrying that attribute would otherwise drop its own text out of the frame and shift every
  // offset below it — the same defect from the other direction (Charter #176).
  function outsideBlockText(node) {
    return node[OWNED] === true || isNonVisible(node);
  }

  // The block's own text nodes in document order, plus their values. Concatenating `texts` gives the block's
  // text content — the SINGLE reference frame that both the recorded start/end offsets and the panel's quote
  // lookup are expressed in, and the frame an agent reads those offsets against.
  function blockTextNodes(block) {
    var nodes = [];
    var texts = [];
    (function walk(node) {
      if (!node) return;
      if (node.nodeType === 1) {
        if (outsideBlockText(node)) return;
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
    //
    // Asked of the SELECTION as well as of the resolved block, because the two stopped being the same
    // question with the containment predicate: a diagram nested inside a ::::note now resolves OUT to the
    // callout, so `isDiagramBlock(block)` alone would let a drag across Mermaid's own markup become a text
    // range on the callout — with offsets measured through the <svg>'s theme <style>. It used to be refused
    // here only by accident, because the walk stopped on the id-less <pre>.
    if (diagramBlock(selection.anchorNode) || isDiagramBlock(block)) return null;
    // No id, no anchor — refuse BEFORE the composer opens, never after the reviewer has typed into it
    // (Charter #178). isAnchorElement makes this unreachable today; it is kept because the cost of being
    // wrong is a note the reviewer wrote and never gets back, and because the composer is invited open by
    // whatever this returns.
    var anchorId = anchorIdOf(block);
    if (!anchorId) return null;
    var span = blockSpan(block, range);
    return {
      kind: KIND.textRange,
      anchorId: anchorId,
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

  // The text a human sees INSIDE a block — the same exclusions as the offset frame, from the one definition
  // they share (outsideBlockText): SDK-owned chrome, because a count badge injected into the block would
  // otherwise pollute the composer's "what am I annotating" line and the panel entry's target label; and
  // <style>/<script>, because their contents are source rather than words.
  function visibleText(root) {
    if (!root) return '';
    var out = [];
    (function walk(node) {
      if (!node) return;
      if (node.nodeType === 1) {
        if (outsideBlockText(node)) return;
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
      // The composer opens BEFORE this is reached, so by now the reviewer has typed the note — and this used
      // to emit over the postMessage boundary and return, which reaches a test harness and no human at all
      // (Charter #178). Nothing is saved either way; the only question is whether the reviewer finds out.
      explain('That note was not saved — Charter could not tell which block it belongs to.');
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
          state.queueWrites++;   // this page now knows something no earlier snapshot can (#209)
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

  // A QUEUE READ IS A SNAPSHOT OF A MOMENT, AND THE MOMENT IS WHEN THE SERVER TOOK IT (Charter #209).
  //
  // It is authoritative about everything that had happened by then, and says nothing whatever about what
  // this page did afterwards — so assigning it over `state.annotations` ERASES any note saved while it was
  // in flight. The window is neither hypothetical nor narrow: hydrate() runs at init, and again on every
  // `queue` frame (an agent drained), so a save landing between the server reading its queue and the
  // browser receiving that read is all it takes. #209 is that window measured — a badge showing two notes
  // counted one again the instant a read taken between them arrived, and STAYED at one, because nothing
  // renders afterwards to correct it.
  //
  // Note what the repair cannot be. Not a wait, on either side: the regression arrives AFTER every signal a
  // reviewer or a test could observe — after the POST, after its render, after `submitted`. And emphatically
  // not "wait for one more render", which is the trap this defect's whole family is named for: the late
  // render IS the damage. The only thing separating a usable snapshot from a harmful one is whether this
  // page has written past it, and that is a question the page can simply ask.
  //
  // So: read the write clock before asking, compare it when the answer lands, and DECLINE a snapshot the
  // page has moved past. What is given up by declining is knowledge of what LEFT the queue (#124's delivery
  // axis) until the next read arrives — and the write that caused the decline is itself about to be drained,
  // which brings one. What is kept is every note the reviewer saved. That trade is not close.
  function hydrate() {
    var url = '/api/annotations?key=' + encodeURIComponent(state.key || '');
    var taken = state.queueWrites;
    return fetch(url, { headers: { 'Accept': 'application/json' } }).then(function (res) {
      if (!res.ok) {
        setStatus('Could not load your notes (' + res.status + ').');
        emit('list-error', { status: res.status });
        return null;
      }
      return res.json().then(function (list) {
        // Declined out loud, never silently: `stale` is a structural fact a test can assert on, so the
        // guard cannot rot into a branch nothing ever proves was taken.
        if (state.queueWrites !== taken) {
          emit('list-loaded', { count: state.annotations.length, stale: true });
          return state.annotations;
        }
        state.annotations = (list && typeof list.length === 'number') ? list : [];
        render();
        emit('list-loaded', { count: state.annotations.length, stale: false });
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
  //
  // "I COULD NOT LOOK" IS NOT "THERE IS NOTHING THERE" (Charter #221).
  //
  // The view now carries an `outcome`: `present` (a directory read, holding logs), `empty` (a directory read,
  // holding none) or `unknown` (there was no directory to read, after the server's bounded retry). Only the
  // first two are ANSWERS about the review log; `unknown` is the absence of one. Assigning it over `state.log`
  // is the same class of mistake #209 fixed one function up — trading knowledge the page already holds for a
  // reading that never happened — and the damage is worse here, because the panel is not a badge: render()
  // destroys and rebuilds every card, so an applied zero-comment view removes the element the reviewer was
  // reading and focus lands on <body>.
  //
  // The window is ordinary, not exotic. `.review/` is created lazily on the first append and lives in the
  // working tree, so a branch switch, a `git clean` or a `git pull` can take it out from under an in-flight
  // read while the reviewer is mid-comment. There is nothing to wait for: the directory is gone, and the next
  // `review-log` frame — the watcher reports the deletion too — brings another read that answers the same way.
  //
  // So: decline it, keep what the panel is showing, and say so. What is given up is any teammate comment
  // committed in the moment the directory was away, until it comes back and the next read lands. What is kept
  // is every comment already on screen and the reviewer's place among them. As in #209, the decline is
  // announced rather than silent — `declined` is a structural fact a test can assert on, so the guard cannot
  // rot into a branch nothing ever proves was taken.
  //
  // But `unknown` ALONE is not the trigger, for the same reason it is not the whole trigger for `charter poll`'s
  // exit 4. An absent `.review/` is the ORDINARY state of a plan nobody has committed a comment on — a charter
  // served with no review-log writer never creates the directory, so every read of it answers `unknown` from the
  // first one. The decline is for a non-answer that would ERASE something; where the panel holds nothing from a
  // successful read there is nothing to erase, applying it is a no-op on `state.log`, and the render it carries
  // is load-bearing well past this panel — it sweeps every badge and marker on the page. So the drain's
  // discrimination (Unknown is a FAILED read only where a read had succeeded before) is asked here of the state
  // the panel is actually showing.
  function holdingAReadLog() {
    return state.log.comments.length > 0 ||
      state.log.diagnostics.length > 0 ||
      state.log.unreadable.length > 0;
  }

  function hydrateLog() {
    var url = '/api/review-log?key=' + encodeURIComponent(state.key || '');
    return fetch(url, { headers: { 'Accept': 'application/json' } }).then(function (res) {
      if (!res.ok) {
        emit('review-log-error', { status: res.status });
        return null;
      }
      return res.json().then(function (view) {
        if (view && view.outcome === 'unknown' && holdingAReadLog()) {
          // The count reported is the one the panel is STILL showing, not the zero the declined view carried.
          emit('review-log-loaded', {
            count: state.log.comments.length,
            diagnostics: state.log.diagnostics.length,
            unreadable: state.log.unreadable.length,
            declined: true
          });
          return state.log;
        }
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
          unreadable: state.log.unreadable.length,
          declined: false
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
        state.queueWrites++;   // an edit is a queue write too: a snapshot older than it carries the old text
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
  // Charter #158 — the reviewer's side of a thread. `AppendReply` has always defaulted its actor to
  // `human` because it was written for exactly this caller, and until now nothing could reach it: the panel
  // could create, edit, retract and resolve, but not CONTINUE. So a thread was one round deep by
  // construction, and a reviewer who disagreed with the agent's reply could only settle a thing they did not
  // agree with, or start a new note that had lost its parent.
  function replyToNote(id, text) {
    return fetch(annotationUrl(id, 'reply'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ note: text })
    }).then(function (res) {
      if (res.ok) {
        setStatus('');
        hydrateLog();
        emit('annotation-replied', { id: id });
        return true;
      }
      setStatus('Could not post that reply (' + res.status + ').');
      emit('annotation-reply-error', { id: id, status: res.status });
      return false;
    }).catch(function () {
      setStatus('Could not reach the review server.');
      emit('annotation-reply-error', { id: id, reason: 'network' });
      return false;
    });
  }

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
    // The mirror of the save case (#209): a snapshot taken before this retract still lists the note, so
    // applying it would put a withdrawn note back on the page.
    state.queueWrites++;
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
    // A statement of fact, and nothing else. The instruction that follows from it is the copyable command
    // row below the button — which carries the reviewer's REAL path rather than a `<plan>` placeholder they
    // would have to fill in, and `--watch` rather than `--wait` (one ~30s cycle). Repeating it here left two
    // instructions on screen that disagreed about both.
    //
    // `charter resolve` used to be offered here as the alternative. It is not one: resolve folds queued
    // ANSWERS inline and does nothing whatever for annotations, so a reviewer who has just sent a round of
    // notes would have followed it and delivered none of them.
    return ' No agent has checked this session yet.';
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

  // The plan's path, read once. Key-gated like every other read, and kept OUT of emit() — the postMessage
  // broadcast deliberately carries no request URLs, and a local absolute path is the same class of thing.
  function hydrateSession() {
    var url = '/api/sessions?key=' + encodeURIComponent(state.key || '');
    return fetch(url, { headers: { 'Accept': 'application/json' } }).then(function (res) {
      if (!res.ok) return null;
      return res.json().then(function (descriptor) {
        state.sourcePath = (descriptor && descriptor.sourcePath) || null;
        // Whether this machine can resolve /charter-drain (#116). Absent on an older server ⇒ leave the
        // optimistic default, so a new page against an old server behaves exactly as it did before.
        if (descriptor && typeof descriptor.drainSkillInstalled === 'boolean') {
          state.drainSkillInstalled = descriptor.drainSkillInstalled;
        }
        syncCommands();
        return state.sourcePath;
      }, function () { return null; });
    }).catch(function () { return null; });
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
      // ORDER IS LOAD-BEARING (#204). syncSendButton disables this button, and disabling the control a
      // keyboard reviewer just pressed Enter on has to hand their focus somewhere — which for this gesture
      // is the status line, because that is where the sentence they now need is written. So the panel is
      // opened and the sentence written FIRST, and only then is the button taken away. Reversed, the
      // hand-on would arrive at a `display: none` region with nothing in it.
      showPanel();
      setSentStatus();
      syncSendButton();
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
    // Charter #204 — handing a round off disables this button, so the reviewer who pressed Enter on it is
    // dropped to <body> at the very moment the panel writes the sentence they now need. They are handed on
    // to the region carrying that sentence instead: the status line while it is showing (sendRound writes
    // it FIRST for exactly this), otherwise the standing hint under the button, which always says why
    // sending is unavailable, and finally the labelled panel itself.
    disableChrome(send, state.round.submitted || nothingToSend, function () {
      return [visibleStatusLine(), state.ui.hint, state.ui.panel];
    });
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
    syncCommands();
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
  //
  // `data-question-id` is one more name a :::custom-html body can carry, and this is the gateway to
  // everything the SDK does to a question form — intercepting its native submit, re-labelling its Save
  // button, disabling and re-enabling it, hanging a status line off it, and putting it all back on dispose.
  // Done to an author's own <form>, that is the escape hatch being rewritten by the review chrome, which is
  // the defect Charter #176 is about wearing different clothes. So the same monotone containment rule
  // applies: inside an opaque region it is the author's form, not Charter's. A real question is a top-level
  // BLOCK and is never inside one. (Charter #203 corrected the parenthetical that used to sit here, "or
  // nested in a callout": a :::question inside a callout renders no form at all any more. It used to render a
  // live one that no answer could ever be folded back through, so the renderer now degrades it to a visible,
  // non-answerable .question-error placeholder carrying no data-question-id — which means it never reaches
  // this function in the first place. No SDK behaviour changed for it; there is simply nothing to find.)
  function questionRoot(form) {
    if (!form || form.nodeType !== 1 || form.tagName !== 'FORM') return null;
    var root = form.hasAttribute('data-question-id')
      ? form
      : (form.closest ? form.closest('[data-question-id]') : null);
    return (root && !insideOpaqueRegion(root)) ? root : null;
  }

  // Every rendered :::question in the PLAN, in document order — the sweep twin of questionRoot, filtered by
  // the same rule so the two cannot disagree about which forms are Charter's.
  function questionForms() {
    var found = document.querySelectorAll('form[data-question-id]');
    var forms = [];
    for (var i = 0; i < found.length; i++) {
      if (questionRoot(found[i])) forms.push(found[i]);
    }
    return forms;
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
    var forms = questionForms();
    for (var i = 0; i < forms.length; i++) {
      ensureWired(forms[i], forms[i]);
      syncSubmitState(forms[i], forms[i]);
    }
  }

  // Re-sync on every edit of a question control. Capture phase and delegated from the document, like
  // the SDK's other listeners, so it needs no per-control bookkeeping.
  // TYPING IN A WRITE-IN BOX IS ANSWERING WITH IT (Charter #239).
  //
  // `collectValues` reads only CHECKED controls, and `effectiveValue` consults the paired text field only
  // when that control is checked — so text typed beside an unchecked "Something else" was invisible to the
  // form, in two different ways:
  //
  //   nothing else selected   the signature stayed EMPTY_ANSWER, so Save never enabled. The reviewer typed
  //                           a sentence, watched a dead button, and was told nothing.
  //   an option selected      the signature still equalled THAT OPTION, so Save was enabled and recorded it
  //                           — while the reviewer's words sat visible on screen, discarded.
  //
  // The second is a wrong answer that looks like a legitimate one, and it defeats the escape hatch: #109
  // exists because the agent writing the options is least qualified to know they are exhaustive, and
  // "without this … the real decision is lost". Needing a second, undiscoverable click lost it another way.
  //
  // `input` (not `keyup`) is what this hangs off, so PASTE and IME composition both count.
  function selectWriteInIfTyping(target) {
    if (!target || !target.getAttribute) return;
    if (target.getAttribute('data-answer-other-text') !== '1') return;

    var label = target.closest ? target.closest('label') : null;
    var control = label ? label.querySelector('[data-answer-other="1"]') : null;
    if (!control || control.checked) return;

    // Assigning `checked` does NOT move focus, which is the property this depends on: the reviewer is
    // mid-word and the caret must stay under their hands. Three fixed defects in this codebase are about
    // focus leaving where the reviewer put it (#168, #200, #221), so the browser test asserts it rather
    // than trusting this sentence.
    //
    // On a RADIO this deselects whatever was chosen before, which is what radios mean and is the same act
    // as clicking "Something else" — reached one keystroke sooner. On a CHECKBOX it is purely additive:
    // Other combines with declared options.
    //
    // Only ever SET. Emptying the box does not deselect: an Other checked with no text already yields
    // nothing, and auto-deselecting would fight a reviewer who selects-all and retypes.
    control.checked = true;
  }

  function onQuestionInput(ev) {
    var target = ev && ev.target;
    var form = target && target.form;
    var root = questionRoot(form);
    if (!root) return;

    // BEFORE the sync, not after: the signature is computed from checked controls, so syncing first would
    // read a state one keystroke stale and leave Save lagging the reviewer's typing by a character.
    selectWriteInIfTyping(target);
    syncSubmitState(form, root);
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
    var forms = questionForms();
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

    // Reserve the panel's column so the plan is never UNDER it (#131).
    //
    // The document is centred at a 52rem measure, and the panel is fixed to the right edge — so the two
    // collide on any viewport narrower than about 1512px, which is most laptops. A reviewer on a 1440-wide
    // MacBook loses roughly 36px of every line of the document they are annotating.
    //
    // Reserved from FIRST PAINT rather than when the panel opens, and that is the whole point: the panel
    // opens itself the moment a note is saved, so reserving on open would jerk the document sideways at
    // exactly the instant the reviewer finished annotating — moving the block they had just commented on.
    // It also means the layout is settled BEFORE any diagram is measured, so nothing needs re-pinning and
    // the reflow hazard behind #113 never arises. The cost is 340px of width for a reviewer who never opens
    // the panel; stillness is worth more.
    //
    // It lives in the SDK's stylesheet, never in charter.css: `render` and `export` share that file and must
    // keep emitting a centred, full-width document. This layout exists only where the panel does.
    //
    // Below RESERVE_MIN_VIEWPORT the reserve is dropped and the panel overlays as before — squeezing a
    // narrow screen to a 600px measure would cost the reader more than the occlusion does.
    '@media (min-width: ' + RESERVE_MIN_VIEWPORT + 'px) {',
    '  html.charter-reserved { box-sizing: border-box; padding-right: ' + PANEL_WIDTH + 'px; }',
    '}',
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
    '  margin-bottom: 8px; background: var(--charter-code-bg); cursor: pointer; }',
    // Selecting a card is the note→content half of the link a badge click already provides
    // content→note (#137). The selected card and its anchor are marked with the SAME accent so the
    // pair reads as one thing across the two panes.
    '.charter-item[data-charter-selected="true"], .charter-item[data-charter-selected="orphaned"] {',
    '  border-color: var(--charter-accent); box-shadow: inset 3px 0 0 0 var(--charter-accent); }',
    '.charter-item:focus-visible { outline: 2px solid var(--charter-accent); outline-offset: 2px; }',
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
    // The command rows (#116, #126). The command itself is the loud part — it is what gets copied, and it
    // must stay legible and selectable even when the clipboard refuses.
    '.charter-commands { border-top: 1px solid var(--charter-border); padding: 8px 10px; }',
    '.charter-command + .charter-command { margin-top: 10px; }',
    '.charter-command-label { font-size: 12px; margin-bottom: 4px; }',
    '.charter-command-text { display: block; user-select: all; -webkit-user-select: all;',
    '  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px;',
    '  background: var(--charter-code-bg, rgba(127,127,127,0.12)); border: 1px solid var(--charter-border);',
    '  border-radius: 4px; padding: 6px 8px; margin-bottom: 6px; overflow-wrap: anywhere; }',
    '.charter-command-note { font-size: 11px; color: var(--charter-muted); margin-top: 6px; }',
    '.charter-item[data-charter-status="retracted"] .charter-item-note { font-style: italic;',
    '  color: var(--charter-muted); }',
    // The two "here is a fact you cannot see on the page" lines share one look on purpose: an orphan's block is
    // gone, a retracted block's marker is gone, and neither absence is visible from the plan (#170).
    '.charter-item-orphan, .charter-item-unmarked { font-size: 11px; color: var(--charter-muted);',
    '  margin-bottom: 6px; }',
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

    // Expand mode (Charter #234). The block itself goes `position: fixed` and fills the viewport IN PLACE:
    // it is never reparented, because ancestry is what the anchor walk and the source map are read through,
    // and a diagram that moved in the DOM to be read would come back with its notes attached somewhere else.
    // The native Fullscreen API is disqualified for a concrete reason rather than a stylistic one — in
    // fullscreen the browser paints only the fullscreen element's subtree, and the composer mounts on
    // document.body, so Alt+click while expanded would open a composer that exists, takes focus, and is
    // invisible.
    //
    // Declared here rather than written as inline styles, which buys two things. Collapsing is then EXACTLY
    // the removal of one class, so the block's original box comes back without anything having had to
    // remember it; and `!important` beats applyZoom's own INLINE max-height and overflow, which are right for
    // the reading window a zoomed diagram gets inside the column and wrong for one that already owns the
    // screen.
    //
    // Nothing here is display: none and nothing outside the block is touched at all — the expanded diagram
    // PAINTS OVER the page. It sits above the annotation overlay and below the panel, the panel toggle and
    // the composer, so a note can still be written inside it and the reviewer's notes stay reachable. That is
    // a rule rather than a taste: #221 is an undiagnosed focus defect whose leading hypothesis is that focus
    // into a display:none subtree silently does nothing, and this feature manufactures none of that.
    '.charter-expand { position: fixed !important; top: 0; left: 0; right: 0; bottom: 0;',
    '  width: auto; height: auto; max-width: none !important; max-height: none !important;',
    '  margin: 0 !important; padding: 40px 12px 12px; overflow: auto !important;',
    '  z-index: 2147481500; background: var(--charter-bg); }',

    '.charter-has-annotations { position: relative; box-shadow: inset 3px 0 0 0 var(--charter-accent); }',
    // The accent bar on a table's scroll region has to be drawn OUTSIDE it (#167).
    //
    // An inset box-shadow paints on the element's own background layer, underneath every descendant — which is
    // fine for a <p>, a <ul> or a <pre>, whose left edge nothing else paints on. A table's cells DO paint
    // there: `th { background: var(--charter-code-bg) }` covers the bar across the whole header, and with
    // `border-collapse: collapse` the collapsed row borders chop what is left into one short segment per body
    // row. Measured on the served page: four disconnected dashes and nothing on the header — decoration that
    // reads as row striping rather than a marker.
    //
    // An OUTER box-shadow is clipped to outside the border box, so nothing inside the block can paint over it,
    // and a shadow of any offset costs zero layout — an annotated table still lays out pixel-identically to an
    // unannotated one. It lands flush against the region's left edge, occupying [left-3, left] where the inset
    // bar occupies [left, left+3], so the marker reads the same at a glance on every block type.
    '.table-scroll.charter-has-annotations { box-shadow: -3px 0 0 0 var(--charter-accent); }',
    // user-select: none is load-bearing, not cosmetic (#164). The badge sits INSIDE the block it counts, so
    // a reviewer selecting the whole block — the ordinary gesture on a list or a table — used to drag the
    // digit into the selection and therefore into the note's `quote`. blockTextNodes skips every
    // [data-charter-ui] subtree, so the stored quote could then never be found again: findQuoteRange returns
    // -1 and the highlight silently never draws. Unselectable text cannot be captured in the first place.
    '.charter-annotation-badge { position: absolute; top: 2px; right: 2px; z-index: 3; font: inherit;',
    '  font-size: 11px; line-height: 1; min-width: 18px; padding: 3px 6px; border-radius: 999px;',
    '  cursor: pointer; background: var(--charter-accent); color: #fff;',
    '  border: 1px solid var(--charter-accent);',
    '  -webkit-user-select: none; user-select: none; }',
    // The sibling badge rail (#164): the positioning context for a badge on a block that cannot legally
    // contain one. Zero height and no margin, so it collapses through between its neighbours and the plan's
    // layout is exactly what the renderer emitted; position: relative so the badge inside it is placed
    // against the reading column's right edge. It is a SIBLING of the block, never an ancestor — see
    // mountBadgeRail for why that distinction is the whole design.
    '.charter-badge-rail { position: relative; height: 0; margin: 0; }',
    '.charter-annotate-target { outline: 2px solid var(--charter-accent); outline-offset: 2px; }',
    '.charter-anchor-flash { outline: 2px dashed var(--charter-accent); outline-offset: 3px; }',
    // DASHED flash vs SOLID selection, deliberately: the flash is a transient "here it is" that a timer
    // clears, while selection persists for as long as the note stays selected so the reviewer can look
    // back and forth between the note and what it is attached to (#137).
    '.charter-anchor-selected { outline: 2px solid var(--charter-accent); outline-offset: 3px; }'
  ].join('\n');

  // Every element the SDK puts in the document is built here, which is what makes ownership decidable: the
  // property below is set on construction and cannot be reached from markup (see OWNED).
  function make(tag, className, uiName, text) {
    var el = document.createElement(tag);
    el[OWNED] = true;
    if (className) el.className = className;
    if (uiName) el.setAttribute(UI_ATTR, uiName);
    if (text !== undefined && text !== null) el.textContent = text;
    return el;
  }

  // Take a class OFF one of the PLAN's own elements. classList.remove empties the attribute but does not
  // delete it, so a block the renderer emitted with no class keeps a bare class="" for the rest of the
  // session — and after dispose(), which is where it stops being cosmetic: the SDK's standing claim is that a
  // disposed document is indistinguishable from the exported artifact's (invariant 1), and class="" is a
  // difference. Every marker the SDK paints on plan content comes off through here.
  function dropClass(el, name) {
    if (!el || !el.classList) return;
    el.classList.remove(name);
    if (!el.className) el.removeAttribute('class');
  }

  function button(className, uiName, text) {
    var b = make('button', className, uiName, text);
    b.type = 'button';   // never a form submit — the SDK's chrome must not trigger navigation
    return b;
  }

  // ---- copyable commands (#116, #126) ---------------------------------------------------------------
  // Charter never runs an agent — the browser records and signals, and the human drives. That leaves two
  // moments where the page knows exactly what needs running and the reviewer has to reconstruct it: a round
  // handed to nobody (#116), and a plan ready to break down (#126). Both are the same gap — the command is
  // knowable, the path is knowable, and the reviewer was retyping both from memory.
  //
  // Handing over the exact string keeps every boundary intact. The page executes nothing, so it owes no
  // progress state, needs no runner vocabulary, and asks for no trust it did not already have.
  //
  // The command is ALWAYS rendered as selectable text. The copy is the convenience; the visible line is the
  // contract, so a clipboard that refuses degrades to "select this" rather than to nothing at all.

  // Quote a path for a command line, and prefer forward slashes on Windows: .NET, PowerShell and git-bash
  // all accept them, while a backslash reaches an agent's shell as an escape (`\U`, `\M`…). An unquoted
  // `C:\Users\Dave\My Plans\api.charter.md` is silently truncated at the space by whatever reads it.
  function quotePath(path) {
    return '"' + String(path || '').replace(/\\/g, '/').replace(/"/g, '\\"') + '"';
  }

  // Copy, with a fallback that never silently does nothing.
  //
  // WebKit requires writeText to be reached SYNCHRONOUSLY from the user gesture: any await before it — a
  // fetch for the path, say — drops the transient activation and the promise rejects with NotAllowedError.
  // That is why the command string is composed at hydrate and merely read here (#111's exact signature).
  function copyCommand(text, onDone) {
    var settled = false;
    function done(ok) {
      if (settled) return;
      settled = true;
      onDone(ok);
    }

    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(function () { done(true); }, function () { done(false); });
        return;
      }
    } catch (e) { /* fall through to the selection fallback */ }

    done(false);
  }

  // Build one command row: a label, the command as selectable <code>, and a Copy button. Returns the row so
  // callers can show/hide it; the text is set through make(), so a path can never be parsed as markup.
  function commandRow(uiName, label, command, note) {
    var row = make('div', 'charter-command', uiName);
    row.appendChild(make('div', 'charter-command-label', uiName + '-label', label));

    var line = make('code', 'charter-command-text', uiName + '-text', command);
    row.appendChild(line);

    var copy = button('charter-btn', uiName + '-copy', 'Copy');
    copy.addEventListener('click', function () {
      copyCommand(command, function (ok) {
        copy.textContent = ok ? 'Copied' : 'Select and copy';
        if (!ok) selectText(line);
        emit('command-copied', { command: uiName, ok: ok });
        window.setTimeout(function () { copy.textContent = 'Copy'; }, 2500);
      });
    }, false);
    row.appendChild(copy);

    if (note) row.appendChild(make('div', 'charter-command-note', uiName + '-note', note));
    return row;
  }

  // Which commands the panel is currently offering, rebuilt whenever the facts behind them change.
  //
  // #116 — the DRAIN command. Offered when a round has been handed over and NOTHING HAS EVER CHECKED this
  // session: the reviewer clicked Send and, by construction, no agent will ever pick it up. That is the gap
  // #116 was filed for, and it is the one presence fact that is reliable — "has anything ever polled" is
  // certain, where "is an agent working right now" is not observable at all. It is deliberately NOT offered
  // merely because presence says nobody is waiting AT THIS INSTANT: an agent between poll cycles reads
  // exactly the same, and telling a reviewer with a working agent to start a second one invites two writers.
  //
  // #126 — the BREAKDOWN command. Offered once there is nothing outstanding to hand over, which is the
  // closest honest proxy for "this plan is ready". It starts a BREAKDOWN and never a run: `guardrails run`
  // executes real work and merges to a branch, and each stage stays the human's to begin.
  function syncCommands() {
    var ui = state.ui;
    if (!ui || !ui.commands) return;
    if (!state.sourcePath) return;               // no real path yet ⇒ nothing worth offering

    var agent = state.agent;
    var neverChecked = !!agent && !agent.waiting && typeof agent.lastSeenSecondsAgo !== 'number';
    var handedOff = state.round.submitted || state.awaitingRevision;
    var settled = pendingCount() === 0 && !state.round.submitted;

    if (!ui.installCommand) {
      // Handing over a skill call that resolves to NOTHING trades one silent failure for another (#116) —
      // and silent-failure-that-looks-like-success is the exact defect #144 set out to remove. Shown only
      // when the server looked for the skill and did not find it, and shown ABOVE the drain row because
      // that is the order you would do them in.
      ui.installCommand = commandRow(
        'install-command',
        'Your agent needs the Charter skills first. Run this in your terminal:',
        'charter skills install',
        'One-off, per machine. Restart your agent afterwards so it picks the skills up.');
      ui.commands.appendChild(ui.installCommand);
    }

    if (!ui.drainCommand) {
      // A SKILL invocation, not a command line (#144). The old row handed over
      // `charter poll <plan> --watch --apply` under the label "Run this where your agent is" — an invitation
      // to do the one thing that must not happen. Run in a terminal it WORKS: it prints a wall of JSON
      // envelopes forever and drains the round into a console nobody is reading, and nothing says so. A
      // slash command fails loudly in the wrong hands (`command not found`) instead of succeeding quietly,
      // and it keeps the flag mechanics with the agent that owns them.
      ui.drainCommand = commandRow(
        'drain-command',
        'Nothing has picked this up. Paste this to your agent:',
        '/charter-drain ' + quotePath(state.sourcePath),
        DRAIN_NOTE);
      ui.commands.appendChild(ui.drainCommand);
      ui.drainNote = ui.drainCommand.querySelector('[' + UI_ATTR + '="drain-command-note"]');
    }

    // The invocation is offered EITHER WAY (#116): the lookup cannot enumerate every place an agent might
    // have skills installed, so a wrong "missing" must cost the reviewer one extra true sentence rather than
    // a withheld instruction. What changes is what sits beside it.
    var haveSkill = state.drainSkillInstalled !== false;
    ui.drainCommand.setAttribute('data-charter-drain-skill', haveSkill ? 'installed' : 'missing');
    if (ui.drainNote) {
      var drainNote = haveSkill
        ? DRAIN_NOTE
        : 'Charter cannot find that skill on this machine — install it first, above. ' + DRAIN_NOTE;
      if (ui.drainNote.textContent !== drainNote) ui.drainNote.textContent = drainNote;
    }

    if (!ui.breakdownCommand) {
      ui.breakdownCommand = commandRow(
        'breakdown-command',
        'Ready to break this plan into tasks? Paste this to your agent:',
        '/plan-breakdown ' + quotePath(state.sourcePath),
        BREAKDOWN_NOTE);
      ui.commands.appendChild(ui.breakdownCommand);
      ui.breakdownNote = ui.breakdownCommand.querySelector('[' + UI_ATTR + '="breakdown-command-note"]');
    }

    // An empty QUEUE is not a finished REVIEW (#145). The queue empties the instant anything drains, so
    // gating on it alone told a reviewer their plan was ready for breakdown while their own unresolved notes
    // sat in the same panel — and a breakdown built from a plan whose feedback has not landed is an expensive
    // DAG of a stale document.
    //
    // Said rather than silently withdrawn. Hiding the row would be the #124 mistake in a new place: a state
    // the reviewer can neither see nor account for. Some notes are informational and will never be resolved,
    // so the reviewer keeps the choice — they just stop making it uninformed.
    var open = openNoteCount();
    if (ui.breakdownNote) {
      var note = open > 0
        ? open + ' review note(s) are still open — a breakdown now will not include them. ' + BREAKDOWN_NOTE
        : BREAKDOWN_NOTE;
      if (ui.breakdownNote.textContent !== note) ui.breakdownNote.textContent = note;
    }

    ui.breakdownCommand.setAttribute('data-charter-open-notes', String(open));

    var offerDrain = handedOff && neverChecked;
    // The install row rides with the drain row and only when the skill is genuinely missing: it is a
    // prerequisite for the invocation beside it, not standing advice.
    show(ui.installCommand, offerDrain && !haveSkill);
    show(ui.drainCommand, offerDrain);
    show(ui.breakdownCommand, settled);
    show(ui.commands, offerDrain || settled);
  }

  function show(el, visible) {
    if (!el) return;
    if (visible) el.classList.remove('charter-hidden');
    else el.classList.add('charter-hidden');
  }

  // Put the command under the reviewer's cursor so Ctrl/⌘+C works, for the case where the clipboard API is
  // unavailable or refuses. Selection only — it never writes.
  function selectText(el) {
    try {
      var range = document.createRange();
      range.selectNodeContents(el);
      var selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    } catch (e) { /* a browser that cannot select still shows the text */ }
  }

  // Build the SDK chrome once. Idempotent; safe to call from any entry point.
  function ensureUi() {
    if (state.ui || !document.body) return state.ui;

    var style = make('style', null, 'style');
    style.textContent = STYLE;
    (document.head || document.documentElement).appendChild(style);

    // Claim the panel's column as soon as the chrome exists — before diagrams are measured, so a diagram
    // that needs pan/zoom is decided against the width it will actually have (#131). The media query above
    // is what makes this a no-op on a narrow screen; the class is set unconditionally.
    document.documentElement.classList.add('charter-reserved');

    var panel = make('div', 'charter-panel charter-hidden', 'panel');
    panel.setAttribute('role', 'complementary');
    panel.setAttribute('aria-label', 'Charter review notes');
    // Programmatically focusable, never a Tab stop (#168). Opening the panel moves focus here so the labelled
    // region is what gets announced; -1 keeps it out of the sequential order, where a landmark div would
    // otherwise cost every reviewer an extra press on the way past.
    panel.setAttribute('tabindex', '-1');

    var header = make('div', 'charter-panel-header', 'panel-header');
    var title = make('span', 'charter-panel-title', 'panel-title', 'Review notes (0)');
    var close = button('charter-btn', 'panel-close', 'Hide');
    // Explicitly `{ focus: true }` rather than passing hidePanel as the listener: the DOM would hand it the
    // MouseEvent as its options object, and — more importantly — WebKit does not focus a <button> on click at
    // all, so "was focus inside the panel?" answers differently on the two engines for the same gesture.
    // Dismissing the panel on purpose always returns the reviewer to the toggle.
    close.addEventListener('click', function () { hidePanel({ focus: true }); }, false);
    header.appendChild(title);
    header.appendChild(close);

    var list = make('div', 'charter-panel-list', 'panel-list');
    // Delegated to the list, not bound per card, so arrow navigation survives the panel being rebuilt
    // by render(). Only claims the arrows while focus is inside the notes list — the composer's own
    // textarea keeps them for caret movement (#137).
    list.addEventListener('keydown', function (ev) {
      if (ev.key !== 'ArrowDown' && ev.key !== 'ArrowUp') return;
      if (ev.target && ev.target.closest && ev.target.closest('.charter-composer')) return;
      ev.preventDefault();
      moveSelection(ev.key === 'ArrowDown' ? 1 : -1);
    }, false);
    var status = make('div', 'charter-panel-status charter-hidden', 'panel-status');
    // Programmatically focusable, never a Tab stop — the same shape the panel itself carries (#168). It is
    // where a reviewer is handed on when the control they just used disables itself, because it is where
    // the sentence explaining that is written (#204). -1 keeps it out of the sequential order, so nobody
    // who is not being handed on pays a keystroke for it.
    status.setAttribute('tabindex', '-1');

    // The round hand-off. Disabled until there is queued feedback to send (and again once sent), so the
    // control can never post an empty round or double-hand-off the same one.
    var actions = make('div', 'charter-panel-actions', 'panel-actions');
    var hint = make('span', 'charter-panel-hint', 'panel-hint',
      'Save your notes, then hand them to the agent.');
    // Focusable only as a hand-on target (#204), for the case where Send is disabled by something OTHER
    // than the reviewer's own hand-off — a drain emptying the queue under them — and the status line is
    // therefore not carrying an explanation. This line always is.
    hint.setAttribute('tabindex', '-1');
    actions.appendChild(hint);
    var send = button('charter-btn charter-btn-primary charter-send', 'send-to-agent', 'Send to agent');
    send.disabled = true;
    send.setAttribute('data-charter-sent', 'false');
    send.addEventListener('click', function () { sendRound(); }, false);
    actions.appendChild(send);

    // The two command rows live here but stay hidden until the state that justifies them is true.
    var commands = make('div', 'charter-commands charter-hidden', 'commands');

    panel.appendChild(header);
    panel.appendChild(list);
    panel.appendChild(actions);
    panel.appendChild(commands);
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
      commands: commands, installCommand: null, drainCommand: null, drainNote: null,
      breakdownCommand: null, breakdownNote: null,
      status: status, toggle: toggle, overlay: overlay, banner: null,
      // The quarantine notice is built on demand (renderStaleQueue) and lives inside the panel, so disposing
      // the panel disposes it. It is never in the saved artifact — invariant 1 — like the rest of this chrome.
      stale: null
    };
    syncSendButton();
    renderStaleQueue();
    return state.ui;
  }

  // The status line only when it is actually SHOWING something — an empty one is `display: none`, which
  // cannot take focus, and landing a reviewer on a blank region would announce nothing at all (#204).
  function visibleStatusLine() {
    var status = state.ui && state.ui.status;
    if (!status || String(status.className).indexOf('charter-hidden') >= 0) return null;
    return (status.textContent || '').trim() ? status : null;
  }

  function setStatus(text) {
    if (!state.ui) return;
    // Any other message replaces the hand-off line and takes ownership of it — so a later presence
    // refresh cannot clobber an error the reviewer needs to see.
    state.statusIsSent = false;
    state.ui.status.textContent = text || '';
    state.ui.status.className = text ? 'charter-panel-status' : 'charter-panel-status charter-hidden';
  }

  // Say why a deliberate gesture produced nothing, WHERE THE REVIEWER CAN READ IT.
  //
  // setStatus alone is not enough: the status line lives inside the panel, and the panel is closed by
  // default — so a message written to it while it is shut is the same silence it was meant to end. The panel
  // therefore opens, as it already does when a note is saved, when a round is handed off and when a
  // quarantined queue needs explaining. Focus is NOT moved (the reviewer is reading the document, not the
  // panel), which is the same call all three of those make.
  //
  // This is the #170 rule generalised past markers: Charter has no vocabulary for "nothing happened, and
  // that was correct", so absence gets a sentence rather than being left to be inferred.
  function explain(text) {
    setStatus(text);
    showPanel();
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

  // Move focus without ever letting the gesture scroll the document. The panel and the toggle are both
  // position: fixed, so there is nothing to scroll to — but `focus()` walks every scrollable ancestor, and an
  // engine that decides otherwise would yank the reviewer away from the block they were reading.
  function focusChrome(el) {
    if (!el || !el.focus) return;
    try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); }
  }

  // Opening a disclosure has to put focus INSIDE what it disclosed — and here that is not a nicety.
  //
  // The floating toggle HIDES ITSELF the moment the panel opens (setToggleVisible(false) → display: none), so
  // the control the reviewer just activated stops being focusable and the browser resets focus to <body>. A
  // keyboard reviewer was then dropped back at the top of the document and had to re-traverse the whole tab
  // order — badge, table region, table region, badge, the panel's own Hide — before reaching the first note
  // (#168: measured at ~20 keystrokes to the first Jump on a six-note plan, and reproduced here as focus
  // landing on BODY the instant Enter opened the panel).
  //
  // Focus lands on the PANEL, not on its first control: the container carries role="complementary" and the
  // "Charter review notes" label, so what a screen reader announces is the region that just opened, and the
  // next Tab is the first thing inside it. Landing on a note card instead would announce one note and say
  // nothing about where it arrived.
  //
  // `focus` is opt-IN, and every automatic open leaves it off deliberately. The panel opens itself when a note
  // is saved, when a round is handed off, and once when a quarantined queue needs explaining — none of those
  // are the reviewer asking for the panel, and stealing the caret from a document they are reading is a worse
  // bug than the one this fixes.
  function showPanel(opts) {
    if (!ensureUi()) return;
    state.ui.panel.className = 'charter-panel';
    setToggleVisible(false);
    if (opts && opts.focus) focusChrome(state.ui.panel);
    emit('panel-opened', {});
  }

  // ...and the way back out, which is the same defect mirrored: closing the panel makes it display: none, so
  // focus still inside it would be dropped to <body> exactly as opening used to drop it. The toggle is the
  // control that reappears in the panel's place, so that is where the reviewer is put back.
  //
  // Focus that was NEVER in the panel is left where it is. A reviewer who clicked Hide and then clicked into
  // the plan, or a programmatic close, must not have the caret pulled out of the document they are reading —
  // returning focus is repair, not a claim on it.
  function hidePanel(opts) {
    if (!state.ui) return;
    var returning = (opts && opts.focus) || panelHasFocus();
    state.ui.panel.className = 'charter-panel charter-hidden';
    setToggleVisible(true);
    if (returning) focusChrome(state.ui.toggle);
    emit('panel-closed', {});
  }

  function panelHasFocus() {
    var active = document.activeElement;
    return !!(active && state.ui && state.ui.panel.contains(active));
  }

  // The disclosure gesture itself, so both directions move focus: this is only ever reached from the toggle's
  // own click (or a host calling api.panel(), which is the same request said in code).
  function togglePanel() {
    if (!ensureUi()) return;
    if (state.ui.panel.className.indexOf('charter-hidden') >= 0) showPanel({ focus: true });
    else hidePanel({ focus: true });
  }

  // ---- keeping the reviewer's place across a rebuild (Charter #200) ---------------------
  //
  // The teardown is not the bug and is not touched here — see FOCUS for why render() has to destroy and
  // rebuild. What was missing is the other half: putting the reviewer back on the rebuilt counterpart of the
  // control they were on. Without it a keyboard reviewer poised on a block's count badge loses their place
  // because somebody else committed a note, and lands on <body> with nothing to say why.
  //
  // Three sentences the design will not say, each of which would be a worse bug than the one being fixed:
  //
  //   * "focus belongs to Charter". It does not. Restoration happens ONLY for chrome the SDK built, that the
  //     SDK itself just removed, and only while nothing else has claimed focus in the meantime. A render
  //     landing while the reviewer is typing in the composer or reading the plan must not move the caret —
  //     the same call #168 made when it left focus opt-in for every automatic panel open.
  //   * "the element that had focus is the element to restore". It is destroyed; holding the reference across
  //     the rebuild is exactly the stale read #198 documented. What is carried across is a NAME.
  //   * "any landing place beats <body>". It does not. See restoreChromeFocus for the vanished case.

  // Name one rebuildable control, and register the instance this pass built. A caller with nothing stable to
  // name it by passes no key and the control simply does not participate: two cards keyed 'item:' would make
  // the ledger answer for the wrong note, which is the one failure worse than not restoring at all.
  function keyChrome(el, key, fallback, gone) {
    if (!key) return el;
    el[FOCUS] = { key: key, fallback: fallback || '', gone: gone || '' };
    state.focusIndex[key] = el;
    return el;
  }

  // Ask for focus, and CHECK it was taken. A rebuilt control can come back disabled — Jump on a note whose
  // block has just left the plan — and focus() on a disabled element silently does nothing, which would
  // leave the reviewer on <body> with this code believing it had put them back.
  function landChromeFocus(el) {
    if (!el) return false;
    focusChrome(el);
    return document.activeElement === el;
  }

  // Is this element still in the document the reviewer is looking at? The rebuilt page is what decides
  // whether focus was actually taken away, so nothing is inferred from having called clearMarkers.
  function inDocument(el) {
    if (!el) return false;
    if (typeof el.isConnected === 'boolean') return el.isConnected;
    return !!(document.body && document.body.contains(el));
  }

  // What the reviewer is on RIGHT NOW, if it is chrome this render is about to rebuild.
  //
  // Everything else answers null and is never touched afterwards: the composer they are typing in (SDK
  // chrome, but render() does not rebuild it), the panel toggle, a block of the plan, a zoomable diagram.
  // Ownership is read from the construction-time property, so an author's markup cannot present itself as
  // something to hand focus to (#176).
  function takeChromeFocus() {
    var active = document.activeElement;
    if (!active || !isSdkUi(active)) return null;
    var focus = active[FOCUS];
    return focus ? { el: active, focus: focus } : null;
  }

  // ...and put them back on its counterpart, if the rebuild really did take it away.
  //
  // The vanished case — the note was retracted, the block was edited out, so this render built no
  // counterpart and no surviving fallback either. Focus is NOT moved. Every landing place Charter could
  // invent (the toggle, the first badge, the top of the document) is one the reviewer did not ask for, and
  // relocating them there silently is worse for a screen-reader user than the drop itself: they would be
  // told where they are and never told that what they were on is gone. So the browser's own outcome stands,
  // and the absence gets a sentence — #170's rule, one trigger over: Charter has no vocabulary for
  // "this is gone, and that was correct", so it says so rather than leaving it to be inferred. explain() is
  // the existing shape for that, and it deliberately opens the panel WITHOUT taking focus (#168).
  function restoreChromeFocus(taken) {
    if (!taken) return;

    // Still on the page, so the rebuild did not touch it and the browser still has focus exactly where the
    // reviewer left it. Moving it would be a steal, not a repair.
    if (inDocument(taken.el)) return;

    // Something else has claimed focus since it was captured — a composer opening on a saved note, a host
    // driving the page. Whatever it is, it is more recent than what was captured. The browser only falls
    // back to <body> when it removes the focused element, so that is the one state worth repairing.
    var active = document.activeElement;
    if (active && active !== document.body && active !== document.documentElement) return;

    // Read the counterparts BEFORE trying them, because whether they were BUILT is a different fact from
    // whether they took focus — and the two get opposite sentences (Charter #221).
    var target = state.focusIndex[taken.focus.key];
    var fallback = taken.focus.fallback ? state.focusIndex[taken.focus.fallback] : null;

    if (landChromeFocus(target)) {
      emit('focus-restored', { key: taken.focus.key });
      return;
    }
    if (fallback && landChromeFocus(fallback)) {
      emit('focus-restored', { key: taken.focus.fallback });
      return;
    }

    // `landChromeFocus` answers false for TWO different facts, and saying the stronger one for both is a
    // lie the reviewer cannot check:
    //
    //   nothing was built   the note really did leave the list, or the block really did lose its marker.
    //                       ITEM_GONE / BADGE_GONE are TRUE, and are what this path is for.
    //   built, not focused  the control is right there on screen. focus() returns silently when the element
    //                       is disabled, or when it sits in a display:none subtree — the panel hides exactly
    //                       that way. Telling the reviewer their note "is no longer in the list" while it is
    //                       visibly in the list is worse than saying nothing: it is a confident answer that
    //                       contradicts what they can see.
    //
    // This is Charter #217's shape one layer over. There, a probe collapsed "nothing is listening" and "I
    // could not tell" into one null and the caller reported absence. Here a single false collapses "no
    // counterpart" and "a counterpart that would not take focus", and the caller reported absence.
    var built = !!(target || fallback);

    if (built) {
      explain(NOT_FOCUSABLE);
    } else if (taken.focus.gone) {
      explain(taken.focus.gone);
    }

    // `built` is the discriminator, and it is emitted rather than only rendered: it is the one fact that
    // separates the two mechanisms behind #221's intermittent failures, and a post-mortem reading the wire
    // should not have to infer it from a screenshot.
    emit('focus-not-restored', { key: taken.focus.key, built: built });
  }

  // ---- a control that disables itself under the reviewer (Charter #204) ----------------
  //
  // The third route to #168's end state, and the only one the reviewer causes themselves. #168 fixed the
  // panel-toggle route (the control HIDES itself); #200 fixed the rebuild route (the control is REMOVED and
  // replaced). This is the route where the control simply stops being focusable: `disabled` drops focus to
  // <body> exactly as removal does, and #200's repair is structurally blind to it — its first guard returns
  // early while the captured element is still in the document, which here it always is. That early return is
  // right and is not touched.
  //
  // The frequency is what makes it worth its own answer. Reset on the zoom bar disables Reset, so EVERY
  // successful reset drops the reviewer; `−` down to fit disables `−` and Reset together; `+` at the ceiling
  // disables `+`. And "Send to agent" disables itself at the end of the most deliberate gesture in the
  // product, at the exact moment the panel writes the sentence the reviewer now needs to read.
  //
  // THE CONDITION, and why it cannot steal. This fires only when the element BEING DISABLED is the one that
  // holds focus at that instant, and it is the one line that decides everything: there is no focus anywhere
  // else for it to move, so no render — automatic, a teammate's note, a drain, any of them — can pull a
  // caret out of the plan or out of a half-typed composer. That is a strictly tighter test than #200's,
  // which had to name a counterpart and check the browser had really dropped to <body> first. Here the
  // browser has not dropped anything yet: the move happens BEFORE the disable, in the same synchronous turn,
  // so there is no <body> moment at all rather than one that is repaired afterwards.
  //
  // WHERE FOCUS GOES is a real decision, taken per site, and it deliberately differs from #200's ruling for
  // a vanished anchor ("do not move; disclose the absence"). That case had no landing place the reviewer had
  // asked for. This one does: the reviewer is standing IN a control group they are operating, and they got
  // here by their own gesture. Each caller passes an ordered ladder of candidates and the first one that
  // actually takes focus wins — never a guess that focus() succeeded, which is how a rebuilt-but-disabled
  // control would silently leave the reviewer on <body> (see landChromeFocus).
  //
  //   * the zoom bar hands on to the control that still MEANS something — the opposite direction, which can
  //     never be disabled at the same time — and failing that to the zoomable block itself, already a tab
  //     stop with role="group" and a label naming it;
  //   * Send hands on to the panel STATUS LINE, which is where the sentence explaining the new state is
  //     written ("Sent. … The conversation continues in your agent's terminal."). That is #168's precedent
  //     rather than a new rule: a disclosure lands on the region carrying it. sendRound() writes the line
  //     BEFORE it disables the button precisely so this can be true.
  //
  // If no candidate takes focus, nothing is moved and the browser's own outcome stands — #200's ruling,
  // which applies again the moment there is genuinely nowhere to go.
  function disableChrome(el, disabled, landing) {
    if (!el) return;
    var dropping = !!disabled && !el.disabled && isSdkUi(el) && document.activeElement === el;
    if (dropping) {
      var to = handOnFocus(landing);
      // ...and only now. Setting `disabled` first would let the browser drop focus to <body> in between,
      // and a repair afterwards is a different (weaker) contract than never dropping it — #198's "no yield
      // between the teardown and the fix" applied to the one case where the teardown can be reordered.
      emit(to ? 'focus-handed-on' : 'focus-not-handed-on', { from: uiNameOf(el), to: uiNameOf(to) });
    }
    el.disabled = !!disabled;
  }

  function handOnFocus(landing) {
    var candidates = (typeof landing === 'function' ? landing() : landing) || [];
    for (var i = 0; i < candidates.length; i++) {
      if (landChromeFocus(candidates[i])) return candidates[i];
    }
    return null;
  }

  function uiNameOf(el) {
    return (el && el.getAttribute) ? (el.getAttribute(UI_ATTR) || '') : '';
  }

  // ---- the composer: a near-target, dismissible popover (replaces window.prompt, #41) ---

  function closeComposer(reason) {
    var open = state.composer;
    if (!open) return;
    state.composer = null;
    if (open.root && open.root.parentNode) open.root.parentNode.removeChild(open.root);
    dropClass(open.outlined, 'charter-annotate-target');
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

  function openComposerForReply(record, target) {
    showComposer({
      context: 'Replying in the thread on: ' + recordLabel(record),
      note: '',
      target: target,
      saveLabel: 'Reply',
      onSave: function (text) { return replyToNote(record.id, text); }
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

  // A note still in the queue, as `/api/annotations` reports it. That route re-resolves every anchor through
  // AnchorResolution before answering (#78), so its `anchorStatus` is the SAME verdict the drain gives the
  // agent — and it used to be thrown away here and hardcoded null, which left the panel unable to say
  // "orphaned" about a note the server had already called orphaned. The panel then fell back to "is the
  // element on the page?", which says nothing at all about whether Charter can map it to a markdown line: an
  // anchor the assignment pass never produced (an author's own id inside :::custom-html — Charter #166) is
  // right there in the DOM and unmappable, so the card was drawn healthy while the agent got sourceLine null.
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
      anchorStatus: annotation.anchorStatus || null,
      // Review-log only on the wire, so this is null for every pending note — carried rather than hardcoded
      // so there is one shape of record and no second place for the two to drift apart.
      baseStatus: annotation.baseStatus || null,
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

  // Unresolved business: notes nobody has settled. RETRACTED does not count (withdrawn, not left undone) and
  // a note the agent has already taken DOES (delivery and resolution are different axes — #124). One
  // definition, used by the pill (#134) and by the breakdown gate (#145), so the two can never disagree about
  // whether a review is finished.
  function openNoteCount() {
    var records = mergedRecords();
    var n = 0;
    for (var i = 0; i < records.length; i++) {
      if ((records[i].status || 'open') === 'open') n++;
    }
    return n;
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
    return verb + ' by ' + byline(side);
  }

  // ---- attribution (#157) ---------------------------------------------------------------
  //
  // `author` is PROVENANCE, not voice. Charter reads identity from git, an agent has no git identity of its
  // own, and these records travel to teammates by git — so every record an agent writes legitimately carries
  // the human's name. `actor` is the field that says who actually spoke, it has been recorded correctly all
  // along, and the panel simply never read it: an agent's replies rendered under the reviewer's own name.
  //
  // That made `charter reply --as-human` unobservable — the one distinction the CLI asks an agent to be
  // careful about had no effect on the only surface a human reads, and the careless outcome the flag exists
  // to prevent was what you got by default. It misattributes DISAGREEMENT specifically, because a reply is
  // where an agent pushes back, and these logs are permanent in git history.
  function actorOf(entry) {
    return (entry && entry.actor) || 'human';
  }

  // "Agent (via David Maltby)" keeps BOTH facts: who spoke, and whose checkout the record travelled through.
  // Dropping the provenance would be its own small lie — the record really is in that person's log file.
  function byline(entry) {
    var who = (entry && (entry.authorName || entry.authorEmail)) || 'someone';
    return actorOf(entry) === 'human' ? who : 'Agent (via ' + who + ')';
  }

  // The sentence a reviewer gets when the card they were on is not in the new render (#200 / #170).
  // Charter #221 -- what to say when the control IS still on screen but would not take focus. Deliberately
  // does NOT claim anything about where the control went, because it went nowhere: the reviewer can see it.
  // It also does not guess WHY (disabled? hidden? mid-rebuild?), since a wrong reason is worse than none and
  // the honest answer is on the wire in `focus-not-restored`.
  var NOT_FOCUSABLE =
    'That control is still here but could not take keyboard focus just now, so your focus was left where it ' +
    'is rather than moved somewhere you did not ask for. Tab or click to it to carry on.';

  var ITEM_GONE =
    'That note is no longer in the list, so your keyboard focus was left where it is rather than moved ' +
    'somewhere you did not ask for.';

  function buildItem(entry, marking) {
    var record = entry.record;
    // An anchor resolves by EXACT block-id match or it is orphaned (§4.3) — there is no fuzzy re-binding.
    //
    // A DISJUNCTION, deliberately, because the two sources answer different questions and either one alone is
    // blind. The server knows whether the anchor maps to a markdown LINE, which the DOM cannot tell you — an
    // id the assignment pass never produced is present on the page and unmappable (#166). The DOM knows
    // whether the block is on the PAGE, which the server cannot tell you — a note whose element this render
    // does not contain is stranded here whatever the plan file says. So "the server wins outright" would draw
    // a missing block as healthy, and "the DOM wins outright" is what #166 was.
    var orphaned = record.anchorStatus === 'orphaned' || !entry.el;
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
    //
    // The THIRD sentence is earned by the same discipline, one case further down. `baseStatus` is review-log
    // only, so a pending orphan can never reach the first branch — and the second branch is a plain falsehood
    // whenever the block IS still on the page and it is the anchor that cannot be mapped (#166): the reviewer
    // is looking straight at the thing it says is gone. Each sentence therefore states only what its own
    // evidence supports, and none of them says "addressed".
    if (orphaned) {
      var orphan = make('div', 'charter-item-orphan', 'item-orphan');
      orphan.appendChild(make('div', null, 'item-orphan-note',
        record.baseStatus === 'different'
          ? 'The plan has changed since this comment was written.'
          : entry.el
            ? 'This is still on the page, but Charter cannot trace it back to a line of the plan.'
            : 'The block this comment was written on is not in the plan.'));
      if (record.quote) {
        orphan.appendChild(make('div', 'charter-item-quote', 'item-quote',
          '“' + truncate(record.quote, 160) + '”'));
      }
      item.appendChild(orphan);
    }

    // The same duty, one trigger over: retracting a block's LAST live note removes both the accent bar and the
    // count, and until now removed them in silence (#170). The behaviour is correct — a withdrawn comment must
    // not keep marking a block — but it produced exactly the "did I break something?" reading #164 was filed
    // for. Charter has one vocabulary for ANNOTATED and one for HOW MANY, and none at all for "annotated, but
    // not shown on the page", so an absence here is unsignalled by construction unless something says it.
    //
    // Said the way the orphan block above says its own fact: neutral, and never blind. Not the quote, though —
    // an orphan prints one because its block is GONE and the quote is the only way back to it, while this
    // block is still in the plan and the card's target line already names it. Not said on an orphan either:
    // that card has already explained why nothing marks its block, and two competing reasons would be worse
    // than one.
    if (retracted && !orphaned && !(marking && marking[record.anchorId])) {
      item.appendChild(make('div', 'charter-item-unmarked', 'item-unmarked',
        'Withdrawn, and the last open note here — so this block no longer shows a marker.'));
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
        byline(reply) + ': ' +
        (reply.retracted ? '(reply withdrawn by author)' : (reply.body || '')));
      replyEl.setAttribute('data-charter-actor', actorOf(reply));
      item.appendChild(replyEl);
    }

    // The whole card is the selection gesture (#137). The Jump button STAYS: it is the discoverable
    // affordance and the only one that reads as an action, and retiring it would leave the behaviour
    // undiscoverable for anyone who does not think to click the card.
    item.setAttribute('tabindex', '0');
    // ...which makes the card a tab stop, and renderPanel empties the list on every render — so a reviewer
    // reading a card loses it to a teammate's note exactly as a badge does (#200). Keyed by the note's own
    // id, and not at all without one: two cards sharing a key would restore focus to the wrong note.
    var cardKey = record.id ? 'item:' + record.id : '';
    keyChrome(item, cardKey, '', ITEM_GONE);

    // Each control in the row is the same control across a rebuild when it does the same thing to the same
    // note, and it falls back to the CARD. A control routinely disappears by being USED — Resolve is gone
    // the moment the comment is resolved — and the card that carried it is still there and is where the
    // reviewer was; landing there is a restore, not the "it vanished" case.
    function keyed(el, uiName) {
      return cardKey ? keyChrome(el, uiName + ':' + record.id, cardKey, ITEM_GONE) : el;
    }
    item.addEventListener('click', function (ev) {
      // The action row owns its own clicks — a Resolve must not also scroll the document.
      if (ev.target && ev.target.closest && ev.target.closest('.charter-item-actions')) return;
      selectNote(record);
    }, false);
    item.addEventListener('keydown', function (ev) {
      if (ev.key !== 'Enter' && ev.key !== ' ') return;
      if (ev.target && ev.target.closest && ev.target.closest('.charter-item-actions')) return;
      ev.preventDefault();
      selectNote(record);
    }, false);

    var actions = make('div', 'charter-item-actions', 'item-actions');
    var jump = keyed(button('charter-btn', 'item-jump', 'Jump'), 'item-jump');
    jump.disabled = !entry.el || orphaned;
    jump.addEventListener('click', function () { selectNote(record); }, false);
    actions.appendChild(jump);

    // Edit and Delete are the AUTHOR's own: a retract by anyone else is retained and reported by the fold
    // but never applied, so offering the button would only promise something the model refuses.
    if (record.mine && !retracted) {
      var edit = keyed(button('charter-btn', 'item-edit', 'Edit'), 'item-edit');
      edit.addEventListener('click', function () { openComposerForEdit(record, item); }, false);
      var remove = keyed(button('charter-btn', 'item-delete', 'Delete'), 'item-delete');
      remove.addEventListener('click', function () { deleteNote(record.id); }, false);
      actions.appendChild(edit);
      actions.appendChild(remove);
    }

    // Reply, on any committed comment that has not been withdrawn — INCLUDING a resolved one (#158).
    // Reviewers whose only way to move on was "resolve" have settled threads they actually wanted to
    // continue, so refusing them the reply would punish them for the very gap this closes. A reply does not
    // change the status: reopening a settled decision as a side effect of adding a sentence would be a
    // surprising write, and reopen deserves to stay its own act.
    if (record.committed && !retracted) {
      var reply = keyed(button('charter-btn', 'item-reply-btn', 'Reply'), 'item-reply-btn');
      reply.addEventListener('click', function () { openComposerForReply(record, item); }, false);
      actions.appendChild(reply);
    }

    // Resolve is open to anyone — review is collaborative — but only for a committed comment that is not
    // already settled closed or withdrawn.
    if (record.committed && !retracted && record.status !== 'resolved') {
      var resolve = keyed(button('charter-btn', 'item-resolve', 'Resolve'), 'item-resolve');
      resolve.addEventListener('click', function () { resolveNote(record.id); }, false);
      actions.appendChild(resolve);
    }

    item.appendChild(actions);
    return item;
  }

  function renderPanel(entries, marking) {
    var ui = state.ui;
    var list = ui.list;
    while (list.firstChild) list.removeChild(list.firstChild);

    if (entries.length === 0) {
      list.appendChild(make('div', 'charter-panel-empty', 'panel-empty',
        'No notes yet. ' + MODIFIER + '+click a block \u2014 or select some text \u2014 to comment on it.'));
    } else {
      for (var i = 0; i < entries.length; i++) list.appendChild(buildItem(entries[i], marking));
    }

    // A selected note that has since been deleted or retracted out of the list must not leave a
    // dangling anchor outline behind; otherwise re-project the selection onto the rebuilt cards.
    if (state.selectedId && !entryById(state.selectedId)) clearSelection();
    else markSelectedItem();

    ui.title.textContent = 'Review notes (' + entries.length + ')';

    // The collapsed pill's whole job is the state where the panel is SHUT and the reviewer is reading the
    // plan — and in that state the total is the one number that never moves (#134). Resolve everything and
    // `Notes 5` reads exactly as it did when nothing had been dealt with. The count that says "something is
    // waiting on you" was the only one they could not see, and the only way to find it was to open the panel
    // and count badges.
    //
    // A RETRACTED note is not open: it was withdrawn, not left undone. A note that has been drained and
    // badged `sent` IS open — delivery and resolution are different axes (#124), and treating the agent
    // having it as business finished would tell the reviewer the opposite of the truth. A teammate's open
    // comment counts too: the pill describes the plan's state, not authorship.
    var open = openNoteCount();

    // No `(0 open)` when everything is settled — the absence of the clause is the signal.
    ui.toggle.textContent = open > 0
      ? 'Notes ' + entries.length + ' (' + open + ' open)'
      : 'Notes ' + entries.length;
    ui.toggle.setAttribute('data-charter-open-count', String(open));
    ui.toggle.setAttribute(
      'aria-label',
      open > 0
        ? 'Show Charter review notes — ' + open + ' of ' + entries.length + ' still open'
        : 'Show Charter review notes');
  }

  // ---- on-page markers: which blocks already carry a note ------------------------------
  //
  // Elements that must not host an APPENDED badge: makeBadge returns a <button>, and a <button> as a direct
  // child of these violates the content model, so the browser relocates it out of the block. Two different
  // outcomes follow, and the split is the point of #164 — the deny-list used to end the story, which left the
  // blocks that collect the MOST notes (lists and tables) as the only ones showing no count at all.
  //
  //   BADGE_RAILED — reachable as an anchor, so the count matters and is drawn on a SIBLING rail instead:
  //     TABLE  a top-level table is the block anchor; its .table-scroll wrapper is anchor-invisible (#68)
  //     UL/OL  the list's own id still answers a click on its padding, even now that each <li> anchors (#164)
  //     HR     a thematic break is a top-level block with a stable id like any other
  //
  //   Denied outright, no rail, because none of them can BE an anchor element — anchorElement could never
  //   return one, so a rail for them would be dead code asserting a case that cannot arise:
  //     THEAD/TBODY/TFOOT/TR  the renderer stamps ids on top-level blocks and on sub-anchor rows, and
  //                           SubAnchors yields only list items — no <tr> is ever stamped
  //     IMG                   a top-level image is inline content, so it renders inside the <p> that anchors
  //     BR                    likewise inline, and never carries an attribute of its own
  //
  // DL is deliberately absent: BlockModel enables no definition-list extension, so Markdig cannot emit one.
  //
  // These two lists are the CONTENT-MODEL half only. A block can also need a rail because it is its own
  // scroll box, which no tag list can decide — see badgePlacement, which is the actual rule.
  var BADGE_DENIED = ['TABLE', 'THEAD', 'TBODY', 'TFOOT', 'TR', 'UL', 'OL', 'HR', 'IMG', 'BR'];
  var BADGE_RAILED = ['TABLE', 'UL', 'OL', 'HR'];

  // Where one block's badge goes: 'append' inside the block, 'rail' on a sibling rail before it, or 'none'.
  //
  // There are TWO independent reasons a block cannot host an appended badge, and the deny-list above is only
  // the first of them:
  //
  //   CONTENT MODEL — makeBadge returns a <button>, which is not a legal child of TABLE/UL/OL/HR, so the
  //   browser relocates it out of the block (#164).
  //
  //   ITS OWN SCROLL BOX — a non-diagram <pre> is `overflow: auto` in charter.css and gains
  //   `position: relative` the moment it is annotated, which makes it both the containing block for the badge
  //   AND the scroll container that badge lives in. An absolutely positioned box inside a scroll container
  //   translates with the content, so scrolling right to read a long line carried the badge clean out of the
  //   viewport: measured on the served page at scrollLeft 2543, a badge that started at x=863 ended at
  //   x=-1680 and answered for nothing under its own centre (#165). That is #51's lesson again — and a <pre>
  //   has no wrapper to hoist past, because the <pre> IS the scroll box, so the rail goes before it.
  //
  // pre.mermaid is excluded, exactly as charter.css excludes it from the scroll regions (`pre:not(.mermaid)`).
  // A diagram is not a scroll box as rendered; it becomes one only under #51's review-time pan/zoom, and
  // pinDiagramChrome already pushes its in-block badge back by scrollLeft/scrollTop on every scroll.
  function badgePlacement(el) {
    if (BADGE_RAILED.indexOf(el.tagName) >= 0) return 'rail';
    if (BADGE_DENIED.indexOf(el.tagName) >= 0) return 'none';
    if (el.tagName === 'PRE' && !(el.classList && el.classList.contains('mermaid'))) return 'rail';
    return 'append';
  }

  // What a railed badge's accessible name calls the block it precedes. The rail reads BEFORE its block, so
  // the name has to point forward — "on this block" would be a lie about which way to look. Each word is
  // DISTINCT, because two badges on one page announcing identically leave a screen-reader user unable to tell
  // which block either belongs to — the defect the old shared "N review note(s) on this block" name had for
  // every badge at once. A numbered list is not a bullet list, so it does not borrow that word.
  var RAIL_SUBJECT = {
    TABLE: 'table', UL: 'list', OL: 'numbered list', HR: 'horizontal rule', PRE: 'code block'
  };

  // Undo the last marker pass — EXACTLY it, and nothing that merely resembles it.
  //
  // This used to sweep the whole document for `.charter-annotation-badge`, `.charter-badge-rail`,
  // `.charter-has-annotations` and `[data-charter-annotation-count]` and destroy every match: elements
  // removed, classes and attributes stripped. Every one of those names is a name a :::custom-html author can
  // write — a plan DOCUMENTING Charter is the obvious case — and the escape hatch's one promise is that the
  // author's markup is rendered as written. So an author's element was deleted from the served page, and the
  // sweep runs on EVERY pass, annotated or not (renderMarkers calls this first), which means every SSE frame:
  // a teammate's note arriving by pull was enough to do it (Charter #176).
  //
  // A narrower selector would not have fixed it, because there is no name an author cannot write. The fix is
  // to stop asking the document what the SDK did and to REMEMBER it instead — the same rule make() applies to
  // ownership. A block that has since left the DOM simply has nothing to undo, which the parentNode / element
  // guards already handle.
  //
  // Rails first: each one OWNS the badge inside it, so removing the rail removes its badge with it. Without
  // that a dispose() would leave empty rails behind and the document would stop being indistinguishable from
  // the exported artifact's.
  function clearMarkers() {
    var marks = state.marks;
    state.marks = newMarks();

    for (var r = marks.created.length - 1; r >= 0; r--) {
      var node = marks.created[r];
      if (node && node.parentNode) node.parentNode.removeChild(node);
    }
    for (var i = 0; i < marks.classed.length; i++) {
      dropClass(marks.classed[i], 'charter-has-annotations');
    }
    // Tracked apart from the class since #167: the accent bar is painted on the block's outermost box of its
    // own (.table-scroll for a top-level table) while the COUNT stays on the anchor element, so the two are
    // not always the same node.
    for (var c = 0; c < marks.counted.length; c++) {
      if (marks.counted[c]) marks.counted[c].removeAttribute('data-charter-annotation-count');
    }
  }

  // How many notes are MARKING each block right now, and in what order the blocks were met.
  //
  // A withdrawn comment must not keep badging its block — the thread survives in the panel, but the block no
  // longer carries an open note. This is the ONE place that rule is applied, so the panel can state what the
  // page is showing rather than re-derive it and drift (#170).
  function markingCounts(entries) {
    var order = [];
    var counts = Object.create(null);
    for (var i = 0; i < entries.length; i++) {
      var id = entries[i].record.anchorId;
      if (!id || !entries[i].el || entries[i].record.status === 'retracted') continue;
      if (counts[id] === undefined) { counts[id] = 0; order.push(id); }
      counts[id]++;
    }
    return { order: order, counts: counts };
  }

  function renderMarkers(marking) {
    clearMarkers();
    var order = marking.order;
    var counts = marking.counts;

    var rails = [];
    for (var k = 0; k < order.length; k++) {
      var anchorId = order[k];
      var el = anchorElement(anchorId);
      if (!el) continue;
      // Recorded as it is applied, never re-derived: clearMarkers undoes this ledger and only this ledger.
      var box = markerBox(el);
      box.classList.add('charter-has-annotations');
      state.marks.classed.push(box);
      el.setAttribute('data-charter-annotation-count', String(counts[anchorId]));
      state.marks.counted.push(el);
      var placement = badgePlacement(el);
      if (placement === 'append') {
        var badge = makeBadge(anchorId, counts[anchorId], notePhrase(counts[anchorId]) + ' on this block');
        el.appendChild(badge);
        state.marks.created.push(badge);
      } else if (placement === 'rail') {
        var rail = mountBadgeRail(el, anchorId, counts[anchorId]);
        if (rail) { rails.push(rail); state.marks.created.push(rail.rail); }
      }
    }

    // A badge inside a PANNED diagram is a fresh element with no scroll compensation on it yet, so it
    // would render at the content's offset instead of pinned to the block's corner (Charter #51).
    for (var v = 0; v < state.diagrams.length; v++) pinDiagramChrome(state.diagrams[v]);

    // LAST, and after pinDiagramChrome: a rail badge's offset is MEASURED from two live rects, so anything
    // that reflows the page between placing it and measuring it puts the badge somewhere it does not belong.
    for (var w = 0; w < rails.length; w++) positionRailBadge(rails[w]);

    emit('markers-rendered', { blocks: order.length, rails: rails.length });
  }

  // The element the rail is inserted BEFORE, or null when this block must stay unbadged.
  //
  // Climb from the anchor while the parent carries no anchor of its own, stopping at the first ancestor that
  // does; rail only if that climb lands on a direct child of <body>. Two things fall out of one rule:
  //
  //   * a top-level <table> climbs through its anchor-invisible .table-scroll wrapper to a body child, so the
  //     rail lands OUTSIDE the horizontal scroll box — which is what stops the badge riding away with
  //     scrollLeft, the Charter #51 lesson;
  //   * a :::custom-html block is `div.custom-html`, a body child, so it climbs to itself and badges in
  //     place — and it is the only element inside that block that CAN be an anchor, because everything in
  //     `.custom-html-scroll` is inside an opaque region (see isAnchorElement). Nothing here needs to know
  //     that: an author id can no longer reach this function at all.
  function railMount(el) {
    var node = el;
    while (node && node.parentElement && node.parentElement !== document.body) {
      var parent = node.parentElement;
      if (parent.id ||
          parent.hasAttribute('data-charter-anchor') ||
          parent.hasAttribute('data-anchor')) {
        return null;
      }
      node = parent;
    }
    return (node && node.parentElement === document.body) ? node : null;
  }

  // The box that PAINTS the "this block is annotated" accent bar — not always the anchor element.
  //
  // The bar is an inset box-shadow, which is a decoration of the element's own border box, and a top-level
  // <table> lives INSIDE .table-scroll. So on a table scrolled right the bar had already travelled out of the
  // visible region while the rail's badge stayed pinned: measured at scrollLeft 4377, the <table>'s left edge
  // sat at x=-4323 against a scroll box whose visible left edge was x=54. Two halves of one signal in two
  // coordinate spaces (#167).
  //
  // Painting it on the scroll REGION answers the second half of the same report for free. With
  // `border-collapse: collapse` a <table> has no continuous left edge of its own to decorate, so the inset
  // shadow came out as one segment per body row and none at all on the header — four dashes that read as row
  // striping rather than as an annotation marker. A plain <div> paints one unbroken bar down the whole block.
  //
  // railMount is REUSED rather than re-derived: "climb to the outermost box that is still this block, stopping
  // at any ancestor that owns an anchor of its own" is exactly the question both callers ask. It answers `el`
  // itself for every block the renderer does not wrap, and null where the climb would pass an anchored
  // ancestor (a list item, a diff line) — which is precisely where the bar belongs on the element itself.
  function markerBox(el) {
    return railMount(el) || el;
  }

  // Insert the rail as the mount's PREVIOUS SIBLING and hang the badge in it.
  //
  // A sibling, never a wrapper. UNANCHORABLE includes [data-charter-ui] and closestAnchored tests
  // el.closest(UNANCHORABLE) BEFORE it walks for an id — so a chrome-marked ANCESTOR would make every
  // Alt+click inside the block resolve to null, silently killing annotation on exactly the blocks this fix
  // exists to serve. Reparenting the block would also destroy .table-scroll's scrollLeft and any focus inside
  // it, and render() runs on every SSE frame, so a teammate's note arriving by pull would blow away a
  // scrolled, focused table. The rail carries no id, no data-anchor and no data-charter-anchor, and is never
  // an ancestor of plan content.
  function mountBadgeRail(el, anchorId, count) {
    var mount = railMount(el);
    if (!mount || !mount.parentNode) return null;
    var badge = makeBadge(anchorId, count, railLabel(el.tagName, count));
    var rail = make('div', 'charter-badge-rail', 'badge-rail');
    rail.appendChild(badge);
    mount.parentNode.insertBefore(rail, mount);
    return { rail: rail, mount: mount, badge: badge };
  }

  // Place the badge against the top-right corner of the block that FOLLOWS the rail. The rail has zero
  // height, so the vertical offset has to be measured rather than inherited: `top` is computed and `bottom`
  // is pinned to auto, because leaving the inherited `top: 2px` standing alongside any `bottom` squashes the
  // button to a sliver.
  function positionRailBadge(placed) {
    var mountRect = placed.mount.getBoundingClientRect();
    var railRect = placed.rail.getBoundingClientRect();
    var badgeHeight = placed.badge.getBoundingClientRect().height;
    // A block shorter than the badge has no corner to hang one off — centre it on the block instead. That is
    // the <hr> case: charter.css gives a thematic break a real 12px box (#169), deliberately kept below the
    // badge's own height so this branch, and not the corner branch, is the one that governs a rule.
    var within = mountRect.height < badgeHeight ? (mountRect.height - badgeHeight) / 2 : 2;
    placed.badge.style.top = ((mountRect.top - railRect.top) + within) + 'px';
    placed.badge.style.bottom = 'auto';
    nudgePastInnerBadges(placed);
  }

  // A note on the whole list and a note on its first bullet both want that same top-right corner, now that
  // each <li> anchors in its own right (#164 milestone 1). Left alone the item's in-block badge lands ON TOP
  // of the rail's and the rail badge cannot be clicked at all — the affordance is drawn and still unusable,
  // which is the failure this whole fix exists to end. Shift the container's badge left past whatever it
  // covers, so the pair reads [list][item] outward from the block.
  function nudgePastInnerBadges(placed) {
    var mine = placed.badge.getBoundingClientRect();
    var inner = placed.mount.querySelectorAll('.charter-annotation-badge');
    var shift = 0;
    for (var i = 0; i < inner.length; i++) {
      var other = inner[i].getBoundingClientRect();
      if (other.right <= mine.left || other.left >= mine.right) continue;
      if (other.bottom <= mine.top || other.top >= mine.bottom) continue;
      shift = Math.max(shift, (mine.right - other.left) + 4);
    }
    if (shift > 0) placed.badge.style.right = (2 + shift) + 'px';
  }

  function notePhrase(count) {
    return count + ' review note' + (count === 1 ? '' : 's');
  }

  function railLabel(tagName, count) {
    return notePhrase(count) + ' on the following ' + (RAIL_SUBJECT[tagName] || 'block');
  }

  // The sentence a reviewer gets when the badge they were on is not in the new render (#200 / #170).
  var BADGE_GONE =
    'That block no longer shows a review marker, so your keyboard focus was left where it is rather than ' +
    'moved somewhere you did not ask for.';

  function makeBadge(anchorId, count, label) {
    var badge = button('charter-annotation-badge', 'badge', String(count));
    badge.setAttribute('data-anchor-id', anchorId);
    badge.setAttribute('aria-label', label);
    // The same badge across a rebuild is the one counting the same block — appended or railed alike, since
    // renderMarkers builds exactly one per anchor. No fallback: a block either shows a marker or it does
    // not, and there is no enclosing chrome that would mean anything to land on (#200).
    keyChrome(badge, 'badge:' + anchorId, '', BADGE_GONE);
    badge.addEventListener('click', function (ev) {
      ev.preventDefault();
      ev.stopPropagation();
      showPanel();
      focusPanelEntry(anchorId);
    }, false);
    return badge;
  }

  // Reveal EVERY note on the badged block, not just the first (#164). A badge reading 3 that selected note 1
  // of 3 and said nothing about the other two promised a group and delivered a single card — and on a list or
  // a table, where one anchor routinely collects several notes, that is the normal case rather than the edge.
  // orderedEntries sorts by anchor element and falls back to arrival order within one element, so a block's
  // notes are CONTIGUOUS in the panel: revealing the last and then the first brings the whole run into view
  // where it fits, and the top of it where it does not.
  function focusPanelEntry(anchorId) {
    if (!state.ui) return;
    var quoted = String(anchorId).replace(/["\\]/g, '\\$&');
    var items = [];
    try { items = state.ui.list.querySelectorAll('[data-anchor-id="' + quoted + '"]'); } catch (e) { items = []; }
    if (!items.length) return;
    var first = items[0];
    var last = items[items.length - 1];
    if (last.scrollIntoView) last.scrollIntoView({ block: 'nearest' });
    if (first.scrollIntoView) first.scrollIntoView({ block: 'nearest' });
    // Select the first, but do NOT jump: the reviewer clicked the badge, so they are already looking at the
    // anchor. Scrolling the content back to it would be the pane arguing with the gesture (#137). Selection
    // lands on the first so an arrow-key walk continues through the rest of the group.
    var entry = entryById(first.getAttribute('data-annotation-id'));
    if (entry) selectNote(entry.record, { jump: false });
    // ...and focus it (#168). A badge press is a disclosure too — it opens the panel — and it opens it at ONE
    // named note, so this is the one entry point where the panel container is the wrong landing place and the
    // card is the right one. The card is tabindex="0" and the arrow walk (#137) continues from the selection,
    // so the rest of the block's group is one keystroke away. The scrollIntoView calls above already put it in
    // view, which is why focusChrome refuses to scroll for it.
    focusChrome(first);
  }

  function render() {
    if (!ensureUi()) return;
    // Read BEFORE anything is torn down, and only ever from chrome this pass is about to rebuild. The index
    // is then emptied, so what a control is restored to is what THIS pass built and nothing older (#200).
    var held = takeChromeFocus();
    state.focusIndex = Object.create(null);
    var entries = orderedEntries();
    // Computed ONCE and handed to both halves: the panel says what the page is showing, so it must read the
    // same numbers the markers were painted from rather than a second opinion about them (#170).
    var marking = markingCounts(entries);
    renderPanel(entries, marking.counts);
    renderMarkers(marking);
    syncSendButton();
    // Last, and inside the same synchronous turn as the teardown that took the focus away — #198's rule
    // applied to focus rather than to layout: nothing may yield between removing the element and putting the
    // reviewer back, or a keystroke arrives at <body>.
    restoreChromeFocus(held);
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

  // ---- selection (#137) ----------------------------------------------------------------
  //
  // Clicking a badge already jumped content→note; note→content needed a small secondary button,
  // so the link was bidirectional in capability but not in gesture. Selecting a card now does what
  // the Jump button does — and keeps doing it, which the transient flash cannot.
  //
  // `jump: false` is the anti-ping-pong lever. A badge click has already put the reviewer where the
  // anchor is; selecting the matching card must mark it, not scroll the content back out from under
  // them. The two panes agree on WHAT is selected without arguing about WHERE to scroll.
  function selectNote(record, opts) {
    if (!record) return;
    opts = opts || {};
    var changed = state.selectedId !== record.id;
    state.selectedId = record.id || '';

    var el = anchorElement(record.anchorId);
    markSelectedAnchor(el);
    markSelectedItem();

    // An orphan's card already states, in its own words, that its block is not in the plan. The rule
    // it must obey is only that selecting it never scrolls nowhere and pretends that was a jump.
    if (!el) {
      if (changed) emit('annotation-selected', { id: record.id, anchor: 'orphaned' });
      return;
    }

    // Never yank the viewport out from under someone mid-sentence. Marking is still correct — it is
    // the SCROLL that would be rude — so selection lands and the jump is simply skipped.
    var composing = !!(state.composer && hasDraft());
    if (opts.jump === false || composing) {
      if (changed) emit('annotation-selected', { id: record.id, anchor: 'resolved', jumped: false });
      return;
    }

    jumpTo(record);
    // Persistent, unlike jumpTo's own 1600ms overlay: a selected text range stays painted for as long
    // as the note stays selected. drawOverlay with no duration registers no timer, and the existing
    // onViewportChange redraw keeps it on the text through scrolling.
    if (record.kind === KIND.textRange && record.quote) {
      var range = findQuoteRange(el, record.quote);
      if (range) drawOverlay(range);
    }
    if (changed) emit('annotation-selected', { id: record.id, anchor: 'resolved', jumped: true });
  }

  function clearSelection() {
    if (!state.selectedId) return;
    state.selectedId = '';
    markSelectedAnchor(null);
    markSelectedItem();
    clearOverlay();
  }

  // The anchor mark lives on the plan's own element, so it is a CLASS toggle and nothing else — the
  // same discipline flash() and the overlay follow (§ never mutate the plan's DOM).
  function markSelectedAnchor(el) {
    dropClass(state.selectedAnchorEl, 'charter-anchor-selected');
    state.selectedAnchorEl = el || null;
    if (el && el.classList) el.classList.add('charter-anchor-selected');
  }

  // Re-applied after every render(): the panel is rebuilt from scratch each time, so selection has to
  // be re-projected onto the new cards rather than living in them.
  function markSelectedItem() {
    if (!state.ui || !state.ui.list) return;
    var items = state.ui.list.querySelectorAll('.charter-item');
    for (var i = 0; i < items.length; i++) {
      var el = items[i];
      var mine = state.selectedId && el.getAttribute('data-annotation-id') === state.selectedId;
      if (!mine) { el.setAttribute('data-charter-selected', 'false'); continue; }
      el.setAttribute('data-charter-selected',
        el.getAttribute('data-charter-orphan') === 'true' ? 'orphaned' : 'true');
    }
  }

  // Arrow keys walk the rendered order and select as they go, so a review pass is drivable from the
  // keyboard. Delegated to the list so it survives the panel being re-rendered under it.
  function moveSelection(delta) {
    if (!state.ui || !state.ui.list) return;
    var items = state.ui.list.querySelectorAll('.charter-item');
    if (!items.length) return;
    var at = -1;
    for (var i = 0; i < items.length; i++) {
      if (items[i].getAttribute('data-annotation-id') === state.selectedId) { at = i; break; }
    }
    var next = at < 0 ? (delta > 0 ? 0 : items.length - 1) : at + delta;
    if (next < 0 || next >= items.length) return;   // stop at the ends rather than wrapping
    var id = items[next].getAttribute('data-annotation-id');
    var entry = entryById(id);
    if (!entry) return;
    if (items[next].scrollIntoView) items[next].scrollIntoView({ block: 'nearest' });
    if (items[next].focus) items[next].focus();
    selectNote(entry.record);
  }

  function entryById(id) {
    if (!id) return null;
    var entries = orderedEntries();
    for (var i = 0; i < entries.length; i++) {
      if (entries[i].record && entries[i].record.id === id) return entries[i];
    }
    return null;
  }

  function flash(el) {
    if (state.flashTimer) window.clearTimeout(state.flashTimer);
    dropClass(state.flashed, 'charter-anchor-flash');
    el.classList.add('charter-anchor-flash');
    state.flashed = el;
    state.flashTimer = window.setTimeout(function () {
      dropClass(state.flashed, 'charter-anchor-flash');
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
  // `pre.mermaid` is a CLASS, and a :::custom-html body is free to carry it — so this scan is narrowed the
  // same way the anchor walk is, by the monotone containment predicate: a <pre class="mermaid"> inside an
  // opaque region is the author's markup, not a Charter diagram, and giving it a zoom bar, a tab stop, a
  // role and a rewritten <svg> width would be the SDK editing a document it does not own (Charter #177's
  // sibling; the renderer's Mermaid bootstrap is narrowed by the same rule). Forgery cannot widen the set:
  // the real region encloses the whole body, so a forged inner one only ever adds an ancestor match.
  function scanDiagrams() {
    if (typeof document.querySelectorAll !== 'function') return;
    var blocks = document.querySelectorAll(DIAGRAM_BLOCK);
    for (var i = 0; i < blocks.length; i++) {
      if (!insideOpaqueRegion(blocks[i])) watchDiagram(blocks[i]);
    }
  }

  function watchDiagram(block) {
    syncDiagram(block);
    if (typeof MutationObserver !== 'function' || block.charterDiagramObserver) return;
    var observer = new MutationObserver(function () { syncDiagram(block); });
    try { observer.observe(block, { childList: true }); } catch (e) { return; }
    block.charterDiagramObserver = observer;
    state.diagramObservers.push({ block: block, observer: observer });
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
      // the very view being used. An EXPANDED view (Charter #234) is the same case for the same reason: the
      // diagram is being shown at viewport width on purpose, and releasing it here would take the bar, the
      // tab stop and the only way back out with it.
      if (view.scale > 1 || view.expanded) return;
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
      expanded: false,     // Charter #234 — this diagram is currently filling the viewport
      bar: null, level: null, hint: null, zoomOut: null, zoomIn: null, reset: null, expand: null,
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
    // Charter #234's control. With the hint below it is the WHOLE of expand mode's discovery surface: a
    // keyboard shortcut into the view was offered in review and refused, so there is no chord in (Escape only
    // ever LEAVES). Built exactly like its siblings — a real <button>, a data-charter-ui name, no id — so
    // Enter/Space, the tab stop and the browser's own focus handling come for free, and so closestAnchored
    // still refuses it as a note target (#166, where SDK chrome with an id captured the block's own anchor).
    view.expand = button('charter-btn charter-zoom-btn charter-expand-btn', 'diagram-expand', '');
    syncExpandControl(view);
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
    view.expand.addEventListener('click', function (ev) {
      ev.preventDefault(); setExpanded(view, !view.expanded);
    }, false);

    bar.appendChild(view.zoomOut);
    bar.appendChild(view.level);
    bar.appendChild(view.zoomIn);
    bar.appendChild(view.reset);
    bar.appendChild(view.expand);
    bar.appendChild(view.hint);

    view.el.appendChild(bar);
    view.bar = bar;
  }

  function syncZoomBar(view) {
    if (!view.bar) return;
    var atFit = view.scale <= DIAGRAM_ZOOM.min + 0.001;
    var atCeiling = view.scale >= view.maxScale - 0.001;
    view.level.textContent = Math.round(view.scale * 100) + '%';
    // Charter #204 — every one of these three can disable the very button the reviewer just pressed, and
    // pressing Reset does it EVERY time it works. The hand-on ladders below are ordered by what still means
    // something at the new scale: at fit the only direction left is in, at the ceiling the only direction
    // left is out, and the two ends can never both be reached at once (ceilingFor floors maxScale at 2), so
    // the first candidate is always live. The block itself is the backstop — a tab stop with role="group"
    // and a label that names the diagram, which is where the reviewer actually is.
    //
    // Everything legal at the NEW scale is re-enabled before anything is taken away, or a hand-on could be
    // offered a button that is still disabled and fall through to the backstop for no reason. Reset from
    // the ceiling is exactly that case: `+` becomes legal again in the very pass that takes `Reset` away.
    if (!atFit) { view.zoomOut.disabled = false; view.reset.disabled = false; }
    if (!atCeiling) { view.zoomIn.disabled = false; }
    disableChrome(view.zoomOut, atFit, function () { return [view.zoomIn, view.el]; });
    disableChrome(view.reset, atFit, function () { return [view.zoomIn, view.el]; });
    disableChrome(view.zoomIn, atCeiling, function () { return [view.zoomOut, view.reset, view.el]; });
    syncExpandControl(view);
    // Progressive disclosure: name the gesture that is USEFUL right now, not the whole vocabulary. Charter
    // #234 adds a third state to a slot that only ever holds one string, so the precedence is settled here:
    //
    //   * PANNING WINS over expand once the reviewer has zoomed. Expand answers "I cannot read this at the
    //     size the column gives it", which is the question being asked BEFORE anything is touched; a reviewer
    //     who has zoomed in has already answered it their own way and panning is what they need next. The
    //     control stays one press away in the bar either way — the hint is a discovery route, not the
    //     affordance.
    //   * While EXPANDED, expand is behind them: the useful pair is the zoom gesture and the way out.
    //
    // isZoomable() is only meaningful at fit, which is the one state it is asked in: zooming widens the <svg>
    // PAST its intrinsic width, so it reports false on exactly the diagram the hint would be about.
    view.hint.textContent = !atFit
      ? 'drag or arrow keys to pan'
      : (view.expanded
          ? 'Ctrl+scroll to zoom, Esc to close'
          : (isZoomable(view.svg) ? 'Expand for a full-screen view' : 'Ctrl+scroll to zoom'));
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

  // ---- expand mode (Charter #234) -------------------------------------------------------
  //
  // Zooming a two-subgraph diagram inside the review column trades one problem for another: the labels
  // become legible and the reviewer pans blind through a 640px window. Expand mode gives that diagram the
  // whole viewport instead, which is the cheap half of the answer — #51 already did the expensive one, since
  // widening the <svg> rather than transforming it means there is no coordinate frame of Charter's own and
  // "make the container bigger" is genuinely all this is.
  //
  // Everything about the mechanism lives in the stylesheet's `.charter-expand` rule; the code below only ever
  // toggles that class and re-measures what the class changed.

  // The control names what it will DO next, the way the panel toggle does — never a state attribute beside a
  // label that already says the same thing, which announces twice.
  function syncExpandControl(view) {
    if (!view.expand) return;
    view.expand.textContent = view.expanded ? 'Close' : 'Expand';
    view.expand.setAttribute(
      'aria-label',
      view.expanded ? 'Close the expanded diagram' : 'Expand the diagram to fill the screen');
  }

  function expandedDiagram() {
    for (var i = 0; i < state.diagrams.length; i++) {
      if (state.diagrams[i].expanded) return state.diagrams[i];
    }
    return null;
  }

  // The width the <svg> is drawn at with NO zoom applied — re-read after the block's own box changes size,
  // because `baseWidth` is what every zoom level is multiplied FROM. Left stale, the first `+` pressed inside
  // an expanded view would draw the diagram SMALLER than the fit it is already showing.
  //
  // Measured by clearing the zoom's two inline properties, reading, and putting them straight back. The read
  // forces layout synchronously and both writes land in the same task, so nothing is ever PAINTED at the
  // intermediate size.
  function remeasureDiagram(view) {
    var svg = view.svg;
    var width = svg.style.width;
    var maxWidth = svg.style.maxWidth;
    svg.style.width = '';
    svg.style.maxWidth = '';
    view.baseWidth = svg.getBoundingClientRect().width || view.baseWidth;
    svg.style.width = width;
    svg.style.maxWidth = maxWidth;
    view.maxScale = ceilingFor(svg, view.baseWidth);
  }

  // Enter or leave expand mode for ONE diagram. In place: the block keeps its parent, its anchor, its tab
  // stop, its bar and its zoom level, and nothing else on the page is hidden, moved or removed.
  function setExpanded(view, expanded) {
    expanded = !!expanded;
    if (!!view.expanded === expanded) return;

    // One at a time. Two viewport-filling boxes would stack, and the one underneath would be unreachable
    // while still reporting itself expanded.
    if (expanded) {
      var other = expandedDiagram();
      if (other && other !== view) setExpanded(other, false);
    }

    view.expanded = expanded;
    if (expanded) view.el.classList.add('charter-expand');
    else view.el.classList.remove('charter-expand');

    // `restingHeight` is deliberately NOT re-measured: it is the height this diagram has IN THE COLUMN, and
    // it is what stops the first zoom after a collapse shrinking the reading window to nothing.
    remeasureDiagram(view);
    view.scale = clampScale(view, view.scale);
    // A zoomed <svg> carries an inline width computed from the OLD base, so it has to be rewritten. At fit
    // there is nothing written to rewrite and the bar is all that needs to catch up.
    if (view.scale > DIAGRAM_ZOOM.min) applyZoom(view);
    else syncZoomBar(view);

    // The block's box just changed by most of the viewport, so everything below it moved. The transient text
    // highlight is painted in viewport coordinates from a Range and has to be repainted for exactly the
    // reason a scroll or a resize repaints it.
    onViewportChange();
    emit('diagram-expanded', { anchorId: anchorIdOf(view.el), expanded: expanded });
  }

  // Escape LEAVES an expanded diagram — it is the first thing anyone presses to get out of something that
  // took the screen, and a view with no keyboard exit is a trap. It is an EXIT and never a way in.
  //
  // Registered on the document in the BUBBLE phase, deliberately. The composer already handles Escape and
  // calls stopPropagation(), so a note being written inside an expanded view swallows the first press and
  // closes itself, and the second press leaves the view — the precedence a reviewer expects, for free. A
  // capture-phase listener here would reach PAST the composer and do both at once: the reviewer cancelling a
  // note is thrown out of the diagram they were annotating AND loses what they typed, and both halves of that
  // are silent.
  function onExpandKeyDown(ev) {
    if (!ev || ev.key !== 'Escape') return;
    if (ev.altKey || ev.ctrlKey || ev.metaKey || ev.shiftKey) return;
    var view = expandedDiagram();
    if (!view) return;
    ev.preventDefault();
    setExpanded(view, false);
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

    // An expanded diagram comes back to the column first (Charter #234). A released view is one that has
    // been put back to the markup the renderer emitted, and a `position: fixed` <pre> covering the viewport
    // is very much something the SDK would have left behind (invariant 1).
    view.expanded = false;
    view.el.classList.remove('charter-expand');

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
    // Each watch is retired through the block it was armed on, rather than by re-querying `pre.mermaid`
    // across the document: the class is one an author can write, and a dispose has no business reaching
    // into markup the SDK never touched (Charter #176's rule, same as clearMarkers').
    for (var i = 0; i < state.diagramObservers.length; i++) {
      var watch = state.diagramObservers[i];
      try { watch.observer.disconnect(); } catch (e) { /* ignore */ }
      watch.block.charterDiagramObserver = null;
    }
    state.diagramObservers = [];
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

    // A rendered :::question is the one block a reviewer can point straight at and not annotate — it is
    // native controls, and a note competing with the answer the block exists to collect would be worse than
    // no note. That refusal is deliberate; being MUTE about it was not (Charter #178). The gesture produced
    // no composer, no outline and no message, which leaves a reviewer unable to tell a rule from a bug.
    //
    // Default is prevented along with it, so the same Alt+click cannot half-work by ticking a radio while
    // refusing the note.
    if (refusesNotes(ev.target)) {
      ev.preventDefault();
      explain('A question block is answered with its own controls, so it cannot take a note. ' +
              'Answer it here, or comment on a block beside it.');
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
    // Through questionForms, so an author's own <form data-question-id="…"> with an enabled submit button
    // cannot look like a half-made decision and defer the reviewer's live reload for the life of the page.
    var forms = questionForms();
    for (var i = 0; i < forms.length; i++) {
      var button = forms[i].querySelector(SUBMIT_SELECTOR);
      if (button && !button.disabled) return true;
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
      // BUBBLE phase, unlike every listener above it — see onExpandKeyDown for why the composer has to be
      // able to swallow the Escape that closes it.
      document.addEventListener('keydown', onExpandKeyDown, false);
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
    hydrateSession();
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
      document.removeEventListener('keydown', onExpandKeyDown, false);
    }
    unwireQuestionForms();
    if (state.ageTicker) { window.clearInterval(state.ageTicker); state.ageTicker = 0; }
    // Give the column back: a disposed SDK must leave the document indistinguishable from the exported
    // artifact's, layout included.
    document.documentElement.classList.remove('charter-reserved');
    // Every pan/zoom view is torn down to the markup the renderer emitted — inline styles cleared, classes,
    // tab stop, role and label removed — so a disposed SDK leaves the block indistinguishable from the
    // exported artifact's.
    disposeDiagrams();
    if (state.events) {
      try { state.events.close(); } catch (e) { /* ignore */ }
      state.events = null;
    }
    closeComposer(null);
    // The selection outline sits on the PLAN's own element and, unlike flash(), no timer will ever take
    // it off. A disposed SDK must leave the document indistinguishable from the exported artifact's.
    clearSelection();
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
    state.queueWrites = 0;
    state.log = { comments: [], diagnostics: [], unreadable: [], selfEmail: null };
    state.round = { submitted: false, pending: { annotations: 0, answers: 0 } };
    state.staleQueue = null;
    state.staleQueueShown = false;
    state.selectedId = '';
    state.selectedAnchorEl = null;
    // The chrome it indexed has just been removed, so keeping the map would hold detached nodes alive for
    // the life of the page — and a disposed SDK must leave nothing of itself behind (invariant 1).
    state.focusIndex = Object.create(null);
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
