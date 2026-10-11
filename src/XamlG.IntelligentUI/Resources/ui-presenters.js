// Standalone presenter projection. Native control templates remain private to Avalonia;
// these adapters do not turn a template boundary into an ordinary response descendant.
widgets.set('ContentPresenter',{
  create(){const element=document.createElement('div');return {element,slot:element};},
  update(entry,children){
    if(children.length)reconcile(entry.slot,children);
    else entry.slot.textContent=entry.node.properties.Content??'';
  }
});
widgets.set('ItemsPresenter',{
  create(){const element=document.createElement('div');return {element,slot:element};},
  // Without a native templated ItemsControl owner, there are no generated containers.
  update(entry){reconcile(entry.slot,[]);}
});
// Presentation capability sets are not a CLR inheritance hierarchy: ContentPresenter is
// a Control, but its registered appearance contract includes these explicit properties.
styleTemplated.add('ContentPresenter');styleContent.add('ContentPresenter');
function validateUiPresenters(node){
  if(node.type==='ItemsPresenter'&&node.children.length)throw new Error('Standalone ItemsPresenter cannot own response children.');
  if(node.type==='ContentPresenter'&&(node.children.length>1||node.children.length&&node.properties.Content!==undefined))
    throw new Error('ContentPresenter requires one unambiguous content value.');
}
