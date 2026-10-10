import { readExecutionConfig, executionAssets } from './ui-execution-policy.js';

// Render-host client. Only a reviewed execution frame may create this capability.
// The isolated iframe and its dedicated worker receive assets and UI declarations, never tools.
export async function create(assetBase) {
  const base = new URL(assetBase);
  if (globalThis.origin !== 'null' || !['https:','http:'].includes(base.protocol) || base.username || base.password || !base.pathname.endsWith('/'))
    throw new Error('Worker execution is available only inside the approved opaque frame.');
  const controller = new AbortController();
  const frame = document.createElement('iframe'); frame.title = 'Isolated C# worker'; frame.sandbox = 'allow-scripts'; frame.hidden = true;
  const channel = new MessageChannel(), waiting = new Map();
  let disposed = false, sequence = 0, resolveConnected, rejectConnected, resolveReady, rejectReady;
  const connected = new Promise((resolve,reject) => { resolveConnected=resolve;rejectConnected=reject; }); connected.catch(()=>{});
  const ready = new Promise((resolve,reject) => { resolveReady=resolve;rejectReady=reject; }); ready.catch(()=>{});
  const bootTimer = setTimeout(() => dispose('Execution worker startup timed out.'),120000);
  function dispose(message='Execution worker reset.') {
    if(disposed)return;disposed=true;controller.abort();clearTimeout(bootTimer);
    rejectConnected(new Error(message));rejectReady(new Error(message));
    for(const item of waiting.values()){clearTimeout(item.timer);item.reject(new Error(message));}waiting.clear();
    try{channel.port1.postMessage({type:'dispose'});}catch{}
    channel.port1.close();removeEventListener('message',onMessage);frame.remove();
  }
  function onMessage(event) {
    if(disposed||event.source!==frame.contentWindow)return;
    if(event.data?.type==='xamlg-worker-supervisor-rejected'){dispose('The worker supervisor is not isolated.');return;}
    if(event.data?.type==='xamlg-worker-supervisor-ready')frame.contentWindow.postMessage({type:'xamlg-worker-connect'},'*',[channel.port2]);
  }
  channel.port1.onmessage = event => {
    if(disposed)return;const message=event.data;
    if(message?.type==='connected'){resolveConnected();return;}
    if(message?.type==='ready'){clearTimeout(bootTimer);resolveReady(message.limits);return;}
    if(message?.type==='fatal'){dispose(message.error||'Execution worker stopped.');return;}
    if(message?.type!=='result'||!waiting.has(message.id)){dispose('Invalid isolated worker response.');return;}
    const item=waiting.get(message.id);waiting.delete(message.id);clearTimeout(item.timer);
    if(typeof message.json!=='string'||message.json.length>2097152){dispose('Oversized execution result.');item.reject(new Error('Oversized execution result.'));}
    else if(message.error)item.reject(new Error(String(message.error).slice(0,4096)));else item.resolve(message.json);
  };
  channel.port1.start();addEventListener('message',onMessage);
  frame.src=new URL('ui-execution-worker.html',base).href;document.body.append(frame);
  try {
    const read = async path => {
      const response=await fetch(new URL(path,base),{credentials:'omit',signal:controller.signal,cache:'no-cache'});
      if(!response.ok)throw new Error('Cannot load isolated runtime asset '+path+': '+response.status);
      const declared=Number(response.headers.get('content-length')||0);if(declared>67108864)throw new Error('Execution asset is too large.');
      const bytes=await response.arrayBuffer();if(bytes.byteLength>67108864)throw new Error('Execution asset is too large.');return bytes;
    };
    const manifest=new TextDecoder().decode(await read('ui-execution-assets.txt')).replace(/^\uFEFF/,'');
    const paths=manifest.split(/\r?\n/).map(path=>path.trim()).filter(Boolean);
    if(paths.length<5||paths.length>2048||new Set(paths).size!==paths.length||paths.some(path=>!/^(?:_framework|references)\/[A-Za-z0-9_.\/-]+$/.test(path)||path.includes('..')))throw new Error('Invalid execution asset manifest.');
    const loader=paths.find(path=>/^_framework\/dotnet(?:\.[A-Za-z0-9_-]+)?\.js$/.test(path)&&!path.includes('native')&&!path.includes('runtime')&&!path.includes('boot'));
    const boot=paths.find(path=>/^_framework\/dotnet\.boot(?:\.[A-Za-z0-9_-]+)?\.(?:js|json)$/.test(path));
    if(!loader)throw new Error('The published .NET loader is required for isolation.');
    const loaderBytes=await read(loader);
    // .NET 10 production builds can embed their manifest in dotnet.js instead of
    // publishing dotnet.boot.js. Read its JSON payload without executing loader code.
    const config=readExecutionConfig(new TextDecoder().decode(boot?await read(boot):loaderBytes));
    const selected=executionAssets(config,paths,loader);
    const files=new Array(selected.length);let index=0,total=0;
    await Promise.all(Array.from({length:6},async()=>{
      while(index<selected.length){const current=index++,name=selected[current],bytes=name===loader?loaderBytes:await read(name);total+=bytes.byteLength;if(total>536870912)throw new Error('Execution assets exceed 512 MiB.');files[current]={name,bytes};}
    }));
    const source='const executionPolicySource=' + JSON.stringify(new TextDecoder().decode(await read('ui-execution-policy.js'))) + ';\n' +
      new TextDecoder().decode(await read('ui-execution-worker.js'));
    await connected;
    channel.port1.postMessage({type:'bootstrap',base:base.href,source,config,loader,files},files.map(file=>file.bytes));
    const limits=await ready;
    return {
      limits,
      call(method,json){
        if(disposed||waiting.size!==0)return Promise.reject(new Error('Execution worker is unavailable or busy.'));
        const id=++sequence;
        return new Promise((resolve,reject)=>{
          const timer=setTimeout(()=>dispose('C# deadline exceeded; the isolated worker was discarded.'),method==='publish'?22000:5000);
          waiting.set(id,{resolve,reject,timer});channel.port1.postMessage({type:'call',id,method,json});
        });
      },dispose
    };
  }catch(error){dispose(error.message);throw error;}
}
