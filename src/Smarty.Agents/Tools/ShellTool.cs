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
        return new AgentTool(
            name,
            // The two rules that used to sit in the worker's system prompt, describing a tool most workers do not
            // even have. They belong on the tool: whoever can see this can act on it, and nobody else pays for it.
            "Runs a command in the local system shell and returns its output. A capable fallback when no other " +
            "tool fits: system info, local files, an API the web can't reach. NOT for downloading — download_file " +
            "does that and looks like a browser. Never use it to base64-encode a file: a picture is already " +
            "stored and already has a URL, and hand-encoding one produces a string too big to fit through a tool " +
            "result, which has twice cost a run its entire budget.",
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

        // Stopped at the source, not trimmed afterwards.
        //
        // A command's output has no natural size: one unbounded recursive listing of a home directory is megabytes,
        // and truncating it later is too late — by then it has been collected, kept, and resent on every turn of
        // the run. Asked to send an email, a worker ran several of those hunting for mail settings and exhausted
        // the model's context in a single result.
        //
        // Detected by cost rather than by shape on purpose. A blocklist of reckless commands is unwinnable: it
        // would need -Recurse without -First, dir /s, grep -r /, and whatever gets invented next. A byte limit
        // catches every unbounded command, including the ones nobody thought of, and the one that trips it learns
        // something specific — narrow the question.
        bool overflowed = false;
        void Collect(StringBuilder into, string? line)
        {
            if (line is null || overflowed) return;
            if (stdout.Length + stderr.Length + line.Length > MaxOutputChars)
            {
                overflowed = true;
                try { process.Kill(entireProcessTree: true); } catch { /* it may already be gone */ }
                return;
            }
            into.AppendLine(line);
        }

        process.OutputDataReceived += (_, e) => Collect(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Collect(stderr, e.Data);

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

        if (overflowed)
            return ToolOutput.Error(
                $"Stopped: this command produced more than {MaxOutputChars:N0} characters and was killed part-way, " +
                "so what came back is incomplete and the rest does not exist. Running it again will do the same " +
                "thing. Ask a smaller question instead — a path rather than a drive, a filter, -First, a count, or " +
                "a pattern.\n\n" + Combine(stdout, stderr));

        int exitCode = process.ExitCode;
        string combined = Combine(stdout, stderr);
        bool failed = exitCode != 0 || stderr.Length > 0;

        if (combined.Length == 0)
            combined = $"(command exited with code {exitCode} and produced no output)";
        if (failed)
            combined = $"[exit code {exitCode}]\n{combined}";

        return new ToolOutput(combined, failed);
    }

    /// <summary>
    /// How much of a command's output may be collected before the command is killed.
    /// <para>
    /// A limit rather than a trim: trimming implies the output was gathered and then shortened, which is the
    /// expensive half. Nothing beyond this is ever read, so a runaway command costs a moment and a partial
    /// answer instead of a run.
    /// </para>
    /// <para>
    /// Two thousand characters, not a hundred thousand. A hundred thousand is twenty-five thousand tokens for one
    /// result, which is not a limit — it is a slower version of the problem. Two thousand fits a listing, a status,
    /// a version, a count: the things a command is actually good for. Anything that does not fit was a question
    /// that should have been asked more precisely, or asked of a different tool.
    /// </para>
    /// </summary>
    private const int MaxOutputChars = 2_000;

    private static string Combine(StringBuilder stdout, StringBuilder stderr)
    {
        var parts = new List<string>();
        if (stdout.Length > 0) parts.Add(stdout.ToString().TrimEnd());
        if (stderr.Length > 0) parts.Add(stderr.ToString().TrimEnd());
        return string.Join("\n", parts).Trim();
    }
}
