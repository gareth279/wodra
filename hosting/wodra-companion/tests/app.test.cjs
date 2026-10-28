const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync(require('node:path').join(__dirname, '../public/app.js'), 'utf8');
const elements = new Map();
const handlers = new Map();
const saved = [];
let wakeStateCalls=0;
const element = id => {
  if (!elements.has(id)) elements.set(id, {id, hidden:false, value:'', textContent:'', className:'', checked:true,
    dataset:{}, attrs:{}, children:[], addEventListener(name, callback) { handlers.set(`${id}:${name}`, callback); },
    setAttribute(name,value){this.attrs[name]=value;}, replaceChildren(...items){this.children=items;}, append(...items){this.children.push(...items);}});
  return elements.get(id);
};
const tabs=['ride','route','riders'].map(tab=>({dataset:{tab},attrs:{},addEventListener(name,fn){handlers.set(`tab-${tab}:${name}`,fn);},setAttribute(n,v){this.attrs[n]=v;}}));
const panels=['ride','route','riders'].map(panel=>({dataset:{panel},set hidden(v){this._hidden=v;},get hidden(){return this._hidden;}}));
let reads = 0;
const actions = [];
const fetch = async (_url, options) => {
  const body = JSON.parse(options.body); actions.push(body.action);
  const reply = (ok, data) => ({ok, json:async()=>({protocol:body.protocol,...data})});
  if (body.action === 'remembered_claim') return reply(true,{status:'paired', device_id:'device-id'});
  if (body.action === 'remembered_forget') return reply(true,{status:'forgotten'});
  if (body.action === 'remembered_read') {
    reads++;
    if (reads === 1) return reply(false,{error:'unauthorized'});
    if (reads === 2) return reply(true,{status:'remembered',desktop_online:false,snapshot:null,snapshot_at:0});
    return reply(true,{status:'remembered',desktop_online:true,snapshot_at:Math.floor(Date.now()/1000),route_version:'route-v1',route:body.route_version?'':{
      version:'route-v1',name:'Test route',environment:'Road',discipline:'Road',type:'Loop',distance_m:2500,ascent_m:40,
      points:[[0,0,0,2,0],[1250,10,5,8,2],[2500,0,0,2,0]]},snapshot:{
      state:'riding',connected:true,paused:false,data_stale:false,route:'Test route',speed_kph:30,power_w:200,
      environment:'Road',route_distance_m:2500,route_progress:.2,lap:2,remaining_m:null,
      riders:reads===4?[]:[{name:'Alex',kind:'AI',relative_m:45},{name:'Taylor',kind:'Rider',relative_m:-70}],
      cadence_rpm:90,heart_rate_bpm:null,distance_m:500,elapsed_s:60,gradient_pct:0}});
  }
  throw new Error(`Unexpected companion API action ${body.action}`);
};
const document = {hidden:false, addEventListener(name, callback) { handlers.set(`document:${name}`, callback); },
  getElementById:id=>element(id), querySelectorAll(selector){return selector==='[data-tab]'?tabs:selector==='[data-panel]'?panels:[];},
  createElement(){return element(`generated-${elements.size}`);}};
const store = {getItem:()=>null, setItem:(key,value)=>saved.push(['set',key,value]), removeItem:key=>saved.push(['remove',key,null])};
const window = {WodraWakeLock:{setRideActive:()=>{wakeStateCalls++;return Promise.resolve();}}};
const context = {document, window, location:{href:'https://wodra.g5.co.nz/',hash:'',pathname:'/',search:''},
  history:{replaceState(){}}, localStorage:store, sessionStorage:store, fetch,
  setTimeout:()=>1, clearTimeout(){}, AbortController, URL, Date, Math, Number, String, JSON, Error, Promise};
vm.runInNewContext(source, context);

function bootFailure(fetchStub) {
  const scenarioElements = new Map();
  const scenarioElement = id => {
    if (!scenarioElements.has(id)) scenarioElements.set(id,{id,hidden:false,value:'',textContent:'',className:'',checked:true,
      addEventListener(){}});
    return scenarioElements.get(id);
  };
  const scenarioDocument = {hidden:false,addEventListener(){},getElementById:scenarioElement,querySelectorAll(){return [];},createElement(){return scenarioElement(`generated-${scenarioElements.size}`);}};
  vm.runInNewContext(source,{document:scenarioDocument,window:{WodraWakeLock:{setRideActive:()=>Promise.resolve()}},
    location:{href:'https://wodra.g5.co.nz/',hash:'',pathname:'/',search:''},history:{replaceState(){}},
    localStorage:store,sessionStorage:store,fetch:fetchStub,setTimeout:()=>1,clearTimeout(){},AbortController,
    URL,Date,Math,Number,String,JSON,Error,Promise});
  return scenarioElements;
}

