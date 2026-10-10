import {test,expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';
const html=loadUiResource();
const node=(key,type,properties={},children=[],styles=[])=>({key,type,properties,children,styles});
const rule=(selector,properties)=>({selector,properties});
const byKey=(app,key)=>app.locator(`[data-ui-key="${key}"]`);
async function mount(page,nodes){
  await page.setContent('<iframe title="styles" sandbox="allow-scripts" style="width:900px;height:700px"></iframe>');
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
test('scoped classes names hover and explicit local values preserve precedence',async({page})=>{
  const outer=node('/root','StackPanel',{},[
    node('/styled','TextBlock',{Text:'Styled',Classes:'accent'}),
    node('/local','TextBlock',{Text:'Local',Classes:'accent',Foreground:'Red'}),
    node('/scope','StackPanel',{},[node('/nested','TextBlock',{Text:'Nested',Classes:'accent'})],[rule('TextBlock.accent',{Foreground:'Green'})]),
    {...node('/hover','Button',{Content:'Hover',Classes:'primary',Name:'named'}),actionId:'click'}
  ],[rule('TextBlock.accent',{Foreground:'Blue',FontSize:23}),rule('Button#named',{Padding:10}),rule('Button.primary',{Background:'Blue'}),rule('Button.primary:pointerover',{Background:'Red'})]);
  const app=await mount(page,[outer]);
  await expect(byKey(app,'/styled')).toHaveCSS('color','rgb(0, 0, 255)');await expect(byKey(app,'/styled')).toHaveCSS('font-size','23px');
  await expect(byKey(app,'/local')).toHaveCSS('color','rgb(255, 0, 0)');await expect(byKey(app,'/nested')).toHaveCSS('color','rgb(0, 128, 0)');
  await expect(byKey(app,'/hover')).toHaveCSS('padding-top','10px');await expect(byKey(app,'/hover')).toHaveCSS('background-color','rgb(0, 0, 255)');
  await byKey(app,'/hover').hover();await expect(byKey(app,'/hover')).toHaveCSS('background-color','rgb(255, 0, 0)');
  await expect(app.getByRole('alert')).toBeEmpty();
});
test('removing styles and local SVG values restores lower precedence without recreation',async({page})=>{
  const path=node('/path','Path',{Data:'M0,0 L20,20',Stroke:'Green',StrokeThickness:4,StrokeDashArray:'2,3'});
  const scope=node('/root','StackPanel',{},[path],[rule('Path',{Stroke:'Blue',StrokeThickness:2})]);
  const app=await mount(page,[scope]);const shape=byKey(app,'/path').locator('path');
  await expect(shape).toHaveCSS('stroke','rgb(0, 128, 0)');
  expect(await shape.evaluate(e=>getComputedStyle(e).strokeDasharray.replaceAll('calc(','').replaceAll(')',''))).toBe('8px, 12px');
  await shape.evaluate(e=>e.identityForTest=true);
  path.properties={Data:'M0,0 L30,30'};await page.evaluate(nodes=>window.publishStyles(nodes),[scope]);
  await expect(app.getByRole('status')).toContainText('revision 2');await expect(shape).toHaveCSS('stroke','rgb(0, 0, 255)');
  expect(await shape.evaluate(e=>e.identityForTest)).toBe(true);
  scope.styles=[];await page.evaluate(nodes=>window.publishStyles(nodes),[scope]);
  await expect(app.getByRole('status')).toContainText('revision 3');await expect(shape).toHaveCSS('stroke','none');
});
test('nested content style alignment yields to local values and retires cleanly',async({page})=>{
  const expander=node('/e','Expander',{Header:'Content',IsExpanded:true,Classes:'center'},[node('/text','TextBlock',{Text:'Child'})]);
  const scope=node('/root','StackPanel',{},[expander],[rule('Expander.center',{HorizontalContentAlignment:'Center'})]);
  const app=await mount(page,[scope]);const slot=byKey(app,'/e').locator('[data-ui-style-slot]');
  await expect(slot).toHaveCSS('justify-content','center');
  expander.properties.HorizontalContentAlignment='Right';await page.evaluate(nodes=>window.publishStyles(nodes),[scope]);
  await expect(app.getByRole('status')).toContainText('revision 2');await expect(slot).toHaveCSS('justify-content','flex-end');
  delete expander.properties.HorizontalContentAlignment;scope.styles=[];await page.evaluate(nodes=>window.publishStyles(nodes),[scope]);
  await expect(app.getByRole('status')).toContainText('revision 3');await expect(slot).toHaveCSS('display','block');
});
for(const bad of [rule('Button',{IsEnabled:false}),rule('TextBlock',{FontSize:-1}),rule('TextBlock;body',{Foreground:'Red'}),rule('Window',{Opacity:0}),rule('Slider',{Value:20}),rule('Button',{Background:'url(https://example.com/image)'})]){
  test('rejects invalid style before changing the committed DOM: '+JSON.stringify(bad),async({page})=>{
    const scope=node('/root','StackPanel',{},[node('/text','TextBlock',{Text:'Good'})],[rule('TextBlock',{Foreground:'Blue'})]);
    const app=await mount(page,[scope]);scope.styles=[bad];scope.children[0].properties.Text='Invalid';
    await page.evaluate(nodes=>window.publishStyles(nodes),[scope]);
    await expect(app.getByRole('alert')).not.toBeEmpty();await expect(app.getByRole('status')).toContainText('revision 1');
    await expect(byKey(app,'/text')).toHaveText('Good');await expect(byKey(app,'/text')).toHaveCSS('color','rgb(0, 0, 255)');
  });
}
