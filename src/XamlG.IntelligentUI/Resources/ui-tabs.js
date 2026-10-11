// Header identity is derived from the authored child key, not from its current index.
// Reconciliation preserves keyboard focus across selection echoes and row reordering.
let uiTabIdentity=0;
function updateUiTabs(entry,children,hidden){
  const node=entry.node,p=node.properties,previous=entry.tabs??new Map(),next=new Map(),headers=[];
  const controlled=p.SelectedIndex!==undefined||node.stateKey;
  let selected=controlled?(p.SelectedIndex??0):node.children.findIndex(child=>child.key===entry.localTabKey);
  if(selected<0&&!controlled)selected=node.children.length?0:-1;
  node.children.forEach((child,index)=>{
    let tab=previous.get(child.key);
    if(!tab){
      tab=document.createElement('button');tab.type='button';tab.id='ui-tab-'+(++uiTabIdentity);tab.setAttribute('role','tab');
      const tabKey=child.key;
      tab.addEventListener('click',()=>{
        const currentIndex=entry.node.children.findIndex(item=>item.key===tabKey);
        if(currentIndex<0||!uiInputAvailable(entry)||tab.disabled)return;
        if(entry.node.stateKey)changeState(entry,currentIndex).catch(error);
        else {entry.localTabKey=tabKey;entry.selectTab(currentIndex);}
      });
    }
    tab.textContent=child.properties.Header||label(child);
    tab.disabled=entry.disabled||busy||child.properties.IsEnabled===false;
    tab.hidden=hidden||child.properties.IsVisible===false;
    const panel=children[index];panel.id=tab.id+'-panel';panel.setAttribute('role','tabpanel');panel.setAttribute('aria-labelledby',tab.id);
    tab.setAttribute('aria-controls',panel.id);next.set(child.key,tab);headers.push(tab);
  });
  entry.tabs=next;entry.selectTab=select;reconcile(entry.nav,headers);select(selected);
  function select(index){
    children.forEach((child,i)=>child.hidden=i!==index||hidden||node.children[i].properties.IsVisible===false);
    headers.forEach((tab,i)=>{
      tab.setAttribute('aria-selected',String(i===index));tab.tabIndex=p.IsTabStop===false||i!==index?-1:0;
    });
  }
}
