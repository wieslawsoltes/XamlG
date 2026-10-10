import {test,expect} from '@playwright/test';
import {inputNode as n,mountInput} from './input-host.mjs';
const key=(app,id)=>app.locator(`[data-ui-key="${id}"]`);
const values=page=>page.evaluate(()=>window.inputCalls.filter(c=>c.name==='xamlg_ui_state').map(c=>c.value));

test('nullable three-state toggles preserve mixed state and send one typed change per activation',async({page})=>{
  const app=await mountInput(page,{roots:[n('/toggle','CheckBox',{Content:'Three states',IsThreeState:true,IsChecked:null},{stateKey:'checked'})],state:{checked:null}});
  const toggle=app.getByRole('checkbox');await expect(toggle).toHaveAttribute('aria-checked','mixed');
  for(const value of [false,true,null]){
    await toggle.click();await expect.poll(()=>page.evaluate(()=>window.readInput().state.checked)).toEqual(value);
    await expect(toggle).toHaveAttribute('aria-checked',value===null?'mixed':String(value));
  }
  expect(await values(page)).toEqual([false,true,null]);await expect(app.getByRole('alert')).toBeEmpty();
});
test('slider keys use authored increments, direction and absolute endpoints',async({page})=>{
  const app=await mountInput(page,{roots:[n('/range','Slider',{Minimum:0,Maximum:100,Value:50,SmallChange:2,LargeChange:15,IsDirectionReversed:true},{stateKey:'amount'})],state:{amount:50}});
  const range=app.getByRole('slider');
  for(const [button,value]of [['ArrowRight',48],['PageDown',63],['Home',0],['End',100]]){
    await range.press(button);await expect.poll(()=>page.evaluate(()=>window.readInput().state.amount)).toBe(value);
  }
  expect(await values(page)).toEqual([48,63,0,100]);
});
test('caret and reverse selection survive unrelated echoes and reset with removed declarations',async({page})=>{
  const text=n('/text','TextBox',{Text:'abcdef',SelectionStart:5,SelectionEnd:2,TabIndex:3,IsTabStop:false,'AutomationProperties.AutomationId':'editor','AutomationProperties.HelpText':'Help'},{stateKey:'text'});
  const range=n('/range','Slider',{Value:1},{stateKey:'amount'});
  const app=await mountInput(page,{roots:[text,range],state:{text:'abcdef',amount:1}}),edit=key(app,'/text');
  expect(await edit.evaluate(e=>[e.selectionStart,e.selectionEnd,e.selectionDirection])).toEqual([2,5,'backward']);
  await expect(edit).toHaveAttribute('tabindex','-1');await expect(edit).toHaveAttribute('aria-description','Help');
  await edit.evaluate(e=>{e.retained=true;e.setSelectionRange(3,4);});
  await app.getByRole('slider').press('ArrowRight');await expect.poll(()=>page.evaluate(()=>window.readInput().state.amount)).toBe(2);
  expect(await edit.evaluate(e=>[e.selectionStart,e.selectionEnd])).toEqual([3,4]);
  text.properties={Text:'abcdef',CaretIndex:4};await page.evaluate(roots=>window.publishInput({roots}),[text,range]);
  await expect(app.getByRole('status')).toContainText('revision 2');
  await expect(edit).toHaveAttribute('tabindex','0');expect(await edit.getAttribute('aria-description')).toBeNull();
  expect(await edit.getAttribute('data-ui-automation-id')).toBeNull();expect(await edit.evaluate(e=>e.retained)).toBe(true);
  expect(await edit.evaluate(e=>[e.selectionStart,e.selectionEnd])).toEqual([4,4]);
});
test('default action commits a draft first and rejected edits cannot invoke it',async({page})=>{
  const app=await mountInput(page,{roots:[n('/edit','TextBox',{Text:'Old'},{stateKey:'name'}),n('/default','Button',{Content:'Save',IsDefault:true},{actionId:'save'})],state:{name:'Old',count:0},actions:[{id:'save',kind:'state'}]});
  const edit=app.getByRole('textbox');await edit.fill('New');await edit.press('Enter');
  await expect.poll(()=>page.evaluate(()=>window.readInput().state)).toEqual({name:'New',count:1});
  const calls=await page.evaluate(()=>window.inputCalls.filter(c=>c.name!=='xamlg_ui_read'));
  expect(calls.map(c=>c.name)).toEqual(['xamlg_ui_state','xamlg_ui_state_action']);expect(calls[1].expectedStateRevision).toBe(1);
  await page.evaluate(()=>window.failInput=true);await edit.fill('Rejected');await edit.press('Enter');
  await expect(app.getByRole('alert')).toContainText('Rejected input');expect(await page.evaluate(()=>window.readInput().state.count)).toBe(1);
});
test('Escape uses the declared cancel action and external actions still require review',async({page})=>{
  const app=await mountInput(page,{roots:[n('/edit','TextBox',{Text:'text'}),n('/cancel','Button',{Content:'Cancel',IsCancel:true},{actionId:'cancel'})],actions:[{id:'cancel',kind:'message'}]});
  await app.getByRole('textbox').press('Escape');await expect(app.locator('#review')).toBeVisible();
  expect(await page.evaluate(()=>window.inputCalls.filter(c=>c.name==='xamlg_ui_state_action').length)).toBe(0);
  await app.getByRole('textbox').press('Escape');await expect(app.locator('#review')).toBeHidden();
});
test('tab order preserves explicit zero, local groups, none and once scopes',async({page})=>{
  const local=n('/local','StackPanel',{'KeyboardNavigation.TabNavigation':'Local',TabIndex:1},{children:[n('/b','TextBox',{Text:'B',TabIndex:5}),n('/c','TextBox',{Text:'C',TabIndex:0})]});
  const once=n('/once','StackPanel',{'KeyboardNavigation.TabNavigation':'Once',TabIndex:3},{children:[n('/f','TextBox',{Text:'F'}),n('/g','TextBox',{Text:'G'})]});
  const none=n('/none','StackPanel',{'KeyboardNavigation.TabNavigation':'None'},{children:[n('/excluded','TextBox',{Text:'Excluded'})]});
  const app=await mountInput(page,{roots:[n('/a','TextBox',{Text:'A',TabIndex:2}),local,n('/d','TextBox',{Text:'D'}),once,none]});
  await key(app,'/c').focus();
  for(const id of ['/b','/a','/f','/d']){await page.keyboard.press('Tab');await expect(key(app,id)).toBeFocused();}
  await key(app,'/g').focus();await page.keyboard.press('Tab');await expect(key(app,'/d')).toBeFocused();
  await page.keyboard.press('Shift+Tab');await expect(key(app,'/g')).toBeFocused();
});
for(const mode of ['Cycle','Contained'])test('tab boundaries honor '+mode,async({page})=>{
  const app=await mountInput(page,{roots:[n('/group','StackPanel',{'KeyboardNavigation.TabNavigation':mode},{children:[n('/a','TextBox',{Text:'A'}),n('/b','TextBox',{Text:'B'})]})]});
  await key(app,'/b').focus();await page.keyboard.press('Tab');await expect(key(app,mode==='Cycle'?'/a':'/b')).toBeFocused();
});
test('repeat button serializes a slow host and stops on release and source replacement',async({page})=>{
  const button=n('/repeat','RepeatButton',{Content:'Increment',Delay:40,Interval:16},{actionId:'increment'});
  const app=await mountInput(page,{roots:[button],state:{count:0},actions:[{id:'increment',kind:'state'}]});
  await page.evaluate(()=>window.inputDelay=50);await key(app,'/repeat').hover();await page.mouse.down();
  await expect.poll(()=>page.evaluate(()=>window.readInput().state.count)).toBeGreaterThanOrEqual(2);
  await page.mouse.up();await expect.poll(()=>page.evaluate(()=>window.inputCalls.filter(c=>c.name==='xamlg_ui_state_action').length-window.readInput().state.count)).toBe(0);
  const stopped=await page.evaluate(()=>window.readInput().state.count);await page.waitForTimeout(120);expect(await page.evaluate(()=>window.readInput().state.count)).toBe(stopped);
  await key(app,'/repeat').hover();await page.mouse.down();await expect.poll(()=>page.evaluate(()=>window.readInput().state.count)).toBeGreaterThan(stopped);
  await page.evaluate(()=>window.publishInput({roots:[]}));await expect(app.getByRole('status')).toContainText('revision 2');await page.mouse.up();
  const after=await page.evaluate(()=>window.inputCalls.length);await page.waitForTimeout(120);expect(await page.evaluate(()=>window.inputCalls.length)).toBe(after);
  await expect(app.getByRole('alert')).toBeEmpty();
});
for(const properties of [{TabIndex:-1},{IsTabStop:'false'},{SelectionStart:16385},{Delay:20},{'KeyboardNavigation.TabNavigation':'unsupported'}]){
  test('invalid input properties cannot replace committed text: '+JSON.stringify(properties),async({page})=>{
    const app=await mountInput(page,{roots:[n('/edit','TextBox',{Text:'Committed'})]});
    await page.evaluate(roots=>window.publishInput({roots}),[n('/edit','TextBox',{Text:'Invalid',...properties})]);
    await expect(app.getByRole('alert')).not.toBeEmpty();await expect(app.getByRole('textbox')).toHaveValue('Committed');
    await expect(app.getByRole('status')).toContainText('revision 1');
  });
}

