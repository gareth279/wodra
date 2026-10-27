(() => {
  'use strict';
  const toggle = document.getElementById('wake-toggle');
  const status = document.getElementById('wake-status');
  if (!toggle || !status) return;
  let activeRide = false;
  let sentinel = null;
  let generation = 0;

  const wanted = () => toggle.checked && activeRide;
  function message(text) { status.textContent = text; }
  async function release() {
    const lock = sentinel;
    sentinel = null;
    if (lock && !lock.released) {
      try { await lock.release(); } catch { /* The browser may have already released it. */ }
    }
  }
  async function update() {
    const current = ++generation;
    if (!wanted()) {
      await release();
      message(toggle.checked ? 'Screen wake is only requested during an active ride.' : 'Screen wake is off.');
      return;
    }
    if (document.visibilityState !== 'visible') {
      await release();
      message('Wake lock will be requested when this page is visible.');
      return;
    }
    if (!('wakeLock' in navigator) || typeof navigator.wakeLock.request !== 'function') {
      message('This browser does not support keeping the screen awake.');
      return;
    }
    if (sentinel && !sentinel.released) return;
    try {
      const lock = await navigator.wakeLock.request('screen');
      if (current !== generation || !wanted() || document.visibilityState !== 'visible') {
        try { await lock.release(); } catch { /* Superseded while request was pending. */ }
        return;
      }
      sentinel = lock;
      message('Screen wake is active for this ride.');
      lock.addEventListener('release', () => {
        if (sentinel === lock) sentinel = null;
        if (wanted() && document.visibilityState === 'visible') message('The browser released screen wake. Toggle it off and on to retry.');
      }, { once: true });
    } catch {
      message('The browser could not keep the screen awake. You can continue riding.');
    }
  }
  toggle.addEventListener('change', update);
  document.addEventListener('visibilitychange', update);
  window.addEventListener('pagehide', release);
  window.WodraWakeLock = { setRideActive(active) { activeRide = Boolean(active); return update(); }, release };
})();
