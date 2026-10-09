using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using XamlG.Playground;
using XamlG.Playground.Editing;
using Dockyard.Blazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<RootHost>("#app");
builder.RootComponents.RegisterDockyardBlazor();
builder.Services.AddDockyardBlazor();
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserCompilerService>();
builder.Services.AddScoped<AvaloniaPreviewHost>();
builder.Services.AddScoped<EditorInteropModule>();
builder.Services.AddScoped<IntelligentUiWorkspaceContext>();
await builder.Build().RunAsync();
