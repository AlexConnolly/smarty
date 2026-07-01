using System.Diagnostics;

namespace Smarty.Api;

/// <summary>
/// Converts an arbitrary audio file to the 16 kHz mono 16-bit PCM WAV that <see cref="WhisperTranscriber"/>
/// expects. The web client does this resample in-browser before upload; on the server (e.g. a Slack voice
/// clip) we have the raw file — Slack clips are AAC in an mp4 container — so we shell out to ffmpeg, the
/// pragmatic, format-agnostic route. Lives beside WhisperTranscriber as the other half of the audio pipeline.
///
/// Best-effort by design: a missing ffmpeg or a failed conversion returns <c>false</c> (logged) rather than
/// throwing, so a transcode problem degrades to "no local transcript" instead of breaking the caller. Missing
/// ffmpeg is also auto-installed at startup via <see cref="Ensure"/>, the same way DataScienceCapability brings
/// Python in with winget.
/// </summary>
public sealed class AudioTranscoder
{
    private readonly string _ffmpegPath;

    // Warn about a missing ffmpeg once per process, not once per clip — otherwise every voice note spams the log.
    private static bool _warnedMissing;

    public AudioTranscoder(string? ffmpegPath = null)
        => _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;

    /// <summary>Resolve the ffmpeg executable: explicit <c>SMARTY_FFMPEG</c> override, else bare "ffmpeg" (found
    /// on PATH). Transcoding still degrades gracefully if the binary turns out not to be runnable.</summary>
    public static string Resolve()
        => Environment.GetEnvironmentVariable("SMARTY_FFMPEG") is { Length: > 0 } p ? p : "ffmpeg";

    /// <summary>
    /// Resolve ffmpeg AND install it if missing — mirrors DataScienceCapability's Python bootstrap: try the
    /// configured/PATH binary, then known install locations, then <c>winget install</c> (Windows), then re-scan.
    /// Returns a runnable path, or falls back to "ffmpeg" (the caller then degrades gracefully with a clear
    /// one-time message). Called once at startup when voice notes are enabled, so a slow install doesn't recur.
    /// </summary>
    public static string Ensure()
    {
        var configured = Resolve();
        if (Runs(configured)) return configured;

        foreach (var p in KnownFfmpegPaths())
            if (Runs(p)) { Console.WriteLine($"[audio] found ffmpeg at {p}"); return p; }

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("[audio] ffmpeg not found — installing via winget (Gyan.FFmpeg); this may take a few minutes…");
            try
            {
                RunToCompletion("winget", "install Gyan.FFmpeg --silent --accept-source-agreements --accept-package-agreements");
                Console.WriteLine("[audio] winget install completed.");
            }
            catch (Exception ex) { Console.Error.WriteLine($"[audio] winget install failed: {ex.Message}"); }

            // The current process's PATH won't reflect the install, so probe the on-disk locations directly.
            if (Runs("ffmpeg")) return "ffmpeg";
            foreach (var p in KnownFfmpegPaths())
                if (Runs(p)) { Console.WriteLine($"[audio] using freshly installed ffmpeg at {p}"); return p; }
            Console.Error.WriteLine("[audio] ffmpeg still not resolvable after install — voice transcription will degrade to Slack's native transcripts.");
        }
        else
        {
            Console.Error.WriteLine("[audio] ffmpeg not found and auto-install is Windows-only — install ffmpeg or set SMARTY_FFMPEG.");
        }

        return "ffmpeg"; // last resort; ToWhisperWavAsync degrades gracefully with a clear one-time hint
    }

    public async Task<bool> ToWhisperWavAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(inputPath)) { Console.Error.WriteLine($"[audio] transcode: no input at {inputPath}"); return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // -y overwrite · -i input · 16 kHz · mono · signed 16-bit little-endian PCM · WAV container.
            foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath,
                                      "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", outputPath })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) { Console.Error.WriteLine("[audio] ffmpeg failed to start"); return false; }

            // Drain stderr so a long conversion can't deadlock on a full pipe buffer.
            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"[audio] ffmpeg exit {proc.ExitCode}: {Tail(stderr)}");
                return false;
            }
            // A bare WAV header is 44 bytes; anything larger means we actually got audio.
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 44;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ffmpeg couldn't be launched — it isn't installed / not on PATH (and auto-install didn't take). Say
            // so once, clearly and actionably, instead of repeating the raw "cannot find the file specified" per clip.
            if (!_warnedMissing)
            {
                _warnedMissing = true;
                Console.Error.WriteLine(
                    $"[audio] ffmpeg not found (tried \"{_ffmpegPath}\") — voice-note transcription is disabled. " +
                    "Install ffmpeg and put it on PATH, or set SMARTY_FFMPEG to its full path. Clips with a native " +
                    "Slack transcript still work; others can't be transcribed until ffmpeg is available.");
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] transcode error: {ex.Message}");
            return false;
        }
    }

    // Can this ffmpeg path actually run? A quick `-version` with a short timeout.
    private static bool Runs(string ffmpeg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Where ffmpeg.exe tends to land after a winget/choco/manual install — the current process's PATH won't have
    // picked up a fresh install, so we probe disk directly (mirrors DataScienceCapability's GetStandardPythonPaths).
    private static IEnumerable<string> KnownFfmpegPaths()
    {
        var paths = new List<string>();
        void Add(string p) { if (!string.IsNullOrEmpty(p)) paths.Add(p); }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe")); // winget shim, if created
            // Gyan.FFmpeg unpacks under WinGet\Packages\Gyan.FFmpeg…\ffmpeg-*\bin\ffmpeg.exe — search for it.
            var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
                try { paths.AddRange(Directory.GetFiles(pkgs, "ffmpeg.exe", SearchOption.AllDirectories)); }
                catch { /* a locked/odd dir — skip */ }
        }
        Add(@"C:\ProgramData\chocolatey\bin\ffmpeg.exe");
        Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf)) Add(Path.Combine(pf, "ffmpeg", "bin", "ffmpeg.exe"));

        return paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // Run a command to completion, streaming its output to the console (so a long winget install shows progress).
    private static void RunToCompletion(string cmd, string args)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new Exception($"could not start {cmd}");
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[{cmd}] {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[{cmd}] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new Exception($"{cmd} exited with code {proc.ExitCode}");
    }

    private static string Tail(string s) => s.Length <= 300 ? s : "…" + s[^300..];
}
