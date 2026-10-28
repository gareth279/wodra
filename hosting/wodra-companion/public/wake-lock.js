(() => {
  'use strict';
  const toggle = document.getElementById('wake-toggle');
  const status = document.getElementById('wake-status');
  if (!toggle || !status) return;

  const action = document.createElement('button');
  action.type = 'button';
  action.className = 'quiet wake-action';
  status.insertAdjacentElement('afterend', action);

  let activeRide = false, wanted = false, attempted = false, generation = 0;
  let sentinel = null, pending = null;
  const supported = () => typeof navigator.wakeLock?.request === 'function';
  const visible = () => document.visibilityState === 'visible';
  const policy = () => {
    const api = document.permissionsPolicy || document.featurePolicy;
    try { return typeof api?.allowsFeature === 'function' ? (api.allowsFeature('screen-wake-lock') ? 'allowed' : 'blocked') : 'unknown'; }
    catch { return 'unknown'; }
  };
  const activation = () => navigator.userActivation?.isActive === true ? 'yes' : navigator.userActivation?.isActive === false ? 'no' : 'unknown';
  function message(text) { status.textContent = text; }
  function showAction(text) { action.hidden = false; action.disabled = Boolean(pending); action.textContent = text; }
  function sanitize(text) {
    return String(text || '').replace(/[\u0000-\u001f\u007f]/g, ' ')
      .replace(/https?:\/\/\S+/gi, '[URL]')
      .replace(/\b(?:token|code|authorization|credential)\s*[=:]\s*\S+/gi, '[redacted]')
      .replace(/\b[A-Za-z0-9_-]{40,}\b/g, '[redacted]').replace(/\s+/g, ' ').trim().slice(0, 180);
  }
  function rejection(error, source, userActivation) {
    const name = sanitize(error?.name) || 'UnknownError';
    const detail = sanitize(error?.message);
    const hint = source === 'automatic' && userActivation === 'no' ? ' Automatic request had no user gesture; tap to retry.' : '';
    message(`Wake Lock rejected (${name})${detail ? `: ${detail}` : '.'}${hint} [request=${source}; userActivation=${userActivation}; visibility=${document.visibilityState}; secureContext=${typeof isSecureContext === 'boolean' ? isSecureContext : 'unknown'}; permissionsPolicy=${policy()}]`);
    showAction('Tap to keep screen awake');
  }
  function releaseLock(lock) {
    if (!lock || lock.released) return;
    try { Promise.resolve(lock.release()).catch(() => {}); } catch { /* Already released by the browser. */ }
  }
  function releaseCurrent() { const lock = sentinel; sentinel = null; releaseLock(lock); }
  function waitForTap(text = 'Tap to keep screen awake.') { message(text); showAction('Tap to keep screen awake'); }

  function acquire(source) {
    if (sentinel && !sentinel.released) { message('Screen wake is active.'); showAction('Turn screen wake off'); return Promise.resolve(sentinel); }
    if (pending) return pending;
    if (!supported()) { message('This browser does not support keeping the screen awake.'); action.hidden = true; return Promise.resolve(null); }
    if (!visible()) { waitForTap('Screen wake is waiting for this page to be visible. Return and tap to resume.'); return Promise.resolve(null); }

    const userActivation = activation(), requestGeneration = ++generation;
    message('Requesting screen wake…');
    action.disabled = true;
    let request;
    try { request = navigator.wakeLock.request('screen'); } // Directly called from toggle/button handlers when source is user initiated.
    catch (error) { rejection(error, source, userActivation); return Promise.resolve(null); }

    let task;
    task = Promise.resolve(request).then(lock => {
      if (requestGeneration !== generation || !visible() || !wanted || (activeRide && !toggle.checked)) {
        releaseLock(lock);
        if (wanted) waitForTap('Screen wake changed while a request was pending. Tap to try again.');
        else { message(toggle.checked ? 'Screen wake is ready for a ride.' : 'Screen wake is off.'); showAction('Test screen awake'); }
        return null;
      }
      sentinel = lock;
      message('Screen wake is active.');
      showAction('Turn screen wake off');
      lock.addEventListener('release', () => {
        if (sentinel !== lock) return;
        sentinel = null;
        if (wanted || (activeRide && toggle.checked)) waitForTap('The browser released screen wake. Tap to resume it.');
        else { message('Screen wake was released.'); showAction(activeRide ? 'Tap to keep screen awake' : 'Test screen awake'); }
      }, {once:true});
      return lock;
    }, error => {
      if (requestGeneration === generation) rejection(error, source, userActivation);
      return null;
    }).finally(() => {
      if (pending === task) pending = null;
      action.disabled = Boolean(pending);
    });
    pending = task;
    return task;
  }

  function setRideActive(active) {
    if (active === null || active === undefined) return; // Missing telemetry is not proof that the ride ended.
    const next = Boolean(active);
    if (next === activeRide) return;
    const wasActive = activeRide;
    activeRide = next;
    if (!activeRide) {
      if (wasActive) { generation++; wanted = false; releaseCurrent(); }
      message(toggle.checked ? 'Screen wake is ready. Tap Test screen awake to check it.' : 'Screen wake is off.');
      showAction('Test screen awake');
      return;
    }
    if (!toggle.checked) { generation++; wanted = false; releaseCurrent(); message('Screen wake is off.'); showAction('Test screen awake'); return; }
    if (sentinel && !sentinel.released) return;
    wanted = true;
    if (attempted || pending) { waitForTap('Tap to keep screen awake during this ride.'); return; }
    attempted = true;
    void acquire('automatic');
  }

  toggle.addEventListener('change', () => {
    if (!toggle.checked) {
      generation++; wanted = false; attempted = false; releaseCurrent();
      message('Screen wake is off.'); showAction(activeRide ? 'Tap to keep screen awake' : 'Test screen awake');
    } else if (activeRide) {
      wanted = true; attempted = true;
      void acquire('toggle'); // The change event is a user gesture; request before returning to Safari.
    } else {
      message('Screen wake is enabled for rides. Tap Test screen awake to check it now.');
      showAction(sentinel ? 'Turn screen wake off' : 'Test screen awake');
    }
  });

  action.addEventListener('click', () => {
    if (sentinel && !sentinel.released) {
      generation++; wanted = false; releaseCurrent();
      message('Screen wake was released.'); showAction(activeRide ? 'Tap to keep screen awake' : 'Test screen awake');
      return;
    }
    if (activeRide && !toggle.checked) { message('Screen wake is off. Enable the ride preference to keep the screen awake.'); return; }
    wanted = true; attempted = true;
    void acquire('button'); // No await or other asynchronous work precedes request('screen').
  });

  function visibilityChanged() {
    if (!visible()) {
      if (sentinel || pending || wanted) {
        generation++; releaseCurrent();
        if (wanted) waitForTap('Screen wake paused in the background. Tap to resume when visible.');
      }
    } else if (wanted && !sentinel) waitForTap('Tap to keep screen awake after returning to this page.');
  }
  document.addEventListener('visibilitychange', visibilityChanged);
  window.addEventListener('pagehide', () => { if (sentinel || pending) { generation++; releaseCurrent(); } });
  window.addEventListener('pageshow', () => { if (wanted && !sentinel) waitForTap('Tap to keep screen awake after returning to this page.'); });

  if (!supported()) { message('This browser does not support keeping the screen awake.'); action.hidden = true; }
  else { message('Screen wake is ready. Tap Test screen awake to check it, or enable it for a ride.'); showAction('Test screen awake'); }
  window.WodraWakeLock = {setRideActive, release:releaseCurrent};
})();
