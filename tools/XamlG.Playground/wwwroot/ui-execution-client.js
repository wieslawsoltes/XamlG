// Owner-controlled bridge. It accepts no tools, URLs, storage commands or agent requests.
export function create(host, assetBase) {
  const base = new URL(assetBase);
  if (base.origin !== location.origin || !base.pathname.endsWith('/')) throw new Error('The execution guest must use this deployment.');
  const frame = document.createElement('iframe'); frame.title = 'Approved full C# preview'; frame.sandbox = 'allow-scripts';
  frame.style.cssText = 'width:100%;height:560px;border:1px solid var(--ide-border,#777);background:transparent';
  let disposed = false, used = false, ready = false, pending = null, readyResolve, readyReject;
  const readyPromise = new Promise((resolve,reject)=>{readyResolve=resolve;readyReject=reject;});readyPromise.catch(()=>{});
  const readyTimer = setTimeout(()=>dispose('The approved preview did not become ready.'),120000);
  function dispose(message='Approved execution was reset.') {
    if(disposed)return;disposed=true;clearTimeout(readyTimer);removeEventListener('message',onMessage);
    readyReject(new Error(message));
    if(pending){clearTimeout(pending.timer);pending.reject(new Error(message));pending=null;}
    frame.remove();
  }
  function onMessage(event) {
    if(disposed||event.source!==frame.contentWindow)return;
    const message=event.data;
    if(message?.type==='xamlg-ui-execution-ready') {ready=true;clearTimeout(readyTimer);readyResolve();return;}
    if(message?.type!=='xamlg-ui-execution-result'||!pending||message.nonce!==pending.nonce)return;
    if(JSON.stringify(message).length>262144){dispose('The approved execution result is too large.');return;}
    const task=pending;pending=null;clearTimeout(task.timer);
    if(message.error){task.reject(new Error(String(message.error).slice(0,4096)));dispose('Approved execution failed and was discarded.');}
    else task.resolve(message.result);
  }
  addEventListener('message',onMessage);frame.src=new URL('ui-execution.html',base).href;host.replaceChildren(frame);
  return {
    async execute(declaration) {
      if(disposed||used)throw new Error('Review a new declaration in a fresh execution frame.');
      used=true;if(JSON.stringify(declaration).length>1048576)throw new Error('The approved declaration exceeds its bound.');
      if(!ready)await readyPromise;if(disposed)throw new Error('Execution frame is retired.');
      const nonce=crypto.randomUUID().replaceAll('-','');
      return new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>dispose('Approved execution timed out and its worker was discarded.'),150000);
        pending={nonce,resolve,reject,timer};frame.contentWindow.postMessage({type:'xamlg-ui-execute',nonce,request:declaration},'*');
      });
    },dispose
  };
}
