import { test, expect } from './studio-fixture.mjs';
import { openStudio, connectMcp } from './live-preview.mjs';
const panelFor = page => page.getByRole('complementary',{name:'Intelligent UI workspace',exact:true});
const canvas = frame => frame.locator('canvas.avalonia-canvas');
const declaration = id => ({id,expectedRevision:0,sequence:1,
 xaml:'<StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" Spacing="12"><Slider Width="300" Height="40" HorizontalAlignment="Left" ui:Key="seats" ui:Bind="seats" Minimum="1" Maximum="50" IsSnapToTickEnabled="True" TickFrequency="1"/><TextBlock Text="{ui:Expr &quot;$&quot; + state.seats * data.unitPrice}"/></StackPanel>',initialState:{seats:8},data:{unitPrice:29},fallbackMarkdown:'Native pricing'});

test.afterEach(async({page},info)=>{
 if(info.status===info.expectedStatus)return;
 for(const frame of page.frames())console.log('UI_FAILURE',JSON.stringify(await frame.evaluate(()=>({url:location.href,text:document.body.innerText.slice(-8000),canvases:[...document.querySelectorAll('canvas')].map(e=>({class:e.className,rect:e.getBoundingClientRect().toJSON()}))})).catch(error=>({error:error.message}))));
});

test('private UI workspace saves, restores after reload and forgets without reviving permissions',async({page})=>{
 await openStudio(page,false);await page.getByTestId('agent-workbench').click();
 const pane=page.getByRole('region',{name:'Coding agent workbench'});
 await pane.locator('.intelligent-ui-demo > summary').click();await pane.getByRole('button',{name:'Try intelligent UI',exact:true}).click();
 const card=pane.getByRole('article',{name:'Intelligent UI response'});
 await card.locator('details.ui-inspect > summary').click();await card.getByLabel('UI state seats',{exact:true}).fill('17');await card.getByLabel('UI state seats',{exact:true}).press('Tab');
 await expect(card.locator('header')).toContainText('state 1');
 let panel=panelFor(page);await panel.getByRole('button',{name:'Intelligent UI workspace',exact:true}).click();
 await expect(panel.getByRole('button',{name:'Save UI workspace',exact:true})).toBeDisabled();
 await panel.getByLabel('Allow local UI workspace storage').check();await panel.getByRole('button',{name:'Save UI workspace',exact:true}).click();
 await expect(panel.getByRole('status').first()).toContainText('UI workspace saved.');const identity=await panel.locator('p > code').first().innerText();
 await page.reload();await expect(page.locator('.studio')).toHaveAttribute('data-ready','true');
 panel=panelFor(page);await panel.getByRole('button',{name:'Intelligent UI workspace',exact:true}).click();await expect(panel.locator('p > code').first()).toHaveText(identity);
 await expect(panel.getByLabel('Allow local UI workspace storage')).not.toBeChecked();await panel.getByLabel('Allow local UI workspace storage').check();
 await panel.getByRole('button',{name:'Restore saved UI',exact:true}).click();await expect(panel.getByRole('status').first()).toContainText('Saved UI restored and revalidated.');
 await panel.locator('details > summary').filter({hasText:/^demo-/}).click();await expect(panel).toContainText('$493');
 await page.getByTestId('agent-access').click();await expect(page.getByLabel('Enable access to this live project')).not.toBeChecked();
 await panel.getByRole('button',{name:'Forget saved UI',exact:true}).click();await expect(panel.getByRole('status').first()).toContainText('Saved UI forgotten.');await expect(panel).toContainText('1 live cards');
});

test('full C# proposal requires owner review and runs only in a disposable opaque-origin frame',async({page,request})=>{
 test.setTimeout(240000);await openStudio(page);const mcp=await connectMcp(page,request);
 try{
  const panel=panelFor(page);await panel.getByRole('button',{name:'Intelligent UI workspace',exact:true}).click();await panel.locator('.ui-full-csharp > summary').click();
  const proposal=await mcp.call('xamlg_ui_csharp_propose',{...declaration('full-query'),xaml:'<StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui"><Slider Width="300" Height="40" HorizontalAlignment="Left" ui:Bind="n" Minimum="1" Maximum="20" IsSnapToTickEnabled="True" TickFrequency="1"/><TextBlock Text="{ui:Expr &quot;Sum: &quot; + Enumerable.Range(1, checked((int)state.GetProperty(&quot;n&quot;).GetDecimal())).Sum()}"/></StackPanel>',initialState:{n:3},data:{}});
  expect(proposal.status).toBe('pending');await expect(page.locator('iframe[title="Approved full C# preview"]')).toHaveCount(0);
  await panel.getByRole('button',{name:new RegExp('Review proposal '+proposal.id.slice(0,8))}).click();await panel.getByRole('button',{name:'Review full C#',exact:true}).click();
  await expect(panel.getByRole('region',{name:'Review full C# execution'})).toContainText('Enumerable.Range');await expect(page.locator('iframe[title="Approved full C# preview"]')).toHaveCount(0);
  await panel.getByRole('button',{name:'Approve and run full C#',exact:true}).click();const frame=page.frameLocator('iframe[title="Approved full C# preview"]');
  await expect.poll(async()=>{const state=await mcp.call('xamlg_ui_csharp_status',{id:proposal.id});if(state.status==='failed')throw new Error(state.error);return state.status;}).toBe('completed');
  await expect(frame.getByRole('status')).toContainText('revision 1');await expect(canvas(frame)).toBeVisible();await frame.locator('details > summary').click();await expect(frame.locator('details')).toContainText('Sum: 6');
  expect(await frame.locator('body').evaluate(()=>{let parentBlocked=false,storageBlocked=false;try{void parent.document.body;}catch{parentBlocked=true;}try{void localStorage.length;}catch{storageBlocked=true;}return{parentBlocked,storageBlocked};})).toEqual({parentBlocked:true,storageBlocked:true});
  await canvas(frame).click({position:{x:240,y:20}});await expect(frame.getByRole('status')).not.toContainText('state 0');await expect(frame.locator('details')).not.toContainText('Sum: 6');
  await expect(frame.getByRole('alert')).toHaveCount(0);
  await panel.getByRole('button',{name:'Reset execution frame',exact:true}).click();await expect(page.locator('iframe[title="Approved full C# preview"]')).toHaveCount(0);
 }finally{await mcp.close();}
});

