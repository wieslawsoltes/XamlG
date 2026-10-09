// Owner-only frame launcher. Only an explicit reviewed UI action calls execute().
export function create(host, assetBase) {
  const base=new URL(assetBase);
  if(base.origin!==location.origin)throw new Error('Execution assets must belong to this Studio deployment.');
  const frame=document.createElement('iframe');frame.title='Approved full C# preview';frame.sandbox='allow-scripts';
  frame.style.cssText='width:100%;height:560px;border:1px solid #666';
  let disposed=false, used=false, current=null, resolveReady,rejectReady;
  const ready=new Promise((resolve,reject)=>{resolveReady=resolve;rejectReady=reject;});
  // Mark the rejection observed even when the owner resets before execute attaches.
  ready.catch(()=>{});
  const bootTimer=setTimeout(()=>rejectReady(new Error('The isolated runtime did not start.')),120000);
  function receive(event){
    if(disposed||event.source!==frame.contentWindow)return;
    const message=event.data;
    if(message?.type==='xamlg-ui-execution-ready'){clearTimeout(bootTimer);resolveReady();}
    if(message?.type==='xamlg-ui-execution-result'&&current&&message.nonce===current.nonce){
      const item=current;current=null;clearTimeout(item.timer);
      if(JSON.stringify(message).length>262144)item.reject(new Error('Execution result exceeds its bound.'));
      else if(message.error)item.reject(new Error(String(message.error)));else item.resolve(message.result);
    }
    // Deliberately do not process tools/call, MCP, navigation, storage or agent messages.
  }
  addEventListener('message',receive);frame.src=new URL('ui-execution.html',base).href;host.replaceChildren(frame);
  return {
    async execute(request){
      if(disposed||used)throw new Error('Create a fresh frame for each approved source.');
      used=true;await ready;if(disposed)throw new Error('Execution was reset.');
      if(JSON.stringify(request).length>1048576)throw new Error('Execution declaration exceeds its bound.');
      const nonce=crypto.randomUUID().replaceAll('-','');
      return new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>{current=null;reject(new Error('Execution timed out. Reset the frame to discard it.'));},120000);
        current={nonce,resolve,reject,timer};frame.contentWindow.postMessage({type:'xamlg-ui-execute',nonce,request},'*');
      });
    },
    dispose(){if(disposed)return;disposed=true;clearTimeout(bootTimer);rejectReady(new Error('Execution reset.'));if(current){clearTimeout(current.timer);current.reject(new Error('Execution reset.'));current=null;}removeEventListener('message',receive);frame.remove();}
  };
}
