import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const extDir = path.resolve(__dirname, '..', 'MacSafariBounceExtension');
const manifestPath = path.join(extDir, 'manifest.json');
const bgPath = path.join(extDir, 'background.js');
const contentPath = path.join(extDir, 'content.js');

// 1. Validate manifest.json & JS syntax
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
assert.equal(manifest.version, '2.2.0', 'manifest version must be 2.2.0');
assert.ok(manifest.permissions.includes('scripting'), 'manifest must include scripting permission');
assert.ok(manifest.background && manifest.background.service_worker === 'background.js', 'background.js missing in manifest');

execFileSync(process.execPath, ['--check', bgPath], { stdio: 'pipe' });
execFileSync(process.execPath, ['--check', contentPath], { stdio: 'pipe' });

const contentCode = fs.readFileSync(contentPath, 'utf8');

function createHarness() {
  let nowMs = 1000;
  let rafSeq = 1;
  const rafQueue = new Map();
  const listeners = new Map();

  function makeStyle(initial = {}) {
    return {
      translate: '',
      willChange: '',
      overflowY: initial.overflowY ?? '',
      backgroundColor: initial.backgroundColor ?? '',
      position: initial.position ?? 'static'
    };
  }

  class MockElement {
    constructor(opts = {}) {
      this.style = makeStyle(opts.style);
      this.computed = {
        overflowY: opts.overflowY ?? 'visible',
        backgroundColor: opts.backgroundColor ?? 'rgba(0, 0, 0, 0)',
        position: opts.position ?? 'static'
      };
      this.clientHeight = opts.clientHeight ?? 800;
      this.scrollHeight = opts.scrollHeight ?? 800;
      this.scrollTop = opts.scrollTop ?? 0;
      this.parentElement = null;
      this.children = [];
      this.firstElementChild = null;
      this.dataset = {};
      this.attrs = new Set(opts.attrs || []);
    }
    hasAttribute(k) {
      return this.attrs.has(k);
    }
    appendChild(child) {
      child.parentElement = this;
      this.children.push(child);
      this.firstElementChild = this.children[0] || null;
      return child;
    }
  }

  const html = new MockElement({
    clientHeight: 800,
    scrollHeight: 4000,
    overflowY: 'auto',
    backgroundColor: 'rgba(0, 0, 0, 0)'
  });
  const body = new MockElement({
    clientHeight: 800,
    scrollHeight: 4000,
    overflowY: 'visible',
    backgroundColor: 'rgb(15, 17, 23)'
  });
  html.appendChild(body);

  let hookState = null;

  const sandbox = {
    Element: MockElement,
    AbortController,
    Math,
    performance: { now: () => nowMs },
    requestAnimationFrame: (cb) => {
      const id = rafSeq++;
      rafQueue.set(id, cb);
      return id;
    },
    window: null,
    document: {
      documentElement: html,
      body,
      scrollingElement: html,
      fullscreenElement: null
    },
    __ommTestHook: (api) => {
      hookState = api;
    },
    addEventListener: (type, fn) => {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(fn);
    },
    getComputedStyle: (el) => el.computed,
    innerHeight: 800
  };
  sandbox.window = sandbox;

  vm.runInNewContext(contentCode, sandbox);

  function parseTranslateY(el) {
    const t = el.style.translate || '';
    if (!t) return 0;
    const m = /0px\s+(-?\d+(?:\.\d+)?)px/.exec(t);
    return m ? parseFloat(m[1]) : 0;
  }

  function stepMs(ms) {
    nowMs += ms;
    const batch = Array.from(rafQueue.entries());
    rafQueue.clear();
    for (const [, cb] of batch) {
      cb(nowMs);
    }
  }

  function dispatchWheel(deltaY, target = body) {
    const handlers = listeners.get('wheel') || [];
    for (const fn of handlers) {
      fn({
        deltaY,
        deltaX: 0,
        deltaMode: 0,
        ctrlKey: false,
        metaKey: false,
        defaultPrevented: false,
        target
      });
    }
  }

  return { html, body, getState: () => hookState.getState(), parseTranslateY, stepMs, dispatchWheel, setNow: (t) => { nowMs = t; } };
}

// Scenario 1: 125Hz Momentum Coast Impact (tau = 560ms, 1.4s decaying tail)
{
  const h = createHarness();
  h.html.scrollTop = 100;
  // Scroll up at ~1600 px/s
  for (let i = 0; i < 6; i++) {
    h.stepMs(8);
    h.dispatchWheel(-14);
    h.html.scrollTop = Math.max(0, h.html.scrollTop - 14);
  }
  // Impact frame hits scrollTop = 0 with dy = -45
  h.stepMs(8);
  h.dispatchWheel(-45);
  h.html.scrollTop = 0;

  let peakY = h.parseTranslateY(h.body);
  let settledAtMs = -1;
  // Continue OpenMagicMouse decaying 8ms coast tail for 420ms
  let tailDy = 36;
  for (let ms = 8; ms <= 420; ms += 8) {
    tailDy *= Math.exp(-8 / 560);
    h.stepMs(8);
    if (tailDy >= 1) h.dispatchWheel(-tailDy);
    const y = h.parseTranslateY(h.body);
    if (y > peakY) peakY = y;
    if (ms > 120 && settledAtMs < 0 && h.body.style.translate === '') {
      settledAtMs = ms;
    }
  }

  assert.ok(peakY >= 12 && peakY <= 85, `Scenario 1 peakY out of range [12..85]: ${peakY}`);
  assert.ok(settledAtMs > 0 && settledAtMs <= 420, `Scenario 1 failed to settle in <=420ms: ${settledAtMs}`);
  assert.equal(h.body.style.translate, '', 'Scenario 1 did not clean up translate');
  console.log(`[PASS] Scenario 1: 125Hz Coast Impact peak=${peakY.toFixed(1)}px, settled=${settledAtMs}ms`);
}

