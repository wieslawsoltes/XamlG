import {test,expect} from '@playwright/test';
import {readFileSync} from 'node:fs';
const source=readFileSync(new URL('../wwwroot/ui-native-guest.js',import.meta.url),'utf8');
async function mount(page,sandbox){
 await page.route('https://native-bridge.test/**',route=>{const js=route.request().url().endsWith('.js');return route.fulfill({contentType:js?'text/javascript':'text/html',headers:{'Access-Control-Allow-Origin':'*'},body:js?source:`<!doctype html><body><p id="status">Loading</p><script type="module">import{connect}from'/bridge.js';window.calls=[];try{window.bridge=connect({invokeMethodAsync:async(method,args)=>{window.calls.push({method,args});return{ok:true};}},'execution');document.querySelector('#status').textContent='connected';}catch(error){document.querySelector('#status').textContent=error.message;}</script></body>`});});
 await page.setContent(`<iframe title="guest" ${sandbox?'sandbox="allow-scripts"':''} src="https://native-bridge.test/guest.html"></iframe>`);return page.frameLocator('iframe');
}
test('ordinary URL iframe cannot receive executable source in the Studio origin',async({page})=>{
 const guest=await mount(page,false);await expect(guest.locator('#status')).toContainText('Opaque-origin sandbox required');
 await page.evaluate(()=>document.querySelector('iframe').contentWindow.postMessage({type:'xamlg-ui-execute',nonce:'a'.repeat(32),request:{xaml:'untrusted'}},'*'));expect(await guest.locator('body').evaluate(()=>window.calls)).toEqual([]);
});
test('opaque execution guest accepts only its parent and one reviewed payload',async({page})=>{
 const guest=await mount(page,true);await expect(guest.locator('#status')).toHaveText('connected');
 await guest.locator('body').evaluate(()=>window.postMessage({type:'xamlg-ui-execute',nonce:'a'.repeat(32),request:{xaml:'spoofed'}},'*'));expect(await guest.locator('body').evaluate(()=>window.calls)).toEqual([]);
 await page.evaluate(()=>document.querySelector('iframe').contentWindow.postMessage({type:'xamlg-ui-execute',nonce:'a'.repeat(32),request:{xaml:'reviewed'}},'*'));await expect.poll(()=>guest.locator('body').evaluate(()=>window.calls.length)).toBe(1);
 await page.evaluate(()=>document.querySelector('iframe').contentWindow.postMessage({type:'xamlg-ui-execute',nonce:'b'.repeat(32),request:{xaml:'second'}},'*'));expect(await guest.locator('body').evaluate(()=>window.calls)).toEqual([{method:'ExecuteApproved',args:{xaml:'reviewed'}}]);
 await expect(guest.locator('body').evaluate(()=>window.bridge.action({kind:'tool',tool:'xamlg_document_write'}))).rejects.toThrow('no host-action bridge');
});
