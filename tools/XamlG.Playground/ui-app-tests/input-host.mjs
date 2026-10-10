import {expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';

export const inputNode=(key,type,properties={},extra={})=>({key,type,properties,children:[],...extra});
// A strict revisioned parent fixture. This exercises the shipping resource, not a reimplementation.
export function inputHost({html,initial}){
  const frame=document.querySelector('iframe');let value={id:'input',sessionId:'c'.repeat(32),revision:1,stateRevision:0,
    sequence:1,isFinal:true,state:{},data:{},actions:[],diagnostics:[],fallbackMarkdown:'Input fallback',...initial};
  const send=message=>frame.contentWindow.postMessage(message,'*');
  const marker=()=>({format:'xamlg.intelligent-ui/1',id:value.id,sessionId:value.sessionId,revision:value.revision,stateRevision:value.stateRevision});
  const notify=()=>send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker()}});
  const flat=roots=>roots.flatMap(node=>[node,...flat(node.children)]);
  window.inputCalls=[];window.inputDelay=0;window.failInput=false;
  window.publishInput=next=>{value={...value,...next,revision:value.revision+1};notify();};
  window.readInput=()=>value;
  window.teardownInput=()=>send({jsonrpc:'2.0',id:'teardown',method:'ui/resource-teardown',params:{}});
  addEventListener('message',async event=>{
    if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;
    const m=event.data,reply=result=>send({jsonrpc:'2.0',id:m.id,result});
    if(m.method==='ui/initialize')reply({protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{},message:{}},hostContext:{theme:'light'}});
    else if(m.method==='ui/notifications/initialized')notify();
    else if(m.method==='tools/call'){
      const {name,arguments:a}=m.params;window.inputCalls.push({name,...a});
      if(window.inputDelay&&name!=='xamlg_ui_read')await new Promise(resolve=>setTimeout(resolve,window.inputDelay));
      if(name!=='xamlg_ui_read'&&(a.expectedRevision!==value.revision||a.expectedStateRevision!==value.stateRevision)){
        reply({isError:true,content:[{type:'text',text:'revision_conflict'}]});return;
      }
      if(name==='xamlg_ui_state'){
        if(window.failInput){window.failInput=false;reply({isError:true,content:[{type:'text',text:'Rejected input'}]});return;}
        const node=flat(value.roots).find(node=>node.stateKey===a.key);
        const property=['CheckBox','ToggleButton','ToggleSwitch','RadioButton'].includes(node.type)?'IsChecked':node.type==='TextBox'?'Text':node.type==='TabControl'?'SelectedIndex':'Value';
        value={...value,stateRevision:value.stateRevision+1,state:{...value.state,[a.key]:a.value}};
        node.properties={...node.properties,[property]:a.value};
      }else if(name==='xamlg_ui_state_action'){
        value={...value,stateRevision:value.stateRevision+1,state:{...value.state,count:(value.state.count??0)+1}};
      }else if(name==='xamlg_ui_action'){
        reply({structuredContent:{kind:'message',text:'Review before sending'},content:[]});return;
      }
      reply({structuredContent:value,content:[]});
    }else if(m.id!==undefined&&m.method)reply({});
  });
  frame.srcdoc=html;
}
export async function mountInput(page,initial){
  await page.setContent('<iframe title="Input surface" sandbox="allow-scripts" style="width:900px;height:700px"></iframe>');
  await page.evaluate(inputHost,{html:loadUiResource(),initial});
  const app=page.frameLocator('iframe');await expect(app.getByRole('status')).toContainText('revision 1');return app;
}
