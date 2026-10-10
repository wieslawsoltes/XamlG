import {test,expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';
const html=loadUiResource();
const names='StackPanel Grid Border TextBlock SelectableTextBlock Button RepeatButton TextBox Slider CheckBox ToggleButton RadioButton ToggleSwitch ProgressBar Separator ScrollViewer Panel Canvas WrapPanel DockPanel UniformGrid Viewbox ContentControl UserControl Expander TabControl TabItem ItemsControl ListBox ListBoxItem ComboBox ComboBoxItem TreeView TreeViewItem NumericUpDown DatePicker CalendarDatePicker Calendar TimePicker Rectangle Ellipse Line Path Polyline Polygon LayoutTransformControl Label ContentPresenter ItemsPresenter'.split(' ');
async function mount(page,nodes){
  await page.setContent('<iframe title="catalog" sandbox="allow-scripts" style="width:900px;height:800px"></iframe>');
  await page.evaluate(({html,nodes})=>{
    const frame=document.querySelector('iframe'),marker={format:'xamlg.intelligent-ui/1',id:'catalog',revision:1,stateRevision:0,sessionId:'a'.repeat(32)};
    window.catalogCalls=[];let stateRevision=0;
    const snapshot=()=>({...marker,roots:nodes,sequence:1,isFinal:true,state:{},data:{},actions:[],diagnostics:[],fallbackMarkdown:'Expanded catalog'});
    const send=message=>frame.contentWindow.postMessage(message,'*');
    addEventListener('message',event=>{
      if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;const m=event.data;
      const reply=result=>send({jsonrpc:'2.0',id:m.id,result});
      if(m.method==='ui/initialize')reply({protocolVersion:'2026-01-26',hostCapabilities:{serverTools:{}},hostContext:{theme:'light'}});
      else if(m.method==='ui/notifications/initialized')send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker}});
      else if(m.method==='tools/call'){
        window.catalogCalls.push(m.params);
        if(m.params.name==='xamlg_ui_state'){
          const a=m.params.arguments;const input=nodes.find(node=>node.stateKey===a.key);
          input.properties[input.type==='ComboBox'?'SelectedIndex':input.type==='DatePicker'?'SelectedDate':'Value']=a.value;
          stateRevision++;
        }
        reply({structuredContent:{...snapshot(),stateRevision},content:[]});
      }else if(m.id!==undefined)reply({});
    });
    window.replaceCatalog=roots=>{nodes=roots;send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker}});};
    frame.srcdoc=html;
  },{html,nodes});
  const app=page.frameLocator('iframe');await expect(app.getByRole('status')).toContainText('revision 1');return app;
}
const node=(type,p={},children=[],extra={})=>({key:'/'+type,type,properties:p,children,...extra});
test('portable renderer accepts every declared default control without external code',async({page})=>{
  expect(names).toHaveLength(49);
  const app=await mount(page,names.map(name=>node(name,name==='NumericUpDown'?{Value:null}:name==='ComboBox'||name==='ListBox'||name==='ItemsControl'?{ItemsSource:['A','B']}:name==='Line'?{StartPoint:'0,0',EndPoint:'80,40',Stroke:'Blue'}:{})));
  await expect(app.getByRole('alert')).toBeEmpty();await expect(app.locator('#surface > .node')).toHaveCount(49);await expect(app.locator('svg')).toHaveCount(6);
});
test('portable selection, nullable numeric and date inputs send typed values',async({page})=>{
  const app=await mount(page,[node('ComboBox',{ItemsSource:['A','B'],SelectedIndex:0},[],{stateKey:'choice'}),node('NumericUpDown',{Value:2,Minimum:0,Maximum:10},[],{stateKey:'amount'}),node('DatePicker',{SelectedDate:null},[],{stateKey:'date'})]);
  await app.getByLabel('choice',{exact:true}).selectOption({index:1});
  await expect.poll(()=>page.evaluate(()=>window.catalogCalls.filter(c=>c.name==='xamlg_ui_state').map(c=>c.arguments.value))).toEqual([1]);
  await app.getByLabel('amount',{exact:true}).fill('');await app.getByLabel('amount',{exact:true}).press('Tab');
  await expect.poll(()=>page.evaluate(()=>window.catalogCalls.filter(c=>c.name==='xamlg_ui_state').map(c=>c.arguments.value))).toEqual([1,null]);
  await app.getByLabel('date',{exact:true}).fill('2026-10-09');await app.getByLabel('date',{exact:true}).press('Tab');
  await expect.poll(()=>page.evaluate(()=>window.catalogCalls.filter(c=>c.name==='xamlg_ui_state').map(c=>c.arguments.value))).toEqual([1,null,'2026-10-09T00:00:00Z']);
});
