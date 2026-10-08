import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

const dotnetRuntime = await dotnet
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();

await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);

// Expose the managed validation entry point only for an explicitly requested test run.
if (new URLSearchParams(globalThis.location.search).has('xamlg-validate')) {
    const exports = await dotnetRuntime.getAssemblyExports(config.mainAssemblyName);
    globalThis.xamlgValidateCatalog = () => exports.Program.ValidateCatalog();
}
