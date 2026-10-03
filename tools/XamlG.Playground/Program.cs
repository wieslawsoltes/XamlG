using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using XamlG.Playground;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<RootHost>("#app");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserCompilerService>();
builder.Services.AddScoped<AvaloniaPreviewHost>();
await builder.Build().RunAsync();
