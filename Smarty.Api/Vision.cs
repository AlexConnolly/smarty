using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Looking at a picture, rather than guessing from its filename.
/// <para>
/// Every image tool so far hands back something the model cannot see — "the image is shown to the user, you cannot
/// see it" — so pictures were chosen by alt text and filename and hope. That is how a company logo and a stock
/// photo of a different pool ended up in a brochure. This asks a vision model what is actually in the frame.
/// </para>
/// <para>
/// The cost problem is real and the answer is not a budget that can be argued with. A page can carry four hundred
/// images; describing them all would cost a few pounds and a supervisor asked "is this progressing?" would happily
/// wave it through, exactly as it waved through two hundred wasted page reads. So instead:
/// </para>
/// <para>
/// • A CACHE keyed on the image address, so describing the same picture twice is free. This is the important one —
///   it makes a loop cost nothing, which no per-call limit can achieve. A run that calls this forty times on four
///   pictures pays for four.
/// • A HARD CAP on distinct images per run, never extended. Not to save money — twenty images is about two pence —
///   but so a runaway cannot invent work.
/// • And the caller is pushed to filter FIRST: chrome_images already returns biggest-first with sizes, so the
///   sensible flow is to narrow to a handful of plausible photographs and then look at those.
/// </para>
/// </summary>
public static class Vision
{
    /// <summary>
    /// The vision model, chosen by testing rather than by reading a list.
    /// <para>
    /// Most of Together's vision models — the Llama 3.2 Vision pair, Qwen2-VL, Qwen3-VL, Llama 4 Scout — answer
    /// "unable to access non-serverless model: create a dedicated endpoint", which means renting a GPU by the hour
    /// whether it is used or not. Advertised per-token pricing does not tell you which: Qwen3-VL-8B lists at
    /// $0.18/M and still refuses. The only reliable test is to send it an image.
    /// </para>
    /// <para>
    /// This one is serverless and the cheapest of them at $0.06/M in, $0.12/M out — a photograph costs about three
    /// hundred tokens, so a fiftieth of a penny. Cost is genuinely not the constraint; the cache and the cap are
    /// there to stop a runaway inventing work, not to save money.
    /// </para>
    /// </summary>
    private const string Model = "google/gemma-3n-E4B-it";

    /// <summary>Distinct images one run may look at. Repeats don't count — they never reach the model.</summary>
    private const int DistinctImagesPerRun = 20;


    // Keyed on the image address and shared across runs: the same hotel photograph recurs between attempts and
    // between chats, and it should be paid for once rather than once per try.
    private static readonly Dictionary<string, string> Seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// What a bare <c>describe_image</c> (no question) asks the vision model.
    /// <para>
    /// This used to ask for "one or two sentences" plus whether the picture was a logo, a map or a stock shot —
    /// a VETTING prompt, written for checking an image before putting it in a brochure, where it works well. But
    /// the tool invites a bare call for "a plain description", and a description is not what it returned: asked
    /// to write about a photo of someone, the model got back "a man standing in front of a hair salon", learned
    /// almost nothing, and immediately asked again with a real question. The cache keys on the question, so the
    /// second look was a full-price call — the image was paid for twice, nearly every time the intent was to see
    /// the picture rather than police it.
    /// </para>
    /// <para>
    /// So the default now actually describes, and keeps the stock/logo/map flag as a rider rather than the whole
    /// job — one call that satisfies both reasons for asking.
    /// </para>
    /// </summary>
    internal const string DefaultPrompt =
        "Describe this image: what is in it, the setting, and any text or signage you can read. Be specific and " +
        "concrete — names, colours, what people are wearing and doing, what the place looks like — because the " +
        "description is all the reader gets; they cannot see the picture. Keep it to a short paragraph. Then, if " +
        "it is a logo, a map, a screenshot, an illustration, a stock photograph or a placeholder rather than a " +
        "genuine photograph of a real place, say so plainly at the end.";

