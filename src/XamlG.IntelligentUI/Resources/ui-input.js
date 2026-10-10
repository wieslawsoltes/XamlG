// Typed input projection. Presentation styles never grant input or action capabilities.
const uiActionButtons=new Set(['Button','RepeatButton',...toggles]);
const uiNavigationModes=new Set(['Continue','Cycle','Contained','Once','None','Local']);
function validateUiInput(node){
  const p=node.properties;
  const requireType=(name,allowed)=>{if(p[name]!==undefined&&(typeof allowed==='string'?node.type!==allowed:!allowed.has(node.type)))throw new Error('Input property does not belong to its control: '+name);};
  for(const name of ['IsTabStop','IsDefault','IsCancel','IsThreeState','IsDirectionReversed'])
    if(p[name]!==undefined&&typeof p[name]!=='boolean')throw new Error('Boolean input property required.');
  if(p.TabIndex!==undefined)integer(p.TabIndex,0,32767);
  if(p['KeyboardNavigation.TabNavigation']!==undefined&&!uiNavigationModes.has(p['KeyboardNavigation.TabNavigation']))throw new Error('Unknown keyboard navigation mode.');
  for(const name of ['AutomationProperties.AutomationId','AutomationProperties.HelpText'])
    if(p[name]!==undefined&&(typeof p[name]!=='string'||p[name].length>16384))throw new Error('Invalid accessibility text.');
  for(const name of ['IsDefault','IsCancel'])requireType(name,uiActionButtons);
  for(const name of ['IsThreeState','IsChecked'])requireType(name,toggles);
  if(p.IsChecked!==undefined&&p.IsChecked!==null&&typeof p.IsChecked!=='boolean')throw new Error('Nullable Boolean toggle value required.');
  for(const name of ['CaretIndex','SelectionStart','SelectionEnd']){
    requireType(name,'TextBox');if(p[name]!==undefined)integer(p[name],0,16384);
  }
  for(const name of ['SmallChange','LargeChange','IsDirectionReversed'])requireType(name,'Slider');
  for(const name of ['SmallChange','LargeChange'])if(p[name]!==undefined)number(p[name],0,1000000);
  for(const name of ['Delay','Interval']){
    requireType(name,'RepeatButton');if(p[name]!==undefined)integer(p[name],name==='Delay'?0:16,60000);
  }
}
function uiInputTarget(entry){return entry.input??entry.summary??entry.element;}
function uiInputAvailable(entry,duringApply=false){
  return !disposed&&(!applying||duringApply)&&snapshot&&entries.get(entry.node.key)===entry&&!entry.disabled&&
    entry.element.isConnected&&!entry.element.closest('[hidden],[inert]')&&entry.element.getClientRects().length>0;
}
function installUiInput(entry){
  const target=uiInputTarget(entry);
  entry.inputDefaults={tabIndex:target.getAttribute('tabindex'),focusable:target.matches('button,input,textarea,select,summary')};
  if(toggles.has(entry.type)){
    target.addEventListener('change',()=>{if(!applying&&!entry.node.stateKey){entry.localToggle=target.indeterminate?null:target.checked;applyUiToggle(entry);}});
    target.addEventListener('click',event=>{
      const p=entry.node.properties;
      if(!p.IsThreeState||entry.type==='RadioButton')return;
      event.preventDefault();
      if(!uiInputAvailable(entry))return;
      const old=drafts.has(entry.node.key)?drafts.get(entry.node.key):!entry.node.stateKey&&Object.hasOwn(entry,'localToggle')?entry.localToggle:p.IsChecked;
      const next=old===true?null:old===null?false:true;
      if(entry.node.stateKey){drafts.set(entry.node.key,next);changeState(entry,next).catch(error);}
      else entry.localToggle=next;
      // Cancelling checkbox activation rolls back its native checked state after dispatch.
      // Restore the logical state after that rollback, without dispatching another event.
      queueMicrotask(()=>{if(!disposed&&entries.get(entry.node.key)===entry)applyUiToggle(entry);});
    });
  }
  if(entry.type==='Slider')target.addEventListener('keydown',event=>{
    if(event.isComposing||event.altKey||event.ctrlKey||event.metaKey||event.shiftKey)return;
    const p=entry.node.properties,small=p.SmallChange??1,large=p.LargeChange??10;
    const delta={ArrowLeft:-small,ArrowDown:-small,ArrowRight:small,ArrowUp:small,PageDown:-large,PageUp:large}[event.key];
    if(delta===undefined&&!['Home','End'].includes(event.key))return;
    event.preventDefault();event.stopPropagation();if(!uiInputAvailable(entry))return;
    const min=p.Minimum??0,max=p.Maximum??100;
    let next=event.key==='Home'?min:event.key==='End'?max:Number(target.value)+delta*(p.IsDirectionReversed?-1:1);
    if(p.IsSnapToTickEnabled&&p.TickFrequency>0&&next!==min&&next!==max)next=min+Math.round((next-min)/p.TickFrequency)*p.TickFrequency;
    next=Math.min(max,Math.max(min,next));target.value=String(next);
    if(entry.node.stateKey){drafts.set(entry.node.key,next);changeState(entry,next).catch(error);}
  },true);
  if(entry.type==='RepeatButton'){
    entry.element.addEventListener('pointerdown',event=>{if(event.button===0)startUiRepeat(entry);});
    entry.element.addEventListener('pointerleave',()=>stopUiRepeat(entry));
    entry.element.addEventListener('keydown',event=>{if(event.key===' '&&!event.repeat)startUiRepeat(entry);});
    entry.element.addEventListener('blur',()=>stopUiRepeat(entry));
  }
  const dispose=entry.dispose;
  entry.dispose=()=>{stopUiRepeat(entry);dispose?.();};
}
function applyUiToggle(entry){
  const p=entry.node.properties;
  let value=p.IsChecked;
  if(drafts.has(entry.node.key))value=drafts.get(entry.node.key);
  else if(!entry.node.stateKey&&Object.hasOwn(entry,'localToggle'))value=entry.localToggle;
  entry.input.checked=value===true;entry.input.indeterminate=value===null;
  // ARIA switches and radios are binary. A three-state toggle is exposed as a checkbox.
  const triState=entry.type!=='RadioButton'&&(p.IsThreeState||value===null);
  if(triState)entry.input.setAttribute('role','checkbox');
  else if(entry.type==='ToggleSwitch')entry.input.setAttribute('role','switch');
  else entry.input.removeAttribute('role');
  entry.input.setAttribute('aria-checked',triState&&value===null?'mixed':String(value===true));
}
function applyUiInput(entry){
  const p=entry.node.properties,old=entry.appliedInputProperties,target=uiInputTarget(entry),defaults=entry.inputDefaults;
  const canFocus=p.Focusable??defaults.focusable;
  // Keep the DOM's outside-surface traversal sequential. Authored TabIndex is sorted by
  // our scoped traversal; unlike HTML, native Avalonia orders explicit zero before defaults.
  if(canFocus)target.tabIndex=p.IsTabStop===false?-1:0;
  else if(p.Focusable!==undefined||p.IsTabStop===false)target.tabIndex=-1;
  else if(defaults.tabIndex===null)target.removeAttribute('tabindex');else target.setAttribute('tabindex',defaults.tabIndex);
  const attribute=(name,value)=>{if(value===undefined)target.removeAttribute(name);else target.setAttribute(name,String(value));};
  attribute('data-ui-tab-index',p.TabIndex);
  attribute('data-ui-automation-id',p['AutomationProperties.AutomationId']);
  attribute('aria-description',p['AutomationProperties.HelpText']);
  if(entry.type==='TextBox'){
    const changed=name=>(p[name]!==undefined||old?.[name]!==undefined)&&(!old||old[name]!==p[name]);
    // Text is assigned first by the control adapter. Do not reset a user's selection on
    // unrelated state echoes; a changed/removed declaration deliberately takes precedence.
    if(['CaretIndex','SelectionStart','SelectionEnd'].some(name=>(p[name]!==undefined||old?.[name]!==undefined)&&changed(name))){
      let start=target.selectionStart,end=target.selectionEnd;
      // Clear removed declarations before applying new ones, like the native adapter.
      if(old?.CaretIndex!==undefined&&p.CaretIndex===undefined)start=end=0;
      if(old?.SelectionStart!==undefined&&p.SelectionStart===undefined)start=0;
      if(old?.SelectionEnd!==undefined&&p.SelectionEnd===undefined)end=0;
      if(p.CaretIndex!==undefined&&changed('CaretIndex'))start=end=Math.min(target.value.length,p.CaretIndex);
      if(p.SelectionStart!==undefined&&changed('SelectionStart'))start=Math.min(target.value.length,p.SelectionStart);
      if(p.SelectionEnd!==undefined&&changed('SelectionEnd'))end=Math.min(target.value.length,p.SelectionEnd);
      target.setSelectionRange(Math.min(start,end),Math.max(start,end),start>end?'backward':'forward');
    }
  }
  if(toggles.has(entry.type)){
    if(old&&old.IsChecked!==p.IsChecked)delete entry.localToggle;
    applyUiToggle(entry);
  }
  if(entry.type==='Slider'){
    if(drafts.has(entry.node.key))target.value=String(drafts.get(entry.node.key));
    target.style.direction=p.IsDirectionReversed?'rtl':'ltr';
    target.setAttribute('aria-orientation',p.Orientation==='Vertical'?'vertical':'horizontal');
  }
  entry.appliedInputProperties=p;
  if(heldUiRepeat?.entry===entry&&!heldUiRepeat.alive())stopUiRepeat(entry);
}
let heldUiRepeat=null;
function stopUiRepeat(entry=null){
  if(!heldUiRepeat||entry&&heldUiRepeat.entry!==entry)return;
  clearTimeout(heldUiRepeat.timer);heldUiRepeat=null;
}
function startUiRepeat(entry){
  if(heldUiRepeat?.entry===entry||!uiInputAvailable(entry)||!snapshot.actions?.some(a=>a.id===entry.node.actionId&&a.kind==='state'))return;
  stopUiRepeat();const generation=epoch,revision=snapshot.revision,action=entry.node.actionId;
  const hold={entry,timer:0,alive:()=>current(generation)&&snapshot?.revision===revision&&entry.node.actionId===action&&uiInputAvailable(entry,true)&&document.visibilityState!=='hidden'};
  heldUiRepeat=hold;
  const tick=async()=>{
    if(heldUiRepeat!==hold||!hold.alive()){if(heldUiRepeat===hold)stopUiRepeat();return;}
    // Await each host result. A slow host never creates an unbounded timer/request backlog.
    const completed=await prepare(entry);
    if(completed!==true){if(heldUiRepeat===hold)stopUiRepeat();return;}
    if(heldUiRepeat===hold&&hold.alive())hold.timer=setTimeout(tick,entry.node.properties.Interval??100);
  };
  hold.timer=setTimeout(tick,entry.node.properties.Delay??300);
}
function uiStopOnKeyUp(event){if(event.key===' ')stopUiRepeat();}
function uiStopOnVisibility(){if(document.visibilityState==='hidden')stopUiRepeat();}
window.addEventListener('pointerup',stopUiRepeatFromEvent);
window.addEventListener('pointercancel',stopUiRepeatFromEvent);
window.addEventListener('blur',stopUiRepeatFromEvent);
window.addEventListener('keyup',uiStopOnKeyUp);
document.addEventListener('visibilitychange',uiStopOnVisibility);
function stopUiRepeatFromEvent(){stopUiRepeat();}
function disposeUiInput(){
  stopUiRepeat();window.removeEventListener('pointerup',stopUiRepeatFromEvent);
  window.removeEventListener('pointercancel',stopUiRepeatFromEvent);window.removeEventListener('blur',stopUiRepeatFromEvent);
  window.removeEventListener('keyup',uiStopOnKeyUp);document.removeEventListener('visibilitychange',uiStopOnVisibility);
}
