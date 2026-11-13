(() => {
  'use strict';

  const VERSION = '2.2.0';
  const htmlRoot = document.documentElement;
  const isExtensionWorld =
    typeof chrome !== 'undefined' && Boolean(chrome && chrome.runtime && chrome.runtime.id);

  if (htmlRoot) {
    htmlRoot.dataset.ommSafariBounce = VERSION;
    if (isExtensionWorld) {
      htmlRoot.dataset.ommExtActive = '1';
    }
  }

  // Prevent double-binding inside same JS world OR across isolated world & demo <script>
  if (window.__ommSafariBounceVer === VERSION) {
    return;
  }
  if (htmlRoot && htmlRoot.dataset.ommBounceBound === VERSION) {
    return;
  }

  if (window.__ommSafariBounceAbort && typeof window.__ommSafariBounceAbort.abort === 'function') {
    try {
      window.__ommSafariBounceAbort.abort();
    } catch {
      // Ignore stale cleanup error
    }
  }

  const abortController = new AbortController();
  window.__ommSafariBounceAbort = abortController;
  window.__ommSafariBounceVer = VERSION;
  if (htmlRoot) {
    htmlRoot.dataset.ommBounceBound = VERSION;
  }

  const MAX_STRETCH = 108;
  const OMEGA = 21.5;
  const SPRING_K = OMEGA * OMEGA; // 462.25
  const DAMPING_C = 2 * OMEGA;    // 43.0 (critically damped, zeta = 1.0)

  let mode = 'idle'; // 'idle' | 'pull' | 'spring'
  let rawPull = 0;
  let visualY = 0;
  let velocity = 0;
  let lastBoundaryWheelMs = 0;
  let pullStartMs = 0;
  let peakPullAbsDy = 0;
  let prevPullAbsDy = 0;
  let decayStreak = 0;

  let absorbingCoastTail = false;
  let tailSign = 0;
  let tailRefAbsDy = 0;
  let lastTailTickMs = 0;

  let lastNormalScrollMs = 0;
  let lastNormalSampleMs = 0;
  let lastNormalScroller = null;
  let normalScrollVel = 0;

  let activeTargets = [];
  let activeIsRoot = false;
  let savedHtmlOverflowY = null;
  let savedHtmlBg = null;

  let rafId = 0;
  let lastFrameTs = 0;

  function stampDemo(y) {
    const html = document.documentElement;
    if (html && html.hasAttribute('data-omm-demo')) {
      html.dataset.ommBounceY = y.toFixed(1);
    }
  }

  function clearRenderTargets() {
    for (const el of activeTargets) {
      if (el && el.style) {
        el.style.translate = '';
        el.style.willChange = '';
      }
    }
    const html = document.documentElement;
    if (html && activeIsRoot) {
      if (savedHtmlOverflowY !== null) {
        html.style.overflowY = savedHtmlOverflowY;
        savedHtmlOverflowY = null;
      }
      if (savedHtmlBg !== null) {
        html.style.backgroundColor = savedHtmlBg;
        savedHtmlBg = null;
      }
    }
    activeTargets = [];
    activeIsRoot = false;
  }

  function applyDisplacement(y) {
    const val = `0px ${y.toFixed(2)}px`;
    for (const el of activeTargets) {
      if (el && el.style) {
        el.style.willChange = 'translate';
        el.style.translate = val;
      }
    }
    stampDemo(y);
  }

  function resetVisualState() {
    mode = 'idle';
    rawPull = 0;
    visualY = 0;
    velocity = 0;
    clearRenderTargets();
    stampDemo(0);
  }

  function isTransparentColor(bg) {
    return !bg || bg === 'transparent' || bg === 'rgba(0, 0, 0, 0)';
  }

  function setActiveRenderTargets(res) {
    const nextTargets = res.renderTargets || [];
    const sameTargets =
      activeTargets.length === nextTargets.length &&
      nextTargets.every((el, idx) => activeTargets[idx] === el);

    if (!sameTargets) {
      clearRenderTargets();
      activeTargets = nextTargets.slice();
      activeIsRoot = Boolean(res.isRoot);
    }

    const html = document.documentElement;
    if (html && res.isRoot) {
      if (res.shortPage && savedHtmlOverflowY === null) {
        savedHtmlOverflowY = html.style.overflowY || '';
        html.style.overflowY = 'hidden';
      }
      if (savedHtmlBg === null && document.body) {
        const htmlBg = window.getComputedStyle(html).backgroundColor;
        const bodyBg = window.getComputedStyle(document.body).backgroundColor;
        if (isTransparentColor(htmlBg) && !isTransparentColor(bodyBg)) {
          savedHtmlBg = html.style.backgroundColor || '';
          html.style.backgroundColor = bodyBg;
        }
      }
    }
  }

  function resolveScrollTarget(target, dy) {
    const rootSc = document.scrollingElement || document.documentElement;
    let cur = target instanceof Element ? target : target && target.parentElement;

    while (cur && cur !== rootSc && cur !== document.body && cur !== document.documentElement) {
      const style = window.getComputedStyle(cur);
      const oy = style ? style.overflowY : '';
      if (
        (oy === 'auto' || oy === 'scroll' || oy === 'overlay') &&
        cur.clientHeight >= 60 &&
        cur.scrollHeight > cur.clientHeight + 3
      ) {
        const maxScroll = Math.max(0, cur.scrollHeight - cur.clientHeight);
        if ((dy < 0 && cur.scrollTop + dy > 1.5) || (dy > 0 && cur.scrollTop + dy < maxScroll - 1.5)) {
          return { kind: 'scrolling', scroller: cur };
        }

        let renderTargets = [cur];
        if (cur.children && cur.children.length === 1) {
          const child = cur.firstElementChild;
          const childStyle = child ? window.getComputedStyle(child) : null;
          if (child && (!childStyle || childStyle.position !== 'fixed')) {
            renderTargets = [child];
          }
        }

        return {
          kind: 'boundary',
          scroller: cur,
          renderTargets,
          maxScroll,
          isRoot: false,
          shortPage: false
        };
      }
      cur = cur.parentElement;
    }

    if (!rootSc || !document.body) {
      return { kind: 'ignore' };
    }

    const viewH = window.innerHeight || rootSc.clientHeight;
    const maxScroll = Math.max(0, rootSc.scrollHeight - viewH);

    if (maxScroll <= 2) {
      const bodyOy = window.getComputedStyle(document.body).overflowY;
      const htmlOy = htmlRoot ? window.getComputedStyle(htmlRoot).overflowY : '';
      if (bodyOy === 'hidden' || htmlOy === 'hidden') {
        return { kind: 'ignore' };
      }
      return {
        kind: 'boundary',
        scroller: rootSc,
        renderTargets: [document.body],
        maxScroll: 0,
        isRoot: true,
        shortPage: true
      };
    }

    if ((dy < 0 && rootSc.scrollTop + dy > 1.5) || (dy > 0 && rootSc.scrollTop + dy < maxScroll - 1.5)) {
      return { kind: 'scrolling', scroller: rootSc };
    }

    return {
      kind: 'boundary',
      scroller: rootSc,
      renderTargets: [document.body],
      maxScroll,
      isRoot: true,
      shortPage: false
    };
  }

  function startSpringLoop() {
    if (rafId) return;
    lastFrameTs = performance.now();

    const step = (now) => {
      if (abortController.signal.aborted) {
        rafId = 0;
        return;
      }

      const dt = Math.min(Math.max((now - lastFrameTs) / 1000, 0.001), 0.032);
      lastFrameTs = now;

      if (mode === 'pull' && now - lastBoundaryWheelMs > 42) {
        mode = 'spring';
        rawPull = 0;
      }

      if (mode === 'spring') {
        const subDt = dt * 0.5;
        for (let i = 0; i < 2; i++) {
          const accel = -SPRING_K * visualY - DAMPING_C * velocity;
          velocity += accel * subDt;
          visualY += velocity * subDt;
        }

        if (Math.abs(visualY) < 0.35 && Math.abs(velocity) < 6.0) {
          resetVisualState();
          rafId = 0;
          return;
        }
        applyDisplacement(visualY);
      }

      rafId = requestAnimationFrame(step);
    };

    rafId = requestAnimationFrame(step);
  }

  function onWheel(e) {
    if (
      e.ctrlKey ||
      e.metaKey ||
      e.defaultPrevented ||
      document.fullscreenElement ||
      Math.abs(e.deltaY) < 0.1 ||
      Math.abs(e.deltaY) <= Math.abs(e.deltaX) * 1.1
    ) {
      return;
    }

    let dy = e.deltaY;
    if (e.deltaMode === 1) dy *= 28;
    else if (e.deltaMode === 2) dy *= (window.innerHeight || 800) * 0.6;

    const now = performance.now();
    const res = resolveScrollTarget(e.target, dy);
    if (res.kind === 'ignore') return;

    if (res.kind === 'scrolling') {
      const dtSec = lastNormalSampleMs > 0 ? Math.min(Math.max((now - lastNormalSampleMs) / 1000, 0.005), 0.05) : 0.012;
      const instVel = Math.abs(dy) / dtSec;
      normalScrollVel = res.scroller === lastNormalScroller ? 0.6 * instVel + 0.4 * normalScrollVel : instVel;
      lastNormalScrollMs = now;
      lastNormalSampleMs = now;
      lastNormalScroller = res.scroller;
      absorbingCoastTail = false;

      if (Math.abs(visualY) > 0.4) {
        resetVisualState();
      }
      return;
    }

    const pushSign = -Math.sign(dy); // dy < 0 (upwards wheel) pulls page DOWN (+visualY)

    // Opposite-direction scroll brings an already-stretched page smoothly back toward 0
    if (Math.abs(visualY) > 0.4 && pushSign !== 0 && pushSign !== Math.sign(visualY)) {
      absorbingCoastTail = false;
      rawPull += -dy * 0.85;
      visualY += -dy * 0.55;
      if ((visualY > 0 && dy > 0 && rawPull <= 0) || (visualY < 0 && dy < 0 && rawPull >= 0) || Math.abs(visualY) < 0.8) {
        resetVisualState();
        return;
      }
      velocity = 0;
      mode = 'pull';
      lastBoundaryWheelMs = now;
      applyDisplacement(visualY);
      startSpringLoop();
      return;
    }

    // Absorb decaying CoastLoop tail once a rebound is already springing back
    if (absorbingCoastTail) {
      if (
        pushSign === tailSign &&
        now - lastTailTickMs <= 68 &&
        Math.abs(dy) <= tailRefAbsDy * 1.38 + 3.5
      ) {
        tailRefAbsDy = Math.min(tailRefAbsDy, Math.abs(dy)) * 1.02;
        lastTailTickMs = now;
        return;
      }
      absorbingCoastTail = false;
    }

    // Regime A: Momentum Coasting Impact Collision into boundary
    if (now - lastNormalScrollMs <= 95 && res.scroller === lastNormalScroller) {
      const overshoot = dy < 0 ? -(res.scroller.scrollTop + dy) : res.scroller.scrollTop + dy - res.maxScroll;
      if (overshoot <= 0.5) return;

      setActiveRenderTargets(res);
      const impactSpeed = Math.min(Math.max(normalScrollVel, Math.abs(dy) / 0.01, 440), 2350);
      mode = 'spring';
      rawPull = 0;
      velocity = pushSign * impactSpeed;
      visualY = Math.abs(visualY) > 0.5 ? visualY : pushSign * Math.min(overshoot * 0.35, 14);
      absorbingCoastTail = true;
      tailSign = pushSign;
      tailRefAbsDy = Math.abs(dy);
      lastTailTickMs = now;
      lastNormalScrollMs = 0;

      applyDisplacement(visualY);
      startSpringLoop();
      return;
    }

    // Regime B: Direct Pull at Boundary (already sitting on top/bottom)
    const effDy =
      dy < 0
        ? Math.min(0, res.scroller.scrollTop + dy)
        : Math.max(0, res.scroller.scrollTop + dy - res.maxScroll);
    if ((dy < 0 && effDy >= -0.01) || (dy > 0 && effDy <= 0.01)) {
      return;
    }

    setActiveRenderTargets(res);

    if (mode !== 'pull') {
      if (Math.abs(visualY) > 0.5) {
        const s = visualY >= 0 ? 1 : -1;
        const frac = Math.min(Math.abs(visualY) / MAX_STRETCH, 0.96);
        rawPull = (s * (MAX_STRETCH * frac)) / (0.58 * (1 - frac));
      } else {
        rawPull = 0;
      }
      mode = 'pull';
      pullStartMs = now;
      peakPullAbsDy = Math.abs(dy);
      prevPullAbsDy = Math.abs(dy);
      decayStreak = 0;
    } else {
      if (now - lastBoundaryWheelMs <= 30 && Math.abs(dy) < prevPullAbsDy * 0.999) {
        decayStreak++;
      } else if (Math.abs(dy) > prevPullAbsDy * 1.05) {
        decayStreak = 0;
      }
      peakPullAbsDy = Math.max(peakPullAbsDy, Math.abs(dy));
      prevPullAbsDy = Math.abs(dy);

      // Detect Finger-Lift -> CoastLoop exponential decay and release spring immediately
      if (
        (now - pullStartMs >= 65 && decayStreak >= 4) ||
        (now - pullStartMs >= 85 && Math.abs(dy) < peakPullAbsDy * 0.56)
      ) {
        mode = 'spring';
        rawPull = 0;
        velocity = pushSign * Math.min(Math.abs(dy) * 38, 620);
        absorbingCoastTail = true;
        tailSign = pushSign;
        tailRefAbsDy = Math.abs(dy);
        lastTailTickMs = now;
        startSpringLoop();
        return;
      }
    }

    rawPull += -effDy * 0.75;
    const sign = rawPull >= 0 ? 1 : -1;
    const absPull = Math.abs(rawPull);
    visualY = sign * MAX_STRETCH * (1 - 1 / ((absPull * 0.58) / MAX_STRETCH + 1));
    velocity = 0;
    lastBoundaryWheelMs = now;
    applyDisplacement(visualY);
    startSpringLoop();
  }

  window.addEventListener('wheel', onWheel, { passive: true, signal: abortController.signal });

  if (typeof window.__ommTestHook === 'function') {
    window.__ommTestHook({
      getState: () => ({ mode, visualY, velocity, rawPull, absorbingCoastTail })
    });
  }
})();
