// Public MCP Apps host context, separate from model-authored component properties.
// Missing fields are patches, not a request to reset unrelated theme or layout state.
const hostStyleKeys=new Set(['--font-sans','--font-mono','--border-width-regular']);
for(const role of ['background','text','border'])for(const tone of ['primary','secondary','tertiary','inverse','ghost','info','danger','success','warning','disabled'])hostStyleKeys.add(`--color-${role}-${tone}`);
for(const tone of ['primary','secondary','inverse','info','danger','success','warning'])hostStyleKeys.add('--color-ring-'+tone);
for(const weight of ['normal','medium','semibold','bold'])hostStyleKeys.add('--font-weight-'+weight);
for(const [role,sizes]of [['text',['xs','sm','md','lg']],['heading',['xs','sm','md','lg','xl','2xl','3xl']]])for(const size of sizes)for(const metric of ['size','line-height'])hostStyleKeys.add(`--font-${role}-${size}-${metric}`);
for(const size of ['xs','sm','md','lg','xl','full'])hostStyleKeys.add('--border-radius-'+size);
for(const size of ['hairline','sm','md','lg'])hostStyleKeys.add('--shadow-'+size);
function applyHostContext(value){
  if(!value||typeof value!=='object'||Array.isArray(value))return;
  const html=document.documentElement;
  if(value.theme==='dark'||value.theme==='light'){html.style.colorScheme=value.theme;html.dataset.theme=value.theme;}
  if(typeof value.locale==='string'&&value.locale.length<=128){try{const locales=Intl.getCanonicalLocales(value.locale);if(locales[0])html.lang=locales[0];}catch{/* Ignore invalid optional locale metadata. */}}
  if(typeof value.timeZone==='string'&&value.timeZone.length<=128){try{html.dataset.timeZone=new Intl.DateTimeFormat('en',{timeZone:value.timeZone}).resolvedOptions().timeZone;}catch{/* Ignore invalid optional timezone metadata. */}}
  if(['web','desktop','mobile'].includes(value.platform))html.dataset.platform=value.platform;
  if(['inline','fullscreen','pip'].includes(value.displayMode))html.dataset.displayMode=value.displayMode;
  for(const capability of ['touch','hover'])if(typeof value.deviceCapabilities?.[capability]==='boolean')html.dataset[capability]=String(value.deviceCapabilities[capability]);
  const dimensions=value.containerDimensions;
  if(dimensions&&typeof dimensions==='object'){
    const height=dimensions.height??dimensions.maxHeight,width=dimensions.width??dimensions.maxWidth;
    if(typeof height==='number'&&Number.isFinite(height)&&height>=0)root.style.maxHeight=Math.min(height,1600)+'px';
    if(typeof width==='number'&&Number.isFinite(width)&&width>=0)root.style.maxWidth=Math.min(width,10000)+'px';
  }
  if(value.safeAreaInsets&&typeof value.safeAreaInsets==='object')for(const side of ['top','right','bottom','left']){
    const inset=value.safeAreaInsets[side];
    if(typeof inset==='number'&&Number.isFinite(inset)&&inset>=0){
      const bounded=Math.min(inset,512);html.style.setProperty('--ui-safe-'+side,bounded+'px');
      document.body.style['padding'+side[0].toUpperCase()+side.slice(1)]=(12+bounded)+'px';
    }
  }
  const variables=value.styles?.variables;
  if(variables&&typeof variables==='object'&&!Array.isArray(variables)&&Object.keys(variables).length<=128){
    for(const [key,token]of Object.entries(variables)){
      if(!hostStyleKeys.has(key))continue;
      if(token===null){html.style.removeProperty(key);continue;}
      if(typeof token!=='string'||token.length>1024||/[{};<>\\]/.test(token)||/(?:url|image-set|expression)\s*\(|@import/i.test(token))continue;
      html.style.setProperty(key,token);
    }
  }
  // Host-provided stylesheet/font-face blocks are deliberately not injected: this resource
  // has no font/network CSP grants. Local --font-sans/--font-mono tokens remain supported.
}
