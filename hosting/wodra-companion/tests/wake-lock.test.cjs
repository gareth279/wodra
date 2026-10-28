const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync(require('node:path').join(__dirname, '../public/wake-lock.js'), 'utf8');

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return {promise, resolve, reject};
}

function makeSentinel() {
  const listeners = new Map();
  return {released:false, addEventListener(name, callback) { listeners.set(name, callback); }, async release() {
    if (this.released) return;
    this.released = true;
    listeners.get('release')?.();
  }};
}

function harness({supported = true, request, activation = false, policy = true, secure = true} = {}) {
  const listeners = new Map();
  const status = {textContent:'', inserted:null, insertAdjacentElement(position, node) {
    assert.equal(position, 'afterend');
    this.inserted = node;
  }};
  const toggle = {checked:true, addEventListener(name, callback) { listeners.set(`toggle:${name}`, callback); }};
  const document = {visibilityState:'visible', getElementById(id) { return id === 'wake-toggle' ? toggle : status; },
    createElement() { return {hidden:false, disabled:false, textContent:'', addEventListener(name, callback) { listeners.set(`action:${name}`, callback); }}; },
    addEventListener(name, callback) { listeners.set(`document:${name}`, callback); }};
  document.permissionsPolicy = {allowsFeature(name) {
    assert.equal(name, 'screen-wake-lock');
    return policy;
  }};
  const navigator = {userActivation:{isActive:activation}};
  let calls = 0, inFlight = 0, maxInFlight = 0, released = 0;
  if (supported) navigator.wakeLock = {request(kind) {
    assert.equal(kind, 'screen');
    calls++;
    inFlight++;
    maxInFlight = Math.max(maxInFlight, inFlight);
    let result;
    try { result = request ? request(calls, navigator.userActivation.isActive) : Promise.resolve(makeSentinel()); }
    catch (error) { inFlight--; throw error; }
    return Promise.resolve(result).then(lock => {
      inFlight--;
      const originalRelease = lock.release.bind(lock);
      lock.release = async () => { const wasReleased = lock.released; await originalRelease(); if (!wasReleased && lock.released) released++; };
      return lock;
    }, error => { inFlight--; throw error; });
  }};
  const window = {addEventListener(name, callback) { listeners.set(`window:${name}`, callback); }};
  vm.runInNewContext(source, {document, navigator, window, Boolean, Promise, Error, String, isSecureContext:secure});
  const action = status.inserted;
  const flush = async () => { await Promise.resolve(); await Promise.resolve(); await new Promise(resolve => setImmediate(resolve)); };
  return {listeners,status,toggle,action,document,navigator,window,flush,
    get calls(){return calls;},get inFlight(){return inFlight;},get maxInFlight(){return maxInFlight;},get released(){return released;}};
}

