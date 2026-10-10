import {test,expect} from './studio-fixture.mjs';
import {openStudio,connectMcp} from './live-preview.mjs';
import {mountNativeUi} from './intelligent-ui-host.mjs';

// Executes actual Avalonia input handlers in the production Wasm guest. The fixture only
// relays the public host protocol; it does not synthesize renderer input events or state.
test('native input discovery, three-state activation and slider keys preserve typed state',async({page,request,context},info)=>{
  test.setTimeout(240000);await openStudio(page);const mcp=await connectMcp(page,request);let app;
  try{
    const catalog=await mcp.call('xamlg_ui_catalog',{});
    const example=await mcp.call('xamlg_ui_native_present',{...catalog.inputExample,id:'native-input-example'});
    app=await mountNativeUi(context,mcp,example);
    await expect(app.guest.locator('details')).toContainText('Mixed choice');
    await expect(app.guest.locator('details')).toContainText('Saved: 0');
    await app.host.screenshot({path:info.outputPath('native-input-authoring.png'),fullPage:true});
    const marker=await mcp.call('xamlg_ui_native_present',{
      id:'native-input-events',expectedRevision:0,sequence:1,initialState:{choice:null,amount:50},
      xaml:`<StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui">
        <CheckBox ui:Key="choice" ui:Bind="choice" IsThreeState="True" Content="Choice" Width="300" Height="40" HorizontalAlignment="Left"/>
        <Slider ui:Key="amount" ui:Bind="amount" SmallChange="2" LargeChange="15" IsDirectionReversed="True"
                Minimum="0" Maximum="100" Width="300" Height="40" HorizontalAlignment="Left"/>
        <TextBlock Text="{ui:Expr state.choice == null ? &quot;Mixed&quot; : state.choice ? &quot;Checked&quot; : &quot;Unchecked&quot;}"/>
        <TextBlock Text="{Binding state.amount, StringFormat='Amount: {0}'}"/>
      </StackPanel>`
    });
    await app.host.evaluate(value=>window.publishNative(value),marker);
    await expect(app.guest.locator('details')).not.toContainText('Saved: 0');
    await expect(app.guest.locator('details')).toContainText('Mixed');
    // The stock CheckBox template only hit-tests its glyph/content, not the empty
    // remainder of the authored width. The generic Button helper's x=80 hits that
    // empty area for this short label. Target the actual glyph, with real pointer input.
    const input=app.guest.locator('.avalonia-native-host');
    await expect(input).toBeVisible();
    await app.guest.locator('body').evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    await input.click({position:{x:10,y:20}});
    await expect(app.guest.locator('details')).toContainText('Unchecked');
    await app.host.keyboard.press('Space');await expect(app.guest.locator('details')).toContainText('Checked');
    await app.host.keyboard.press('Space');await expect(app.guest.locator('details')).toContainText('Mixed');
    expect((await mcp.call('xamlg_ui_read',{id:marker.id})).state.choice).toBeNull();
    // Native Tab traversal moves from the retained checkbox to the native slider.
    await app.host.keyboard.press('Tab');
    for(const [key,amount]of [['ArrowRight',48],['PageDown',63],['Home',0],['End',100]]){
      await app.host.keyboard.press(key);await expect(app.guest.locator('details')).toContainText('Amount: '+amount);
      expect((await mcp.call('xamlg_ui_read',{id:marker.id})).state.amount).toBe(amount);
    }
    const calls=await app.host.evaluate(()=>window.nativeRequests.filter(c=>c.name==='xamlg_ui_state'));
    expect(calls.slice(0,3).map(c=>c.arguments.value)).toEqual([false,true,null]);
    await expect(app.guest.getByRole('alert')).toHaveCount(0);expect(app.errors).toEqual([]);
  }finally{if(app)await app.host.close();await mcp.close();}
});
