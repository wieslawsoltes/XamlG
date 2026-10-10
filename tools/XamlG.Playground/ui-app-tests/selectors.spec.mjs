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
test('logical children ignore presenter wrappers and positional selectors follow keyed order',async({page})=>{
  const group=node('/group','Expander',{IsExpanded:true,Header:'Group'},[node('/direct','TextBlock',{Text:'Direct'})]);
  const scope=node('/root','StackPanel',{Classes:'scope'},[
    node('/first','TextBlock',{Text:'First',Classes:'muted'}),node('/second','TextBlock',{Text:'Second'}),group,
    node('/repeat','RepeatButton',{Content:'Repeat'})
  ],[rule('StackPanel.scope > TextBlock:nth-child(2)',{FontSize:30}),rule('StackPanel.scope TextBlock:not(.muted)',{Foreground:'Blue'}),
    rule('Expander > TextBlock:nth-last-child(1)',{FontSize:28}),rule(':is(Button)',{FontSize:23})]);
  const app=await mount(page,[scope]);
  await expect(byKey(app,'/second')).toHaveCSS('font-size','30px');
  await expect(byKey(app,'/direct')).toHaveCSS('font-size','28px');
  await expect(byKey(app,'/repeat')).toHaveCSS('font-size','23px');
  await expect(byKey(app,'/first')).not.toHaveCSS('color','rgb(0, 0, 255)');
  await byKey(app,'/first').evaluate(e=>e.retainedMarker=true);
  [scope.children[0],scope.children[1]]=[scope.children[1],scope.children[0]];
  await page.evaluate(nodes=>window.publishStyles(nodes),[scope]);
  await expect(app.getByRole('status')).toContainText('revision 2');
  await expect(byKey(app,'/first')).toHaveCSS('font-size','30px');
  await expect(byKey(app,'/second')).not.toHaveCSS('font-size','30px');
  expect(await byKey(app,'/first').evaluate(e=>e.retainedMarker)).toBe(true);
});
test('ancestor pseudo classes and nested negation remain live without a new snapshot',async({page})=>{
  const scope=node('/root','Border',{},[node('/text','TextBlock',{Text:'Hover parent'})],[
    rule('Border:not(:pointerover) > TextBlock',{Foreground:'Blue'}),
    rule('Border:pointerover > TextBlock:not(:not(.active))',{Foreground:'Red'})
  ]);scope.children[0].properties.Classes='active';
  const app=await mount(page,[scope]);await page.mouse.move(0,0);
  await expect(byKey(app,'/text')).toHaveCSS('color','rgb(0, 0, 255)');
  await byKey(app,'/root').hover();await expect(byKey(app,'/text')).toHaveCSS('color','rgb(255, 0, 0)');
  await expect(app.getByRole('status')).toContainText('revision 1');
});
test('native template boundaries never select ordinary response descendants',async({page})=>{
  const scope=node('/root','StackPanel',{},[node('/button','Button',{},[node('/border','Border',{},[node('/text','TextBlock',{Text:'Response content'})])])],[
    rule('Button /template/ Border',{Background:'Red'}),rule('Button /template/ ContentPresenter',{Opacity:0})
  ]);
  const app=await mount(page,[scope]);await expect(app.getByRole('alert')).toBeEmpty();
  await expect(byKey(app,'/border')).not.toHaveCSS('background-color','rgb(255, 0, 0)');
  await expect(byKey(app,'/text')).toBeVisible();
});
for(const selector of ['StackPanel >','TextBlock:not()','Button:nth-child(2n1)','Window TextBlock','StackPanel TextBlock:not(Window)','TextBlock:nth-child(999999999999n)','TextBlock:not(.a,.b)']){
  test('rejects invalid logical selector without replacing committed content: '+selector,async({page})=>{
    const scope=node('/root','StackPanel',{},[node('/text','TextBlock',{Text:'Good'})],[rule('TextBlock',{Foreground:'Blue'})]);
    const app=await mount(page,[scope]);scope.styles=[rule(selector,{Opacity:.5})];scope.children[0].properties.Text='Invalid';
    await page.evaluate(nodes=>window.publishStyles(nodes),[scope]);
    await expect(app.getByRole('alert')).not.toBeEmpty();await expect(app.getByRole('status')).toContainText('revision 1');
    await expect(byKey(app,'/text')).toHaveText('Good');await expect(byKey(app,'/text')).toHaveCSS('color','rgb(0, 0, 255)');
  });
}
