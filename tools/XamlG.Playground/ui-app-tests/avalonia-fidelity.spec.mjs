import {test,expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';
const html=loadUiResource();
const node=(key,type,properties={},children=[])=>({key,type,properties,children});
async function mount(page,nodes){
  await page.setContent('<iframe title="fidelity" sandbox="allow-scripts" style="width:900px;height:700px"></iframe>');
  await page.evaluate(({html,nodes})=>{
    const frame=document.querySelector('iframe');let revision=1;
    const marker=()=>({format:'xamlg.intelligent-ui/1',id:'fidelity',sessionId:'b'.repeat(32),revision,stateRevision:0});
    const send=message=>frame.contentWindow.postMessage(message,'*');
    window.publishFidelity=roots=>{nodes=roots;revision++;send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});};
    window.fidelityContext=theme=>send({jsonrpc:'2.0',method:'ui/notifications/host-context-changed',params:{theme}});
    addEventListener('message',event=>{
      if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;
      const m=event.data,reply=result=>send({jsonrpc:'2.0',id:m.id,result});
      if(m.method==='ui/initialize')reply({protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{}},hostContext:{theme:'light'}});
      else if(m.method==='ui/notifications/initialized')send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});
      else if(m.method==='tools/call')reply({structuredContent:{...marker(),roots:nodes,sequence:revision,isFinal:true,state:{},data:{},actions:[],diagnostics:[],fallbackMarkdown:'Drawing fallback'},content:[]});
      else if(m.id!==undefined)reply({});
    });
    frame.srcdoc=html;
  },{html,nodes});
  const app=page.frameLocator('iframe');await expect(app.getByRole('status')).toContainText('revision 1');return app;
}
const byKey=(app,key)=>app.locator(`[data-ui-key="${key}"]`);

test('geometry typography transforms and pen-relative dashes survive portable projection',async({page})=>{
  const app=await mount(page,[
    node('/path','Path',{Data:'F1 M0,0 L40,0 40,40 Z',Width:120,Height:80,Stroke:'Blue',StrokeThickness:2,StrokeDashArray:'2,3',StrokeDashOffset:1,StrokeLineCap:'Round',StrokeJoin:'Bevel',RenderTransform:'matrix(1,0,0,1,12,-4)',RenderTransformOrigin:'25%,75%',Clip:'M0,0 L120,0 120,80 Z',Margin:'-2,3,4,5',ZIndex:7}),
    node('/polygon','Polygon',{Points:'0,0 40,0 20,40',Fill:'Red',FillRule:'NonZero'}),
    node('/polyline','Polyline',{Points:'0,0 40,0 20,40',Stroke:'Green'}),
    node('/label','Label',{Content:'Native vocabulary',FontFamily:'Arial',FontSize:128,FontStyle:'Italic',Foreground:'Blue',Padding:'2,4',FlowDirection:'RightToLeft'})
  ]);
  await expect(app.getByRole('alert')).toBeEmpty();
  const path=byKey(app,'/path');
  await expect(path.locator('path')).toHaveAttribute('d','M0,0 L40,0 40,40 Z');
  await expect(path.locator('path')).toHaveAttribute('fill-rule','nonzero');
  await expect(path.locator('path')).toHaveAttribute('stroke-dasharray','4 6');
  await expect(path.locator('path')).toHaveAttribute('stroke-dashoffset','2');
  await expect(path.locator('path')).toHaveAttribute('stroke-linecap','round');
  await expect(path.locator('path')).toHaveAttribute('stroke-linejoin','bevel');
  await expect(path).toHaveCSS('transform','matrix(1, 0, 0, 1, 12, -4)');
  await expect(path).toHaveCSS('margin-left','-2px');await expect(path).toHaveCSS('z-index','7');
  await expect(byKey(app,'/polygon').locator('polygon')).toHaveAttribute('points','0 0 40 0 20 40');
  await expect(byKey(app,'/polyline').locator('polyline')).toHaveAttribute('points','0 0 40 0 20 40');
  await expect(byKey(app,'/label')).toHaveCSS('font-size','128px');await expect(byKey(app,'/label')).toHaveCSS('font-style','italic');
  await expect(byKey(app,'/label')).toHaveCSS('direction','rtl');
});

