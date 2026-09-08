using System.Text;
using Whisper.net;

namespace Smarty.Api;

/// <summary>
/// Local speech-to-text via Whisper.net (whisper.cpp). The GGML model is downloaded once to disk and
/// cached; the factory is built lazily on first use. Expects 16 kHz mono PCM WAV input (the client
/// converts the recording before sending it).
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly string _modelPath;
    private readonly string _modelUrl;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperFactory? _factory;

    public WhisperTranscriber(string modelPath, string modelUrl)
    {
        _modelPath = modelPath;
        _modelUrl = modelUrl;
    }

    /// <summary>
    /// How the transcriber is actually asked to work — and it was asked for almost none of this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One option used to be set: <c>WithLanguage("auto")</c>, meaning "work out for yourself what language this is".
    /// Everything that made always-on listening unusable came out of that line, and it is worth writing down exactly,
    /// because the symptoms looked like everything except a configuration mistake. Handed five seconds of an empty room,
    /// this model with language=auto answers <b>ご視聴ありがとうございました!</b> — "thanks for watching" — in Japanese.
    /// That is not a metaphor for the bug, it IS the bug: the messages in another script that turned up in a
    /// conversation, and the "don't forget to like and subscribe" that arrived from nobody. A transcriber with no
    /// language decides, and on noise it decides wrongly and confidently, in whatever language its subtitle training
    /// data was thickest.
    /// </para>
    /// <para>
    /// Measured on the same recording of a quiet voice four metres from a laptop:
    /// </para>
    /// <list type="bullet">
    /// <item>as it was — 11.5s — "Hey Pip! What is the capital of France? * More Sound ]"</item>
    /// <item>as it is now — 3.6s — "Hey Pip. What is the capital of France?"</item>
    /// </list>
    /// <para>
    /// Three times faster and clean, from settings alone. Each one earns its place: a fixed LANGUAGE stops it inventing
    /// another; NO CONTEXT stops the last segment's words seeding the next one's guess, which is how a hallucination
    /// runs away; TEMPERATURE ZERO with no increment removes the "try again more creatively" fallback that produces
    /// stock phrases; and the THRESHOLDS are how it is allowed to answer "there was nothing there" instead of always
    /// producing something.
    /// </para>
    /// </remarks>
    private static WhisperProcessorBuilder Sober(WhisperProcessorBuilder builder, string? prompt) =>
        builder
            // Fixed, not guessed. This one line is the difference between a quiet room and a Japanese sign-off.
            .WithLanguage("en")
            .WithNoContext()
            .WithTemperature(0f)
            .WithTemperatureInc(0f)
            // Permission to say nothing. Without these it will always find words, and it does.
            .WithNoSpeechThreshold(0.5f)
            .WithEntropyThreshold(2.2f)
            .WithLogProbThreshold(-0.8f)
            /*
             * What it should expect to hear.
             *
             * The largest single improvement of the lot, and the cheapest: told that "Hey Pip." is a thing people say
             * to it, the same audio came back as "Hey Pip." rather than "a pep" — and in a third of the time, because a
             * confident decode does not go round the fallback loop. A wake word is exactly the case a prompt is for: a
             * short phrase, out of context, that the model has no reason to expect.
             */
            .WithPrompt(prompt is { Length: > 0 } ? prompt : string.Empty);

    /// <param name="expecting">
    /// What is likely to be said, when the caller knows — the assistant's own name, for always-on listening. Not a
    /// filter and not a constraint: it biases the decoding towards words that would otherwise be unlikely.
    /// </param>
    public async Task<string> TranscribeAsync(Stream wav, CancellationToken ct = default, string? expecting = null)
    {
        var factory = await GetFactoryAsync(ct).ConfigureAwait(false);
        using var processor = Sober(factory.CreateBuilder(), expecting).Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(wav, ct).ConfigureAwait(false))
            sb.Append(segment.Text);

        return sb.ToString().Trim();
    }

    private async Task<WhisperFactory> GetFactoryAsync(CancellationToken ct)
    {
        if (_factory is not null) return _factory;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_factory is null)
            {
                if (!File.Exists(_modelPath))
                    await DownloadModelAsync(ct).ConfigureAwait(false);

                _factory = WhisperFactory.FromPath(_modelPath);
            }
        }
        finally
        {
            _initLock.Release();
        }

        return _factory;
    }

    private async Task DownloadModelAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        var temp = _modelPath + ".download";
        using (var response = await http.GetAsync(_modelUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(temp);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        File.Move(temp, _modelPath, overwrite: true);
    }

    public void Dispose() => _factory?.Dispose();
}