test('tab headers retain focus and identity across state echoes and skip disabled headers',async({page})=>{
  const tabs=n('/tabs','TabControl',{SelectedIndex:0},{stateKey:'tab',children:[
    n('/one','TabItem',{Header:'One'}),n('/disabled','TabItem',{Header:'Disabled',IsEnabled:false}),n('/two','TabItem',{Header:'Two'})]});
  const app=await mountInput(page,{roots:[tabs],state:{tab:0}});
  const one=app.getByRole('tab',{name:'One',exact:true}),two=app.getByRole('tab',{name:'Two',exact:true});
  await two.evaluate(e=>e.retained=true);await one.focus();await one.press('ArrowRight');
  await expect.poll(()=>page.evaluate(()=>window.readInput().state.tab)).toBe(2);
  await expect(two).toBeFocused();expect(await two.evaluate(e=>e.retained)).toBe(true);
  await expect(two).toHaveAttribute('aria-selected','true');await expect(two).toHaveAttribute('tabindex','0');
  await two.press('Home');await expect(one).toBeFocused();await expect(one).toHaveAttribute('aria-selected','true');
});
test('unbound three-state inputs cycle locally without host mutations',async({page})=>{
  const app=await mountInput(page,{roots:[n('/toggle','CheckBox',{IsChecked:false,IsThreeState:true})]});
  const toggle=app.getByRole('checkbox');
  for(const value of ['true','mixed','false']){await toggle.click();await expect(toggle).toHaveAttribute('aria-checked',value);}
  expect(await values(page)).toEqual([]);
});