test('actual native MCP resource renders Avalonia and round-trips live input through the companion',async({page,request,context})=>{
 test.setTimeout(240000);await openStudio(page);const mcp=await connectMcp(page,request);let host;
 try{
  const local=await page.evaluate(()=>window.xamlgAutomation.catalog());
  console.log('BROWSER_UI_METADATA',JSON.stringify(local.resources.filter(r=>r.uri.startsWith('ui:'))));
  expect(local.resources.find(r=>r.uri==='ui://xamlg/intelligent-ui/native-v1')).toMatchObject({mimeType:'text/html;profile=mcp-app'});
  const listed=await mcp.rpc('resources/list');console.log('MCP_UI_METADATA',JSON.stringify(listed.resources.filter(r=>r.uri.startsWith('ui:'))));
  const marker=await mcp.call('xamlg_ui_native_present',declaration('native-pricing'));
  const resources=await mcp.rpc('resources/read',{uri:'ui://xamlg/intelligent-ui/native-v1'});
  console.log('NATIVE_RESOURCE_CONTENTS',JSON.stringify(resources.contents.map(item=>({...item,text:item.text?.slice(0,300)}))));
  expect(resources.contents).toHaveLength(1);expect(resources.contents[0]).toMatchObject({mimeType:'text/html;profile=mcp-app'});expect(resources.contents[0]._meta.ui.csp.frameDomains).toHaveLength(1);
  const html=resources.contents[0].text;expect(html).toContain('ui-native.html');
  host=await context.newPage();const errors=[];host.on('pageerror',error=>errors.push(error.message));await host.exposeFunction('mcpRequest',(method,params)=>mcp.rpc(method,params));
  await host.setContent('<!doctype html><iframe title="MCP native resource" sandbox="allow-scripts" style="width:900px;height:900px;border:0"></iframe>');
  await host.evaluate(({html,marker})=>{
   const frame=document.querySelector('iframe');window.nativeCalls=[];
   addEventListener('message',async event=>{
    if(event.source!==frame.contentWindow||event.data?.jsonrpc!=='2.0')return;const m=event.data,send=message=>frame.contentWindow.postMessage(message,'*');
    if(m.method==='ui/initialize')send({jsonrpc:'2.0',id:m.id,result:{protocolVersion:'2026-01-26',hostInfo:{name:'native-acceptance',version:'1'},hostCapabilities:{serverTools:{},updateModelContext:{}},hostContext:{theme:'light'}}});
    else if(m.method==='ui/notifications/initialized')send({jsonrpc:'2.0',method:'ui/notifications/tool-result',params:{structuredContent:marker,content:[]}});
    else if(m.method==='tools/call'){window.nativeCalls.push(m.params);try{send({jsonrpc:'2.0',id:m.id,result:await window.mcpRequest('tools/call',m.params)});}catch(error){send({jsonrpc:'2.0',id:m.id,error:{code:-32603,message:error.message}});}}
    else if(m.id!==undefined)send({jsonrpc:'2.0',id:m.id,result:{}});
   });frame.srcdoc=html;
  },{html,marker});
  const guest=host.frameLocator('iframe[title="MCP native resource"]').frameLocator('iframe[title="Native Avalonia UI"]');
  await expect(guest.getByRole('status')).toContainText('revision '+marker.revision);await expect(canvas(guest)).toBeVisible();await guest.locator('details > summary').click();await expect(guest.locator('details')).toContainText('$232');
  await canvas(guest).click({position:{x:240,y:20}});await expect.poll(async()=>(await mcp.call('xamlg_ui_read',{id:marker.id})).stateRevision).toBeGreaterThan(0);
  const snapshot=await mcp.call('xamlg_ui_read',{id:marker.id});await expect(guest.locator('details')).toContainText('$'+snapshot.state.seats*29);
  expect(await host.evaluate(()=>window.nativeCalls.some(call=>call.name==='xamlg_ui_state'))).toBe(true);expect(errors).toEqual([]);
 }catch(error){if(host)console.log('NATIVE_GUEST_FAILURE',JSON.stringify(await Promise.all(host.frames().map(frame=>frame.evaluate(()=>({url:location.href,text:document.body.innerText.slice(0,6000)})).catch(failure=>({error:failure.message}))))));throw error;}
 finally{if(host)await host.close();await mcp.close();}
});
