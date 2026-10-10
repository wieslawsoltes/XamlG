// Keyboard traversal follows authored groups rather than incidental HTML slot wrappers.
const uiFocusSelector='button,input,textarea,select,summary,[tabindex]';
const uiOnceFocus=new WeakMap();
function uiEntryFor(target){
  const element=target instanceof Element?target.closest('[data-ui-key]'):null;
  return element?entries.get(element.dataset.uiKey):null;
}
function uiFocusVisible(element){
  return !element.disabled&&!element.closest('[hidden],[inert]')&&element.getClientRects().length>0;
}
function uiFocusChain(element,scope=root){
  const result=[];
  for(let current=element.closest('[data-ui-key]');current;current=current.parentElement?.closest('[data-ui-key]')){
    const entry=entries.get(current.dataset.uiKey);if(entry)result.push(entry);
    if(current===scope)break;
  }
  return result.reverse();
}
function uiFocusOrder(scope){
  let ordinal=0;const tree={items:[],groups:new Map(),element:scope,mode:'Continue'};
  const candidates=[...(scope.matches(uiFocusSelector)?[scope]:[]),...scope.querySelectorAll(uiFocusSelector)];
  for(const target of candidates){
    const entry=uiEntryFor(target);
    if(target.tabIndex<0||!entry||entry.disabled||!uiFocusVisible(target))continue;
    const chain=uiFocusChain(target,scope);
    if(chain.some(parent=>parent!==entry&&parent.node.properties['KeyboardNavigation.TabNavigation']==='None'))continue;
    let group=tree;
    for(const parent of chain){
      const mode=parent.node.properties['KeyboardNavigation.TabNavigation']??'Continue';
      if(parent.element===scope||!['Local','Once','Cycle','Contained'].includes(mode))continue;
      let next=group.groups.get(parent);
      if(!next){
        next={items:[],groups:new Map(),element:parent.element,mode,rank:parent.node.properties.TabIndex??2147483647,order:ordinal++};
        group.groups.set(parent,next);group.items.push(next);
      }
      group=next;
    }
    group.items.push({target,rank:entry.node.properties.TabIndex??2147483647,order:ordinal++});
  }
  function flatten(group){
    const result=group.items.sort((a,b)=>a.rank-b.rank||a.order-b.order).flatMap(item=>item.target?[item.target]:flatten(item));
    if(group.mode!=='Once'||!result.length)return result;
    const active=result.find(target=>target===document.activeElement),remembered=uiOnceFocus.get(group.element);
    return [active??(result.includes(remembered)?remembered:result[0])];
  }
  return flatten(tree);
}
function navigateUiTab(event){
  const active=document.activeElement;
  let scope=root,mode='Continue';
  for(const entry of uiFocusChain(active).reverse()){
    const candidate=entry.node.properties['KeyboardNavigation.TabNavigation'];
    if(candidate==='Cycle'||candidate==='Contained'){scope=entry.element;mode=candidate;break;}
  }
  const candidates=uiFocusOrder(scope),index=candidates.indexOf(active),direction=event.shiftKey?-1:1;
  let next=index<0?(direction>0?0:candidates.length-1):index+direction;
  if(next>=0&&next<candidates.length){event.preventDefault();candidates[next].focus();return;}
  if(mode!=='Continue'){
    event.preventDefault();
    if(mode==='Cycle'&&candidates.length)candidates[(next+candidates.length)%candidates.length].focus();
    return;
  }
  // Cross the response boundary without sending a made-up host focus protocol message.
  // Review buttons, the fallback disclosure and the toolbar retain normal document order.
  const outside=[...document.querySelectorAll(uiFocusSelector)].filter(element=>!root.contains(element)&&element.tabIndex>=0&&uiFocusVisible(element));
  const target=direction>0?outside.find(element=>root.compareDocumentPosition(element)&Node.DOCUMENT_POSITION_FOLLOWING):
    outside.filter(element=>root.compareDocumentPosition(element)&Node.DOCUMENT_POSITION_PRECEDING).at(-1);
  if(target){event.preventDefault();target.focus();}
}
function uiDefaultButton(property){
  return [...entries.values()].find(entry=>entry.node.properties[property]===true&&entry.node.actionId&&uiInputAvailable(entry));
}
function uiRememberFocus(event){
  for(const entry of uiFocusChain(event.target))
    if(entry.node.properties['KeyboardNavigation.TabNavigation']==='Once')uiOnceFocus.set(entry.element,event.target);
}
function uiKeyboard(event){
  if(disposed||applying||event.defaultPrevented||event.isComposing||event.altKey||event.ctrlKey||event.metaKey)return;
  const entry=uiEntryFor(event.target);if(!entry)return;
  if(event.key==='Tab'){navigateUiTab(event);return;}
  if(event.shiftKey)return;
  if(entry.nav&&entry.nav.contains(event.target)&&['ArrowLeft','ArrowRight','Home','End'].includes(event.key)){
    event.preventDefault();const tabs=[...entry.nav.children].filter(tab=>!tab.disabled&&uiFocusVisible(tab)),current=tabs.indexOf(event.target);
    if(!tabs.length)return;
    const index=event.key==='Home'?0:event.key==='End'?tabs.length-1:(current+(event.key==='ArrowLeft'?-1:1)+tabs.length)%tabs.length;
    const target=tabs[index];if(!target||target.disabled)return;
    target.focus();target.click();return;
  }
  if(!['Enter','Escape'].includes(event.key))return;
  if(review){if(event.key==='Escape'){event.preventDefault();review=null;$('review').hidden=true;}return;}
  // Do not steal multiline editing, selection widgets, ordinary button activation or
  // the form's own validation/submission path. Enter on a single-line textbox is handled.
  if(event.key==='Enter'&&(entry.node.properties.AcceptsReturn||entry.node.form?.role==='input'||
    ['ComboBox','ListBox','DatePicker','CalendarDatePicker','Calendar','TimePicker'].includes(entry.type)||
    event.target.matches('button,summary,input[type=checkbox],input[type=radio]')))return;
  const button=uiDefaultButton(event.key==='Enter'?'IsDefault':'IsCancel');
  if(button||event.key==='Enter'&&entry.type==='TextBox')event.preventDefault();
  if(!button||event.repeat)return;
  const generation=epoch,revision=snapshot.revision;
  // Commit the focused draft before resolving exact action revisions. A rejected edit
  // never invokes the previous value's default/cancel action.
  changeState(entry).then(()=>{
    if(current(generation)&&snapshot?.revision===revision&&uiInputAvailable(button))return prepare(button);
  }).catch(error);
}
root.addEventListener('keydown',uiKeyboard);
root.addEventListener('focusin',uiRememberFocus);
function disposeUiKeyboard(){root.removeEventListener('keydown',uiKeyboard);root.removeEventListener('focusin',uiRememberFocus);}