test('removing visual properties resets styles and retains keyed SVG identity',async({page})=>{
  const app=await mount(page,[node('/p','Path',{Data:'M0,0 L10,10',RenderTransform:'matrix(2,0,0,2,0,0)',StrokeDashArray:'2,3',ZIndex:5})]);
  await byKey(app,'/p').evaluate(element=>{element.retainedForTest=true;});
  await page.evaluate(nodes=>window.publishFidelity(nodes),[node('/p','Path',{Data:'M0,0 L20,20'})]);
  await expect(app.getByRole('status')).toContainText('revision 2');
  expect(await byKey(app,'/p').evaluate(element=>element.retainedForTest)).toBe(true);
  await expect(byKey(app,'/p')).toHaveCSS('transform','none');await expect(byKey(app,'/p')).toHaveCSS('z-index','auto');
  await expect(byKey(app,'/p').locator('path')).toHaveAttribute('stroke-dasharray','none');
});

for(const properties of [{Data:'M0'},{Data:'M0,0 A1,2 0 2 1 3,4'},{RenderTransform:'matrix(1,0,0,1,0)'},{StrokeDashArray:'0,0'},{Clip:'https://example.com/image.svg'}]){
  test('rejects invalid geometry before modifying the committed DOM: '+JSON.stringify(properties),async({page})=>{
    const app=await mount(page,[node('/p','Path',{Data:'M0,0 L10,10'})]);
    await page.evaluate(nodes=>window.publishFidelity(nodes),[node('/p','Path',{Data:'M0,0 L20,20',...properties})]);
    await expect(app.getByRole('alert')).not.toBeEmpty();
    await expect(byKey(app,'/p').locator('path')).toHaveAttribute('d','M0,0 L10,10');
    await expect(app.getByRole('status')).toContainText('revision 1');
  });
}

test('layout transform contributes transformed content dimensions and releases removed content',async({page})=>{
  const app=await mount(page,[node('/layout','LayoutTransformControl',{LayoutTransform:'matrix(0,1,-1,0,0,0)'},[node('/child','Border',{Width:60,Height:20})])]);
  await expect(byKey(app,'/layout')).toHaveCSS('width','20px');await expect(byKey(app,'/layout')).toHaveCSS('height','60px');
  await page.evaluate(nodes=>window.publishFidelity(nodes),[node('/label','Label',{Content:'Replaced'})]);
  await expect(byKey(app,'/layout')).toHaveCount(0);await expect(byKey(app,'/child')).toHaveCount(0);
  await expect(app.getByRole('alert')).toBeEmpty();
});

test('TextBox AcceptsTab edits the draft instead of moving focus',async({page})=>{
  const app=await mount(page,[node('/editor','TextBox',{Text:'a',AcceptsTab:true,TextWrapping:'Wrap',MaxLength:4})]);
  const editor=byKey(app,'/editor');await editor.fill('ab');await editor.press('End');await editor.press('Tab');
  await expect(editor).toHaveValue('ab\t');await expect(editor).toBeFocused();
  await expect(editor).toHaveCSS('white-space','pre-wrap');
});

test('host origin remains pinned after initialization and malformed host messages are ignored',async({page})=>{
  const errors=[];page.on('pageerror',error=>errors.push(error.message));
  const app=await mount(page,[node('/label','Label',{Content:'Origin pinned'})]);
  const frame=page.frames().find(frame=>frame.parentFrame());
  await frame.evaluate(()=>{
    dispatchEvent(new MessageEvent('message',{source:parent,origin:'https://untrusted.example',data:{jsonrpc:'2.0',method:'ui/notifications/host-context-changed',params:{theme:'dark'}}}));
    const cyclic={jsonrpc:'2.0'};cyclic.self=cyclic;
    dispatchEvent(new MessageEvent('message',{source:parent,origin:'null',data:cyclic}));
  });
  await expect(app.locator('html')).toHaveCSS('color-scheme','light');
  await page.evaluate(()=>window.fidelityContext('dark'));
  await expect(app.locator('html')).toHaveCSS('color-scheme','dark');expect(errors).toEqual([]);
});
