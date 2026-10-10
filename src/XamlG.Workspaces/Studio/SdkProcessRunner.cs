using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace XamlG.Workspaces.Studio;

public sealed record SdkCommandResult(int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated);
public interface ISdkProcessRunner
{
    Task<SdkCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Runs a trusted SDK command. Never invokes a shell or accepts a caller-selected executable.</summary>
public sealed class SdkProcessRunner : ISdkProcessRunner
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly int _outputLimit;

    public SdkProcessRunner(string? executable = null, TimeSpan? timeout = null, int outputLimit = 1024 * 1024)
    {
        _executable = executable ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (outputLimit is < 1024 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(outputLimit));
        _outputLimit = outputLimit;
    }

    public async Task<SdkCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count is 0 or > 256 || arguments.Any(argument => argument == null || argument.Length > 8192 || argument.Contains('\0')))
            throw new ArgumentException("Invalid SDK argument list.");
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory), UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Do not pass companion bearer tokens or common provider credentials into build processes.
        foreach (var name in start.Environment.Keys.Where(IsCompanionSecret).ToArray()) start.Environment.Remove(name);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = new Process { StartInfo = start };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_timeout);
        lifetime.Token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Could not start the .NET SDK.");
        process.StandardInput.Close();
        using var cancellation = lifetime.Token.Register(() => Kill(process));
        var output = DrainAsync(process.StandardOutput, lifetime.Token);
        var error = DrainAsync(process.StandardError, lifetime.Token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(lifetime.Token)).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false);
            return new(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Kill(process);
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("The SDK operation exceeded its execution limit.");
            throw;
        }
    }

    private async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = Math.Max(0, _outputLimit - text.Length);
            text.Append(buffer, 0, Math.Min(remaining, count));
            truncated |= count > remaining;
        }
        return (text.ToString(), truncated);
    }

    private static bool IsCompanionSecret(string name) =>
        name.StartsWith("XAMLG_STUDIO_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("GEMINI_", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GOOGLE_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AZURE_OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase);

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
