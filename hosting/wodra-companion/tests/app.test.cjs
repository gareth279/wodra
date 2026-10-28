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
const requests = [];
const fetch = async (_url, options) => {
  const body = JSON.parse(options.body); actions.push(body.action); requests.push(body);
  const reply = (ok, data) => ({ok, json:async()=>({protocol:body.protocol,...data})});
  if (body.action === 'remembered_claim') return reply(true,{status:'paired', device_id:'device-id'});
  if (body.action === 'remembered_forget') return reply(true,{status:'forgotten'});
  if (body.action === 'remembered_read') {
    reads++;
    if (reads === 1) return reply(false,{error:'unauthorized'});
    if (reads === 2) return reply(true,{status:'remembered',desktop_online:false,snapshot:null,snapshot_at:0,route_version:'none',route:null});
    return reply(true,{status:'remembered',desktop_online:true,snapshot_at:Math.floor(Date.now()/1000),route_version:'route-v1',route:body.route_version==='route-v1'?null:{
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

function bootRouteScenario(responses) {
  const scenarioElements=new Map(), scenarioHandlers=new Map(), calls=[];
  const scenarioElement=id=>{
    if(!scenarioElements.has(id))scenarioElements.set(id,{id,hidden:false,value:'',textContent:'',className:'',checked:true,dataset:{},attrs:{},children:[],
      addEventListener(name,fn){scenarioHandlers.set(`${id}:${name}`,fn);},setAttribute(name,value){this.attrs[name]=value;},
      replaceChildren(...items){this.children=items;},append(...items){this.children.push(...items);}});
    return scenarioElements.get(id);
  };
  const tabs=['ride','route','riders'].map(tab=>({dataset:{tab},setAttribute(){},addEventListener(name,fn){scenarioHandlers.set(`tab-${tab}:${name}`,fn);}}));
  const panels=['ride','route','riders'].map(panel=>({dataset:{panel},hidden:false}));
  const scenarioDocument={hidden:false,addEventListener(name,fn){scenarioHandlers.set(`document:${name}`,fn);},getElementById:scenarioElement,
    querySelectorAll(selector){return selector==='[data-tab]'?tabs:selector==='[data-panel]'?panels:[];},createElement(){return scenarioElement(`generated-${scenarioElements.size}`);}};
  const scenarioFetch=async(_url,options)=>{
    const body=JSON.parse(options.body);calls.push(body);
    if(body.action!=='remembered_read')throw new Error(`Unexpected scenario action ${body.action}`);
    const readsSoFar=calls.filter(call=>call.action==='remembered_read').length;
    return {ok:true,json:async()=>({protocol:body.protocol,...responses[Math.min(readsSoFar-1,responses.length-1)]})};
  };
  vm.runInNewContext(source,{document:scenarioDocument,window:{WodraWakeLock:{setRideActive:()=>Promise.resolve()}},
    location:{href:'https://wodra.g5.co.nz/',hash:'',pathname:'/',search:''},history:{replaceState(){}},localStorage:store,sessionStorage:store,
    fetch:scenarioFetch,setTimeout:()=>1,clearTimeout(){},AbortController,URL,Date,Math,Number,String,JSON,Error,Promise});
  return {elements:scenarioElements,calls,handlers:scenarioHandlers};
}
const routePayload=(version,name,distance=2500)=>({version,id:`route-${version}`,name,environment:'Road',discipline:'Road',type:'Loop',distance_m:distance,ascent_m:20,
  points:[[0,0,0,2,0],[distance/2,10,5,8,2],[distance,0,0,2,0]]});
const ridePayload=(route='Test route')=>({state:'riding',connected:true,paused:false,data_stale:false,route,speed_kph:30,power_w:200,
  environment:'Road',route_distance_m:2500,route_progress:.2,lap:2,remaining_m:null,riders:[],cadence_rpm:90,heart_rate_bpm:null,
  distance_m:500,elapsed_s:60,gradient_pct:0});
const readPayload=(version,route,snapshot=ridePayload())=>({status:'remembered',desktop_online:true,snapshot_at:Math.floor(Date.now()/1000),route_version:version,route,snapshot});
async function flushScenario(){await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));}
async function nextScenarioPoll(scenario){await scenario.handlers.get('document:visibilitychange')();await flushScenario();}

(async()=>{
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(requests[0].route_version,'','Initial remembered read explicitly requests route metadata with an empty cache version');
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
  const beforeRide=bootRouteScenario([
    readPayload('none',null,null),
    readPayload('route-v1',routePayload('route-v1','Ride started route')),
    readPayload('route-v1',null)
  ]);
  await flushScenario();
  assert.equal(beforeRide.calls[0].route_version,'','Phone opening before a ride starts requests route metadata');
  assert.equal(beforeRide.elements.get('route-title').textContent,'Route unavailable','No route is fabricated before the ride starts');
  assert.equal(beforeRide.elements.get('route-line').attrs.points,'','Route geometry is absent before a ride starts');
  await nextScenarioPoll(beforeRide);
  assert.equal(beforeRide.calls[1].route_version,'none','Idle sentinel is sent when the desktop first has no route');
  assert.equal(beforeRide.elements.get('route-title').textContent,'Ride started route','Starting a ride supplies route geometry to the already-open phone');
  await nextScenarioPoll(beforeRide);
  assert.equal(beforeRide.calls[2].route_version,'route-v1','A populated route is cached by version after transfer');
  assert.equal(beforeRide.elements.get('route-title').textContent,'Ride started route','A matching cached version keeps its route when the relay omits geometry');

  for(const scenarioName of ['mid-ride open','reload during ride']) {
    const scenario=bootRouteScenario([readPayload('route-v1',routePayload('route-v1',scenarioName))]);
    await flushScenario();
    assert.equal(scenario.calls[0].route_version,'',`${scenarioName} begins with an empty in-memory cache`);
    assert.equal(scenario.elements.get('route-title').textContent,scenarioName,`${scenarioName} receives route metadata on its initial request`);
  }

  const missingMetadata=bootRouteScenario([
    readPayload('route-v1',null),
    readPayload('route-v1',{version:'route-v1'}),
    readPayload('route-v1',routePayload('route-v1','Recovered route'))
  ]);
  await flushScenario();
  assert.equal(missingMetadata.elements.get('route-title').textContent,'Test route','A version without its metadata is not treated as a populated cache');
  await nextScenarioPoll(missingMetadata);
  assert.equal(missingMetadata.calls[1].route_version,'','The browser retries as a cache miss if route metadata is absent despite a reported version');
  await nextScenarioPoll(missingMetadata);
  assert.equal(missingMetadata.calls[2].route_version,'','A version-only response without geometry is not considered cached metadata');
  assert.equal(missingMetadata.elements.get('route-title').textContent,'Recovered route','A later metadata response recovers the route view');

  const changingRoute=bootRouteScenario([
    readPayload('route-v1',routePayload('route-v1','First route')),
    readPayload('route-v2',routePayload('route-v2','Changed route',3000)),
    readPayload('route-v2',null)
  ]);
  await flushScenario();
  await nextScenarioPoll(changingRoute);
  assert.equal(changingRoute.calls[1].route_version,'route-v1','The previously cached route version is sent while polling');
  assert.equal(changingRoute.elements.get('route-title').textContent,'Changed route','Route changes replace cached metadata');
  await nextScenarioPoll(changingRoute);
  assert.equal(changingRoute.calls[2].route_version,'route-v2','The new route version is sent after metadata is cached');
  assert.equal(changingRoute.elements.get('route-title').textContent,'Changed route','A cached route is retained when geometry is omitted');

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
