#!/usr/bin/env python3
"""Run a clean native package consumer and compile/execute its reactive C# export."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
COMMON = r'''
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using XamlG.Automation;
using XamlG.IntelligentUI;
using XamlG.IntelligentUI.Avalonia;

AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
'''
BOOTSTRAP = r'''
var store = new UiSessionStore();
var snapshot = store.Publish(UiExamples.Pricing(), "package-consumer");
using var renderer = new UiAvaloniaRenderer();
renderer.Apply(snapshot);
var slider = (Slider)renderer.Find("/seats")!;
renderer.StateChanged += change => renderer.Apply(store.ChangeState(change, "package-consumer"));
slider.Value = 10;
if (((TextBlock)renderer.Find("/price")!).Text?.Contains("$290") != true)
    throw new Exception("Packaged native interaction did not update the computed price.");
File.WriteAllText("GeneratedIntelligentView.cs", UiSourceExporter.CSharp(store.Read("pricing", "package-consumer")));
Console.WriteLine("PASS: clean packaged catalog, compiler, state and native renderer.");
'''
EXPORTED = r'''
using var generated = new GeneratedIntelligentView();
var controls = Descendants(generated.View).ToArray();
var slider = controls.OfType<Slider>().Single();
if (slider.Value != 10) throw new Exception("The C# export lost the interacted state.");
slider.Value = 11;
if (!Descendants(generated.View).OfType<TextBlock>().Any(text => text.Text?.Contains("$319") == true))
    throw new Exception("The generated C# component is not reactive.");
Console.WriteLine("PASS: exported C# compiles and executes reactive native Avalonia UI using NuGet references only.");

static IEnumerable<Control> Descendants(Control root)
{
    yield return root;
    IEnumerable<Control> children = root switch
    {
        Panel panel => panel.Children,
        Decorator decorator when decorator.Child != null => new[] { decorator.Child },
        ContentControl content when content.Content is Control child => new[] { child },
        _ => Array.Empty<Control>()
    };
    foreach (var child in children)
        foreach (var descendant in Descendants(child)) yield return descendant;
}
'''

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9A-Za-z.+-]{1,100}', args.version):
        raise ValueError('Invalid candidate version.')
    packages = args.packages.resolve(strict=True)
    versions = {node.attrib['Include']: node.attrib['Version'] for node in ET.parse(ROOT / 'Directory.Packages.props').iter('PackageVersion')}
    with tempfile.TemporaryDirectory(prefix='xamlg-intelligent-ui-consumer-') as temporary:
        work = Path(temporary)
        config = ET.Element('configuration')
        sources = ET.SubElement(config, 'packageSources')
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='candidate', value=str(packages))
        ET.SubElement(sources, 'add', key='public', value='https://api.nuget.org/v3/index.json')
        mapping = ET.SubElement(config, 'packageSourceMapping')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='candidate'), 'package', pattern='XamlG.*')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='public'), 'package', pattern='*')
        ET.ElementTree(config).write(work / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
        project = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
        properties = ET.SubElement(project, 'PropertyGroup')
        for key, value in {'TargetFramework':'net10.0', 'OutputType':'Exe', 'ImplicitUsings':'enable', 'Nullable':'enable', 'TreatWarningsAsErrors':'true'}.items():
            ET.SubElement(properties, key).text = value
        items = ET.SubElement(project, 'ItemGroup')
        ET.SubElement(items, 'PackageReference', Include='XamlG.IntelligentUI.Avalonia', Version=args.version)
        ET.SubElement(items, 'PackageReference', Include='Avalonia.Headless', Version=versions['Avalonia'])
        ET.ElementTree(project).write(work / 'Consumer.csproj', encoding='utf-8', xml_declaration=True)
        environment = dict(os.environ, NUGET_PACKAGES=str(work / 'packages'))
        def run(*arguments):
            subprocess.run(['dotnet', *arguments], cwd=work, env=environment, check=True, timeout=300)
        (work / 'Program.cs').write_text(COMMON + BOOTSTRAP)
        run('restore', 'Consumer.csproj', '--configfile', 'NuGet.Config', '-p:NuGetAudit=false')
        run('run', '--project', 'Consumer.csproj', '-c', 'Release', '--no-restore')
        if not (work / 'GeneratedIntelligentView.cs').is_file():
            raise RuntimeError('The package consumer did not export source.')
        (work / 'Program.cs').write_text(COMMON + EXPORTED)
        run('run', '--project', 'Consumer.csproj', '-c', 'Release', '--no-restore')

if __name__ == '__main__':
    main()
