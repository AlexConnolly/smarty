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

    public async Task<string> TranscribeAsync(Stream wav, CancellationToken ct = default)
    {
        var factory = await GetFactoryAsync(ct).ConfigureAwait(false);
        using var processor = factory.CreateBuilder().WithLanguage("auto").Build();

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
