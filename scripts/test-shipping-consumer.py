#!/usr/bin/env python3
"""Exercise a single XamlG package reference, handwritten loaders and actual emitted assets."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]


def run(command, cwd, env, success=True):
    result = subprocess.run([str(a) for a in command], cwd=cwd, env=env, text=True, capture_output=True, timeout=240)
    if (result.returncode == 0) != success:
        raise RuntimeError('Command failed: ' + str(command[0:3]) + '\n' + result.stdout + '\n' + result.stderr)
    return result.stdout + result.stderr


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--cli', type=Path, required=True)
    args = parser.parse_args()
    version = next(e.get('Version') for e in ET.parse(ROOT / 'Directory.Packages.props').iter('PackageVersion') if e.get('Include') == 'Avalonia')
    with tempfile.TemporaryDirectory(prefix='xamlg-shipping-consumer-') as temporary:
        root = Path(temporary)
        env = dict(os.environ)
        (root / 'NuGet.Config').write_bytes(args.config.read_bytes())
        (root / 'global.json').write_bytes((ROOT / 'global.json').read_bytes())
        (root / 'Assets').mkdir()
        (root / 'Resources').mkdir()
        (root / 'Assets/asset.bin').write_bytes(bytes([0, 1, 127, 128, 255, 0]))
        (root / 'Assets/manifest.txt').write_text('managed resource survives CLI emission', encoding='utf8')
        project = root / 'ShippingConsumer.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks><EnableWindowsTargeting>true</EnableWindowsTargeting>
<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>preview</LangVersion>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<Configuration>Release</Configuration><EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
<CompilerGeneratedFilesOutputPath>obj/$(TargetFramework)/generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup><ItemGroup>
<PackageReference Include="XamlG.Avalonia" Version="{escape(args.version)}" />
<PackageReference Include="Avalonia.Headless" Version="{escape(version)}" />
<AvaloniaResource Include="Assets/asset.bin" />
<EmbeddedResource Include="Assets/manifest.txt" LogicalName="Exact.Asset.Name" />
</ItemGroup></Project>''', encoding='utf8')
        (root / 'View.axaml').write_text('''<StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="ShippingConsumer.View">
<StackPanel.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceInclude Source="Resources/Palette.axaml" /></ResourceDictionary.MergedDictionaries></ResourceDictionary></StackPanel.Resources>
<TextBlock x:Name="output" Text="initial" Foreground="{StaticResource Accent}" />
</StackPanel>''', encoding='utf8')
        (root / 'Resources/Palette.axaml').write_text('''<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><SolidColorBrush x:Key="Accent" Color="#336699" /></ResourceDictionary>''', encoding='utf8')
        (root / 'View.cs').write_text('''using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace ShippingConsumer;
public partial class View : StackPanel
{
    public View() { InitializeComponent(); InitializeComponent(); }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
''', encoding='utf8')
        (root / 'Program.cs').write_text('''using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ShippingConsumer;
AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var view = new View();
if (view.Children.Count != 1 || view.FindControl<TextBlock>("output")!.Text != (args.FirstOrDefault() ?? "initial"))
    throw new Exception("Handwritten initialization or incremental source failed.");
var uri = new Uri("avares://ShippingConsumer/Resources/" + (args.ElementAtOrDefault(1) ?? "Palette.axaml"));
if (AvaloniaXamlLoader.Load(uri) is not ResourceDictionary dictionary || !dictionary.ContainsKey("Accent"))
    throw new Exception("URI loader did not return a compiled factory.");
using var binary = AssetLoader.Open(new Uri("avares://ShippingConsumer/Assets/asset.bin"));
using var content = new MemoryStream(); binary.CopyTo(content);
if (!content.ToArray().SequenceEqual(new byte[] {0, 1, 127, 128, 255, 0})) throw new Exception("Avalonia binary asset lost.");
using var managed = typeof(View).Assembly.GetManifestResourceStream("Exact.Asset.Name") ?? throw new Exception("Managed manifest resource lost.");
using var reader = new StreamReader(managed);
if (reader.ReadToEnd() != "managed resource survives CLI emission") throw new Exception("Manifest data changed.");
Console.WriteLine("PASS: handwritten/URI loaders, idempotence, Avalonia binary and managed resources.");
''', encoding='utf8')
        run(['dotnet', 'restore', project, '-p:NuGetAudit=false'], root, env)
        for target in ('net10.0', 'net10.0-windows'):
            run(['dotnet', 'build', project, '-c', 'Release', '-f', target, '--no-restore', '-warnaserror'], root, env)
            image = root / 'bin/Release' / target / 'ShippingConsumer.dll'
            print(run(['dotnet', image], root, env), end='')
            timestamp = image.stat().st_mtime_ns
            run(['dotnet', 'build', project, '-c', 'Release', '-f', target, '--no-restore', '-warnaserror'], root, env)
            if image.stat().st_mtime_ns != timestamp:
                raise RuntimeError('An unchanged package-consumer build rewrote the application assembly.')
        source = root / 'View.axaml'
        source.write_text(source.read_text().replace('Text="initial"', 'Text="edited"'), encoding='utf8')
        image = root / 'bin/Release/net10.0/ShippingConsumer.dll'
        run(['dotnet', 'build', project, '-c', 'Release', '-f', 'net10.0', '--no-restore', '-warnaserror'], root, env)
        print(run(['dotnet', image, 'edited'], root, env), end='')
        (root / 'Resources/Palette.axaml').rename(root / 'Resources/Renamed.axaml')
        source.write_text(source.read_text().replace('Palette.axaml', 'Renamed.axaml'), encoding='utf8')
        run(['dotnet', 'build', project, '-c', 'Release', '-f', 'net10.0', '--no-restore', '-warnaserror'], root, env)
        print(run(['dotnet', image, 'edited', 'Renamed.axaml'], root, env), end='')
        # Exports belong below obj so a subsequent workspace load cannot mistake them for user C#.
        generated = root / 'obj/cli-generated'
        run([args.cli.resolve(), 'compile', '--project', project, '--target-framework', 'net10.0', '--output', generated,
             '--emit-assembly', image], root, env)
        if not (generated / 'XamlG.LoaderAdapters.g.cs').is_file():
            raise RuntimeError('The CLI export omitted loader adapters.')
        print(run(['dotnet', image, 'edited', 'Renamed.axaml'], root, env), end='')
        adapter = generated / 'XamlG.LoaderAdapters.g.cs'
        adapter.write_text(adapter.read_text() + '\n// application-owned edit\n')
        result = run([args.cli.resolve(), 'compile', '--project', project, '--target-framework', 'net10.0', '--output', generated], root, env, success=False)
        if 'Refusing to overwrite' not in result or 'application-owned edit' not in adapter.read_text():
            raise RuntimeError('CLI did not protect the externally edited generated file: ' + result)
        run(['dotnet', 'clean', project, '-c', 'Release', '-f', 'net10.0'], root, env)
        run(['dotnet', 'build', project, '-c', 'Release', '-f', 'net10.0', '--no-restore', '-warnaserror'], root, env)
        print(run(['dotnet', image, 'edited', 'Renamed.axaml'], root, env), end='')
        print('PASS: single-reference package, two TFMs, unchanged build, edit, resource rename, CLI adapters/assets, output ownership and clean/rebuild.')


if __name__ == '__main__':
    main()
