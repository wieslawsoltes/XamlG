// Brush descriptors are data, never URLs, markup or CSS. SVG paint servers are owned by
// one committed snapshot; replacement/retirement cannot accumulate detached paint resources.
const brushNames=new Set(['Background','Foreground','BorderBrush','Fill','Stroke']);
let brushRegistry={map:new Map(),bank:null};
function brushObject(value,allowed){
  if(!value||typeof value!=='object'||Array.isArray(value)||Object.keys(value).some(name=>!allowed.includes(name)))throw new Error('Invalid brush object.');
}
function brushRadius(value){
  if(typeof value!=='string'||!value.length||value.length>80)throw new Error('Invalid gradient radius.');
  const text=value.trim(),relative=text.endsWith('%'),raw=relative?text.slice(0,-1):text;
  if(!/^[+]?\d*\.?\d+(?:[eE][+-]?\d+)?$/.test(raw))throw new Error('Invalid gradient radius.');
  const n=number(Number(raw),Number.MIN_VALUE,1000000);return {value:relative?n/100:n,relative};
}
function brushPoint(value){
  drawingOrigin(value);const p=value.trim().split(/[ ,]+/),relative=p[0].endsWith('%');
  return {x:Number(relative?p[0].slice(0,-1):p[0])/(relative?100:1),y:Number(relative?p[1].slice(0,-1):p[1])/(relative?100:1),relative};
}
function validateBrush(value){
  if(value===null)return;
  if(typeof value==='string'){color(value);return;}
  const allowed=value?.kind==='solid'?['kind','color','opacity','transform','transformOrigin']:
    value?.kind==='linear'?['kind','opacity','transform','transformOrigin','spreadMethod','startPoint','endPoint','stops']:
    value?.kind==='radial'?['kind','opacity','transform','transformOrigin','spreadMethod','center','gradientOrigin','radiusX','radiusY','stops']:null;
  if(!allowed)throw new Error('Unknown brush kind.');brushObject(value,allowed);
  if(JSON.stringify(value).length>16384)throw new Error('Brush exceeds its text budget.');
  if(value.opacity!==undefined)number(value.opacity,0,1);
  if(value.transform!==undefined)drawingMatrix(value.transform);
  if(value.transformOrigin!==undefined)brushPoint(value.transformOrigin);
  if(value.kind==='solid'){if(value.color!==undefined)color(value.color);return;}
  if(value.spreadMethod!==undefined&&!['Pad','Reflect','Repeat'].includes(value.spreadMethod))throw new Error('Invalid gradient spread.');
  for(const name of ['startPoint','endPoint','center','gradientOrigin'])if(value[name]!==undefined)brushPoint(value[name]);
  for(const name of ['radiusX','radiusY'])if(value[name]!==undefined)brushRadius(value[name]);
  if(!Array.isArray(value.stops)||value.stops.length>64)throw new Error('Invalid gradient stops.');let previous=-1;
  for(const stop of value.stops){brushObject(stop,['color','offset']);color(stop.color);number(stop.offset,0,1);if(stop.offset<previous)throw new Error('Unordered gradient stops.');previous=stop.offset;}
}
function brushSolid(value){
  if(value===null)return 'transparent';if(typeof value==='string')return color(value);
  const c=value.kind==='solid'?value.color??'Transparent':value.stops[0]?.color??'Transparent';
  return value.opacity===undefined||value.opacity===1?color(c):`color-mix(in srgb, ${color(c)} ${value.opacity*100}%, transparent)`;
}
function brushCss(value){
  if(value===null||typeof value==='string'||value.kind==='solid')return brushSolid(value);
  let stops=value.stops.map(stop=>({color:brushSolid({kind:'solid',color:stop.color,opacity:value.opacity??1}),offset:stop.offset}));
  if(!stops.length)return 'transparent';if(stops.length===1)return stops[0].color;
  if(value.spreadMethod==='Reflect')stops=stops.map(stop=>({...stop,offset:stop.offset/2})).concat([...stops].reverse().map(stop=>({...stop,offset:1-stop.offset/2})));
  const values=stops.map(stop=>stop.color+' '+stop.offset*100+'%').join(','),repeat=value.spreadMethod&&value.spreadMethod!=='Pad'?'repeating-':'';
  if(value.kind==='linear'){
    const a=brushPoint(value.startPoint??'0%,0%'),b=brushPoint(value.endPoint??'100%,100%');
    return `${repeat}linear-gradient(${90+Math.atan2(b.y-a.y,b.x-a.x)*180/Math.PI}deg,${values})`;
  }
  const c=brushPoint(value.center??'50%,50%'),rx=brushRadius(value.radiusX??'50%'),ry=brushRadius(value.radiusY??'50%');
  const coordinate=(n,relative)=>n*(relative?100:1)+(relative?'%':'px');
  return `${repeat}radial-gradient(ellipse ${coordinate(rx.value,rx.relative)} ${coordinate(ry.value,ry.relative)} at ${coordinate(c.x,c.relative)} ${coordinate(c.y,c.relative)},${values})`;
}
function prepareUiBrushes(value){
  const ns='http://www.w3.org/2000/svg',map=new Map(),bank=document.createElementNS(ns,'svg'),defs=document.createElementNS(ns,'defs');
  bank.id='ui-brush-bank';bank.setAttribute('width','0');bank.setAttribute('height','0');bank.setAttribute('aria-hidden','true');bank.style.cssText='position:absolute;pointer-events:none';bank.append(defs);
  let count=0,characters=0;
  function add(p){for(const [name,v]of Object.entries(p))if(brushNames.has(name)){
    validateBrush(v);if(v===null||typeof v==='string'||v.kind==='solid')continue;
    const key=JSON.stringify(v);if(map.has(key))continue;
    if(++count>512||(characters+=key.length)>1048576)throw new Error('Snapshot brush budget exceeded.');
    const id='ui-paint-'+count,gradient=document.createElementNS(ns,v.kind==='linear'?'linearGradient':'radialGradient');gradient.id=id;map.set(key,id);
    gradient.setAttribute('spreadMethod',(v.spreadMethod??'Pad').toLowerCase());
    const xy=(p,axis)=>String(p[axis]);let relative,shapeTransform='';
    if(v.kind==='linear'){
      const a=brushPoint(v.startPoint??'0%,0%'),b=brushPoint(v.endPoint??'100%,100%');relative=a.relative&&b.relative;
      const coord=(p,axis)=>p.relative&&!relative?p[axis]*100+'%':xy(p,axis);
      gradient.setAttribute('x1',coord(a,'x'));gradient.setAttribute('y1',coord(a,'y'));gradient.setAttribute('x2',coord(b,'x'));gradient.setAttribute('y2',coord(b,'y'));
    }else{
      const c=brushPoint(v.center??'50%,50%'),f=brushPoint(v.gradientOrigin??'50%,50%'),rx=brushRadius(v.radiusX??'50%'),ry=brushRadius(v.radiusY??'50%');relative=c.relative&&f.relative&&rx.relative&&ry.relative;
      gradient.setAttribute('cx',xy(c,'x'));gradient.setAttribute('cy',xy(c,'y'));gradient.setAttribute('fx',xy(f,'x'));
      const ratio=rx.relative===ry.relative?ry.value/rx.value:1;
      gradient.setAttribute('fy',String(c.y+(f.y-c.y)/ratio));gradient.setAttribute('r',String(rx.value));
      if(ratio!==1)shapeTransform=`translate(${c.x} ${c.y}) scale(1 ${ratio}) translate(${-c.x} ${-c.y})`;
    }
    gradient.setAttribute('gradientUnits',relative?'objectBoundingBox':'userSpaceOnUse');
    if(v.transform){const o=brushPoint(v.transformOrigin??'0%,0%'),m=drawingMatrix(v.transform);shapeTransform=`translate(${o.x} ${o.y}) matrix(${m.join(' ')}) translate(${-o.x} ${-o.y}) `+shapeTransform;}
    if(shapeTransform)gradient.setAttribute('gradientTransform',shapeTransform);
    for(const stop of v.stops){const node=document.createElementNS(ns,'stop');node.setAttribute('offset',String(stop.offset));node.setAttribute('stop-color',color(stop.color));node.setAttribute('stop-opacity',String(v.opacity??1));gradient.append(node);}
    defs.append(gradient);
  }}
  function visit(node){add(node.properties);for(const style of node.styles??[])add(style.properties);node.children.forEach(visit);}value.roots.forEach(visit);
  return {map,bank:count?bank:null};
}
function brushPaint(value){
  if(value===null)return 'none';if(typeof value==='string'||value.kind==='solid')return brushSolid(value);
  const id=brushRegistry.map.get(JSON.stringify(value));if(!id)throw new Error('Brush was not prepared.');return 'url(#'+id+')';
}
function commitUiBrushes(previous){previous.bank?.remove();if(brushRegistry.bank)document.body.append(brushRegistry.bank);}
function retireUiBrushes(){brushRegistry.bank?.remove();brushRegistry={map:new Map(),bank:null};}
