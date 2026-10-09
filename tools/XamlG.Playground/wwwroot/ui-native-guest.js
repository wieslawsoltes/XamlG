// A public guest must be physically opaque-origin, not merely cross-origin to its parent.
export function isIsolated(){return parent!==window&&globalThis.origin==='null';}
export function connect(dotnet,mode){
 if(!isIsolated()||!['mcp','execution'].includes(mode))throw new Error('Opaque-origin sandbox required for intelligent UI guests.');
 let sequence=0,disposed=false,ready=false,executionStarted=false,hostOrigin=null,marker=null,queued=null,capabilities={};
 const pending=new Map(),post=m=>{if(!disposed)parent.postMessage(m,hostOrigin&&hostOrigin!=='null'?hostOrigin:'*');};
 const notify=(method,params)=>post({jsonrpc:'2.0',method,params});
 const request=(method,params)=>new Promise((resolve,reject)=>{
  if(disposed||pending.size>=16){reject(new Error('UI request limit.'));return;}
  const id=++sequence,timer=setTimeout(()=>{pending.delete(id);reject(new Error('UI host request timed out.'));},30000);
  pending.set(id,{resolve,reject,timer,method});post({jsonrpc:'2.0',id,method,params});
 });
 const bounded=value=>{if(JSON.stringify(value).length>2097152)throw new Error('UI host result exceeds 2 MiB.');return value;};
 const tool=async(name,args)=>{
  if(mode!=='mcp'||!ready||!capabilities.serverTools)throw new Error('Host-mediated tools are unavailable.');
  const result=bounded(await request('tools/call',{name,arguments:args}));
  if(result.isError)throw new Error(result.content?.find(item=>item.type==='text')?.text||'Tool failed.');
  return result.structuredContent??JSON.parse(result.content?.find(item=>item.type==='text')?.text||'null');
 };
 const refresh=async()=>{
  if(!marker||!ready)return;
  const snapshot=await tool('xamlg_ui_read',{id:marker.id});
  if(snapshot?.id!==marker.id||snapshot?.sessionId!==marker.sessionId)throw new Error('The UI session was released or replaced.');
  await dotnet.invokeMethodAsync('ReceiveSnapshot',snapshot);
 };
 const fail=error=>dotnet.invokeMethodAsync('ReceiveError',String(error?.message??error).slice(0,4096)).catch(()=>{});
 async function receive(event){
  if(disposed||event.source!==parent||!isIsolated())return;const m=event.data;
  if(mode==='execution'){
   if(m?.type==='xamlg-ui-execute'&&typeof m.nonce==='string'&&/^[a-f0-9]{32}$/i.test(m.nonce)){
    if(executionStarted){post({type:'xamlg-ui-execution-result',nonce:m.nonce,error:'Create a fresh frame for new approved source.'});return;}
    executionStarted=true;hostOrigin=event.origin;
    try{const result=await dotnet.invokeMethodAsync('ExecuteApproved',bounded(m.request));post({type:'xamlg-ui-execution-result',nonce:m.nonce,result});}
    catch(error){post({type:'xamlg-ui-execution-result',nonce:m.nonce,error:String(error.message).slice(0,4096)});}
   }return;
  }
  if(!m||m.jsonrpc!=='2.0')return;
  try{
   bounded(m);
   if(m.id!==undefined&&!m.method){const call=pending.get(m.id);if(!call)return;pending.delete(m.id);clearTimeout(call.timer);if(call.method==='ui/initialize')hostOrigin=event.origin;if(m.error)call.reject(new Error(m.error.message||'Host rejected the request.'));else call.resolve(m.result);return;}
   if(m.method==='ui/notifications/tool-result'){
    if(!ready){queued=m.params;return;}const value=m.params?.structuredContent;
    if(value?.format==='xamlg.intelligent-ui/1'&&typeof value.id==='string'&&typeof value.sessionId==='string'&&value.sessionId.length===32){marker=value;await refresh();}
   }else if(m.method==='notifications/resources/updated'&&marker&&m.params?.uri==='xamlg://ui/'+encodeURIComponent(marker.id))await refresh();
   else if(m.method==='ui/notifications/host-context-changed'&&ready)await dotnet.invokeMethodAsync('HostContextChanged',m.params??{});
   else if(m.method==='ui/resource-teardown'&&m.id!==undefined){post({jsonrpc:'2.0',id:m.id,result:{}});dispose();}
  }catch(error){fail(error);}
 }
 function dispose(){if(disposed)return;disposed=true;removeEventListener('message',receive);observer.disconnect();for(const call of pending.values()){clearTimeout(call.timer);call.reject(new Error('UI guest disposed.'));}pending.clear();}
 addEventListener('message',receive);let lastHeight=0;
 const observer=new ResizeObserver(()=>{const height=Math.min(1600,Math.max(240,document.documentElement.scrollHeight));if(ready&&height!==lastHeight){lastHeight=height;notify('ui/notifications/size-changed',{height});}});observer.observe(document.body);
 if(mode==='mcp')request('ui/initialize',{protocolVersion:'2026-01-26',appInfo:{name:'XamlG Native Avalonia',version:'1.0.0'},appCapabilities:{}}).then(async result=>{
  if(result.protocolVersion!=='2026-01-26')throw new Error('Unsupported MCP Apps protocol version.');capabilities=result.hostCapabilities??{};ready=true;await dotnet.invokeMethodAsync('HostContextChanged',result.hostContext??{});notify('ui/notifications/initialized',{});
  if(queued){const value=queued.structuredContent;queued=null;if(value?.format==='xamlg.intelligent-ui/1'){marker=value;await refresh();}}
 }).catch(fail);else{ready=true;post({type:'xamlg-ui-execution-ready'});}
 return{tool,refresh,dispose,context:state=>capabilities.updateModelContext?request('ui/update-model-context',{structuredContent:state}):Promise.resolve(),async action(intent){
  if(mode!=='mcp')throw new Error('Execution guests have no host-action bridge.');
  if(intent.kind==='tool')return tool(intent.tool,intent.arguments??{});
  if(intent.kind==='message'){if(!capabilities.message)throw new Error('Host messages unavailable.');return request('ui/message',{role:'user',content:[{type:'text',text:intent.text}]});}
  if(intent.kind==='openUrl'){const url=new URL(intent.text);if(!['https:','http:'].includes(url.protocol)||url.username||url.password)throw new Error('Unsupported URL.');if(!capabilities.openLinks)throw new Error('Host links unavailable.');return request('ui/open-link',{url:url.href});}
  if(intent.kind==='copy'){if(!navigator.clipboard)throw new Error('Clipboard unavailable.');return navigator.clipboard.writeText(intent.text);}throw new Error('Unknown UI action.');
 }};
}
