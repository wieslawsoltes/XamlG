#!/usr/bin/env python3
"""Exercise the shipped props, targets and task with real MSBuild, without restoring a sample app."""
from pathlib import Path
import subprocess
import tempfile
import unittest
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / 'src/XamlG.Generator/buildTransitive'


class Inputs(unittest.TestCase):
    def run_project(self, body='', files=('A.axaml',), props='', arguments=(), success=True, before='', framework=''):
        temporary = tempfile.TemporaryDirectory(prefix='xamlg-msbuild-')
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        for file in files:
            path = root / file
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('<Root/>', encoding='utf8')
        project = root / 'Input.proj'
        project.write_text(f'''<Project>
<PropertyGroup><Language>C#</Language><IntermediateOutputPath>obj/</IntermediateOutputPath>{props}</PropertyGroup>
{before}
<Import Project="{escape(str(BUILD / 'XamlG.Generator.props'))}" />
{body}
{framework}
<Target Name="_InjectAvaloniaAdditionalFiles"><ItemGroup><AdditionalFiles Include="@(AvaloniaXaml);@(AvaloniaResource)" /></ItemGroup></Target>
<Import Project="{escape(str(BUILD / 'XamlG.Generator.targets'))}" />
<Target Name="Dump" DependsOnTargets="PrepareXamlGAdditionalFiles">
<WriteLinesToFile File="items.txt" Lines="@(AdditionalFiles->'%(Filename)%(Extension)|%(XamlGLogicalPath)|%(XamlGCompile)|%(Marker)')" Overwrite="true" />
<WriteLinesToFile File="properties.txt" Lines="$(EnableAvaloniaXamlCompilation)|$(AvaloniaNameGeneratorIsEnabled)" Overwrite="true" />
<WriteLinesToFile File="unsafe.txt" Lines="unsafe=$(AllowUnsafeBlocks)" Overwrite="true" />
</Target></Project>''', encoding='utf8')
        result = subprocess.run(['dotnet', 'msbuild', str(project), '-nologo', '-v:q', '-t:Dump', *arguments], cwd=root, capture_output=True, text=True, timeout=90)
        if not success:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            return result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        lines = (root / 'items.txt').read_text(encoding='utf-8-sig').splitlines() if (root / 'items.txt').exists() else []
        return lines, (root / 'properties.txt').read_text(encoding='utf-8-sig').strip(), root

    def test_default_input(self):
        self.assertEqual(self.run_project()[0], ['A.axaml|A.axaml|true|'])
    def test_remove_default_in_project_body(self):
        self.assertEqual(self.run_project('<ItemGroup><XamlGSource Remove="A.axaml" /></ItemGroup>')[0], [])
    def test_update_default_metadata(self):
        self.assertEqual(self.run_project('<ItemGroup><XamlGSource Update="A.axaml" Marker="kept" /></ItemGroup>')[0], ['A.axaml|A.axaml|true|kept'])
    def test_project_body_opt_out(self):
        lines, flags, _ = self.run_project('<PropertyGroup><XamlGEnabled>false</XamlGEnabled></PropertyGroup>', props='<EnableAvaloniaXamlCompilation>true</EnableAvaloniaXamlCompilation><AvaloniaNameGeneratorIsEnabled>true</AvaloniaNameGeneratorIsEnabled>')
        self.assertEqual(lines, [])
        self.assertEqual(flags, 'true|true')
    def test_deferred_resource_compilation_setting(self):
        for framework, avalonia, enabled, original, expected in (
            ('Avalonia', False, True, 'false', 'true'),
            ('Auto', True, True, 'false', 'true'),
            ('Auto', False, True, 'false', 'false'),
            ('Portable', True, True, 'false', 'false'),
            ('Avalonia', True, False, 'false', 'false'),
            ('Auto', True, False, 'false', 'false'),
            ('Portable', False, True, 'true', 'true'),
        ):
            with self.subTest(framework=framework, avalonia=avalonia, enabled=enabled, original=original):
                props = f'<AllowUnsafeBlocks>{original}</AllowUnsafeBlocks>'
                if avalonia:
                    props += '<AvaloniaBuildTasksLocation>test</AvaloniaBuildTasksLocation>'
                body = f'<PropertyGroup><XamlGFramework>{framework}</XamlGFramework><XamlGEnabled>{str(enabled).lower()}</XamlGEnabled></PropertyGroup>'
                _, _, root = self.run_project(body, props=props)
                self.assertEqual((root / 'unsafe.txt').read_text(encoding='utf-8-sig').strip(), f'unsafe={expected}')
    def test_disable_default_items_late(self):
        self.assertEqual(self.run_project('<PropertyGroup><XamlGEnableDefaultItems>false</XamlGEnableDefaultItems></PropertyGroup>')[0], [])
    def test_disable_sdk_defaults_late(self):
        self.assertEqual(self.run_project('<PropertyGroup><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup>')[0], [])
    def test_explicit_input_with_defaults_disabled(self):
        body = '<PropertyGroup><XamlGEnableDefaultItems>false</XamlGEnableDefaultItems></PropertyGroup><ItemGroup><XamlGSource Include="A.axaml" /></ItemGroup>'
        self.assertEqual(len(self.run_project(body)[0]), 1)
    def test_preserve_unrelated_additional_file(self):
        body = '<ItemGroup><AdditionalFiles Include="Other.txt" Marker="other-generator" /></ItemGroup>'
        self.assertIn('Other.txt|||other-generator', self.run_project(body, files=('A.axaml', 'Other.txt'))[0])
    def test_deduplicate_physical_input(self):
        body = '<ItemGroup><AdditionalFiles Include="A.axaml" /><XamlGSource Include="./A.axaml" /></ItemGroup>'
        self.assertEqual(len(self.run_project(body)[0]), 1)
    def test_explicit_logical_path(self):
        body = '<ItemGroup><XamlGSource Update="A.axaml" XamlGLogicalPath="Views/Linked.axaml" /></ItemGroup>'
        self.assertIn('A.axaml|Views/Linked.axaml|true|', self.run_project(body)[0])
    def test_link_metadata(self):
        self.assertIn('A.axaml|Views/Linked.axaml|true|', self.run_project('<ItemGroup><XamlGSource Update="A.axaml" Link="Views/Linked.axaml" /></ItemGroup>')[0])
    def test_false_compile_preserved(self):
        self.assertIn('A.axaml|A.axaml|false|', self.run_project('<ItemGroup><XamlGSource Update="A.axaml" XamlGCompile="false" /></ItemGroup>')[0])
    def test_bad_compile_flag(self):
        self.assertIn('XG2001', self.run_project('<ItemGroup><XamlGSource Update="A.axaml" XamlGCompile="maybe" /></ItemGroup>', success=False))
    def test_conflicting_compile_metadata(self):
        body = '<ItemGroup><AdditionalFiles Include="A.axaml" XamlGCompile="false" /><XamlGSource Update="A.axaml" XamlGCompile="true" /></ItemGroup>'
        self.assertIn('Conflicting XamlGCompile', self.run_project(body, success=False))
    def test_duplicate_logical_names(self):
        body = '<ItemGroup><XamlGSource Update="*.axaml" Link="Same.axaml" /></ItemGroup>'
        self.assertIn('Duplicate logical XAML', self.run_project(body, files=('A.axaml','B.axaml'), success=False))
    def test_traversal_logical_name(self):
        self.assertIn('normalized project-relative', self.run_project('<ItemGroup><XamlGSource Update="A.axaml" Link="../Bad.axaml" /></ItemGroup>', success=False))
    def test_global_compiler_conflict(self):
        self.assertIn('XG2000', self.run_project(arguments=('-p:EnableAvaloniaXamlCompilation=true',), success=False))
    def test_global_name_generator_conflict(self):
        self.assertIn('XG2000', self.run_project(arguments=('-p:AvaloniaNameGeneratorIsEnabled=true',), success=False))
    def test_avalonia_item_removal_is_authoritative(self):
        lines, _, _ = self.run_project(props='<AvaloniaBuildTasksLocation>test</AvaloniaBuildTasksLocation>', before='<ItemGroup><AvaloniaXaml Include="A.axaml" /></ItemGroup>', body='<ItemGroup><AvaloniaXaml Remove="A.axaml" /></ItemGroup>')
        self.assertEqual(lines, [])
    def test_framework_and_explicit_items_coalesce(self):
        lines, _, _ = self.run_project(props='<AvaloniaBuildTasksLocation>test</AvaloniaBuildTasksLocation>', before='<ItemGroup><AvaloniaXaml Include="A.axaml" /></ItemGroup>')
        self.assertEqual(len(lines), 1)
    def test_paml_and_mixed_case(self):
        lines, _, _ = self.run_project(files=('A.paml','B.PAML'), body='<ItemGroup><XamlGSource Include="B.PAML" /></ItemGroup>')
        self.assertEqual(len(lines), 2)
    def test_build_outputs_excluded(self):
        self.assertEqual(len(self.run_project(files=('A.axaml','obj/Generated.axaml','bin/Output.axaml'))[0]), 1)
    def test_input_manifest_is_stable(self):
        _, _, root = self.run_project()
        cache = root / 'obj/XamlG/inputs.cache'
        before = cache.stat().st_mtime_ns
        result = subprocess.run(['dotnet','msbuild','Input.proj','-nologo','-v:q','-t:Dump'], cwd=root, capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(cache.stat().st_mtime_ns, before)
    def test_metadata_conflict_is_not_last_writer_wins(self):
        body = '<ItemGroup><AdditionalFiles Include="A.axaml" Marker="a" /><XamlGSource Update="A.axaml" Marker="b" /></ItemGroup>'
        self.assertIn("Conflicting metadata 'Marker'", self.run_project(body, success=False))


if __name__ == '__main__':
    unittest.main(verbosity=2)
