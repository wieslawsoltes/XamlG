// Typed controls use text nodes and registered renderers only; no model HTML or scripts.
function create(node){
  if(widgets.has(node.type))return {...widgets.get(node.type).create(node),type:node.type,node,disabled:false};
  let e=document.createElement('div'),input=null,caption=null,slot=null,summary=null,nav=null,shape=null;
  if(node.type==='Button'||node.type==='RepeatButton'){e=document.createElement('button');e.type='button';}
  else if(node.type==='TextBox')e=input=document.createElement('textarea');
  else if(['Slider','NumericUpDown','TimePicker',...dates].includes(node.type)){e=input=document.createElement('input');input.type=node.type==='Slider'?'range':node.type==='NumericUpDown'?'number':node.type==='TimePicker'?'time':'date';}
  else if(toggles.has(node.type)){e=document.createElement('label');input=document.createElement('input');input.type=node.type==='RadioButton'?'radio':'checkbox';caption=document.createElement('span');slot=caption;e.append(input,caption);}
  else if(node.type==='ComboBox'||node.type==='ListBox')e=input=document.createElement('select');
  else if(node.type==='ProgressBar')e=document.createElement('progress');else if(node.type==='Separator')e=document.createElement('hr');
  else if(node.type==='Viewbox'){slot=document.createElement('div');slot.style.cssText='position:absolute;transform-origin:0 0;width:max-content;';e.append(slot);}
  else if(node.type==='Expander'||node.type==='TreeViewItem'){e=document.createElement('details');summary=document.createElement('summary');slot=document.createElement('div');e.append(summary,slot);}
  else if(node.type==='TabControl'){nav=document.createElement('div');nav.className='tabs';nav.setAttribute('role','tablist');slot=document.createElement('div');e.append(nav,slot);}
  else if(['Rectangle','Ellipse','Line'].includes(node.type)){e=document.createElementNS('http://www.w3.org/2000/svg','svg');shape=document.createElementNS(e.namespaceURI,({Rectangle:'rect',Ellipse:'ellipse',Line:'line'})[node.type]);e.append(shape);}
  slot??=e;const entry={element:e,input,caption,slot,summary,nav,shape,type:node.type,node,disabled:false};
  if(node.type==='Viewbox'){viewboxes.set(e,entry);boxResize.observe(e);}
  if(input){input.addEventListener('change',()=>changeState(entry).catch(error));input.addEventListener('blur',()=>touchForm(entry));}
  if(node.type==='TextBox')input.addEventListener('input',()=>{if(!applying)drafts.set(entry.node.key,input.value);});
  if(input)input.addEventListener('keydown',event=>{
    if(event.key==='Tab'&&entry.type==='TextBox'&&entry.node.properties.AcceptsTab===true&&!input.readOnly&&!event.shiftKey&&!event.ctrlKey&&!event.altKey&&!event.metaKey){
      const remaining=input.value.length-(input.selectionEnd-input.selectionStart);
      if(remaining<input.maxLength){input.setRangeText('\t',input.selectionStart,input.selectionEnd,'end');drafts.set(entry.node.key,input.value);}
      event.preventDefault();return;
    }
    if(event.key!=='Enter'||event.isComposing||event.shiftKey||event.ctrlKey||event.altKey||event.metaKey||entry.node.properties.AcceptsReturn)return;
    if(entry.node.form?.role==='input'&&!['ComboBox','ListBox'].includes(entry.type)){
      event.preventDefault();const generation=epoch;changeState(entry).then(()=>current(generation)?submitForm(entry.node.form.id):undefined).catch(error);
    }else if(node.type==='TextBox')event.preventDefault();
  });
  if(summary){summary.addEventListener('click',event=>{if(entry.disabled||busy)event.preventDefault();});e.addEventListener('toggle',()=>{if(!applying&&entry.node.stateKey&&e.open!==(entry.node.properties.IsExpanded===true))changeState(entry,e.open).catch(error);});}
  if(node.type==='Button'||node.type==='RepeatButton')e.addEventListener('click',()=>prepare(entry));return entry;
}
function accept(value){
  if(disposed)return;validSnapshot(value);
  if(snapshot&&snapshot.sessionId===value.sessionId&&(value.revision<snapshot.revision||value.revision===snapshot.revision&&value.stateRevision<snapshot.stateRevision))return;
  const previousBrushes=brushRegistry,candidateBrushes=prepareUiBrushes(value);let styleText;
  brushRegistry=candidateBrushes;
  try{styleText=buildUiStyles(value);}catch(failure){brushRegistry=previousBrushes;throw failure;}
  snapshot=value;review=null;$('review').hidden=true;applying=true;const used=new Set();
  try{
    function render(node,disabled=false,hidden=false){
      const p=node.properties;disabled=disabled||p.IsEnabled===false;hidden=hidden||p.IsVisible===false;
      let entry=entries.get(node.key);if(entry&&entry.type!==node.type){retire(entry);entries.delete(node.key);entry=null;}
      if(!entry){entry=create(node);entries.set(node.key,entry);}entry.node=node;entry.disabled=disabled;used.add(node.key);
      const e=entry.element;e.setAttribute('class','node');e.style.cssText='';e.hidden=hidden;e.dataset.uiKey=node.key;common(e,p);
      if(entry.input){
        entry.input.disabled=disabled;entry.input.setAttribute('aria-label',p['AutomationProperties.Name']||p.PlaceholderText||p.Content||node.stateKey||node.key);
        const history=node.form?.role==='input'?formState(node.form.id,value):null,field=history?.fields.find(field=>field.key===node.key);
        if(field)entry.input.setAttribute('aria-invalid',String(!!field.error&&(node.form.errorMode==='Always'||history.submitted||node.form.errorMode==='OnTouch'&&field.touched)));else entry.input.removeAttribute('aria-invalid');
      }
      const children=node.children.map(child=>render(child,disabled,hidden));
      const widget=widgets.get(node.type);
      if(widget){widget.update(entry,children,{disabled,hidden});applyAvaloniaFeatures(entry);applyUiStyleIdentity(entry);return e;}
      if(p.Content!==undefined&&!children.length&&containers.has(node.type)&&node.type!=='ItemsControl')entry.slot.textContent=p.Content;
      else if(containers.has(node.type))reconcile(entry.slot,children);
      switch(node.type){
        case 'StackPanel':e.classList.add('stack');e.style.flexDirection=p.Orientation==='Horizontal'?'row':'column';e.style.gap=px(p.Spacing,0,128);break;
        case 'Grid':e.classList.add('grid');e.style.gridTemplateColumns=tracks(p.ColumnDefinitions||'*');e.style.gridTemplateRows=tracks(p.RowDefinitions||'*');break;
        case 'Panel':e.style.display='grid';children.forEach(child=>{child.style.gridArea='1 / 1';});break;
        case 'Canvas':e.style.position='relative';node.children.forEach((child,i)=>{children[i].style.position='absolute';for(const side of ['Left','Top','Right','Bottom'])if(child.properties['Canvas.'+side]!==undefined)children[i].style[side.toLowerCase()]=px(child.properties['Canvas.'+side],-1000000,1000000);});break;
        case 'WrapPanel':e.style.display='flex';e.style.flexWrap='wrap';e.style.flexDirection=p.Orientation==='Vertical'?'column':'row';children.forEach(child=>{if(p.ItemWidth!==undefined)child.style.width=px(p.ItemWidth);if(p.ItemHeight!==undefined)child.style.height=px(p.ItemHeight);});break;
        case 'DockPanel':e.style.display='flex';e.style.flexWrap='wrap';children.forEach((child,i)=>{const side=node.children[i].properties['DockPanel.Dock'];if(side==='Top'||side==='Bottom')child.style.flexBasis='100%';if(i===children.length-1&&p.LastChildFill!==false)child.style.flex='1';});break;
        case 'UniformGrid':{const cols=p.Columns||Math.max(1,Math.ceil(Math.sqrt(children.length+(p.FirstColumn||0))));e.style.display='grid';e.style.gridTemplateColumns='repeat('+integer(cols,1,64)+',minmax(0,1fr))';if(p.Rows)e.style.gridTemplateRows='repeat('+integer(p.Rows,1,64)+',minmax(0,1fr))';if(children[0]&&p.FirstColumn)children[0].style.gridColumnStart=String(p.FirstColumn+1);break;}
        case 'Border':e.style.padding=thickness(p.Padding??0);e.style.borderRadius=radius(p.CornerRadius??0);e.style.borderStyle='solid';e.style.borderWidth=thickness(p.BorderThickness??0,16);if(p.BorderBrush)e.style.borderColor=brushSolid(p.BorderBrush);break;
        case 'TextBlock':case 'SelectableTextBlock':e.classList.add('text');e.textContent=p.Text||'';if(p.FontSize)e.style.fontSize=px(p.FontSize,.1,512);e.style.fontWeight=({Normal:'400',Medium:'500',SemiBold:'600',Bold:'700'})[p.FontWeight]||'400';if(p.Foreground)e.style.color=brushSolid(p.Foreground);e.style.whiteSpace=p.TextWrapping==='Wrap'?'pre-wrap':'pre';e.style.textAlign=({Left:'left',Center:'center',Right:'right',Justify:'justify'})[p.TextAlignment]||'left';break;
        case 'Button':case 'RepeatButton':if(!children.length)e.textContent=p.Content||'Action';e.disabled=disabled||busy||!node.actionId;break;
        case 'TextBox':{const text=drafts.has(node.key)?drafts.get(node.key):p.Text||'';if(entry.input.value!==text)entry.input.value=text;entry.input.placeholder=p.PlaceholderText||'';entry.input.maxLength=integer(p.MaxLength??16384,1,16384);entry.input.rows=p.AcceptsReturn?3:1;entry.input.readOnly=p.IsReadOnly===true;break;}
        case 'Slider':case 'NumericUpDown':entry.input.min=String(number(p.Minimum??(node.type==='NumericUpDown'?-1000000:0),-1000000,1000000));entry.input.max=String(number(p.Maximum??(node.type==='NumericUpDown'?1000000:100),-1000000,1000000));entry.input.step=node.type==='NumericUpDown'?String(p.Increment??1):p.IsSnapToTickEnabled?String(p.TickFrequency??1):'any';entry.input.value=p.Value==null?'':String(p.Value);if(p.Orientation==='Vertical')entry.input.style.writingMode='vertical-lr';break;
        case 'CheckBox':case 'ToggleButton':case 'RadioButton':case 'ToggleSwitch':entry.input.checked=p.IsChecked===true;e.classList.add('label');if(!children.length)entry.caption.textContent=p.Content||'';if(node.type==='ToggleSwitch')entry.input.setAttribute('role','switch');break;
        case 'ProgressBar':e.max=number((p.Maximum??100)-(p.Minimum??0),.000001,2000000);if(p.IsIndeterminate)e.removeAttribute('value');else e.value=number((p.Value??0)-(p.Minimum??0),0,2000000);break;
        case 'ScrollViewer':e.style.overflow='auto';e.style.maxHeight=px(p.MaxHeight??p.Height??600);break;
        case 'Viewbox':e.style.overflow='hidden';e.style.position='relative';if(p.Width===undefined)e.style.width='100%';break;
        case 'Expander':case 'TreeViewItem':entry.summary.textContent=p.Header||'';e.open=p.IsExpanded===true;entry.summary.setAttribute('aria-disabled',String(disabled));break;
        case 'ItemsControl':if(p.ItemsSource){const rows=p.ItemsSource.map(text=>{const row=document.createElement('div');row.textContent=text;return row;});reconcile(entry.slot,rows);}break;
        case 'ComboBox':case 'ListBox':{const options=p.ItemsSource??node.children.map(label),previous=[...e.options].map(option=>option.text);if(JSON.stringify(previous)!==JSON.stringify(options))e.replaceChildren(...options.map(text=>{const option=document.createElement('option');option.textContent=text;return option;}));e.selectedIndex=integer(p.SelectedIndex??-1,-1,4095);if(node.type==='ListBox')e.size=Math.min(8,Math.max(2,options.length));break;}
        case 'TabControl':{const selected=p.SelectedIndex??0;entry.nav.replaceChildren(...node.children.map((child,index)=>{const tab=document.createElement('button');tab.type='button';tab.setAttribute('role','tab');tab.setAttribute('aria-selected',String(index===selected));tab.textContent=child.properties.Header||label(child);tab.disabled=disabled||busy;tab.onclick=()=>node.stateKey?changeState(entry,index).catch(error):localTab(index);return tab;}));localTab(selected);function localTab(index){children.forEach((child,i)=>{child.hidden=i!==index||hidden||node.children[i].properties.IsVisible===false;});[...entry.nav.children].forEach((tab,i)=>tab.setAttribute('aria-selected',String(i===index)));}break;}
        case 'DatePicker':case 'CalendarDatePicker':case 'Calendar':entry.input.value=p.SelectedDate?.slice(0,10)||'';break;
        case 'TimePicker':entry.input.value=p.SelectedTime?.slice(0,8)||'';entry.input.step=String((p.MinuteIncrement??1)*60);break;
        case 'Rectangle':case 'Ellipse':case 'Line':{const width=p.Width??100,height=p.Height??60,s=entry.shape;e.setAttribute('viewBox',`0 0 ${width||1} ${height||1}`);e.style.width=px(width);e.style.height=px(height);s.setAttribute('fill',p.Fill?brushPaint(p.Fill):'none');s.setAttribute('stroke',p.Stroke?brushPaint(p.Stroke):'none');s.setAttribute('stroke-width',String(p.StrokeThickness??1));if(node.type==='Rectangle'){s.setAttribute('width',width);s.setAttribute('height',height);s.setAttribute('rx',p.RadiusX??0);s.setAttribute('ry',p.RadiusY??0);}else if(node.type==='Ellipse'){s.setAttribute('cx',width/2);s.setAttribute('cy',height/2);s.setAttribute('rx',width/2);s.setAttribute('ry',height/2);}else{const a=tuple(p.StartPoint??'0,0',-1000000,1000000),b=tuple(p.EndPoint??'0,0',-1000000,1000000);if(a.length!==2||b.length!==2)throw new Error('Line points require two coordinates.');s.setAttribute('x1',a[0]);s.setAttribute('y1',a[1]);s.setAttribute('x2',b[0]);s.setAttribute('y2',b[1]);}break;}
      }
      applyAvaloniaFeatures(entry);applyUiStyleIdentity(entry);return e;
    }
    reconcile(root,value.roots.map(node=>render(node)));for(const [key,entry]of entries)if(!used.has(key)){retire(entry);entries.delete(key);}
    uiStyleElement.textContent=styleText;
    $('fallback').textContent=String(value.fallbackMarkdown||'').slice(0,131072);
    status((value.isFinal?'Interactive UI':'Streaming UI')+' · revision '+value.revision+' · state '+value.stateRevision);$('refresh').disabled=busy||!capabilities.serverTools;scheduleLayout();
    commitUiBrushes(previousBrushes);
  }catch(failure){brushRegistry=previousBrushes;throw failure;}finally{applying=false;}
}
