#!/usr/bin/env python3
"""Exercise the reusable automation/agent/MCP packages outside the repository."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='xamlg-studio-consumer-') as temporary:
        work = Path(temporary)
        environment = dict(os.environ, NUGET_PACKAGES=str(work / 'packages'))
        configuration = ET.Element('configuration')
        sources = ET.SubElement(configuration, 'packageSources')
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='candidate', value=str(args.packages.resolve()))
        ET.SubElement(sources, 'add', key='nuget', value='https://api.nuget.org/v3/index.json')
        mapping = ET.SubElement(configuration, 'packageSourceMapping')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='candidate'), 'package', pattern='XamlG.*')
        ET.SubElement(ET.SubElement(mapping, 'packageSource', key='nuget'), 'package', pattern='*')
        config = work / 'NuGet.Config'
        ET.ElementTree(configuration).write(config, encoding='utf-8', xml_declaration=True)
        project = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
        properties = ET.SubElement(project, 'PropertyGroup')
        for name, value in [('TargetFramework', 'net10.0'), ('OutputType', 'Exe'), ('ImplicitUsings', 'enable'), ('Nullable', 'enable'), ('NoWarn', 'OPENAI001')]:
            ET.SubElement(properties, name).text = value
        items = ET.SubElement(project, 'ItemGroup')
        ET.SubElement(items, 'FrameworkReference', Include='Microsoft.AspNetCore.App')
        for package in ('XamlG.Automation', 'XamlG.Mcp', 'XamlG.Agents', 'XamlG.Agents.OpenAI', 'XamlG.Agents.Anthropic', 'XamlG.Agents.Gemini'):
            ET.SubElement(items, 'PackageReference', Include=package, Version=args.version)
        ET.ElementTree(project).write(work / 'Consumer.csproj', encoding='utf-8', xml_declaration=True)
        (work / 'Program.cs').write_text('''
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Responses;
using XamlG.Automation;
using XamlG.Agents;
using XamlG.Agents.OpenAI;
using XamlG.Agents.Anthropic;
using XamlG.Agents.Gemini;
using XamlG.Mcp;

var catalog = new AutomationCatalog();
catalog.Add<Input, object>("echo", "Echo input", AutomationScope.Project, AutomationEffect.Read,
    (args, context) => ValueTask.FromResult<object>(new { echo = args.Text }));
var value = await catalog.CallAsync("echo", AutomationJson.Element(new Input("consumer")), new("test", default));
if (value.GetProperty("echo").GetString() != "consumer") throw new Exception("Typed automation failed.");
var services = new ServiceCollection();
services.AddMcpServer().WithAutomation(catalog);
using var anthropic = new Anthropic.AnthropicClient { ApiKey = "unused-test-key", MaxRetries = 0 };
using var google = new Google.GenAI.Client(enterprise: false, apiKey: "unused-test-key");
IAgentProvider[] providers = [new OpenAIAgentProvider(new ResponsesClient("unused-test-key")), new AnthropicAgentProvider(anthropic), new GeminiAgentProvider(google)];
foreach (var provider in providers)
    if (provider.GetContextBytes(new("test-model", "test", [new(AgentMessageKind.User, "hello")], catalog.Tools, 32)) == 0)
        throw new Exception("Official SDK adapter failed: " + provider.Id);
using var harness = new AgentHarness(catalog);
var task = harness.CreateTask("package consumer", new ScriptedProvider(), "test-model");
await harness.RunAsync(task.Id, "hello", new());
if (task.Status != AgentTaskStatus.Completed) throw new Exception("Standalone agent failed.");
Console.WriteLine("PASS: standalone automation, MCP registration, official OpenAI/Anthropic/Gemini SDK adapters and agent harness.");
record Input(string Text);
sealed class ScriptedProvider : IAgentProvider
{
    public string Id => "test";
    public int GetContextBytes(AgentRequest request) => 100;
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["test-model"]);
    public Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> delta, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentReply("done", [], new(1, 1), new object()));
}
''', encoding='utf-8')
        def run(*command):
            subprocess.run(command, cwd=work, env=environment, check=True)
        run('dotnet', 'restore', '--configfile', str(config), '-p:NuGetAudit=false')
        run('dotnet', 'run', '-c', 'Release', '--no-restore')
        tool_path = work / 'tools'
        run('dotnet', 'tool', 'install', 'XamlG.Studio.Host', '--tool-path', str(tool_path), '--version', args.version, '--configfile', str(config))
        run(str(tool_path / ('xamlg-studio.exe' if os.name == 'nt' else 'xamlg-studio')), '--help')
    print('PASS: temporary consumer, tool installation and isolated package cache removed.')


if __name__ == '__main__':
    main()
