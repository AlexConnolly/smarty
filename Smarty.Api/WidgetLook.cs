using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>What looking at a panel concluded.</summary>
/// <param name="Working">Does it show what it is supposed to show?</param>
/// <param name="Seen">What is actually in the frame, in a line.</param>
/// <param name="Wrong">What is wrong with it, when something is.</param>
/// <param name="Fault">
/// WHICH PART is wrong — looks | data | source — when something is.
///
/// <para>
/// Worth asking for in the same breath as "is it working", because a build that can act on the answer needs to know
/// where to go back to, and the screen is the only place the three can be told apart. A clipped crest is the
/// component; a row of blanks is the mapping; an honest, well-laid-out score for the wrong club is the source. The
/// same "it's broken" sends the build to three different places.
/// </para>
/// </param>
public sealed record LookVerdict(bool Working, string Seen, string? Wrong, string? Fault = null);

/// <summary>
/// Looking at a panel to see whether it works.
///
/// <para>
/// The gap this fills is specific and was invisible to everything else. A panel is watched by two loops: the error
/// boundary, which catches a component that THROWS, and the loader check, which catches data that won't load. A
/// client-mode panel with a broken image trips neither — an <c>&lt;img&gt;</c> whose source 404s does not throw, React
/// renders it perfectly happily, and there is no server load to fail. The camera panel sat there showing a broken-image
/// icon and every mechanism in the system reported it healthy.
/// </para>
/// <para>
/// The only thing that can tell a working camera from a broken-image icon is a pair of eyes. So: render the panel on
/// its own, photograph it, and ask a vision model what it can see. It is the one check that tests the thing the user
/// actually experiences rather than a proxy for it — and it catches the whole family of failures that look fine to
/// code: a blank box, a spinner that never resolves, an image that didn't load, text spilling out of its frame.
/// </para>
/// </summary>
public sealed class WidgetLook
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;
    private readonly string _shotsDir;
    private readonly Action<string>? _trace;
    private readonly WidgetLibrary? _kinds;

    /// <summary>
    /// The vision model. The same one <see cref="Vision"/> settled on by testing rather than by reading a list —
    /// most of Together's vision models refuse without a dedicated endpoint, and this one answers.
    /// </summary>
    private const string Model = "google/gemma-3n-E4B-it";

    /// <summary>
    /// How long the panel gets to finish being itself before the photograph.
    ///
    /// <para>
    /// A panel is not done when the page has loaded. A client-mode one fetches after mount; an image takes as long as
    /// the network takes. Photographing at load time catches every panel mid-spinner and calls them all broken.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    private readonly IModelProvider _judge;
    private readonly string _judgeModel;

    /// <summary>
    /// How the photographed page gets past the password.
    ///
    /// <para>
    /// The page is a shell that reads its panel back through the API, and the API is locked. With no pass it renders
    /// "No panel &lt;id&gt;" — which the vision model then describes perfectly accurately, and every panel in the
    /// system is judged broken on the strength of it. A check that cannot see the panel must not be allowed to have
    /// an opinion about it.
    /// </para>
    /// </summary>
    private readonly OurPass _pass;

    /// <param name="kinds">
    /// The library, so a panel's declared model can be read before its picture is taken. Optional: without it the
    /// look is a photograph only, which is what it always was.
    /// </param>
    public WidgetLook(HttpClient http, Func<string?> apiKey, IModelProvider judge, string judgeModel,
        string baseUrl, string shotsDir, OurPass pass, Action<string>? trace = null, WidgetLibrary? kinds = null)
    {
        _http = http;
        _apiKey = apiKey;
        _judge = judge;
        _judgeModel = judgeModel;
        _baseUrl = baseUrl.TrimEnd('/');
        _shotsDir = shotsDir;
        _pass = pass;
        _trace = trace;
        _kinds = kinds;
    }

    /// <summary>
    /// What the photograph cannot see: whether the data behind the panel honours the model it was written against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The eyes were the only check that ran after a panel was published, and eyes read a rendering. A component
    /// given seven rows of nulls draws seven rows — correct spacing, correct icons, nothing in them — and the honest
    /// description of that screenshot is "a row of cloud icons and a series of circles", which reads as working. The
    /// same data says so plainly in one line, for nothing.
    /// </para>
    /// <para>
    /// Deliberately only the two things the data can prove: nothing arrived, or what arrived does not fill the model
    /// (<see cref="WidgetContract"/>, which already knows that every row being empty is a path and not a Tuesday).
    /// Everything else — a broken image, a chart the wrong way up, text off the edge — is still the picture's job,
    /// so a null here means "look at it", not "it is fine".
    /// </para>
    /// <para>
    /// Client mode is exempt: the component fetches in the page, so there is no server-side reading to judge and its
    /// absence proves nothing.
    /// </para>
    /// </remarks>
    public static LookVerdict? DataFault(Widget panel, WidgetKind? kind)
    {
        if (kind is null || kind.Model.Count == 0) return null;
        if (kind.Loader.Mode == LoaderModes.Client) return null;

        if (panel.Data is not { Length: > 0 } raw)
            return new LookVerdict(false, "no data reached the panel at all",
                "Nothing has ever loaded into this panel, so everything on screen is whatever the component draws " +
                "with no data. The loader is the fault, not the rendering.", "data");

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (WidgetContract.Broken(doc.RootElement, kind.Model, kind.Code) is { } wrong)
                return new LookVerdict(false, "the data behind it doesn't fill its model", wrong, "data");
        }
        catch (System.Text.Json.JsonException)
        {
            return new LookVerdict(false, "what loaded isn't readable",
                "The panel's stored data isn't valid JSON, so the component is rendering from nothing.", "data");
        }

        return null;
    }

    /// <summary>
    /// Photograph a panel and say whether it is doing its job.
    /// </summary>
    /// <returns>The verdict, or null if it couldn't be looked at at all — which is not the panel's fault.</returns>
    public async Task<LookVerdict?> AtAsync(Widget panel, string shouldShow, CancellationToken ct)
    {
        // Asked of the data before the picture, because a picture cannot answer it. A panel whose rows are all empty
        // renders a tidy column of icons and photographs as a working panel — which is exactly how a weather panel
        // with nothing in it was passed as "working" and then failed every refresh for nine hours. Cheaper, too: no
        // screenshot and no vision call for a fault that is legible in the JSON.
        if (DataFault(panel, _kinds?.Get(panel.Kind)) is { } fault)
        {
            _trace?.Invoke($"[look] {panel.Id} \"{panel.Title}\": BROKEN — {fault.Wrong} (from its data, unphotographed)");
            return fault;
        }

        var shot = Path.Combine(_shotsDir, $"widget-{panel.Id}.png");
        try
        {
            Directory.CreateDirectory(_shotsDir);
            if (!await ShootAsync(_pass.On($"{_baseUrl}/widget/{panel.Id}"), shot, ct).ConfigureAwait(false))
            {
                _trace?.Invoke($"[look] {panel.Id} couldn't be photographed");
                return null;
            }

            var verdict = await JudgeAsync(shot, panel.Title, shouldShow, ct).ConfigureAwait(false);
            if (verdict is not null)
                _trace?.Invoke($"[look] {panel.Id} \"{panel.Title}\": " +
                               (verdict.Working ? "working" : $"BROKEN — {verdict.Wrong}") +
                               $" (saw: {verdict.Seen})");
            return verdict;
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[look] {panel.Id} failed: {ex.Message}");
            return null;
        }
        finally
        {
            // The screenshot is evidence for one decision, not an archive.
            try { if (File.Exists(shot)) File.Delete(shot); } catch { }
        }
    }

    /// <summary>
    /// Headless Chrome, one panel, one PNG.
    ///
    /// <para>
    /// Headless rather than the user's own browser on purpose: this runs at the end of every build, and taking over
    /// their window to photograph something is exactly the intrusion the tab cleanup was about. The cost is that a
    /// panel needing their logged-in session cannot be judged this way — but a client-mode panel, which is the case
    /// that needs judging, never does.
    /// </para>
    /// </summary>
    private async Task<bool> ShootAsync(string url, string outPath, CancellationToken ct)
    {
        if (Thumbnails.ChromeExecutable() is not { } chrome) return false;

        var args = string.Join(' ',
            "--headless=new", "--disable-gpu", "--no-sandbox", "--hide-scrollbars",
            // Wait for everything the page asked for before drawing. Without it the shot is taken while the pictures
            // are still arriving, and a panel of photographs photographs as a panel of empty grey boxes — which is
            // then described accurately and judged broken. The check was failing panels for being slower than it.
            "--run-all-compositor-stages-before-draw",
            // Matches the panel's own box, so what is judged is what a large panel actually looks like.
            "--window-size=760,340",
            // The panel is on the same machine and may point at a device on the same network with its own certificate.
            "--ignore-certificate-errors", "--allow-insecure-localhost",
            $"--virtual-time-budget={(int)Settle.TotalMilliseconds}",
            $"--screenshot=\"{outPath}\"", $"\"{url}\"");

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = chrome,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        });
        if (process is null) return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Settle + TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } }

        return File.Exists(outPath) && new FileInfo(outPath).Length > 0;
    }

    /// <summary>
    /// Describe the frame, then judge the description. Two models, each doing the thing it is good at.
    ///
    /// <para>
    /// One call could do both, and the first version did — with the result that the vision model looked at a
    /// broken-image placeholder, wrote "a placeholder image with the text 'Live camera'", and in the same breath
    /// answered <c>working: true</c>. Its description was perfect. Its judgement was wrong, and no amount of
    /// instruction fixed that, because a small vision model is not where judgement lives.
    /// </para>
    /// <para>
    /// So the vision model only says what it can see, which is its strength, and the ordinary model decides whether
    /// that description is a working panel — which is a reading-comprehension question, and its strength. The
    /// description is kept either way, because it is the most legible thing on the record: "a grey box with a
    /// torn-page icon" tells a builder something no exception could.
    /// </para>
    /// </summary>
    private async Task<LookVerdict?> JudgeAsync(string shot, string title, string shouldShow, CancellationToken ct)
    {
        var seen = await DescribeAsync(shot, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(seen)) return null;

        var working = await DecideAsync(title, shouldShow, seen!, ct).ConfigureAwait(false);
        // Undecidable is not the panel's fault, so it is not held against it — but what was seen is still recorded.
        if (working is null) return new LookVerdict(true, seen!, null);

        return working.Value.Working
            ? new LookVerdict(true, seen!, null)
            : new LookVerdict(false, seen!, working.Value.Wrong, working.Value.Fault);
    }

    /// <summary>What is in the frame. No judgement asked for, and none wanted.</summary>
    private async Task<string?> DescribeAsync(string shot, CancellationToken ct)
    {
        if (_apiKey() is not { Length: > 0 } key) return null;

        var image = "data:image/png;base64," +
                    Convert.ToBase64String(await File.ReadAllBytesAsync(shot, ct).ConfigureAwait(false));

        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = 300,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text =
                                "Describe exactly what you can see in this image, in two or three sentences. It is a " +
                                "screenshot of one panel from a web page.\n" +
                                "Be literal and specific. Read out any text or numbers you can see, verbatim. If " +
                                "there is a photograph or video frame, say what is in it. If a picture failed to " +
                                "load — a grey or white box with a small torn-page or picture symbol, or an alt-text " +
                                "caption where an image should be — say exactly that. If the box is empty, say it is " +
                                "empty. Do not say whether it is working or broken; only describe.",
                        },
                        new { type = "image_url", image_url = new { url = image } },
                    },
                },
            },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.together.xyz/v1/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        // Together refuses a request with no User-Agent, with nothing to say the header is the problem.
        request.Headers.TryAddWithoutValidation("User-Agent", "smarty/1.0");
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _trace?.Invoke($"[look] the vision model answered {(int)response.StatusCode}");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString()?.Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// Given what was seen and what the panel is for, is it working? Answered by the ordinary model, schema-forced.
    /// </summary>
    private async Task<(bool Working, string? Wrong, string? Fault)?> DecideAsync(string title, string shouldShow, string seen,
        CancellationToken ct)
    {
        var request = new ModelRequest
        {
            Model = _judgeModel,
            Messages = new List<Message>
            {
                Message.System(
                    "Someone photographed one panel of a home page and described what they could see. Decide whether " +
                    "the panel is doing its job.\n\n" +
                    $"The panel is called \"{title}\".\n" +
                    $"It is supposed to show: {shouldShow}\n\n" +
                    $"What is actually on the screen:\n{seen}\n\n" +
                    "BROKEN if the description mentions: an image that failed to load, a placeholder, a broken-image " +
                    "or torn-page symbol, alt text showing where a picture should be, an empty or blank box, a " +
                    "loading spinner or the word Loading, an error message, or text cut off mid-word.\n" +
                    "WORKING if it describes real content of the kind the panel is for — actual numbers, real rows of " +
                    "text, a photograph or video frame with something in it.\n" +
                    "A panel that is legitimately reporting nothing right now (\"nothing on today\", \"no flights\") " +
                    "is WORKING: it is answering the question.\n\n" +
                    "Be strict. A description that mentions a placeholder or a caption where a picture should be is " +
                    "broken, however tidy the panel looks around it.\n\n" +
                    "If it is broken, say WHICH PART is at fault, because that decides what gets rebuilt:\n" +
                    "  looks  — it is laid out wrong: clipped, overflowing, squeezed, an empty frame, text cut " +
                    "off mid-word. The component is at fault.\n" +
                    "  data   — the layout is right and the values are missing, blank, undefined or nonsense. " +
                    "The mapping from the source is at fault.\n" +
                    "  source — it renders real, tidy values and they are about the wrong thing, or they are " +
                    "plainly out of date. The source is at fault.\n" +
                    "Leave it empty if you genuinely cannot tell — a guess here rebuilds the wrong half."),
            },
            Think = false,
            ResponseFormat = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["working"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean" },
                    ["wrong"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "What is wrong with it, as a SENTENCE someone could act on — not a " +
                                          "category, and never one of the fault words below. Empty when working.",
                    },
                    ["fault"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "string",
                        // Constrained, because the first version asked for one of three words in prose and got them
                        // in the WRONG FIELD: a working panel came back with wrong="source", which is not a sentence,
                        // is not a fault anyone can act on, and is what then got written onto the panel as its error.
                        ["enum"] = new System.Text.Json.Nodes.JsonArray { "looks", "data", "source", "" },
                        ["description"] = "Which part is at fault. Empty when it is working, or when you cannot tell. " +
                                          "This is the ONLY field that takes one of these words.",
                    },
                },
                ["required"] = new System.Text.Json.Nodes.JsonArray { "working", "wrong" },
            },
            MaxOutputTokens = 200,
            TurnTimeout = TimeSpan.FromSeconds(45),
        };

        try
        {
            var answer = await _judge.CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(answer.Content)) return null;

            int first = answer.Content.IndexOf('{'), last = answer.Content.LastIndexOf('}');
            if (first < 0 || last <= first) return null;

            using var doc = JsonDocument.Parse(answer.Content[first..(last + 1)]);
            if (!doc.RootElement.TryGetProperty("working", out var working) ||
                working.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            var wrong = doc.RootElement.TryGetProperty("wrong", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString()?.Trim()
                : null;

            var fault = doc.RootElement.TryGetProperty("fault", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()?.Trim().ToLowerInvariant()
                : null;

            return (working.GetBoolean(), string.IsNullOrWhiteSpace(wrong) ? null : wrong,
                string.IsNullOrWhiteSpace(fault) ? null : fault);
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[look] couldn't judge what was seen: {ex.Message}");
            return null;
        }
    }
}