// Scenario 2: Boundary Flick + Coast Tail at scrollTop == 0
{
  const h = createHarness();
  h.html.scrollTop = 0;
  let peakY = 0;
  // Strong pull for 96ms, then exponential CoastLoop decay for 350ms
  for (let i = 0; i < 12; i++) {
    h.stepMs(8);
    h.dispatchWheel(-24);
    peakY = Math.max(peakY, h.parseTranslateY(h.body));
  }
  let coastDy = 24;
  let settledAtMs = -1;
  for (let ms = 8; ms <= 450; ms += 8) {
    coastDy *= Math.exp(-8 / 380);
    h.stepMs(8);
    if (coastDy >= 1) h.dispatchWheel(-coastDy);
    const y = h.parseTranslateY(h.body);
    peakY = Math.max(peakY, y);
    if (ms > 80 && settledAtMs < 0 && h.body.style.translate === '') {
      settledAtMs = ms;
    }
  }

  assert.ok(peakY >= 15, `Scenario 2 peakY too small: ${peakY}`);
  assert.ok(settledAtMs > 0 && settledAtMs <= 450, `Scenario 2 failed to settle in <=450ms: ${settledAtMs}`);
  console.log(`[PASS] Scenario 2: Boundary Flick + Coast Tail peak=${peakY.toFixed(1)}px, settled=${settledAtMs}ms`);
}

// Scenario 3: Bottom Boundary Bounce (scrollTop == maxScroll)
{
  const h = createHarness();
  const maxScroll = h.html.scrollHeight - h.html.clientHeight; // 3200
  h.html.scrollTop = maxScroll;
  let minY = 0;
  for (let i = 0; i < 6; i++) {
    h.stepMs(8);
    h.dispatchWheel(28);
    minY = Math.min(minY, h.parseTranslateY(h.body));
  }
  for (let ms = 8; ms <= 450; ms += 8) {
    h.stepMs(8);
  }
  assert.ok(minY <= -12, `Scenario 3 negative bounce too small: ${minY}`);
  assert.equal(h.body.style.translate, '', 'Scenario 3 did not settle to empty translate');
  console.log(`[PASS] Scenario 3: Bottom Boundary Bounce minY=${minY.toFixed(1)}px`);
}

// Scenario 4: Inner SPA Container (ChatGPT/Zalo style overflowY: 'auto' with 1 child)
{
  const h = createHarness();
  const spaBox = new h.html.constructor({
    clientHeight: 300,
    scrollHeight: 1500,
    scrollTop: 500,
    overflowY: 'auto'
  });
  const spaChild = new h.html.constructor({ clientHeight: 1500, scrollHeight: 1500 });
  const leaf = new h.html.constructor({ clientHeight: 40, scrollHeight: 40 });
  spaBox.appendChild(spaChild);
  spaChild.appendChild(leaf);
  h.body.appendChild(spaBox);

  // Mid-scroll should NOT bounce anything
  h.stepMs(8);
  h.dispatchWheel(-20, leaf);
  assert.equal(spaChild.style.translate, '', 'Mid-scroll SPA container must not bounce');
  assert.equal(h.body.style.translate, '', 'Mid-scroll SPA must not move root body');

  // Jump to top boundary of SPA container and pull up
  spaBox.scrollTop = 0;
  let spaPeak = 0;
  for (let i = 0; i < 5; i++) {
    h.stepMs(8);
    h.dispatchWheel(-26, leaf);
    spaPeak = Math.max(spaPeak, h.parseTranslateY(spaChild));
  }
  assert.ok(spaPeak >= 12, `SPA inner child did not bounce at boundary: ${spaPeak}`);
  assert.equal(h.body.style.translate, '', 'SPA bounce leaked onto document.body');
  for (let ms = 8; ms <= 450; ms += 8) h.stepMs(8);
  assert.equal(spaChild.style.translate, '', 'SPA inner child failed to settle');
  console.log(`[PASS] Scenario 4: Inner SPA Container isolated bounce peak=${spaPeak.toFixed(1)}px`);
}

// Scenario 5: Short Page (maxScroll == 0, e.g. google.com)
{
  const h = createHarness();
  h.html.scrollHeight = 700; // <= innerHeight (800)
  h.html.scrollTop = 0;
  h.stepMs(8);
  h.dispatchWheel(-40);
  const peakShort = h.parseTranslateY(h.body);
  assert.ok(peakShort >= 10, `Short page did not bounce: ${peakShort}`);
  assert.equal(h.html.style.overflowY, 'hidden', 'Short page must suppress temporary scrollbar during stretch');
  assert.equal(h.html.style.backgroundColor, 'rgb(15, 17, 23)', 'Dark-mode body bg must mirror to html during stretch');

  for (let ms = 8; ms <= 450; ms += 8) h.stepMs(8);
  assert.equal(h.body.style.translate, '', 'Short page failed to settle');
  assert.equal(h.html.style.overflowY, '', 'Short page failed to restore html overflowY');
  assert.equal(h.html.style.backgroundColor, '', 'Short page failed to restore html backgroundColor');
  console.log(`[PASS] Scenario 5: Short Page + Dark-Mode Hygiene peak=${peakShort.toFixed(1)}px`);
}

console.log('ALL 5 SAFARI BOUNCE PHYSICS CHECKS PASSED');