(async () => {
  const slow = deferred();
  const steady = harness({request:() => slow.promise});
  steady.window.WodraWakeLock.setRideActive(true);
  assert.equal(steady.calls, 1, 'active ride makes one automatic request');
  for (let i = 0; i < 8; i++) steady.window.WodraWakeLock.setRideActive(true);
  assert.equal(steady.calls, 1, 'repeated telemetry updates do not retry or overlap acquisition');
  slow.resolve(makeSentinel());
  await steady.flush();
  assert.match(steady.status.textContent, /active/i, 'a slow successful request remains active');
  assert.equal(steady.released, 0, 'repeated telemetry updates do not discard the acquired sentinel');
  steady.toggle.checked = false;
  await steady.listeners.get('toggle:change')();
  await steady.flush();
  assert.equal(steady.released, 1, 'turning the preference off releases the sentinel');
  steady.toggle.checked = true;
  steady.navigator.userActivation.isActive = true;
  steady.listeners.get('toggle:change')();
  assert.equal(steady.calls, 2, 'turning the preference back on requests directly from its user gesture');
  await steady.flush();
  assert.match(steady.status.textContent, /active/i);

  const manualLock = deferred();
  const refused = harness({request:(count) => {
    if (count === 1) throw new DOMException('Blocked at https://private.example/?token=supersecret', 'NotAllowedError');
    return manualLock.promise;
  }});
  refused.window.WodraWakeLock.setRideActive(true);
  await refused.flush();
  assert.equal(refused.calls, 1, 'a rejected automatic request is attempted once');
  assert.equal(refused.action.textContent, 'Tap to keep screen awake', 'automatic rejection exposes a one-tap retry');
  assert.match(refused.status.textContent, /NotAllowedError/, 'the actual DOMException name is visible');
  assert.match(refused.status.textContent, /userActivation=no/, 'diagnostics show the missing automatic user gesture');
  assert.match(refused.status.textContent, /permissionsPolicy=allowed/, 'diagnostics show the policy state');
  assert.doesNotMatch(refused.status.textContent, /private\.example|supersecret/, 'diagnostics redact URLs and credentials');
  for (let i = 0; i < 8; i++) refused.window.WodraWakeLock.setRideActive(true);
  assert.equal(refused.calls, 1, 'repeated polls do not repeat a rejected automatic request');
  refused.navigator.userActivation.isActive = true;
  refused.listeners.get('action:click')();
  assert.equal(refused.calls, 2, 'the retry calls request synchronously in the button click handler');
  assert.equal(refused.action.disabled, true, 'the button prevents overlapping attempts while acquisition is pending');
  assert.equal(refused.navigator.userActivation.isActive, true, 'the retry observes the active user gesture');
  manualLock.resolve(makeSentinel());
  await refused.flush();
  assert.match(refused.status.textContent, /active/i, 'the manual request acquires the lock');

  const racingRequest = deferred();
  const race = harness({request:(count) => count === 1 ? racingRequest.promise : Promise.resolve(makeSentinel())});
  race.window.WodraWakeLock.setRideActive(true);
  race.toggle.checked = false;
  race.listeners.get('toggle:change')();
  race.toggle.checked = true;
  race.navigator.userActivation.isActive = true;
  race.listeners.get('toggle:change')();
  assert.equal(race.calls, 1, 'toggle changes do not start overlapping requests while a slow request is pending');
  racingRequest.resolve(makeSentinel());
  await race.flush();
  assert.equal(race.released, 1, 'a request invalidated by turning the preference off is released when it resolves');
  assert.equal(race.action.disabled, false, 'the retry control is enabled after the stale request settles');
  race.listeners.get('action:click')();
  await race.flush();
  assert.equal(race.calls, 2, 'the user can retry after the stale request settles');
  assert.equal(race.maxInFlight, 1, 'wake-lock requests never overlap');

  const visibility = harness();
  visibility.window.WodraWakeLock.setRideActive(true);
  await visibility.flush();
  visibility.document.visibilityState = 'hidden';
  visibility.listeners.get('document:visibilitychange')();
  await visibility.flush();
  assert.equal(visibility.released, 1, 'backgrounding releases the browser-owned sentinel');
  visibility.document.visibilityState = 'visible';
  visibility.listeners.get('document:visibilitychange')();
  assert.equal(visibility.calls, 1, 'foregrounding does not make a non-gesture request');
  assert.equal(visibility.action.textContent, 'Tap to keep screen awake', 'foregrounding asks for one-tap resume');
  visibility.navigator.userActivation.isActive = true;
  visibility.listeners.get('action:click')();
  await visibility.flush();
  assert.equal(visibility.calls, 2, 'one tap reacquires after returning to the page');
  visibility.listeners.get('window:pagehide')();
  await visibility.flush();
  assert.equal(visibility.released, 2, 'navigation away releases the active sentinel');
  visibility.listeners.get('window:pageshow')();
  assert.equal(visibility.action.textContent, 'Tap to keep screen awake', 'returning through browser history shows a one-tap resume');
  assert.equal(visibility.calls, 2, 'pageshow does not make an automatic request');

  const idle = harness();
  assert.equal(idle.action.textContent, 'Test screen awake', 'manual testing is available without an active ride');
  idle.navigator.userActivation.isActive = true;
  idle.listeners.get('action:click')();
  assert.equal(idle.calls, 1, 'the idle test button requests synchronously');
  await idle.flush();
  assert.match(idle.status.textContent, /active/i);

  const blocked = harness({request:() => { throw new DOMException('Feature denied by policy', 'NotAllowedError'); }, policy:false});
  blocked.window.WodraWakeLock.setRideActive(true);
  await blocked.flush();
  assert.match(blocked.status.textContent, /permissionsPolicy=blocked/, 'diagnostics distinguish a denied Permissions Policy');

  const unsupported = harness({supported:false});
  assert.match(unsupported.status.textContent, /does not support/i, 'unsupported browsers get a clear status');
  assert.equal(unsupported.action.hidden, true, 'unsupported browsers do not show a futile retry action');
  console.log('PASS: Wake Lock gesture retry, diagnostics, idempotent telemetry, acquisition races, visibility resume, idle testing and unsupported state');
})().catch(error => { console.error(error); process.exitCode = 1; });