    /// <param name="mediaDir">Where stored images live, so a /api/media/… address can be read from disk instead of
    /// fetched back over the network.</param>
    /// <param name="filesDir">The conversation's own library — where an image the USER attached is kept. Without
    /// this, the one case a person is most likely to ask about ("what's in this screenshot?") is the one case that
    /// cannot be answered: the model is told the file's name, and a name resolves to nothing.</param>
    public static AgentTool Tool(string mediaDir, string? filesDir = null)
    {
        // Per-run, because the tool is built per task. Distinct images only.
        var describedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new AgentTool(
            "describe_image",
            "Look at an image and say what is actually in it. Takes the name of a file in this conversation (an " +
            "image the user attached — pass the name you were given), the src from chrome_images, or a stored " +
            "/api/media/… address. This is the ONLY way to see inside an image: file_summary reads text, not " +
            "pictures. Use it to CHECK a picture before you put it in front of the user — that it " +
            "shows the place you think it does, and not a logo, a map, a stock shot or someone else's hotel. Its " +
            "answer also makes good alt text. Narrow down first: chrome_images gives you sizes, biggest first, so " +
            "pick the two or three plausible photographs and look at those rather than working through the page. " +
            $"You may look at {DistinctImagesPerRun} different images in a run; asking about the same one again is " +
            "free and returns the same answer.",
            new[]
            {
                ToolParameter.String(
                    "src",
                    "The image: a file name from this conversation, a src from chrome_images, or /api/media/…",
                    required: true),
                ToolParameter.String(
                    "question",
                    "What you actually need to know, e.g. \"is this the exterior of a beach hotel?\". Ask it on " +
                    "the FIRST call — a second look at the same image with a different question is a second " +
                    "full-price call, so one good question beats a vague one followed by a real one. Omit it " +
                    "only when you want a general description, which is what you'll get.",
                    required: false),
            },
            async (args, ct) =>
            {
                var src = args.GetStringOrNull("src")?.Trim();
                if (string.IsNullOrWhiteSpace(src)) return ToolOutput.Error("describe_image needs a src.");

                var question = args.GetStringOrNull("question")?.Trim();
                var cacheKey = src + "|" + (question ?? "");

                lock (Gate)
                    if (Seen.TryGetValue(cacheKey, out var already))
                        return ToolOutput.Ok(already + "\n(Already looked at — this cost nothing.)");

                if (!describedThisRun.Contains(src) && describedThisRun.Count >= DistinctImagesPerRun)
                    return ToolOutput.DeadEnd(
                        $"You have already looked at {DistinctImagesPerRun} different images in this run, which is " +
                        "the limit. Use what you have: pick the best of the ones you have already checked, or go " +
                        "with the largest image whose alt text names your subject.");

                var key = ModelRouting.ApiKey();
                if (string.IsNullOrWhiteSpace(key))
                    return ToolOutput.DeadEnd("No vision model is configured on this instance.");

                try
                {
                    var dataUrl = await AsDataUrl(src, mediaDir, filesDir, ct).ConfigureAwait(false);
                    if (dataUrl is null)
                        return ToolOutput.Error(
                            $"Couldn't read the image at {src}. If it's a file in this conversation, pass its " +
                            "name exactly as given (list_files shows them); if it's from a page, pass the src " +
                            "chrome_images returned.");

                    var prompt = string.IsNullOrWhiteSpace(question) ? DefaultPrompt : question!;

                    var payload = JsonSerializer.Serialize(new
                    {
                        model = Model,
                        // Room for the fuller default description. The old 220 was sized for a two-sentence
                        // verdict, and a ceiling that truncates the answer is another reason to ask again.
                        max_tokens = 400,
                        messages = new object[]
                        {
                            new
                            {
                                role = "user",
                                content = new object[]
                                {
                                    new { type = "text", text = prompt },
                                    new { type = "image_url", image_url = new { url = dataUrl } },
                                },
                            },
                        },
                    });

                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        "https://api.together.xyz/v1/chat/completions");
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    // Together rejects a request with no User-Agent outright — 403, with nothing to say it is
                    // about the header rather than the key or the model. Measured: identical call, UA present
                    // succeeds, UA absent forbidden.
                    request.Headers.TryAddWithoutValidation("User-Agent", "smarty/1.0");
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ToolOutput.Error($"The vision model answered {(int)response.StatusCode}: {Head(body)}");

                    using var doc = JsonDocument.Parse(body);
                    var text = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString()?.Trim();

                    if (string.IsNullOrWhiteSpace(text))
                        return ToolOutput.Error("The vision model returned nothing.");

                    describedThisRun.Add(src);
                    lock (Gate) Seen[cacheKey] = text!;
                    return ToolOutput.Ok(text!);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return ToolOutput.Error($"Couldn't look at that image: {ex.Message}");
                }
            });
    }

    /// <summary>
    /// The image as a data URL. A stored /api/media/… address is read off disk — it is already here, and asking the
    /// network for a file we wrote ourselves would be daft. Anything else is fetched with browser-ish headers,
    /// because the hosts that refuse a bare client are exactly the ones whose photographs we want.
    /// </summary>
    private static async Task<string?> AsDataUrl(string src, string mediaDir, string? filesDir, CancellationToken ct)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return src;

        // Anything on this machine — a stored /api/media/… address, a file the user attached, a path another tool
        // wrote. Checked before the URL parse on purpose: a bare Windows path parses happily as an absolute URI
        // with the file scheme, and would then be "fetched" rather than read.
        if (ResolveLocal(src, mediaDir, filesDir) is { } local)
        {
            var bytes = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
            if (bytes.Length is 0 or > 12 * 1024 * 1024) return null;
            return "data:" + MimeFor(local) + ";base64," + Convert.ToBase64String(bytes);
        }

        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/128.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", uri.GetLeftPart(UriPartial.Authority));

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (data.Length is 0 or > 12 * 1024 * 1024) return null;
        return "data:" + mime + ";base64," + Convert.ToBase64String(data);
    }

    /// <summary>
    /// The image as a path on this machine, or null if it isn't one.
    /// <para>
    /// Three ways it can be named, and the model may use any of them: the bare file name it was told the user
    /// attached, an /api/media/… address of something stored earlier, or a full path another tool produced. The
    /// directory candidates are searched by FILE NAME only, so a name can't walk out of them; a rooted path is
    /// taken as given, which is what read_file and file_summary already do.
    /// </para>
    /// </summary>
    public static string? ResolveLocal(string src, string mediaDir, string? filesDir)
    {
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Except an /api/media/… address, which is ours however it's spelled.
            if (!src.Contains("/api/media/", StringComparison.OrdinalIgnoreCase)) return null;
        }

        var name = Path.GetFileName(src.Split('?')[0]);
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (Path.IsPathRooted(src) && File.Exists(src)) return src;

        foreach (var dir in new[] { filesDir, mediaDir })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        _ => "image/jpeg",
    };

    private static string Head(string s) => s.Length <= 200 ? s : s[..200];
}
