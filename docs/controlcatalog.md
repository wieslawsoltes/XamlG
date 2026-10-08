# ControlCatalog and theme validation

The complete ControlCatalog, desktop/browser hosts, MiniMvvm, Simple theme and Fluent theme are imported from `wieslawsoltes/Avalonia` at `a9429a328057befa287ffb5e981f58b86a86eda0`. Original copies have separate commits; project adaptations and fixes follow them. [The port notice](../samples/UPSTREAM-NOTICE.md) links the retained licenses. The [import manifest](../samples/controlcatalog-upstream.json) records 822 original file hashes and explains every changed imported file.

The catalog contains 219 XAML documents and 218 component classes. Simple contributes all 81 original XAML documents plus its linked Fluent strings dictionary; Fluent contributes all 86. ControlCatalog references these local theme projects. Its XAML and both themes compile with XamlG; other framework libraries retain their upstream build.

## Build and run

Install the .NET SDK pinned in `global.json`, its `wasm-tools` workload, and the native build prerequisites for Avalonia on your platform (Xcode command-line tools on macOS; GTK 3 and `mplayer` for the Linux native embedding demos). Prepare the exact framework revision:

```sh
python3 scripts/prepare-controlcatalog.py
dotnet workload install wasm-tools
dotnet build XamlG.ControlCatalog.slnx -c Release -p:AvsSkipBuildingLegacyTargetFrameworks=True
dotnet run --project samples/ControlCatalog.Desktop -c Release --no-build
```

The preparation script checks all copied files, documented adaptations, component coverage, the framework commit and submodules. `--verify-only` performs the checks without fetching. `--upstream PATH` and the matching MSBuild `ControlCatalogAvaloniaRoot` property allow an existing clean checkout of the pinned revision.

The source dependency is intentional: this catalog uses APIs newer than Avalonia 12.1.3, including the current navigation controls and generated properties. Building the older published packages would require changing or removing samples. `AvsSkipBuildingLegacyTargetFrameworks=True` limits the pinned framework to .NET 10.

## One integration reference

Each copied XAML project adds:

```xml
<ProjectReference Include="../../src/XamlG.Avalonia/XamlG.Avalonia.csproj" />
```

The shared repository `Directory.Build.targets` selects the current generator and its shipped build targets for that reference. XamlG disables Avalonia's XamlX compilation and name generator by default. The copied projects retain Avalonia's resource packaging and non-XAML property generators. Set `XamlGEnabled=false` to opt out.

For projects outside this repository, use the single `XamlG.Avalonia` **PackageReference**; its transitive assets import automatically. MSBuild does not automatically import build assets from arbitrary project references. Source consumers need the shared build integration used here. Neither path requires manually disabling XamlX in every application project.

Referenced libraries compiled by XamlG use typed exported factories. Public URI resources in existing Avalonia libraries use their precompiled loader, with the selected loader preserved for trimming. Private resources that the referenced library does not export remain unavailable through its public URI loader; failures are reported by Avalonia at runtime.

## Validation

```sh
dotnet test samples/ControlCatalog.Tests -c Release -p:AvsSkipBuildingLegacyTargetFrameworks=True
dotnet run --project samples/ControlCatalog.Desktop -c Release --no-build -- \
  --xamlg-validate artifacts/controlcatalog-validation/desktop-results.json
dotnet publish samples/ControlCatalog.Browser -c Release -p:AvsSkipBuildingLegacyTargetFrameworks=True
npm install --prefix tools/XamlG.Playground
npx --prefix tools/XamlG.Playground playwright install chromium
python3 scripts/serve-playground.py \
  --directory samples/ControlCatalog.Browser/bin/Release/net10.0-browser/publish/wwwroot --port 8943
# In another terminal:
node scripts/test-controlcatalog-browser.mjs http://127.0.0.1:8943
```

Set `XAMLG_CATALOG_RESULTS` to select the headless screenshot/report directory. All 76 registered pages, 11 section pages, nine TextBox gallery demos and 102 nested gallery demos run in six configurations: Simple light/dark and Fluent light/dark at normal/compact density. Each host reports 1,188 page/configuration results. The page drivers use the full catalog shell and check Home's ancestor binding, ComboBox's attached item binding and CalendarDatePicker's compiled two-way binding. Headless tests use real Skia rendering and additionally check every imported component factory. They also assert that the application and both theme assemblies have XamlG exports and no XamlX-generated URI loader.

These checks validate construction, layout, realized templates and rendering across the complete catalog registry. Native embedding, GPU support, file dialogs, clipboard and external links depend on the host; rendering their pages does not certify every platform-specific operation. Headless rendering cannot substitute for the real desktop and published WebAssembly runs.

The browser host retains Avalonia's generated OpenGL delegate signatures through trimming. Its function-pointer calls need these declarations when the WebAssembly SDK generates native-call stubs; otherwise the interpreter can abort when the OpenGL lease demo renders.

## Compilation benchmark

On a clean committed worktree, run `python3 scripts/benchmark-controlcatalog.py`. The CI catalog job runs the same comparison and uploads its JSON, individual build logs and Markdown table with the validation artifacts.

The harness builds dependencies before measurement, then forces three Release rebuilds of each theme and ControlCatalog with each compiler. XamlX uses the same ported projects with `XamlGEnabled=false`; XamlG uses their normal source integration. Timed builds disable project-reference builds, restore and shared compilation, use one MSBuild worker, and start fresh compiler processes with a warm filesystem/package cache. The script restores the normal XamlG build afterwards.

Wall time includes project evaluation, resources, C# and XAML compilation and output copying. The compiler-task column combines `Csc` and, for XamlX, `CompileAvaloniaXamlTask`; XamlG executes inside `Csc`. These are project-compilation measurements, not isolated parser timings or application startup measurements. Browser linking and the pinned framework build are excluded. Results record the tested commit, tree, SDK, OS, commands and log checksums.
