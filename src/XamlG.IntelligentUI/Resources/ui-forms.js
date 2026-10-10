// Submission follows a value stamp. New edits may immediately start another submission;
// obsolete continuations cannot delete or complete the replacement operation.
async function submitForm(formKey){
  if(!snapshot||disposed)return;
  const generation=epoch,id=snapshot.id,revision=snapshot.revision,identity=generation+':'+formKey;
  const stamp=formState(formKey)?.stamp;
  if(submitting.get(identity)?.stamp===stamp)return;
  const operation={stamp};submitting.set(identity,operation);error('');
  const active=()=>current(generation)&&submitting.get(identity)===operation;
  const call=()=>({id,expectedRevision:revision,expectedStateRevision:snapshot.stateRevision,formKey});
  async function submit(){return enqueue(async()=>{
    if(!active()||!snapshot||snapshot.id!==id||snapshot.revision!==revision)return null;
    const result=await tool('xamlg_ui_form_submit',call());if(active()){accept(result.snapshot);return result;}return null;
  },generation);}
  try{
    let result=await submit();if(!result||!active())return;
    if(result.requiresValidation){
      const next=await enqueue(async()=>{
        if(!active()||!snapshot||snapshot.revision!==revision)return null;
        const value=await tool('xamlg_ui_form_validate_start',call());if(active())accept(value);return value;
      },generation);
      if(!next||!active())return;
      let validation=formState(formKey,next);const checkedStamp=validation?.stamp,nonce=validation?.validationId,deadline=Date.now()+15000;
      while(validation?.pending){
        if(Date.now()>deadline)throw new Error('Validation did not complete before the UI deadline. Refresh its status.');
        await new Promise(resolve=>setTimeout(resolve,200));if(!active())return;
        const value=await tool('xamlg_ui_read',{id});if(!active())return;
        accept(value);validation=formState(formKey);
        if(!validation||validation.stamp!==checkedStamp||snapshot.revision!==revision||validation.pending&&nonce&&validation.validationId!==nonce)return;
      }
      if(!validation?.isValid)return;result=await submit();
    }
    if(!result||!active())return;
    if(result.focusKey){const target=entries.get(result.focusKey);(target?.input||target?.element)?.focus();target?.element.scrollIntoView({block:'nearest'});}
    if(result.action){const entry=entries.get(result.action.nodeKey);if(entry)await prepare(entry,result.action);}
  }catch(failure){if(active())error(failure);}
  finally{if(submitting.get(identity)===operation)submitting.delete(identity);}
}
