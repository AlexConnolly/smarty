using System.Diagnostics;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The ffmpeg bridge that turns a Slack clip into the 16 kHz mono PCM WAV Whisper needs. The real-conversion
/// test generates its own input WAV (no ffmpeg needed to MAKE it) and only requires ffmpeg for the resample
/// under test; it self-skips if ffmpeg isn't resolvable on the box. The graceful-failure test always runs.
/// </summary>
public class AudioTranscoderTests
{
    [Fact]
    public async Task Missing_ffmpeg_returns_false_not_throws()
    {
        var dir = NewTempDir();
        string input = Path.Combine(dir, "in.wav");
        WriteSineWav(input, sampleRate: 44100, channels: 1, seconds: 0.2);

        var transcoder = new AudioTranscoder(Path.Combine(dir, "definitely-not-ffmpeg"));
        bool ok = await transcoder.ToWhisperWavAsync(input, Path.Combine(dir, "out.wav"));

        Assert.False(ok); // degrades gracefully rather than throwing
    }

    [Fact]
    public async Task Converts_arbitrary_wav_to_16k_mono_pcm()
    {
        string ffmpeg = AudioTranscoder.Resolve();
        if (!FfmpegRuns(ffmpeg)) return; // no ffmpeg on this box — nothing to prove here

        var dir = NewTempDir();
        string input = Path.Combine(dir, "in.wav");
        WriteSineWav(input, sampleRate: 44100, channels: 2, seconds: 0.5); // 44.1 kHz stereo → must become 16 kHz mono
        string output = Path.Combine(dir, "out.wav");

        var transcoder = new AudioTranscoder(ffmpeg);
        bool ok = await transcoder.ToWhisperWavAsync(input, output);

        Assert.True(ok);
        var (format, channels, sampleRate) = ReadWavHeader(output);
        Assert.Equal(1, format);       // 1 = PCM
        Assert.Equal(1, channels);     // mono
        Assert.Equal(16000, sampleRate);
    }

    // ---- helpers ----

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smarty-transcode-test", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool FfmpegRuns(string ffmpeg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, "-version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Minimal 16-bit PCM WAV writer — a quiet sine, just enough for ffmpeg to resample.
    private static void WriteSineWav(string path, int sampleRate, int channels, double seconds)
    {
        int frames = (int)(sampleRate * seconds);
        short[] samples = new short[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 8000);
            for (int c = 0; c < channels; c++) samples[i * channels + c] = v;
        }
        int dataBytes = samples.Length * 2;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF".ToCharArray()); w.Write(36 + dataBytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(sampleRate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
    }

    private static (short format, short channels, int sampleRate) ReadWavHeader(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        short format = BitConverter.ToInt16(b, 20);
        short channels = BitConverter.ToInt16(b, 22);
        int sampleRate = BitConverter.ToInt32(b, 24);
        return (format, channels, sampleRate);
    }
}
