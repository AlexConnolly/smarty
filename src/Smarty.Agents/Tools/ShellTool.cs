using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Smarty.Agents;

/// <summary>
/// Factory for a real <c>run_shell_command</c> tool that actually executes commands on the
/// local machine. Multi-parameter: <c>command</c> (required), <c>working_dir</c> and
/// <c>timeout_seconds</c> (optional). On Windows it runs through PowerShell; elsewhere /bin/sh.
/// </summary>
public static class ShellTool
{
    public static AgentTool Create(string name = "run_shell_command")
        => Create(null, name);

    public static AgentTool Create(IGateProvider? gateProvider, string name = "run_shell_command")
    {
        return new AgentTool(
            name,
            "Runs a command in the local system shell and returns its output. A capable fallback when no other tool fits.",
            new[]
            {
                ToolParameter.String("command", "The command line to execute.", required: true),
                ToolParameter.String("working_dir", "Directory to run the command in. Defaults to the current directory.", required: false),
                ToolParameter.Integer("timeout_seconds", "Maximum seconds to wait before aborting. Defaults to 30.", required: false),
            },
            (args, ct) => RunAsync(args, gateProvider, ct));
    }

    private static async Task<ToolOutput> RunAsync(ToolCallArguments args, IGateProvider? gateProvider, CancellationToken ct)
    {
        string command = args.GetString("command");
        string? workingDir = args.GetStringOrNull("working_dir");
        int timeoutSeconds = args.GetInt("timeout_seconds", 30);

        if (gateProvider != null)
        {
            bool approved = await gateProvider.RequestAccessAsync("run_shell_command", command, ct).ConfigureAwait(false);
            if (!approved)
            {
                return ToolOutput.DeadEnd("Access denied by the user. I cannot perform this action.");
            }
        }

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "powershell.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isWindows)
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            // Silence the progress stream — its prompts break Invoke-WebRequest in non-interactive mode.
            psi.ArgumentList.Add("$ProgressPreference='SilentlyContinue'; " + command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
            psi.WorkingDirectory = workingDir;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return ToolOutput.Error($"Command timed out after {timeoutSeconds}s.\n{Combine(stdout, stderr)}");
        }

        int exitCode = process.ExitCode;
        string combined = Combine(stdout, stderr);
        bool failed = exitCode != 0 || stderr.Length > 0;

        if (combined.Length == 0)
            combined = $"(command exited with code {exitCode} and produced no output)";
        if (failed)
            combined = $"[exit code {exitCode}]\n{combined}";

        return new ToolOutput(combined, failed);
    }

    private static string Combine(StringBuilder stdout, StringBuilder stderr)
    {
        var parts = new List<string>();
        if (stdout.Length > 0) parts.Add(stdout.ToString().TrimEnd());
        if (stderr.Length > 0) parts.Add(stderr.ToString().TrimEnd());
        return string.Join("\n", parts).Trim();
    }
}
