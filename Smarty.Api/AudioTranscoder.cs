using System.Diagnostics;

namespace Smarty.Api;

/// <summary>
/// Converts an arbitrary audio file to the 16 kHz mono 16-bit PCM WAV that <see cref="WhisperTranscriber"/>
/// expects. The web client does this resample in-browser before upload; on the server (e.g. a Slack voice
/// clip) we have the raw file — Slack clips are AAC in an mp4 container — so we shell out to ffmpeg, the
/// pragmatic, format-agnostic route. Lives beside WhisperTranscriber as the other half of the audio pipeline.
///
/// Best-effort by design: a missing ffmpeg or a failed conversion returns <c>false</c> (logged) rather than
/// throwing, so a transcode problem degrades to "no local transcript" instead of breaking the caller.
/// </summary>
public sealed class AudioTranscoder
{
    private readonly string _ffmpegPath;

    public AudioTranscoder(string? ffmpegPath = null)
        => _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;

    /// <summary>Resolve the ffmpeg executable: explicit <c>SMARTY_FFMPEG</c> override, else bare "ffmpeg" (found
    /// on PATH). Transcoding still degrades gracefully if the binary turns out not to be runnable.</summary>
    public static string Resolve()
        => Environment.GetEnvironmentVariable("SMARTY_FFMPEG") is { Length: > 0 } p ? p : "ffmpeg";

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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] transcode error: {ex.Message}");
            return false;
        }
    }

    private static string Tail(string s) => s.Length <= 300 ? s : "…" + s[^300..];
}
