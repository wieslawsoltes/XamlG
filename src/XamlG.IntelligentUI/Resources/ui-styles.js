// Inert style descriptors become package-owned CSS only after validation. This is not a
// stylesheet injection API. Native Avalonia remains authoritative for template/pseudoclass behavior.
const uiStyleElement=document.createElement('style');document.head.append(uiStyleElement);
const styleIdentifier=/^[A-Za-z_][A-Za-z0-9_-]{0,79}$/;
const stylePseudo={pointerover:':hover',pressed:':active',focus:':focus',
  'focus-within':':focus-within','focus-visible':':focus-visible',disabled:'[data-ui-disabled="true"]',
  checked:':is(:checked,:has(>input:checked))',unchecked:':not(:is(:checked,:has(>input:checked)))',
  indeterminate:':is(:indeterminate,:has(>input:indeterminate))',selected:'[aria-selected="true"]',expanded:'[open]'};
const styleTemplated=new Set('Button RepeatButton CheckBox ToggleButton RadioButton ToggleSwitch TextBox Slider ProgressBar Separator ScrollViewer ContentControl UserControl Expander TabControl TabItem ItemsControl ListBox ListBoxItem ComboBox ComboBoxItem TreeView TreeViewItem NumericUpDown DatePicker CalendarDatePicker Calendar TimePicker Label'.split(' '));
const styleContent=new Set('Button RepeatButton CheckBox ToggleButton RadioButton ToggleSwitch ScrollViewer ContentControl UserControl Expander TabItem ListBoxItem ComboBoxItem Label'.split(' '));
const stylePanels=new Set('StackPanel Grid Panel Canvas WrapPanel DockPanel UniformGrid'.split(' '));
const styleShapes=new Set('Rectangle Ellipse Line Path Polyline Polygon'.split(' '));
const styleText=new Set(['TextBlock','SelectableTextBlock']);
const styleCss={Width:['width'],Height:['height'],MinWidth:['min-width'],MinHeight:['min-height'],MaxWidth:['max-width'],MaxHeight:['max-height'],
  Margin:['margin'],Opacity:['opacity'],Background:['background'],Foreground:['color'],BorderBrush:['border-color'],BorderThickness:['border-width','border-style'],Padding:['padding'],CornerRadius:['border-radius'],
  FontFamily:['font-family'],FontSize:['font-size'],FontStyle:['font-style'],FontWeight:['font-weight'],TextAlignment:['text-align'],TextWrapping:['white-space'],
  TextTrimming:['text-overflow','overflow'],MaxLines:['display','-webkit-box-orient','-webkit-line-clamp','overflow'],LineHeight:['line-height'],
  HorizontalAlignment:['justify-self'],VerticalAlignment:['align-self'],HorizontalContentAlignment:['justify-content','display'],VerticalContentAlignment:['align-items','display'],
  RenderTransform:['transform'],RenderTransformOrigin:['transform-origin'],Clip:['clip-path'],Fill:['fill'],Stroke:['stroke'],StrokeThickness:['stroke-width','--ui-pen-width'],
  StrokeDashArray:['stroke-dasharray'],StrokeDashOffset:['stroke-dashoffset'],StrokeLineCap:['stroke-linecap'],StrokeJoin:['stroke-linejoin'],StrokeMiterLimit:['stroke-miterlimit'],
  Stretch:[],RadiusX:['rx'],RadiusY:['ry']};
