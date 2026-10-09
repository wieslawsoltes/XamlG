// Ordered mutations never infer authority from a presentation annotation.
async function publishContext(result,generation){if(current(generation)&&capabilities.updateModelContext)await request('ui/update-model-context',{structuredContent:{surfaceId:result.id,sessionId:result.sessionId,revision:result.revision,stateRevision:result.stateRevision,state:result.state}});}
function changeState(entry,explicit){
  if(applying||!snapshot||entry.disabled||!entry.node.stateKey||entry.node.properties.IsReadOnly)return Promise.resolve();
  const value=explicit!==undefined?explicit:toggles.has(entry.type)?entry.input.checked:['Slider','NumericUpDown'].includes(entry.type)?entry.input.value===''?null:Number(entry.input.value):dates.has(entry.type)?entry.input.value?entry.input.value+'T00:00:00Z':null:entry.type==='TimePicker'?entry.input.value||null:['ComboBox','ListBox'].includes(entry.type)?entry.input.selectedIndex:entry.input.value;
  const generation=epoch,revision=snapshot.revision,id=snapshot.id,key=entry.node.stateKey,nodeKey=entry.node.key;
  return enqueue(async()=>{
    if(!snapshot||snapshot.id!==id||snapshot.revision!==revision||entries.get(nodeKey)!==entry)return;
    if(JSON.stringify(snapshot.state?.[key])===JSON.stringify(value)){if(drafts.get(nodeKey)===value)drafts.delete(nodeKey);return;}
    error('');
    try{
      const result=await tool('xamlg_ui_state',{id,expectedRevision:revision,expectedStateRevision:snapshot.stateRevision,key,value});
      if(current(generation)){if(drafts.get(nodeKey)===value)drafts.delete(nodeKey);accept(result);await publishContext(result,generation);}
    }catch(failure){if(current(generation)){drafts.delete(nodeKey);error(failure);await refresh();}throw failure;}
  },generation);
}
function touchForm(entry){
  if(applying||disposed||entry.node.form?.role!=='input'||entry.disabled||!snapshot)return;
  const generation=epoch,revision=snapshot.revision,id=snapshot.id,formKey=entry.node.form.id,fieldKey=entry.node.key;
  enqueue(async()=>{
    if(!snapshot||snapshot.id!==id||snapshot.revision!==revision)return;
    const form=formState(formKey);if(!form?.fields.some(field=>field.key===fieldKey&&!field.touched))return;
    const next=await tool('xamlg_ui_form_touch',{id,expectedRevision:revision,expectedStateRevision:snapshot.stateRevision,formKey,fieldKey});if(current(generation))accept(next);
  },generation).catch(failure=>{if(current(generation))error(failure);});
}
async function prepare(entry,explicitCall=null){
  if(entry.disabled||!snapshot||disposed)return;
  if(!explicitCall&&entry.node.form?.role==='submit'){await submitForm(entry.node.form.id);return;}
  const generation=epoch,revision=snapshot.revision,id=snapshot.id,nodeKey=entry.node.key;
  return enqueue(async()=>{
    if(busy||!snapshot||snapshot.id!==id||snapshot.revision!==revision||entries.get(nodeKey)!==entry)return;
    const call=explicitCall||{id,expectedRevision:revision,expectedStateRevision:snapshot.stateRevision,nodeKey};
    const local=Array.isArray(snapshot.actions)&&snapshot.actions.some(action=>action.id===entry.node.actionId&&action.kind==='state');busy=true;error('');
    try{
      if(local){const result=await tool('xamlg_ui_state_action',call);busy=false;if(current(generation)){accept(result);await publishContext(result,generation);}return;}
      const intent=await tool('xamlg_ui_action',call);if(!current(generation))return;
      review={call,intent,generation};$('intent').textContent=JSON.stringify(intent,null,2);$('review').hidden=false;
    }catch(failure){if(current(generation))error(failure);}finally{busy=false;if(local&&current(generation)&&snapshot)accept(snapshot);}
  },generation).catch(failure=>{if(current(generation))error(failure);});
}
async function confirm(){
  if(busy||!review)return;const reviewed=review;busy=true;$('confirm').disabled=true;error('');
  try{
    const intent=await tool('xamlg_ui_action',reviewed.call);if(!current(reviewed.generation))return;
    if(JSON.stringify(intent)!==JSON.stringify(reviewed.intent))throw new Error('The action changed. Review it again.');
    if(intent.kind==='tool')await tool(intent.tool,intent.arguments||{});
    else if(intent.kind==='message'){if(!capabilities.message)throw new Error('This host does not support follow-up messages.');await request('ui/message',{role:'user',content:[{type:'text',text:intent.text}]});}
    else if(intent.kind==='openUrl'){if(!capabilities.openLinks)throw new Error('This host does not support opening links.');const url=new URL(intent.text);if(!['https:','http:'].includes(url.protocol)||url.username||url.password)throw new Error('Unsupported URL.');await request('ui/open-link',{url:url.href});}
    else if(intent.kind==='copy'){if(!navigator.clipboard)throw new Error('Clipboard is unavailable. Select the review text to copy it.');await navigator.clipboard.writeText(intent.text);}else throw new Error('Unsupported action.');
    if(current(reviewed.generation)){review=null;$('review').hidden=true;status('Action completed.');}
  }catch(failure){if(current(reviewed.generation))error(failure);}finally{busy=false;$('confirm').disabled=false;}
}
