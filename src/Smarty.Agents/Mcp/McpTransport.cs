using System.Diagnostics;
using System.Text;

namespace Smarty.Agents;

/// <summary>
/// A duplex channel to an MCP server carrying one JSON-RPC message per line. Abstracted so the client can be
/// driven without a child process (in tests, or by a future HTTP transport) — the protocol layer above never
/// knows how the bytes travel.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>False once the far end has gone (the process exited, the socket closed).</summary>
    bool IsAlive { get; }

    /// <summary>Every JSON message the server sent.</summary>
    event Action<string>? MessageReceived;

    /// <summary>Diagnostics from the transport itself — a server's stderr, an exit code.</summary>
    event Action<string>? Log;

    /// <summary>Raised once when the channel is gone, carrying the reason if there was one. Whatever is waiting
    /// on a reply is told here rather than sitting until its timeout.</summary>
    event Action<Exception?>? Closed;

    Task StartAsync(CancellationToken ct = default);

    Task SendAsync(string message, CancellationToken ct = default);
}

/// <summary>
/// The stdio transport: the server runs as a child process and messages are newline-delimited JSON over its
/// stdin/stdout. This is what virtually every MCP server ships as, so it's the one that matters.
/// </summary>
/// <remarks>
/// stderr is never parsed — servers use it for logging, and the spec reserves stdout for protocol traffic. It's
/// forwarded to <see cref="Log"/> instead, which is where a server's own startup complaints end up.
/// </remarks>
public sealed class McpStdioTransport : IMcpTransport
{
    private readonly McpServerConfig _config;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private volatile bool _exited;

    public McpStdioTransport(McpServerConfig config) => _config = config;

    public event Action<string>? MessageReceived;

    public event Action<string>? Log;

    public event Action<Exception?>? Closed;

    public bool IsAlive => _process is { } p && !_exited && !p.HasExited;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.Command))
            throw new InvalidOperationException($"MCP server '{_config.Name}' has no command to run.");

        var info = new ProcessStartInfo
        {
            FileName = ResolveCommand(_config.Command),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // No BOM: a byte-order mark at the head of the first message is not valid JSON to the far end.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in _config.Args) info.ArgumentList.Add(arg);
        foreach (var (key, value) in _config.Env) info.Environment[key] = value;
        if (!string.IsNullOrWhiteSpace(_config.WorkingDirectory))
            info.WorkingDirectory = _config.WorkingDirectory;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            _exited = true;
            var code = ExitCodeOf(process);
            Log?.Invoke($"exited with code {code}");
            Close(new IOException($"the '{_config.Name}' MCP server process exited (code {code})."));
        };

        if (!process.Start())
            throw new InvalidOperationException($"MCP server '{_config.Name}' failed to start ({info.FileName}).");

        // DisposeAsync below is the polite exit and it works — but only when it runs. A force-stop, a crash or a
        // closed terminal skips it entirely and leaves this server alive forever, holding its port. Hand the
        // child to the OS so it dies with us no matter how we go.
        ChildProcessReaper.KillWithUs(process);

        _process = process;

        // Pumps, not awaits: both run for the life of the process. stdout ending is the channel closing, whether
        // or not the process has got round to exiting.
        _ = Task.Run(async () =>
        {
            await PumpAsync(process.StandardOutput, line => MessageReceived?.Invoke(line)).ConfigureAwait(false);
            Close(new IOException($"the '{_config.Name}' MCP server closed its output."));
        });
        _ = Task.Run(() => PumpAsync(process.StandardError, line => Log?.Invoke($"stderr: {line}")));

        return Task.CompletedTask;
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (_process is not { } process || _exited || process.HasExited)
            throw new IOException($"MCP server '{_config.Name}' is not running.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync(message.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        Close(null);
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                // Closing stdin is the polite exit an MCP server is built to notice; killing is the backstop.
                try { process.StandardInput.Close(); } catch { /* already gone */ }
                if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
            }
        }
        catch { /* the process died on its own — nothing to clean up */ }
        finally
        {
            process.Dispose();
            _writeLock.Dispose();
        }

        await Task.CompletedTask;
    }

    // The channel closes once — whichever of the exit, the stream ending, or our own disposal gets there first.
    private void Close(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0) return;
        Closed?.Invoke(reason);
    }

    private int _closedFlag;

    private async Task PumpAsync(StreamReader reader, Action<string> sink)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (line.Length > 0)
                    sink(line);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"stream ended: {ex.Message}");
        }
    }

    private static int? ExitCodeOf(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }

    /// <summary>
    /// Find the executable behind a bare command name. On Windows the launcher for a Node-based server is
    /// <c>npx.cmd</c>, not <c>npx</c>, and a bare name fails to start — so PATH is walked with PATHEXT applied.
    /// A command that's already a path, or that resolves to nothing, is handed over untouched for the OS to
    /// resolve (and to report on).
    /// </summary>
    public static string ResolveCommand(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return command;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(command))
            return command;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), command + ext.ToLowerInvariant());
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry — skip it and keep looking.
                }
            }
        }

        return command;
    }
}