function uiStyleClasses(value){
  if(typeof value!=='string'||value.length>2048)throw new Error('Invalid style classes.');
  const names=value.trim()?value.trim().split(/\s+/):[];
  if(names.length>32||new Set(names).size!==names.length||names.some(name=>!styleIdentifier.test(name)))throw new Error('Invalid style class identifier.');
  return names;
}
function uiStyleSelector(value){
  if(typeof value!=='string'||!value.length||value.length>512)throw new Error('Invalid style selector.');
  const tokens=value.match(/[.#:]?[A-Za-z_][A-Za-z0-9_-]*/g);
  if(!tokens||tokens.join('')!==value||tokens.length>17||/^[.#:]/.test(tokens[0]))throw new Error('Unsupported style selector.');
  const target=tokens[0];if(!types.has(target)&&!Object.hasOwn(drawingTags,target)&&!['Label','LayoutTransformControl'].includes(target))throw new Error('Unknown style target.');
  let css='[data-ui-type="'+target+'"]',named=false;const classes=new Set(),pseudos=new Set();
  for(const token of tokens.slice(1)){
    const name=token.slice(1);if(!styleIdentifier.test(name))throw new Error('Invalid style selector identifier.');
    if(token[0]==='.') {if(classes.has(name))throw new Error('Duplicate class.');classes.add(name);css+='[data-ui-classes~="'+name+'"]';}
    else if(token[0]==='#') {if(named)throw new Error('Duplicate name.');named=true;css+='[data-ui-name="'+name+'"]';}
    else if(token[0]===':'&&Object.hasOwn(stylePseudo,name)&&!pseudos.has(name)){pseudos.add(name);css+=stylePseudo[name];}
    else throw new Error('Unsupported style pseudoclass.');
  }
  return {target,css,conditional:classes.size>0||pseudos.size>0};
}
function validateUiStyleValue(type,name,value){
  if(!Object.hasOwn(styleCss,name))throw new Error('Style property is not a presentation capability.');
  const text=styleText.has(type),templated=styleTemplated.has(type),shape=styleShapes.has(type),panel=stylePanels.has(type),border=type==='Border';
  const choice=values=>{if(typeof value!=='string'||!values.includes(value))throw new Error('Invalid style enumeration.');};
  switch(name){
    case 'Width':case 'Height':case 'MinWidth':case 'MinHeight':case 'MaxWidth':case 'MaxHeight':number(value,0,10000);return;
    case 'Margin':tuple(value,-10000,10000);return;
    case 'Opacity':number(value,0,1);return;
    case 'HorizontalAlignment':choice(['Left','Center','Right','Stretch']);return;
    case 'VerticalAlignment':choice(['Top','Center','Bottom','Stretch']);return;
    case 'RenderTransform':drawingMatrix(value);return;
    case 'RenderTransformOrigin':drawingOrigin(value);return;
    case 'Clip':drawingPath(value);return;
    case 'Background':if(!panel&&!templated&&!border)break;validateBrush(value);return;
    case 'Foreground':if(!text&&!templated)break;validateBrush(value);return;
    case 'BorderBrush':if(!border&&!templated)break;validateBrush(value);return;
    case 'BorderThickness':if(!border&&!templated)break;tuple(value,0,border?16:128);return;
    case 'Padding':case 'CornerRadius':if(!border&&!templated)break;tuple(value,0,128);return;
    case 'FontFamily':if(!text&&!templated)break;validateAvaloniaFeatures({type,properties:{FontFamily:value}});return;
    case 'FontSize':if(!text&&!templated)break;number(value,.1,512);return;
    case 'FontStyle':if(!text&&!templated)break;choice(['Normal','Italic','Oblique']);return;
    case 'FontWeight':if(!text&&!templated)break;choice(['Normal','Medium','SemiBold','Bold']);return;
    case 'TextAlignment':if(!text&&type!=='TextBox')break;choice(['Left','Center','Right','Justify']);return;
    case 'TextWrapping':if(!text&&type!=='TextBox')break;choice(['NoWrap','Wrap']);return;
    case 'TextTrimming':if(!text)break;choice(['None','CharacterEllipsis','WordEllipsis']);return;
    case 'LineHeight':if(!text)break;number(value,.1,10000);return;
    case 'MaxLines':if(!text)break;integer(value,0,4096);return;
    case 'HorizontalContentAlignment':if(!styleContent.has(type))break;choice(['Left','Center','Right','Stretch']);return;
    case 'VerticalContentAlignment':if(!styleContent.has(type))break;choice(['Top','Center','Bottom','Stretch']);return;
    case 'Fill':case 'Stroke':if(!shape)break;validateBrush(value);return;
    case 'StrokeThickness':if(!shape)break;number(value,0,128);return;
    case 'StrokeDashArray':if(!shape)break;validateAvaloniaFeatures({type,properties:{StrokeDashArray:value}});return;
    case 'StrokeDashOffset':if(!shape)break;number(value,-1000000,1000000);return;
    case 'StrokeLineCap':if(!shape)break;choice(['Flat','Round','Square']);return;
    case 'StrokeJoin':if(!shape)break;choice(['Miter','Round','Bevel']);return;
    case 'StrokeMiterLimit':if(!shape)break;number(value,1,10000);return;
    case 'Stretch':if(!shape&&type!=='Viewbox')break;choice(['None','Fill','Uniform','UniformToFill']);return;
    case 'RadiusX':case 'RadiusY':if(type!=='Rectangle')break;number(value,0,10000);return;
  }
  throw new Error('Style property does not belong to its target.');
}
function uiStyleDeclarations(type,p){
  const e=document.createElement('div');common(e,p);applyAvaloniaFeatures({element:e,slot:e,type,node:{properties:p}});
  if(p.TextAlignment!==undefined)e.style.textAlign=p.TextAlignment.toLowerCase();
  if(p.TextWrapping!==undefined)e.style.whiteSpace=p.TextWrapping==='Wrap'?'pre-wrap':'pre';
  if(p.TextTrimming==='None'){e.style.textOverflow='clip';e.style.overflow='visible';}
  if(p.MaxLines===0){e.style.display='block';e.style.webkitBoxOrient='initial';e.style.webkitLineClamp='none';if(p.TextTrimming===undefined||p.TextTrimming==='None')e.style.overflow='visible';}
  const result=new Map();
  for(const name of Object.keys(p))for(const css of styleCss[name]||[]){const value=e.style.getPropertyValue(css);if(value)result.set(css,value);}
  const set=(name,value)=>{if(value!==undefined)result.set(name,String(value));};
  if(p.Fill!==undefined)set('fill',brushPaint(p.Fill));if(p.Stroke!==undefined)set('stroke',brushPaint(p.Stroke));
  if(p.StrokeThickness!==undefined){set('stroke-width',p.StrokeThickness);set('--ui-pen-width',p.StrokeThickness);}
  if(p.StrokeDashArray!==undefined){const values=drawingNumbers(p.StrokeDashArray,64,0,10000);set('stroke-dasharray',values.length?values.map(n=>'calc('+n+' * var(--ui-pen-width, 1))').join(' '):'none');}
  if(p.StrokeDashOffset!==undefined)set('stroke-dashoffset','calc('+p.StrokeDashOffset+' * var(--ui-pen-width, 1))');
  if(p.StrokeLineCap!==undefined)set('stroke-linecap',({Flat:'butt',Round:'round',Square:'square'})[p.StrokeLineCap]);
  if(p.StrokeJoin!==undefined)set('stroke-linejoin',p.StrokeJoin.toLowerCase());set('stroke-miterlimit',p.StrokeMiterLimit);set('rx',p.RadiusX);set('ry',p.RadiusY);
  return result;
}
function buildUiStyles(value){
  let rules=0,setters=0,characters=0,cssLength=0;const output=[],conditional=[];
  function visit(node){
    const p=node.properties;
    if(p.Classes!==undefined)uiStyleClasses(p.Classes);
    if(p.Name!==undefined&&(typeof p.Name!=='string'||!styleIdentifier.test(p.Name)))throw new Error('Invalid control name.');
    const styles=node.styles??[];
    if(!Array.isArray(styles)||styles.length>128||(rules+=styles.length)>512)throw new Error('Style rule budget exceeded.');
    let localSetters=0;
    for(const style of styles){
      if(!style||!style.properties||typeof style.properties!=='object'||Array.isArray(style.properties))throw new Error('Invalid style rule.');
      const selector=uiStyleSelector(style.selector),properties=Object.entries(style.properties);
      if(properties.length>64||(setters+=properties.length)>4096||(localSetters+=properties.length)>512)throw new Error('Style setter budget exceeded.');
      for(const [name,v]of properties){validateUiStyleValue(selector.target,name,v);characters+=name.length+JSON.stringify(v).length;}
      if(characters>1048576)throw new Error('Style text budget exceeded.');
      validateAvaloniaFeatures({type:selector.target,properties:style.properties});
      const declarations=uiStyleDeclarations(selector.target,style.properties),normal=[],shape=[],slot=[];
      const nested=['Expander',...toggles].includes(selector.target);
      for(const [name,v]of declarations){
        const list=name.startsWith('stroke')||['fill','rx','ry','--ui-pen-width'].includes(name)?shape:
          nested&&['display','justify-content','align-items'].includes(name)?slot:normal;
        list.push(name+':'+v+' !important;');
      }
      const scope='[data-ui-key="'+CSS.escape(node.key)+'"]',target=':where('+scope+selector.css+','+scope+' '+selector.css+')';
      const text=target+'{'+normal.join('')+'}'+(shape.length?target+' > :is(path,polyline,polygon,rect,ellipse,line){'+shape.join('')+'}':'')+
        (slot.length?target+' > [data-ui-style-slot]{'+slot.join('')+'}':'');
      if((cssLength+=text.length)>2097152)throw new Error('Projected stylesheet budget exceeded.');(selector.conditional?conditional:output).push(text);
    }
    node.children.forEach(visit);
  }
  value.roots.forEach(visit);return output.concat(conditional).join('\n');
}
function applyUiStyleIdentity(entry){
  const p=entry.node.properties,e=entry.element;e.dataset.uiType=entry.type;e.dataset.uiClasses=(p.Classes===undefined?[]:uiStyleClasses(p.Classes)).join(' ');
  if(p.Name!==undefined)e.dataset.uiName=p.Name;else delete e.dataset.uiName;
  e.dataset.uiDisabled=String(entry.disabled);
  if(entry.slot!==e)entry.slot.dataset.uiStyleSlot='';
  if(entry.shape)entry.shape.style.cssText='';
  // Framework defaults are inline in the DOM projection. Authored styles override those
  // defaults; explicit local properties remain stronger than any scoped/pseudoclass rule.
  const local=Object.fromEntries(Object.entries(p).filter(([name])=>Object.hasOwn(styleCss,name)));
  for(const [name,value]of uiStyleDeclarations(entry.type,local)){
    const target=entry.shape&&(name.startsWith('stroke')||['fill','rx','ry','--ui-pen-width'].includes(name))?entry.shape:(['justify-content','align-items'].includes(name)||name==='display'&&styleContent.has(entry.type)?entry.slot:e);
    target.style.setProperty(name,value,'important');
  }
}
