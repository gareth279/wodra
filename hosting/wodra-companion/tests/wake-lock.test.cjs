const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync(require('node:path').join(__dirname, '../public/wake-lock.js'), 'utf8');

function harness({supported = true, reject = false} = {}) {
  const listeners = new Map();
  const status = {textContent: ''};
  const toggle = {checked: true, addEventListener(name, handler) { listeners.set(`toggle:${name}`, handler); }};
  const document = {visibilityState: 'visible', getElementById(id) { return id === 'wake-toggle' ? toggle : status; },
    addEventListener(name, handler) { listeners.set(`document:${name}`, handler); }};
  let acquired = 0, released = 0;
  const navigator = {};
  if (supported) navigator.wakeLock = {async request(kind) {
    assert.equal(kind, 'screen');
    if (reject) throw new Error('denied');
    acquired++;
    const sentinelListeners = new Map();
    return {released:false, addEventListener(name, callback) { sentinelListeners.set(name, callback); }, async release() {
      if (this.released) return;
      this.released = true; released++;
      sentinelListeners.get('release')?.();
    }};
  }};
  const window = {addEventListener(name, handler) { listeners.set(`window:${name}`, handler); }};
  vm.runInNewContext(source, {document, navigator, window, Boolean, Promise, Error});
  return {listeners, status, toggle, window, document, get acquired(){return acquired;}, get released(){return released;}};
}

(async () => {
  const supported = harness();
  await supported.window.WodraWakeLock.setRideActive(true);
  assert.equal(supported.acquired, 1, 'active ride requests the standard screen lock');
  supported.toggle.checked = false;
  await supported.listeners.get('toggle:change')();
  assert.equal(supported.released, 1, 'turning the option off releases the lock');
  supported.toggle.checked = true;
  await supported.listeners.get('toggle:change')();
  assert.equal(supported.acquired, 2, 're-enabling requests a new lock');
  supported.document.visibilityState = 'hidden';
  await supported.listeners.get('document:visibilitychange')();
  assert.equal(supported.released, 2, 'hiding the page releases the lock');
  supported.document.visibilityState = 'visible';
  await supported.listeners.get('document:visibilitychange')();
  assert.equal(supported.acquired, 3, 'visible page reacquires the lock when allowed');
  await supported.window.WodraWakeLock.setRideActive(false);
  assert.equal(supported.released, 3, 'ending the ride releases the lock');

  const unavailable = harness({supported:false});
  await unavailable.window.WodraWakeLock.setRideActive(true);
  assert.match(unavailable.status.textContent, /does not support/i, 'unsupported browsers get a clear status');
  const refused = harness({reject:true});
  await refused.window.WodraWakeLock.setRideActive(true);
  assert.match(refused.status.textContent, /could not keep/i, 'refused requests fail gracefully');
  console.log('PASS: Wake Lock supported, disabled, released, reacquired, unsupported and refused states');
})().catch(error => { console.error(error); process.exitCode = 1; });
