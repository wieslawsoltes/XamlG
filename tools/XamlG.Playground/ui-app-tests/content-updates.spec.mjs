import {test,expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';
const resource=loadUiResource();
test('literal content retires when a keyed container receives controls or empty content',async({page})=>{
 await page.setContent('<iframe title="content" sandbox="allow-scripts" style="width:900px;height:500px"></iframe>');
 await page.evaluate(html=>{
  const frame=document.querySelector('iframe');let revision=1,roots=[{key:'/content',type:'ContentControl',properties:{Content:'Previous literal'},children:[]}];
  const marker=()=>({format:'xamlg.intelligent-ui/1',id:'content',sessionId:'a'.repeat(32),revision,stateRevision:0});
  const send=m=>frame.contentWindow.postMessage(m,'*'),announce=()=>send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});
  window.updateContent=next=>{roots=next;revision++;announce();};
  addEventListener('message',event=>{
   if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;const m=event.data;
   if(m.method==='ui/initialize')send({jsonrpc:'2.0',id:m.id,result:{protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{}},hostContext:{}}});
   else if(m.method==='ui/notifications/initialized')announce();
   else if(m.method==='tools/call')send({jsonrpc:'2.0',id:m.id,result:{content:[],structuredContent:{...marker(),roots,sequence:revision,isFinal:true,state:{},data:{},actions:[],diagnostics:[],fallbackMarkdown:'Current content'}}});
  });frame.srcdoc=html;
 },resource);
 const app=page.frameLocator('iframe');await expect(app.locator('#surface')).toHaveText('Previous literal');
 await page.evaluate(()=>window.updateContent([{key:'/content',type:'ContentControl',properties:{},children:[{key:'/child',type:'TextBlock',properties:{Text:'New control'},children:[]}]}]));
 await expect(app.getByRole('status')).toContainText('revision 2');await expect(app.locator('#surface')).toHaveText('New control');
 await page.evaluate(()=>window.updateContent([{key:'/content',type:'ContentControl',properties:{},children:[]} ]));
 await expect(app.getByRole('status')).toContainText('revision 3');await expect(app.locator('#surface')).toBeEmpty();
});
