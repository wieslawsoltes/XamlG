async function refresh(){
  if(!ready||!marker||disposed)return;if(refreshing){refreshAgain=true;return;}refreshing=true;
  try{do{refreshAgain=false;const generation=epoch,id=marker.id;
    try{const value=await tool('xamlg_ui_read',{id});if(current(generation))accept(value);}
    catch(failure){if(current(generation)){error(failure);if(/replaced|released|no longer available|unknown_surface/.test(String(failure.message)))retireSurface();}}
  }while(refreshAgain&&!disposed&&marker);}finally{refreshing=false;}
}
async function receiveResult(result){
  if(!ready){queued=result;return;}if(result?.isError){error(result.content?.find(item=>item.type==='text')?.text||'Tool failed.');return;}
  const value=result?.structuredContent;if(!validMarker(value))return;const replaced=marker&&(value.id!==marker.id||value.sessionId!==marker.sessionId);
  epoch++;marker=value;drafts.clear();submitting.clear();review=null;$('review').hidden=true;if(replaced)retireSurface();$('fallback').textContent=String(value.fallbackMarkdown||'').slice(0,131072);await refresh();
}
function context(value){if(!value)return;document.documentElement.style.colorScheme=value.theme==='dark'?'dark':'light';const size=value.containerDimensions;if(size?.height)root.style.maxHeight=px(Math.min(size.height,1600));scheduleLayout();}
function teardown(){
  if(disposed)return;disposed=true;ready=false;epoch++;queued=null;marker=null;review=null;resize.disconnect();boxResize.disconnect();cancelAnimationFrame(layoutFrame);window.removeEventListener('message',onMessage);
  for(const call of pending.values()){clearTimeout(call.timer);call.reject(new Error('UI disposed.'));}pending.clear();
  for(const entry of entries.values())entry.dispose?.();entries.clear();drafts.clear();submitting.clear();snapshot=null;root.replaceChildren();
}
function onMessage(event){
  if(disposed||event.source!==parent||!event.data||event.data.jsonrpc!=='2.0'||origin!==null&&event.origin!==origin)return;
  const message=event.data;
  try{if(JSON.stringify(message).length>2097152)return;}catch{return;}
  if(message.id!==undefined&&!message.method){const call=pending.get(message.id);if(!call)return;if(call.method==='ui/initialize')origin=event.origin;clearTimeout(call.timer);pending.delete(message.id);if(message.error)call.reject(new Error(message.error.message||'Host rejected request.'));else call.resolve(message.result);return;}
  if(message.method==='ui/notifications/tool-result'&&!ready){queued=message.params;return;}if(!ready||origin===null)return;
  if(message.id!==undefined){if(message.method==='ui/resource-teardown'){post({jsonrpc:'2.0',id:message.id,result:{}});teardown();return;}post({jsonrpc:'2.0',id:message.id,...(message.method==='ping'?{result:{}}:{error:{code:-32601,message:'Unsupported UI request'}})});return;}
  if(message.method==='ui/notifications/tool-result')receiveResult(message.params).catch(error);else if(message.method==='ui/notifications/host-context-changed')context(message.params);
  else if(message.method==='notifications/resources/updated'&&marker&&message.params?.uri==='xamlg://ui/'+encodeURIComponent(marker.id))refresh();
  else if(message.method==='ui/notifications/tool-input-partial'||message.method==='ui/notifications/tool-input')status('Receiving UI update…');else if(message.method==='ui/notifications/tool-cancelled')status('UI update cancelled. The last committed view is retained.');
}
const boxResize=new ResizeObserver(changes=>{for(const change of changes){const entry=viewboxes.get(change.target);if(entry?.layout)entry.layout();else if(entry)layoutViewbox(entry);}});
let lastHeight=0;const resize=new ResizeObserver(()=>{const height=Math.min(2000,Math.max(100,Math.ceil(document.body.scrollHeight)));if(ready&&height!==lastHeight){lastHeight=height;notify('ui/notifications/size-changed',{height});}});resize.observe(document.body);
$('refresh').addEventListener('click',()=>{error('');refresh();});$('confirm').addEventListener('click',confirm);$('cancel').addEventListener('click',()=>{review=null;$('review').hidden=true;});
window.addEventListener('message',onMessage);window.addEventListener('pagehide',teardown,{once:true});
request('ui/initialize',{protocolVersion:'2026-01-26',appInfo:{name:'XamlG Intelligent UI',version:'1.0.0'},appCapabilities:{}}).then(result=>{
  if(result.protocolVersion!=='2026-01-26')throw new Error('Unsupported MCP Apps protocol version.');capabilities=result.hostCapabilities||{};ready=true;context(result.hostContext);notify('ui/notifications/initialized',{});status('Waiting for a UI tool result.');$('refresh').disabled=!capabilities.serverTools;if(queued){const latest=queued;queued=null;return receiveResult(latest);}
}).catch(error);
