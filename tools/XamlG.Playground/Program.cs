using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using XamlG.Playground;
using XamlG.Playground.Editing;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<RootHost>("#app");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserCompilerService>();
builder.Services.AddScoped<AvaloniaPreviewHost>();
builder.Services.AddScoped<EditorInteropModule>();
await builder.Build().RunAsync();