(async()=>{
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(element('pair-view').hidden,false,'unremembered phone shows pairing form');
  element('pair-code').value='TRACE12345';
  await handlers.get('pair-form:submit')({preventDefault(){}});
  await new Promise(resolve=>setImmediate(resolve));
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(element('ride-view').hidden,false,'first-time QR/code claim automatically opens the remembered ride view');
  assert.match(element('ride-state').textContent,/Waiting for Wodra/i,'idle desktop is shown as a friendly waiting state');
  assert.equal(element('reconnect').hidden,true,'normal trusted state hides the manual reconnect action');
  await handlers.get('disconnect:click')();
  assert.equal(element('pair-view').hidden,false,'temporary disconnect returns to the pairing view');
  assert.equal(element('reconnect').hidden,false,'temporary disconnect keeps an explicit reconnect action');
  await handlers.get('reconnect:click')();
  await new Promise(resolve=>setImmediate(resolve));
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(element('ride-view').hidden,false,'remembered cookie reconnects without another pairing request');
  assert.equal(element('route-name').textContent,'Test route','reconnect renders the current ride');
  assert.equal(element('route-title').textContent,'Test route','route view uses cached route metadata');
  assert.match(element('route-line').attrs.points,/\d+,\d+/,'route map is built from real sampled geometry');
  assert.equal(element('riders-list').children.length,3,'riders view includes the current rider and actual AI/multiplayer riders');
  await handlers.get('tab-route:click')();
  assert.equal(tabs[1].attrs['aria-current'],'page','route tab selection updates the compact navigation');
  assert.equal(panels[1].hidden,false,'selected route panel is shown without a new connection');
  assert.equal(saved.some(([operation,key,value])=>operation==='set'&&key==='wodra-companion-selected-screen'&&value==='route'),true,'screen selection is saved as a local preference');
  const readCountBeforeTabChange=reads;
  const wakeCountBeforeTabChange=wakeStateCalls;
  assert.equal(reads,readCountBeforeTabChange,'screen switching does not start an additional polling request');
  assert.equal(wakeStateCalls,wakeCountBeforeTabChange,'screen switching does not release or reacquire the session wake lock');
  await handlers.get('document:visibilitychange')();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(element('riders-empty').hidden,false,'an empty nearby-rider set has a useful empty state');
  assert.equal(panels[1].hidden,false,'poll refresh preserves the selected screen');
  await handlers.get('forget-phone:click')();
  assert.equal(actions.filter(action=>action==='remembered_claim').length,1,'reconnect does not pair a second time');
  assert.equal(actions.includes('remembered_forget'),true,'forget calls server-side revocation');
  assert.equal(saved.some(([operation,key])=>operation==='set'&&(key==='wodra-companion-pair-v1'||key==='wodra-companion-pending-v1')),false,
    'protocol 2 does not store a long-lived bearer token in browser storage');
  for (const [error,expected] of [
    ['not_configured',/has not been configured/i],
    ['protocol_mismatch',/version is incompatible/i]
  ]) {
    const scenario = bootFailure(async()=>({ok:false,json:async()=>({protocol:error==='protocol_mismatch'?1:2,error:error==='invalid_response'?'unspecified':error})}));
    await new Promise(resolve=>setImmediate(resolve));
    assert.match(scenario.get('pair-message').textContent,expected,`${error} has a specific companion message`);
  }
  const malformed = bootFailure(async()=>({ok:true,json:async()=>{throw new Error('bad response');}}));
  await new Promise(resolve=>setImmediate(resolve));
  assert.match(malformed.get('pair-message').textContent,/invalid response/i,'invalid JSON has a specific companion message');
  console.log('PASS: companion claim, idle, temporary disconnect, remembered reconnect, server-side forget and browser-storage policy');
})().catch(error=>{console.error(error);process.exitCode=1;});
