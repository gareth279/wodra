(() => {
  'use strict';
  const api = new URL('api.php', location.href).toString();
  const $ = id => document.getElementById(id);
  const pairView = $('pair-view'), rideView = $('ride-view'), msg = $('pair-message'), badge = $('connection');
  const legacyKey = 'wodra-companion-pair-v1', legacyPendingKey = 'wodra-companion-pending-v1';
  const wakePreferenceKey = 'wodra-companion-wake-enabled';
  let trusted = false, legacyPair = null, legacyPending = null, timer = null, busy = false, failures = 0;
  try { $('wake-toggle').checked = localStorage.getItem(wakePreferenceKey) !== 'false'; } catch { /* Private storage may be unavailable. */ }

  async function post(body, protocol = 2) {
    const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 5000);
    try {
      const response = await fetch(api, { method:'POST', headers:{'Content-Type':'application/json'},
        body:JSON.stringify({protocol,...body}), cache:'no-store', credentials:'same-origin', signal:controller.signal });
      let data;
      try { data = await response.json(); } catch { throw new Error('invalid_response'); }
      if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('invalid_response');
      if (!response.ok && data.error === 'not_configured') throw new Error('not_configured');
      if (data.protocol !== protocol) throw new Error('protocol_mismatch');
      if (!response.ok) throw new Error(typeof data.error === 'string' ? data.error : 'request_failed');
      return data;
    } finally { clearTimeout(timeout); }
  }
  function failureMessage(error) {
    switch (error?.message) {
      case 'not_configured': return 'The Wodra companion server has not been configured yet. Try again later.';
      case 'protocol_mismatch': return 'This Wodra companion server version is incompatible. The website and API need to be updated together.';
      case 'invalid_response': return 'The Wodra companion server returned an invalid response. Try again later.';
      case 'unauthorized': return 'This phone is not trusted by Wodra. Pair it again from the desktop app.';
      case 'expired': return 'This phone pairing has expired. Pair it again from the desktop app.';
      case 'not_found': return 'This pairing code or remembered phone is no longer available.';
      case 'rate_limited': return 'Too many pairing attempts. Wait a few minutes and try again.';
      default: return 'The Wodra companion server is unavailable. Check the connection and retry.';
    }
  }
  function showPair(message = '') {
    trusted = false; pairView.hidden = false; rideView.hidden = true;
    badge.textContent = 'Not connected'; badge.className = 'pill'; msg.textContent = message;
    window.WodraWakeLock?.setRideActive(false);
  }
  function value(id, number, digits = 0) { $(id).textContent = Number.isFinite(number) ? Number(number).toFixed(digits) : '—'; }
  function duration(value) {
    if (!Number.isFinite(value)) return '—';
    const seconds = Math.max(0, Math.floor(value)), hours = Math.floor(seconds / 3600), minutes = Math.floor(seconds % 3600 / 60), remainder = seconds % 60;
    return `${hours}:${String(minutes).padStart(2,'0')}:${String(remainder).padStart(2,'0')}`;
  }
  function render(data) {
    trusted = true; pairView.hidden = true; rideView.hidden = false;
    $('reconnect').hidden = true;
    const snapshot = data.snapshot;
    const age = Date.now() / 1000 - Number(data.snapshot_at || 0);
    const desktopUnavailable = !data.desktop_online;
    const stale = desktopUnavailable || !snapshot || age > 12;
    if (stale) {
      badge.textContent = failures ? 'Reconnecting' : desktopUnavailable ? 'Waiting for Wodra' : !snapshot ? 'Waiting for ride' : 'Reconnecting'; badge.className = 'pill warn';
      $('ride-state').textContent = desktopUnavailable
        ? 'Waiting for Wodra. Your phone is remembered; ride data will appear when the desktop reconnects.'
        : !snapshot ? 'Waiting for an active ride. Your phone is remembered and will reconnect automatically.'
          : 'Reconnecting to Wodra. Ride data will return when the desktop connection is restored.';
      $('ride-state').className = 'state-banner'; $('route-name').textContent = !desktopUnavailable && !snapshot ? 'Waiting for ride' : 'Waiting for Wodra';
      ['power','speed','cadence','heart','distance','elapsed','gradient','trainer'].forEach(id => $(id).textContent = '—');
      $('updated').textContent = '';
      window.WodraWakeLock?.setRideActive(null);
      return;
    }
    const state = snapshot.state === 'ended' ? 'Ride ended' : snapshot.paused ? 'Ride paused'
      : snapshot.state === 'riding' ? 'Ride in progress' : 'Waiting for ride';
    const active = snapshot.state === 'riding' || snapshot.state === 'paused';
    const banner = $('ride-state');
    banner.textContent = snapshot.data_stale ? 'Trainer data is stale · live measurements unavailable' : state;
    banner.className = 'state-banner ' + (active && snapshot.connected && !snapshot.data_stale ? 'live' : '');
    badge.textContent = snapshot.state === 'ended' ? 'Ride ended' : snapshot.connected ? 'Live' : 'Trainer disconnected';
    badge.className = 'pill ' + (active && snapshot.connected && !snapshot.data_stale ? 'live' : 'warn');
    $('route-name').textContent = snapshot.route || 'Route unavailable';
    value('power', snapshot.power_w); value('speed', snapshot.speed_kph, 1); value('cadence', snapshot.cadence_rpm);
    value('heart', snapshot.heart_rate_bpm); value('distance', Number.isFinite(snapshot.distance_m) ? snapshot.distance_m / 1000 : NaN, 2);
    $('elapsed').textContent = duration(snapshot.elapsed_s); value('gradient', snapshot.gradient_pct);
    $('trainer').textContent = snapshot.connected ? 'Connected' : 'Disconnected';
    $('updated').textContent = `Updated ${new Date(data.snapshot_at * 1000).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'})}`;
    window.WodraWakeLock?.setRideActive(active);
  }
  async function poll() {
    if (document.hidden || busy || (!trusted && !legacyPair && !legacyPending)) return;
    busy = true;
    try {
      if (legacyPending) {
        const state = await post({action:'pair_status',id:legacyPending.id,token_hash:await digest(legacyPending.token)},1);
        if (state.status === 'active') {
          legacyPair = legacyPending; legacyPending = null; sessionStorage.setItem(legacyKey,JSON.stringify(legacyPair));
          sessionStorage.removeItem(legacyPendingKey);
        } else if (['rejected','cancelled','expired'].includes(state.status)) {
          legacyPending = null; sessionStorage.removeItem(legacyPendingKey);
          showPair('Legacy pairing expired or was declined in Wodra. Request a new pairing code.'); return;
        } else { msg.textContent = 'Waiting for desktop approval for this older pairing request.'; return; }
      }
      if (legacyPair) {
        render(await post({action:'read',id:legacyPair.id,token_hash:await digest(legacyPair.token)},1));
      } else if (trusted) {
        render(await post({action:'remembered_read'}));
      }
      failures = 0;
    } catch (error) {
      if (legacyPair && ['expired','not_found','unauthorized'].includes(error.message)) {
        legacyPair = null; sessionStorage.removeItem(legacyKey); showPair('This older pairing is no longer available. Pair this phone again.'); return;
      }
      if (!legacyPair && ['expired','not_found','unauthorized'].includes(error.message)) {
        trusted = false;
        if (error.message === 'unauthorized') { showPair('This phone is not remembered on this browser. Pair it with Wodra to continue.'); return; }
        showPair('This phone credential expired. Pair it again to restore access.'); return;
      }
      failures++;
      if (trusted) {
        badge.textContent = 'Reconnecting'; badge.className = 'pill warn';
        $('ride-state').textContent = error.message === 'protocol_mismatch'
          ? failureMessage(error) : 'Connection unavailable. Retrying automatically; phone trust is unchanged.';
      } else if (legacyPending) msg.textContent = failureMessage(error);
    } finally { busy = false; }
  }
  function schedule() {
    clearTimeout(timer);
    if ((trusted || legacyPair || legacyPending) && !document.hidden)
      timer = setTimeout(async () => { await poll(); schedule(); }, Math.min(12000, 3000 * Math.max(1, failures)));
  }
  async function claim(code) {
    msg.textContent = 'Pairing this phone securely…';
    try {
      const response = await post({action:'remembered_claim',code});
      if (response.status === 'paired') {
        trusted = true; $('reconnect').hidden = true; msg.textContent = 'Phone remembered. Connecting to Wodra…'; pairView.hidden = true; rideView.hidden = false;
        await poll(); schedule(); return;
      }
      throw new Error('pairing_unavailable');
    } catch (error) {
      if (!['not_found','expired','rate_limited','protocol_mismatch','invalid_request'].includes(error.message)) {
        msg.textContent = failureMessage(error); return;
      }
      // Existing Alpha.77 installations still use protocol v1 and desktop approval.
      try {
        const phone = randomToken();
        const result = await post({action:'claim',code,companion_hash:await digest(phone)},1);
        if (result.status !== 'pending' || !result.id) throw new Error('pairing_unavailable');
        legacyPending = {id:result.id,token:phone}; sessionStorage.setItem(legacyPendingKey,JSON.stringify(legacyPending));
        msg.textContent = 'This Wodra version uses the older pairing approval flow. Keep this page open.'; await poll(); schedule();
      } catch {
        msg.textContent = error.message === 'not_found' ? 'Code is wrong, expired or already used. Request a new one in Wodra.' : 'Pairing could not be requested. Try again.';
      }
    }
  }
  async function digest(value) {
    const bytes = await crypto.subtle.digest('SHA-256',new TextEncoder().encode(value));
    return [...new Uint8Array(bytes)].map(byte=>byte.toString(16).padStart(2,'0')).join('');
  }
  function randomToken() {
    const bytes=crypto.getRandomValues(new Uint8Array(32));
    return btoa(String.fromCharCode(...bytes)).replaceAll('+','-').replaceAll('/','_').replaceAll('=','');
  }
  $('pair-form').addEventListener('submit',event=>{event.preventDefault();const code=$('pair-code').value.replace(/[^a-z0-9]/gi,'').toUpperCase();if(code.length!==10){msg.textContent='Enter the 10-character code from Wodra.';return;}void claim(code);});
  $('disconnect').addEventListener('click',()=>{clearTimeout(timer);trusted=false;legacyPair=null;legacyPending=null;sessionStorage.removeItem(legacyKey);sessionStorage.removeItem(legacyPendingKey);showPair('Disconnected from this ride. Your remembered phone trust is unchanged.');$('reconnect').hidden=false;});
  $('reconnect').addEventListener('click',()=>{trusted=true;$('reconnect').hidden=true;void poll().then(schedule);});
  $('forget-phone').addEventListener('click',async()=>{try{await post({action:'remembered_forget'});}catch{}clearTimeout(timer);trusted=false;legacyPair=null;legacyPending=null;sessionStorage.removeItem(legacyKey);sessionStorage.removeItem(legacyPendingKey);showPair('This phone has been forgotten.');$('reconnect').hidden=true;});
  $('wake-toggle').addEventListener('change',()=>{try{localStorage.setItem(wakePreferenceKey,$('wake-toggle').checked?'true':'false');}catch{};});
  document.addEventListener('visibilitychange',()=>{clearTimeout(timer);if(!document.hidden){void poll();schedule();}});
  const fragment=location.hash.slice(1);if(fragment){$('pair-code').value=fragment.replace(/[^a-z0-9]/gi,'').slice(0,10).toUpperCase();history.replaceState(null,'',location.pathname+location.search);}
  try { legacyPair=JSON.parse(sessionStorage.getItem(legacyKey)||'null'); legacyPending=legacyPair?null:JSON.parse(sessionStorage.getItem(legacyPendingKey)||'null'); } catch { sessionStorage.removeItem(legacyKey);sessionStorage.removeItem(legacyPendingKey); }
  if (legacyPair || legacyPending) { void poll(); schedule(); }
  else {
    void post({action:'remembered_read'}).then(render).then(schedule).catch(error => {
      if (['unauthorized','expired','not_found'].includes(error.message)) { showPair(failureMessage(error)); return; }
      showPair(failureMessage(error));
      trusted = true;
      schedule();
    });
  }
})();
