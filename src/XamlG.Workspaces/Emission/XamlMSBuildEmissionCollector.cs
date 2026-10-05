using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace XamlG.Workspaces;

/// <summary>Runs the trusted project's resource preparation and command-line generation only.
/// No compiler execution, XamlX rewriting or application execution occurs in this stage.</summary>
public static class XamlMSBuildEmissionCollector
{
    public static async Task<XamlEmissionInputs> CollectAsync(string projectPath, XamlWorkspaceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AllowProjectEvaluation)
            throw new InvalidOperationException("Preparing emission inputs can execute MSBuild tasks. Explicitly trust the project first.");
        projectPath = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(projectPath)!;
        var properties = new Dictionary<string, string>(options.GlobalProperties, StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "true", ["BuildingInsideVisualStudio"] = "true",
            ["SkipCompilerExecution"] = "true", ["ProvideCommandLineArgs"] = "true",
            ["BuildProjectReferences"] = "false",
            // CoreCompile must return arguments even if its normal assembly outputs are current.
            ["NonExistentFile"] = Path.Combine(Path.GetTempPath(), "xamlg-emission-" + Guid.NewGuid().ToString("N"))
        };
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("msbuild"); start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-nologo"); start.ArgumentList.Add("-verbosity:quiet");
        start.ArgumentList.Add("-target:PrepareForBuild;ResolveReferences;PrepareResources;Compile");
        start.ArgumentList.Add("-getItem:CscCommandLineArgs");
        foreach (var property in properties)
        {
            if (property.Key.Length == 0 || property.Key.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                throw new ArgumentException("Invalid MSBuild property name: " + property.Key, nameof(options));
            start.ArgumentList.Add("-property:" + property.Key + "=" + EscapeProperty(property.Value));
        }
        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = new Process { StartInfo = start };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("The MSBuild host could not be started.");
        using var abort = lifetime.Token.Register(() => Kill(process));
        try
        {
            var output = ReadAsync(process.StandardOutput, 16 * 1024 * 1024, lifetime);
            var error = ReadAsync(process.StandardError, 4 * 1024 * 1024, lifetime);
            await Task.WhenAll(output, error, process.WaitForExitAsync(lifetime.Token));
            if (process.ExitCode != 0)
                throw new InvalidOperationException("MSBuild resource preparation failed:\n" + await output + "\n" + await error);
            using var json = JsonDocument.Parse((await output).TrimStart('\uFEFF'));
            var items = json.RootElement.GetProperty("Items").GetProperty("CscCommandLineArgs");
            var arguments = items.EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!).ToArray();
            if (arguments.Length == 0) throw new InvalidDataException("MSBuild returned no compiler arguments; refusing to emit an assembly with unknown resource inputs. Select an inner target framework for multi-target projects.");
            return XamlEmissionInputs.FromCommandLine(arguments, directory);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("MSBuild emission-input collection exceeded its time or output budget."); }
        finally { Kill(process); }
    }
    private static string EscapeProperty(string value) => value.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C");
    private static async Task<string> ReadAsync(StreamReader reader, int limit, CancellationTokenSource lifetime)
    {
        try
        {
            var result = new StringBuilder();
            var buffer = new char[8192];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), lifetime.Token)) != 0)
            {
                if ((long)result.Length + count > limit) throw new InvalidDataException("MSBuild output exceeded its configured budget.");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        catch { lifetime.Cancel(); throw; }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
