import { test, expect } from './studio-fixture.mjs';
import { openStudio, connectMcp } from './live-preview.mjs';
import { mountNativeUi } from './intelligent-ui-host.mjs';

const flatten=roots=>roots.flatMap(node=>[node,...flatten(node.children)]);
test('published Avalonia/Wasm renders bound templates, scoped styles and gradients from agent discovery',async({page,request,context},info)=>{
  test.setTimeout(240000);await openStudio(page);const mcp=await connectMcp(page,request);let app;
  try{
    const catalog=await mcp.call('xamlg_ui_catalog',{});
    const marker=await mcp.call('xamlg_ui_native_present',{...catalog.authoringExample,id:'native-authoring'});
    app=await mountNativeUi(context,mcp,marker);
    await expect(app.guest.locator('details')).toContainText('Featured profile');
    await expect(app.guest.locator('details')).toContainText('Grace');
    let snapshot=await mcp.call('xamlg_ui_read',{id:marker.id});
    const featured=flatten(snapshot.roots).find(node=>node.type==='Button');
    const card=flatten(snapshot.roots).find(node=>node.type==='Border');
    expect(card.properties.Background.kind).toBe('linear');expect(card.properties.Background.stops).toHaveLength(2);
    snapshot=await mcp.call('xamlg_ui_state',{id:marker.id,expectedRevision:snapshot.revision,expectedStateRevision:snapshot.stateRevision,key:'minimum',value:90});
    await app.host.evaluate(id=>window.updateNative(id),marker.id);
    await expect(app.guest.getByRole('status')).toContainText('state '+snapshot.stateRevision);
    await expect(app.guest.locator('details')).not.toContainText('Grace');
    await expect(app.guest.locator('details')).toContainText('Ada');
    snapshot=await mcp.call('xamlg_ui_state_action',{id:marker.id,expectedRevision:snapshot.revision,expectedStateRevision:snapshot.stateRevision,nodeKey:featured.key});
    expect(snapshot.state.selected).toBe('featured');
    await app.host.evaluate(id=>window.updateNative(id),marker.id);
    await expect(app.guest.locator('details')).toContainText('Selected: featured');
    await expect(app.guest.getByRole('alert')).toHaveCount(0);expect(app.errors).toEqual([]);
    await app.host.screenshot({path:info.outputPath('native-authoring-gradients.png'),fullPage:true});
  }finally{if(app)await app.host.close();await mcp.close();}
});
