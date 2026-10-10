import {test,expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';
const html=loadUiResource();
const node=(key,type,properties={},children=[],styles=[])=>({key,type,properties,children,styles});
const rule=(selector,properties)=>({selector,properties});
const byKey=(app,key)=>app.locator(`[data-ui-key="${key}"]`);
async function mount(page,nodes){
  await page.setContent('<iframe title="brushes" sandbox="allow-scripts" style="width:900px;height:700px"></iframe>');
  await page.evaluate(({html,nodes})=>{
    const frame=document.querySelector('iframe');let revision=1;
    const marker=()=>({format:'xamlg.intelligent-ui/1',id:'styles',sessionId:'b'.repeat(32),revision,stateRevision:0});
    const send=message=>frame.contentWindow.postMessage(message,'*');
    window.publishStyles=roots=>{nodes=roots;revision++;send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});};
    addEventListener('message',event=>{
      if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;
      const m=event.data,reply=result=>send({jsonrpc:'2.0',id:m.id,result});
      if(m.method==='ui/initialize')reply({protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{}},hostContext:{theme:'light'}});
      else if(m.method==='ui/notifications/initialized')send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});
      else if(m.method==='tools/call')reply({structuredContent:{...marker(),roots:nodes,sequence:revision,isFinal:true,state:{},data:{},actions:[],diagnostics:[],fallbackMarkdown:'Styled fallback'},content:[]});
      else if(m.id!==undefined)reply({});
    });frame.srcdoc=html;
  },{html,nodes});
  const app=page.frameLocator('iframe');await expect(app.getByRole('status')).toContainText('revision 1');return app;
}
const linear={kind:'linear',startPoint:'0%,0%',endPoint:'100%,0%',opacity:.5,spreadMethod:'Reflect',stops:[{color:'Red',offset:0},{color:'#800000FF',offset:1}]};
const radial={kind:'radial',center:'50%,50%',gradientOrigin:'25%,25%',radiusX:'50%',radiusY:'25%',stops:[{color:'White',offset:0},{color:'Blue',offset:1}]};
test('structured brushes render through local SVG paint servers and bounded CSS without asset grants',async({page})=>{
  const app=await mount(page,[node('/root','StackPanel',{},[
    node('/path','Path',{Data:'M0,0 L80,0 L80,50 Z',Width:100,Height:60,Fill:linear,Stroke:radial}),
    node('/background','Border',{Background:linear,Width:100,Height:30}),node('/styled','Ellipse',{Width:40,Height:40})
  ],[rule('Ellipse',{Fill:radial})])]);
  await expect(app.getByRole('alert')).toBeEmpty();await expect(app.locator('#ui-brush-bank linearGradient')).toHaveCount(1);await expect(app.locator('#ui-brush-bank radialGradient')).toHaveCount(1);
  await expect(app.locator('linearGradient')).toHaveAttribute('spreadMethod','reflect');await expect(app.locator('linearGradient stop').first()).toHaveAttribute('stop-opacity','0.5');
  await expect(byKey(app,'/path').locator('path')).toHaveCSS('fill',/url\(/);await expect(byKey(app,'/styled').locator('ellipse')).toHaveCSS('fill',/url\(/);
  await expect(byKey(app,'/background')).toHaveCSS('background-image',/gradient\(/);
  await expect(app.locator('meta[http-equiv="Content-Security-Policy"]')).toHaveAttribute('content',/img-src 'none'/);
});
test('brush replacement deduplicates paint servers and clears them on retirement',async({page})=>{
  const path=node('/path','Path',{Data:'M0,0 L80,40',Stroke:linear});const app=await mount(page,[path]);
  await byKey(app,'/path').evaluate(e=>e.savedIdentity=true);
  path.properties.Stroke=radial;await page.evaluate(nodes=>window.publishStyles(nodes),[path]);await expect(app.getByRole('status')).toContainText('revision 2');
  await expect(app.locator('linearGradient')).toHaveCount(0);await expect(app.locator('radialGradient')).toHaveCount(1);expect(await byKey(app,'/path').evaluate(e=>e.savedIdentity)).toBe(true);
  path.properties.Stroke=null;await page.evaluate(nodes=>window.publishStyles(nodes),[path]);await expect(app.getByRole('status')).toContainText('revision 3');
  await expect(app.locator('#ui-brush-bank')).toHaveCount(0);await expect(byKey(app,'/path').locator('path')).toHaveCSS('stroke','none');
});
for(const [index,bad] of [{kind:'image',uri:'https://example.com'},{...linear,opacity:2},{...linear,stops:[{color:'Red',offset:2}]},{...radial,radiusX:'NaN'},{...radial,radiusX:'1e-320'},{...linear,stops:Array(65).fill({color:'Red',offset:0})}].entries()){
  test('invalid brush '+index+' preserves the committed view and paint bank: '+JSON.stringify(bad).slice(0,100),async({page})=>{
    const path=node('/path','Path',{Data:'M0,0 L80,40',Stroke:linear});const app=await mount(page,[path]);
    const before=await app.locator('#ui-brush-bank').innerHTML();path.properties.Stroke=bad;
    await page.evaluate(nodes=>window.publishStyles(nodes),[path]);await expect(app.getByRole('alert')).not.toBeEmpty();await expect(app.getByRole('status')).toContainText('revision 1');
    expect(await app.locator('#ui-brush-bank').innerHTML()).toBe(before);
  });
}

test('absolute gradient radii accept trailing decimal points consistently with native validation',async({page})=>{
  const app=await mount(page,[node('/ellipse','Ellipse',{Fill:{...radial,radiusX:'50.',radiusY:'25.'}})]);
  await expect(app.getByRole('alert')).toBeEmpty();await expect(app.locator('radialGradient')).toHaveAttribute('r','50');
});
