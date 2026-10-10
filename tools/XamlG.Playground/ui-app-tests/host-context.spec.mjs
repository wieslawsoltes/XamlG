import {test,expect} from '@playwright/test';
import {loadUiResource} from './resource.mjs';
const html=loadUiResource();
async function mount(page,hostContext){
  await page.setContent('<iframe title="host-context" sandbox="allow-scripts" style="width:900px;height:700px"></iframe>');
  await page.evaluate(({html,hostContext})=>{
    const frame=document.querySelector('iframe'),send=message=>frame.contentWindow.postMessage(message,'*');
    window.patchHostContext=value=>send({jsonrpc:'2.0',method:'ui/notifications/host-context-changed',params:value});
    addEventListener('message',event=>{
      if(event.source!==frame.contentWindow||event.data?.method!=='ui/initialize')return;
      send({jsonrpc:'2.0',id:event.data.id,result:{protocolVersion:'2026-01-26',hostCapabilities:{},hostContext}});
    });
    frame.srcdoc=html;
  },{html,hostContext});
  const app=page.frameLocator('iframe');await expect(app.getByRole('status')).toContainText('Waiting');return app;
}
test('host tokens locale safe areas and maximum dimensions are applied',async({page})=>{
  const app=await mount(page,{
    theme:'dark',locale:'pl-pl',timeZone:'Europe/Warsaw',platform:'mobile',displayMode:'inline',deviceCapabilities:{touch:true,hover:false},
    containerDimensions:{maxWidth:640,maxHeight:300},safeAreaInsets:{top:20,right:4,bottom:10,left:8},
    styles:{variables:{'--color-background-primary':'#123456','--color-text-primary':'#ffffff','--font-sans':'Arial'}}
  });
  await expect(app.locator('html')).toHaveAttribute('lang','pl-PL');
  await expect(app.locator('html')).toHaveAttribute('data-time-zone','Europe/Warsaw');
  await expect(app.locator('html')).toHaveAttribute('data-touch','true');
  await expect(app.locator('html')).toHaveAttribute('data-platform','mobile');
  await expect(app.locator('body')).toHaveCSS('background-color','rgb(18, 52, 86)');
  await expect(app.locator('body')).toHaveCSS('padding-top','32px');await expect(app.locator('body')).toHaveCSS('padding-left','20px');
  await expect(app.locator('#surface')).toHaveCSS('max-height','300px');await expect(app.locator('#surface')).toHaveCSS('max-width','640px');
});
test('partial updates do not reset the established theme or unrelated host tokens',async({page})=>{
  const app=await mount(page,{theme:'dark',styles:{variables:{'--font-sans':'Arial'}},safeAreaInsets:{top:10,right:4,bottom:0,left:0}});
  await page.evaluate(()=>window.patchHostContext({containerDimensions:{height:240,width:500},safeAreaInsets:{top:0},styles:{variables:{'--color-text-primary':'#ff0000'}}}));
  await expect(app.locator('html')).toHaveCSS('color-scheme','dark');
  await expect(app.locator('body')).toHaveCSS('padding-top','12px');await expect(app.locator('body')).toHaveCSS('padding-right','16px');
  await expect(app.locator('body')).toHaveCSS('color','rgb(255, 0, 0)');
  expect(await app.locator('html').evaluate(e=>e.style.getPropertyValue('--font-sans'))).toBe('Arial');
});
test('unknown tokens stylesheet injection and invalid context values do not widen resource authority',async({page})=>{
  const errors=[];page.on('pageerror',error=>errors.push(error.message));
  const app=await mount(page,{theme:'light',locale:'en-US',styles:{variables:{'--font-sans':'Arial'}}});
  const trustedStyles=await app.locator('style').allTextContents();
  await page.evaluate(()=>window.patchHostContext({
    theme:'arbitrary',locale:'not_a_language',timeZone:'not/a/zone',containerDimensions:{height:-1},safeAreaInsets:{top:-20},
    styles:{variables:{'--untrusted-token':'red','--font-sans':'url(https://untrusted.example/font.woff2)','--color-text-primary':'red;display:none'},css:{fonts:'@font-face{font-family:Remote;src:url(https://untrusted.example/font.woff2)}'}}
  }));
  // A round-trip barrier makes absence assertions meaningful: the preceding notification
  // has been processed before comparing stylesheet identity, contents and network policy.
  await page.evaluate(()=>window.patchHostContext({locale:'pl-PL'}));
  await expect(app.locator('html')).toHaveAttribute('lang','pl-PL');
  await expect(app.locator('html')).toHaveCSS('color-scheme','light');
  expect(await app.locator('html').evaluate(e=>e.style.getPropertyValue('--font-sans'))).toBe('Arial');
  expect(await app.locator('html').evaluate(e=>e.style.getPropertyValue('--untrusted-token'))).toBe('');
  expect(await app.locator('style').allTextContents()).toEqual(trustedStyles);
  await expect(app.locator('link[rel="stylesheet"]')).toHaveCount(0);
  await expect(app.locator('meta[http-equiv="Content-Security-Policy"]')).toHaveAttribute('content',/connect-src 'none'/);
  await expect(app.locator('body')).toHaveCSS('padding-top','12px');expect(errors).toEqual([]);
});
