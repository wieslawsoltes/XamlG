import {test,expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';

test('removing content alignment restores nested slot styles and preserves keyed children',async({page})=>{
  await page.setContent('<iframe title="alignment" sandbox="allow-scripts"></iframe>');
  await page.evaluate(html=>{
    const frame=document.querySelector('iframe');let revision=1,aligned=true;
    const marker=()=>({format:'xamlg.intelligent-ui/1',id:'alignment',sessionId:'c'.repeat(32),revision,stateRevision:0});
    const send=message=>frame.contentWindow.postMessage(message,'*');
    window.removeAlignment=()=>{aligned=false;revision++;send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});};
    addEventListener('message',event=>{
      if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;
      const message=event.data,reply=result=>send({jsonrpc:'2.0',id:message.id,result});
      if(message.method==='ui/initialize')reply({protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{}},hostContext:{}});
      else if(message.method==='ui/notifications/initialized')send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});
      else if(message.method==='tools/call')reply({content:[],structuredContent:{
        ...marker(),sequence:revision,isFinal:true,state:{},data:{},actions:[],diagnostics:[],fallbackMarkdown:'Retained content',
        roots:[{key:'/parent',type:'Expander',properties:{Header:'Details',IsExpanded:true,...(aligned?{HorizontalContentAlignment:'Right',VerticalContentAlignment:'Bottom'}:{})},
          children:[{key:'/child',type:'Label',properties:{Content:'Retained child'},children:[]}]}]
      }});
    });
    frame.srcdoc=html;
  },loadUiResource());
  const app=page.frameLocator('iframe'),slot=app.locator('[data-ui-key="/parent"] > div');
  await expect(app.getByRole('status')).toContainText('revision 1');
  await expect(slot).toHaveCSS('display','flex');await expect(slot).toHaveCSS('justify-content','flex-end');
  await app.locator('[data-ui-key="/child"]').evaluate(element=>{element.retainedForAlignmentTest=true;});
  await page.evaluate(()=>window.removeAlignment());
  await expect(app.getByRole('status')).toContainText('revision 2');
  await expect(slot).toHaveCSS('display','block');
  expect(await slot.evaluate(element=>element.style.justifyContent)).toBe('');
  expect(await slot.evaluate(element=>element.style.alignItems)).toBe('');
  expect(await app.locator('[data-ui-key="/child"]').evaluate(element=>element.retainedForAlignmentTest)).toBe(true);
  await expect(app.getByRole('alert')).toBeEmpty();
});
