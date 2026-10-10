// Shared lexical runtime. This module is embedded into the resource, not fetched by the view.
const $=id=>document.getElementById(id),root=$('surface'),pending=new Map(),entries=new Map(),viewboxes=new WeakMap();
const types=new Set('StackPanel Grid Border TextBlock SelectableTextBlock Button RepeatButton TextBox Slider CheckBox ToggleButton RadioButton ToggleSwitch ProgressBar Separator ScrollViewer Panel Canvas WrapPanel DockPanel UniformGrid Viewbox ContentControl UserControl Expander TabControl TabItem ItemsControl ListBox ListBoxItem ComboBox ComboBoxItem TreeView TreeViewItem NumericUpDown DatePicker CalendarDatePicker Calendar TimePicker Rectangle Ellipse Line'.split(' '));
const toggles=new Set(['CheckBox','ToggleButton','RadioButton','ToggleSwitch']);
const dates=new Set(['DatePicker','CalendarDatePicker','Calendar']);
const containers=new Set(['StackPanel','Grid','Border','ScrollViewer','Panel','Canvas','WrapPanel','DockPanel','UniformGrid','Viewbox','ContentControl','UserControl','Expander','TabControl','TabItem','ItemsControl','ListBoxItem','ComboBoxItem','TreeView','TreeViewItem','Button','RepeatButton',...toggles]);
const widgets=new Map(),submitting=new Map(),drafts=new Map();
let nextId=0,origin=null,ready=false,disposed=false,capabilities={},snapshot=null,marker=null,queued=null,busy=false,review=null,refreshing=false,refreshAgain=false,applying=false,epoch=0,layoutFrame=0,mutationTail=Promise.resolve(),mutationCount=0;
const status=text=>{if(!disposed)$('status').textContent=text;};
const error=value=>{if(!disposed)$('error').textContent=String(value?.message??value??'').slice(0,4096);};
const post=message=>{if(!disposed)parent.postMessage(message,origin&&origin!=='null'?origin:'*');};
const notify=(method,params)=>post({jsonrpc:'2.0',method,params});
const current=generation=>!disposed&&epoch===generation;
function request(method,params){
  if(disposed||pending.size>=16)return Promise.reject(new Error('UI request limit reached.'));
  const id=++nextId;return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{pending.delete(id);reject(new Error('The host request timed out.'));},30000);pending.set(id,{resolve,reject,timer,method});post({jsonrpc:'2.0',id,method,params});});
}
async function tool(name,args){
  if(!ready||!capabilities.serverTools)throw new Error('This host does not allow UI tool calls. Use the text fallback.');
  const result=await request('tools/call',{name,arguments:args});
  if(result.isError)throw new Error(result.content?.filter(item=>item.type==='text').map(item=>item.text).join('\n')||'Tool invocation failed.');
  if(result.structuredContent!==undefined)return result.structuredContent;
  const text=result.content?.find(item=>item.type==='text')?.text;
  if(typeof text!=='string'||text.length>2097152)throw new Error('No bounded structured result.');return JSON.parse(text);
}
function enqueue(operation,generation=epoch){
  if(disposed||mutationCount>=64)return Promise.reject(new Error('UI mutation queue is full.'));
  mutationCount++;
  const task=mutationTail.then(()=>current(generation)?operation():undefined).finally(()=>mutationCount--);
  mutationTail=task.catch(()=>{});return task;
}
function validMarker(value){return value?.format==='xamlg.intelligent-ui/1'&&typeof value.id==='string'&&/^[A-Za-z0-9_.-]{1,80}$/.test(value.id)&&typeof value.sessionId==='string'&&/^[a-f0-9]{32}$/i.test(value.sessionId);}
function validSnapshot(value){
  if(!value||typeof value.id!=='string'||value.id.length>80||typeof value.sessionId!=='string'||value.sessionId.length!==32||!Number.isSafeInteger(value.revision)||value.revision<1||!Number.isSafeInteger(value.stateRevision)||value.stateRevision<0||!Array.isArray(value.roots))throw new Error('Invalid UI snapshot.');
  if(marker&&(value.id!==marker.id||value.sessionId!==marker.sessionId))throw new Error('This UI session was released or replaced.');
  const seen=new Set();let count=0;
  function check(node,depth){
    if(!node||++count>4096||depth>48||!types.has(node.type)&&!widgets.has(node.type)||typeof node.key!=='string'||node.key.length>8192||seen.has(node.key)||!Array.isArray(node.children)||!node.properties||typeof node.properties!=='object')throw new Error('Invalid component tree.');
    seen.add(node.key);
    for(const [name,v]of Object.entries(node.properties)){
      if(name.length>80)throw new Error('Invalid property name.');
      if(brushNames.has(name)){validateBrush(v);continue;}
      if(widgets.get(node.type)?.validateProperty?.(name,v))continue;
      if(v===null){if(!['Value','SelectedDate','SelectedTime'].includes(name))throw new Error('Invalid nullable property.');continue;}
      if(Array.isArray(v)){if(name!=='ItemsSource'||v.length>512||v.some(item=>typeof item!=='string'||item.length>1024))throw new Error('Invalid item source.');continue;}
      if(!['string','number','boolean'].includes(typeof v)||typeof v==='string'&&v.length>16384||typeof v==='number'&&!Number.isFinite(v))throw new Error('Invalid component property.');
    }
    validateAvaloniaFeatures(node);
    if(node.form&&(typeof node.form.id!=='string'||node.form.id.length>8192||!['form','input','submit','summary','error'].includes(node.form.role)))throw new Error('Invalid form annotation.');
    if(node.formState&&(!Array.isArray(node.formState.fields)||node.formState.fields.length>4096||typeof node.formState.id!=='string'))throw new Error('Invalid form state.');
    for(const child of node.children)check(child,depth+1);
  }
  for(const node of value.roots)check(node,0);
}
function number(value,min,max){if(typeof value!=='number'||!Number.isFinite(value)||value<min||value>max)throw new Error('Numeric UI value is out of bounds.');return value;}
function integer(value,min,max){number(value,min,max);if(!Number.isInteger(value))throw new Error('Integer UI value required.');return value;}
const px=(value,min=0,max=10000)=>number(value??0,min,max)+'px';
function tuple(value,min=0,max=128){const parts=typeof value==='number'?[value]:String(value).split(/[ ,]+/).filter(Boolean).map(Number);if(![1,2,4].includes(parts.length))throw new Error('Invalid coordinate tuple.');parts.forEach(item=>number(item,min,max));return parts;}
function thickness(value,max=128,min=0){const p=tuple(value,min,max);return(p.length===4?[p[1],p[2],p[3],p[0]]:p.length===2?[p[1],p[0]]:p).map(item=>item+'px').join(' ');}
function radius(value){const p=tuple(value);return(p.length===4?[p[0],p[1],p[2],p[3]]:p.length===2?[p[0],p[1],p[0],p[1]]:p).map(item=>item+'px').join(' ');}
function color(value){if(/^#[0-9a-f]{8}$/i.test(value))return '#'+value.slice(3)+value.slice(1,3);if(/^#[0-9a-f]{4}$/i.test(value))return '#'+value.slice(2)+value[1];if(/^#[0-9a-f]{3}([0-9a-f]{3})?$/i.test(value)||/^(Transparent|Black|White|Gray|Red|Green|Blue|Orange|Yellow|Purple|Pink|Silver|Navy|Teal|Lime|Maroon|Olive|Aqua|Fuchsia)$/.test(value))return value;throw new Error('Invalid catalog color.');}
function tracks(value){const parts=String(value).split(',');if(parts.length>64)throw new Error('Too many grid tracks.');return parts.map(raw=>{const text=raw.trim();if(text==='Auto')return 'auto';if(text==='*')return '1fr';const star=text.endsWith('*');return number(Number(star?text.slice(0,-1):text),0,10000)+(star?'fr':'px');}).join(' ');}
function reconcile(parent,children){const wanted=new Set(children);for(const child of [...parent.childNodes])if(child.nodeType!==Node.ELEMENT_NODE||!wanted.has(child))child.remove();children.forEach((child,index)=>{if(parent.children[index]!==child)parent.insertBefore(child,parent.children[index]||null);});}
function retire(entry){boxResize.unobserve(entry.element);viewboxes.delete(entry.element);drafts.delete(entry.node.key);entry.dispose?.();entry.element.remove();}
function retireSurface(){retireUiBrushes();for(const entry of entries.values())retire(entry);entries.clear();drafts.clear();submitting.clear();root.replaceChildren();snapshot=null;review=null;$('review').hidden=true;}
function layoutViewbox(entry){
  if(disposed||!entry.element.isConnected)return;
  const child=entry.slot.firstElementChild;if(!child)return;
  const e=entry.element,p=entry.node.properties,w=child.offsetWidth,h=child.offsetHeight,aw=e.clientWidth;
  if(!w||!h||!aw)return;const ah=p.Height??p.MaxHeight??Infinity;
  let sx=aw/w,sy=Number.isFinite(ah)?ah/h:sx;const stretch=p.Stretch??'Uniform';
  if(stretch==='None')sx=sy=1;else if(stretch==='Uniform')sx=sy=Math.min(sx,sy);else if(stretch==='UniformToFill')sx=sy=Math.max(sx,sy);
  if(p.StretchDirection==='DownOnly'){sx=Math.min(1,sx);sy=Math.min(1,sy);}else if(p.StretchDirection==='UpOnly'){sx=Math.max(1,sx);sy=Math.max(1,sy);}
  if(!Number.isFinite(sx)||!Number.isFinite(sy)||sx<=0||sy<=0)return;
  const height=p.Height??Math.min(p.MaxHeight??Infinity,h*sy),next=height+'px';if(e.style.height!==next)e.style.height=next;
  entry.slot.style.transform=`scale(${sx},${sy})`;entry.slot.style.left=Math.max(0,(aw-w*sx)/2)+'px';entry.slot.style.top=Math.max(0,(height-h*sy)/2)+'px';
}
function scheduleLayout(){if(layoutFrame||disposed)return;layoutFrame=requestAnimationFrame(()=>{layoutFrame=0;for(const entry of entries.values())if(entry.layout)entry.layout();else if(entry.type==='Viewbox')layoutViewbox(entry);});}
function common(element,p){
  for(const [name,css]of [['Width','width'],['Height','height'],['MinWidth','minWidth'],['MinHeight','minHeight'],['MaxWidth','maxWidth'],['MaxHeight','maxHeight']])if(p[name]!==undefined)element.style[css]=px(p[name]);
  if(p.Margin!==undefined)element.style.margin=thickness(p.Margin,10000,-10000);if(p.Opacity!==undefined)element.style.opacity=String(number(p.Opacity,0,1));if(p.ClipToBounds)element.style.overflow='hidden';
  element.title=p['ToolTip.Tip']||'';
  if(p['AutomationProperties.Name'])element.setAttribute('aria-label',p['AutomationProperties.Name']);else element.removeAttribute('aria-label');
  if(p.Focusable!==undefined)element.tabIndex=p.Focusable?0:-1;
  const h=({Left:'start',Center:'center',Right:'end',Stretch:'stretch'})[p.HorizontalAlignment];if(h)element.style.justifySelf=h;
  const v=({Top:'start',Center:'center',Bottom:'end',Stretch:'stretch'})[p.VerticalAlignment];if(v)element.style.alignSelf=v;
  if(p['Grid.Row']!==undefined||p['Grid.RowSpan']!==undefined)element.style.gridRow=(integer(p['Grid.Row']??0,0,63)+1)+' / span '+integer(p['Grid.RowSpan']??1,1,64);
  if(p['Grid.Column']!==undefined||p['Grid.ColumnSpan']!==undefined)element.style.gridColumn=(integer(p['Grid.Column']??0,0,63)+1)+' / span '+integer(p['Grid.ColumnSpan']??1,1,64);
}
const label=node=>String(node.properties.Content??node.properties.Header??node.properties.Text??node.children.map(label).join(' '));
function formState(id,value=snapshot){const stack=[...(value?.roots||[])];while(stack.length){const node=stack.pop();if(node.formState?.id===id)return node.formState;stack.push(...node.children);}return null;}
