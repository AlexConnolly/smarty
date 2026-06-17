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
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string shell = isWindows ? "Windows PowerShell" : "/bin/sh (POSIX shell)";

        return new AgentTool(
            name,
            $"Run a shell command on the local machine and return its combined stdout/stderr output. " +
            $"This machine is {RuntimeInformation.OSDescription} and commands run via {shell}, so you MUST use " +
            $"commands valid for {shell} (e.g. on Windows use PowerShell cmdlets like Get-CimInstance / " +
            $"systeminfo, not Linux tools like 'free' or 'uname'). " +
            "Use this to inspect the system (e.g. memory, disk, OS, processes).",
            new[]
            {
                ToolParameter.String("command", "The command line to execute.", required: true),
                ToolParameter.String("working_dir", "Directory to run the command in. Defaults to the current directory.", required: false),
                ToolParameter.Integer("timeout_seconds", "Maximum seconds to wait before aborting. Defaults to 30.", required: false),
            },
            RunAsync);
    }

    private static async Task<ToolOutput> RunAsync(ToolCallArguments args, CancellationToken ct)
    {
        string command = args.GetString("command");
        string? workingDir = args.GetStringOrNull("working_dir");
        int timeoutSeconds = args.GetInt("timeout_seconds", 30);

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
            psi.ArgumentList.Add(command);
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
