// Trusted CSS/SVG projection. The native guest remains the authoritative Avalonia renderer.
// Parsing is bounded and never enables URLs, arbitrary CSS, HTML, scripts or external assets.
const drawingTags={Path:'path',Polyline:'polyline',Polygon:'polygon'};
for(const [type,tag]of Object.entries(drawingTags))widgets.set(type,{
  create(){const element=document.createElementNS('http://www.w3.org/2000/svg','svg'),shape=document.createElementNS(element.namespaceURI,tag);element.append(shape);return {element,shape,slot:element};},
  update(entry){
    const p=entry.node.properties,e=entry.element,s=entry.shape,width=p.Width??100,height=p.Height??60;
    e.setAttribute('viewBox',`0 0 ${width||1} ${height||1}`);e.style.width=px(width);e.style.height=px(height);
    e.setAttribute('preserveAspectRatio',p.Stretch==='Fill'?'none':p.Stretch==='UniformToFill'?'xMidYMid slice':'xMidYMid meet');
    if(entry.type==='Path'){
      const path=drawingPath(p.Data??'');s.setAttribute('d',path.data);s.setAttribute('fill-rule',path.rule);
    }else{s.setAttribute('points',drawingNumbers(p.Points??'',1024).join(' '));s.setAttribute('fill-rule',p.FillRule==='NonZero'?'nonzero':'evenodd');}
    s.setAttribute('fill',p.Fill?color(p.Fill):'none');s.setAttribute('stroke',p.Stroke?color(p.Stroke):'none');s.setAttribute('stroke-width',String(p.StrokeThickness??1));
  }
});
widgets.set('Label',{
  create(){const element=document.createElement('div');return {element,slot:element};},
  update(entry,children){if(children.length)reconcile(entry.slot,children);else entry.slot.textContent=entry.node.properties.Content??'';}
});
widgets.set('LayoutTransformControl',{
  create(){const element=document.createElement('div'),slot=document.createElement('div');slot.style.cssText='position:absolute;transform-origin:0 0;display:flow-root;width:max-content;';element.append(slot);return {element,slot};},
  update(entry,children){
    reconcile(entry.slot,children);entry.element.style.position='relative';entry.element.style.overflow=entry.node.properties.ClipToBounds===false?'visible':'hidden';
    entry.layout=()=>layoutTransform(entry);viewboxes.set(entry.element,entry);boxResize.observe(entry.element);
  }
});
function drawingReader(source){
  if(typeof source!=='string'||source.length>16384)throw new Error('Drawing text exceeds its budget.');
  let position=0;const numeric=/[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/y;
  const white=()=>{while(position<source.length&&/\s/.test(source[position]))position++;};
  return {
    end(){let p=position;while(p<source.length&&/\s/.test(source[p]))p++;return p===source.length;},
    peek(){white();return source[position]??'';},
    take(){white();if(position===source.length)throw new Error('Incomplete drawing literal.');return source[position++];},
    number(allowComma,requireSeparator=false){
      const before=position;white();if(source[position]===','){if(!allowComma)throw new Error('Unexpected drawing comma.');position++;white();}
      if(requireSeparator&&position===before)throw new Error('Separate drawing values with commas or whitespace.');
      numeric.lastIndex=position;const match=numeric.exec(source);if(!match)throw new Error('Expected a finite drawing number.');position=numeric.lastIndex;
      return number(Number(match[0]),-1000000,1000000);
    }
  };
}
function drawingNumbers(source,maximum,min=-1000000,max=1000000){
  const reader=drawingReader(source),values=[];
  while(!reader.end()){if(values.length===maximum)throw new Error('Drawing value count exceeds its budget.');values.push(number(reader.number(values.length!==0,values.length!==0),min,max));}
  return values;
}
function drawingMatrix(source){
  if(typeof source!=='string'||source.length>16384)throw new Error('Invalid transform.');
  source=source.trim();if(source==='none')return [1,0,0,1,0,0];
  if(!source.startsWith('matrix(')||!source.endsWith(')'))throw new Error('Use a two-dimensional matrix transform.');
  const values=drawingNumbers(source.slice(7,-1),6);if(values.length!==6)throw new Error('A matrix needs six coefficients.');return values;
}
function drawingOrigin(source){
  if(typeof source!=='string'||source.length>256)throw new Error('Invalid transform origin.');
  const parts=source.trim().split(/[ ,]+/);if(parts.length!==2)throw new Error('A transform origin needs two coordinates.');
  const relative=parts[0].endsWith('%');if(parts[1].endsWith('%')!==relative)throw new Error('Transform origin units must agree.');
  return parts.map(part=>{const values=drawingNumbers(relative?part.slice(0,-1):part,1);if(values.length!==1)throw new Error('Invalid transform origin.');return values[0]+(relative?'%':'px');}).join(' ');
}
function drawingPath(source){
  const reader=drawingReader(source);let rule='evenodd',data=source.trim(),command='',started=false,segments=0;
  if(/[Ff]/.test(reader.peek())&&reader.peek()){
    reader.take();const flag=reader.take();if(flag!=='0'&&flag!=='1')throw new Error('Invalid fill rule.');rule=flag==='1'?'nonzero':'evenodd';data=data.replace(/^[Ff]\s*[01]\s*/,'');
  }
  while(!reader.end()){
    if(++segments>512)throw new Error('Path segment budget exceeded.');
    const explicit=/[A-Za-z]/.test(reader.peek());if(explicit)command=reader.take().toUpperCase();
    if(!started&&command!=='M')throw new Error('A path must start with a move.');
    const count=({M:2,L:2,T:2,H:1,V:1,C:6,S:4,Q:4,A:7,Z:0})[command];if(count===undefined)throw new Error('Unknown path command.');
    if(count===0){command='';continue;}const args=[];
    for(let i=0;i<count;i++)args.push(reader.number(i!==0||!explicit));
    if(command==='A'&&(args[0]<0||args[1]<0||![0,1].includes(args[3])||![0,1].includes(args[4])))throw new Error('Invalid arc radii or flags.');
    started=true;if(command==='M')command='L';
  }
  return {data,rule};
}
function validateAvaloniaFeatures(node){
  const p=node.properties;
  for(const name of ['RenderTransform','LayoutTransform'])if(p[name]!==undefined)drawingMatrix(p[name]);
  if(p.RenderTransformOrigin!==undefined)drawingOrigin(p.RenderTransformOrigin);
  if(p.Clip!==undefined)drawingPath(p.Clip);
  if(node.type==='Path'&&p.Data!==undefined)drawingPath(p.Data);
  if(['Polygon','Polyline'].includes(node.type)&&p.Points!==undefined&&drawingNumbers(p.Points,1024).length%2)throw new Error('Points need complete coordinate pairs.');
  if(p.StrokeDashArray!==undefined){const dashes=drawingNumbers(p.StrokeDashArray,64,0,10000);if(dashes.length&&!dashes.some(value=>value>0))throw new Error('Dash pattern needs a positive length.');}
  if(p.FontFamily!==undefined&&(typeof p.FontFamily!=='string'||!p.FontFamily.trim()||p.FontFamily.length>128||/[\x00-\x1f\x7f:/\\#{}]/.test(p.FontFamily)))throw new Error('Use a local font family name.');
  if(p.ZIndex!==undefined)integer(p.ZIndex,-32768,32767);
  if(p.FontSize!==undefined)number(p.FontSize,.1,512);
  if(p.LineHeight!==undefined)number(p.LineHeight,.1,10000);
  if(p.MaxLines!==undefined)integer(p.MaxLines,0,4096);
  if(p.Margin!==undefined)tuple(p.Margin,-10000,10000);
  for(const axis of ['Width','Height'])if(p['Min'+axis]!==undefined&&p['Max'+axis]!==undefined&&p['Min'+axis]>p['Max'+axis])throw new Error('Minimum size exceeds maximum size.');
}
function applyAvaloniaFeatures(entry){
  const p=entry.node.properties,e=entry.element,s=entry.shape;
  if(p.ZIndex!==undefined){e.style.zIndex=String(p.ZIndex);if(!e.style.position)e.style.position='relative';}
  if(p.IsHitTestVisible===false)e.style.pointerEvents='none';
  if(p.FlowDirection!==undefined)e.style.direction=p.FlowDirection==='RightToLeft'?'rtl':'ltr';
  if(p.RenderTransform!==undefined)e.style.transform='matrix('+drawingMatrix(p.RenderTransform).join(',')+')';
  if(p.RenderTransformOrigin!==undefined)e.style.transformOrigin=drawingOrigin(p.RenderTransformOrigin);
  if(p.Clip!==undefined){const clip=drawingPath(p.Clip);e.style.clipPath='path('+clip.rule+', "'+clip.data+'")';}
  if(p.FontFamily!==undefined)e.style.fontFamily=p.FontFamily;
  if(p.FontSize!==undefined)e.style.fontSize=px(p.FontSize,.1,512);
  if(p.FontStyle!==undefined)e.style.fontStyle=({Normal:'normal',Italic:'italic',Oblique:'oblique'})[p.FontStyle]??'normal';
  if(p.FontWeight!==undefined)e.style.fontWeight=({Normal:'400',Medium:'500',SemiBold:'600',Bold:'700'})[p.FontWeight]??'400';
  if(p.Foreground!==undefined)e.style.color=color(p.Foreground);
  if(p.Padding!==undefined)e.style.padding=thickness(p.Padding);
  if(p.CornerRadius!==undefined)e.style.borderRadius=radius(p.CornerRadius);
  if(p.BorderThickness!==undefined){e.style.borderStyle='solid';e.style.borderWidth=thickness(p.BorderThickness);}
  if(p.BorderBrush!==undefined)e.style.borderColor=color(p.BorderBrush);
  if(p.Background!==undefined)e.style.background=color(p.Background);
  if(p.LineHeight!==undefined)e.style.lineHeight=px(p.LineHeight,.1,10000);
  if(p.TextTrimming!==undefined&&p.TextTrimming!=='None'){e.style.overflow='hidden';e.style.textOverflow='ellipsis';}
  if(p.MaxLines>0){e.style.display='-webkit-box';e.style.webkitBoxOrient='vertical';e.style.webkitLineClamp=String(p.MaxLines);e.style.overflow='hidden';}
  const target=entry.slot??e;
  // The renderer resets outer nodes; owned content slots survive keyed updates.
  // Restore only fields set by this adapter, preserving unrelated widget styles.
  if(target!==e&&entry.contentAlignmentStyle){
    for(const [key,value]of Object.entries(entry.contentAlignmentStyle))target.style[key]=value;
    entry.contentAlignmentStyle=null;
  }
  if(p.HorizontalContentAlignment!==undefined||p.VerticalContentAlignment!==undefined){
    if(target!==e)entry.contentAlignmentStyle={display:target.style.display,justifyContent:target.style.justifyContent,alignItems:target.style.alignItems};
    target.style.display='flex';
    target.style.justifyContent=({Left:'flex-start',Center:'center',Right:'flex-end',Stretch:'flex-start'})[p.HorizontalContentAlignment]??'flex-start';
    target.style.alignItems=({Top:'flex-start',Center:'center',Bottom:'flex-end',Stretch:'stretch'})[p.VerticalContentAlignment]??'stretch';
  }
  if(entry.type==='TextBox'&&p.TextWrapping!==undefined)e.style.whiteSpace=p.TextWrapping==='Wrap'?'pre-wrap':'pre';
  if(entry.type==='ScrollViewer'){
    const policy=value=>({Disabled:'hidden',Hidden:'hidden',Auto:'auto',Visible:'scroll'})[value]??'auto';
    if(p.HorizontalScrollBarVisibility!==undefined)e.style.overflowX=policy(p.HorizontalScrollBarVisibility);
    if(p.VerticalScrollBarVisibility!==undefined)e.style.overflowY=policy(p.VerticalScrollBarVisibility);
    if(p.AllowAutoHide===false)e.style.scrollbarGutter='stable';
    // Applied after insertion by the existing bounded layout scheduler; no stale timer survives retirement.
    entry.layout=()=>{if(p.Offset!==undefined){const offset=tuple(p.Offset,0,1000000);e.scrollLeft=offset[0];e.scrollTop=offset[1];}};
  }
  if(s){
    const width=p.StrokeThickness??1,dashes=drawingNumbers(p.StrokeDashArray??'',64,0,10000);
    // Avalonia dash lengths/offsets are multiples of pen thickness; SVG uses user-space lengths.
    s.setAttribute('stroke-dasharray',dashes.length?dashes.map(value=>value*width).join(' '):'none');
    s.setAttribute('stroke-dashoffset',String((p.StrokeDashOffset??0)*width));
    s.setAttribute('stroke-linecap',({Flat:'butt',Round:'round',Square:'square'})[p.StrokeLineCap]??'butt');
    s.setAttribute('stroke-linejoin',({Miter:'miter',Round:'round',Bevel:'bevel'})[p.StrokeJoin]??'miter');
    s.setAttribute('stroke-miterlimit',String(p.StrokeMiterLimit??10));
  }
}
function layoutTransform(entry){
  if(disposed||!entry.element.isConnected)return;
  const e=entry.element,p=entry.node.properties,slot=entry.slot,child=slot.firstElementChild;
  if(!child)return;const m=drawingMatrix(p.LayoutTransform??(p.UseRenderTransform?p.RenderTransform:undefined)??'none'),w=child.offsetWidth,h=child.offsetHeight;
  const corners=[[0,0],[w,0],[0,h],[w,h]].map(([x,y])=>[x*m[0]+y*m[2]+m[4],x*m[1]+y*m[3]+m[5]]);
  const xs=corners.map(point=>point[0]),ys=corners.map(point=>point[1]),left=Math.min(...xs),top=Math.min(...ys),width=Math.max(...xs)-left,height=Math.max(...ys)-top;
  if(p.Width===undefined)e.style.width=width+'px';if(p.Height===undefined)e.style.height=height+'px';
  slot.style.transform='matrix('+[m[0],m[1],m[2],m[3],m[4]-left,m[5]-top].join(',')+')';
}
