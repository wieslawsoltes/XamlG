using System.Net;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

/// <summary>Public MCP Apps resource for the real Avalonia/Wasm guest. The embedding application
/// chooses a trusted, immutable deployment base. The outer app needs only the declared frame
/// origin; Wasm runs in a separately sandboxed URL document with its own CSP.</summary>
public static class UiNativeAppResource
{
    public const string ResourceUri = "ui://xamlg/intelligent-ui/native-v1";
    public static void Register(AutomationCatalog catalog, UiSessionStore store, Uri assetBase, bool useForStandardTools = false)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(store);
        var html = CreateHtml(assetBase); var origin = assetBase.GetLeftPart(UriPartial.Authority);
        catalog.AddResource(new(ResourceUri, "Native Avalonia intelligent UI", "Actual Avalonia/Wasm controls in an opaque-origin guest. Requires the declared frame origin; use the portable resource on hosts that prohibit nested frames.", UiAutomation.MimeType,
            Metadata: AutomationJson.Element(new { ui = new { prefersBorder = true, permissions = new { clipboardWrite = new { } },
                csp = new { connectDomains = Array.Empty<string>(), resourceDomains = Array.Empty<string>(), frameDomains = new[] { origin }, baseUriDomains = Array.Empty<string>() } } })),
            context => { context.CancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(html); });
        catalog.Add<UiPublish, UiPresentation>("xamlg_ui_native_present", "Present an intelligent UI using actual Avalonia/Wasm controls in an MCP Apps guest. Uses the same catalog, owner, state and revision contract as ui_present. Requires host support for the declared nested frame. The portable ui_present resource remains available on restrictive hosts.", AutomationScope.Agent, AutomationEffect.Edit,
            (args, context) =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(context.PrincipalId)) throw new AutomationException("invalid_principal", "A transport-derived principal is required.");
                try { return ValueTask.FromResult(UiPresentation.From(store.Publish(args, context.PrincipalId))); }
                catch (UiException error) { throw new AutomationException(error.Code, error.Message); }
            });
        var metadata = AutomationJson.Element(new { ui = new { resourceUri = ResourceUri, visibility = new[] { "model", "app" } } });
        catalog.SetMetadata("xamlg_ui_native_present", metadata);
        if (useForStandardTools)
            foreach (var name in new[] { "xamlg_ui_present", "xamlg_ui_data", "xamlg_ui_data_bind" })
                if (catalog.Tools.Any(tool => tool.Name == name)) catalog.SetMetadata(name, metadata);
    }
    public static string CreateHtml(Uri assetBase)
    {
        ArgumentNullException.ThrowIfNull(assetBase);
        if (!assetBase.IsAbsoluteUri || assetBase.Scheme != "https" && !(assetBase.Scheme == "http" && assetBase.IsLoopback) ||
            assetBase.UserInfo.Length != 0 || assetBase.Query.Length != 0 || assetBase.Fragment.Length != 0 || !assetBase.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("Use a trusted HTTPS deployment directory (or HTTP loopback for development).", nameof(assetBase));
        var frame = WebUtility.HtmlEncode(new Uri(assetBase, "ui-native.html").AbsoluteUri);
        var origin = WebUtility.HtmlEncode(assetBase.GetLeftPart(UriPartial.Authority));
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-src " + origin + "; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'\">" +
            "<title>Native Avalonia intelligent UI</title><style>body{margin:0;font:14px system-ui}iframe{width:100%;height:560px;border:0}pre{white-space:pre-wrap;padding:12px}</style></head><body>" +
            "<pre id=\"fallback\" role=\"status\">Loading native Avalonia UI…</pre><iframe id=\"guest\" title=\"Native Avalonia UI\" sandbox=\"allow-scripts\" allow=\"clipboard-write\" src=\"" + frame + "\"></iframe><script>" +
            """
            (()=>{'use strict';const frame=document.getElementById('guest'),fallback=document.getElementById('fallback');let ready=false,queue=[],closed=false;
            const valid=m=>m&&m.jsonrpc==='2.0'&&JSON.stringify(m).length<=2097152;
            const methods=new Set(['ui/initialize','ui/notifications/initialized','ui/notifications/size-changed','tools/call','resources/subscribe','resources/unsubscribe','ui/message','ui/open-link','ui/update-model-context','ui/logging/message']);
            addEventListener('message',event=>{if(closed||!valid(event.data))return;const m=event.data;
              if(event.source===frame.contentWindow){
                if(m.method&&!methods.has(m.method))return;
                if(m.method==='ui/initialize'){ready=true;for(const item of queue)frame.contentWindow.postMessage(item,'*');queue=[];}
                if(m.method==='ui/notifications/initialized')fallback.hidden=true;
                if(m.method==='ui/notifications/size-changed'&&Number.isFinite(m.params?.height))frame.style.height=Math.min(1600,Math.max(240,m.params.height))+'px';
                parent.postMessage(m,'*');
              }else if(event.source===parent){
                const marker=m.method==='ui/notifications/tool-result'?m.params?.structuredContent:null;
                if(typeof marker?.fallbackMarkdown==='string')fallback.textContent=marker.fallbackMarkdown.slice(0,131072);
                if(ready)frame.contentWindow.postMessage(m,'*');else{if(queue.length===8)queue.shift();queue.push(m);}
              }});
            addEventListener('pagehide',()=>{closed=true;queue=[];frame.remove();},{once:true});
            })();
            """ + "</script></body></html>";
    }
}
