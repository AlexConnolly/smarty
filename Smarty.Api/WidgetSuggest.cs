using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Noticing that a panel would be useful, and offering it.
///
/// <para>
/// The half of the home page that was built and never wired. A proposal has a card, an accept-with-edits sheet and a
/// decline — but <see cref="WidgetStatus.Proposed"/> was only ever set by a parameter nothing passed as true, so
/// nothing had ever suggested anything. The page could only ever contain what someone had explicitly asked for.
/// </para>
/// <para>
/// What makes it worth having is that this system knows things a website doesn't: where the user is right now, what is
/// on their lists this week, what they have been talking about, and which panels they already keep. A suggestion drawn
/// from those is the difference between a dashboard you configure and one that notices.
/// </para>
/// <para>
/// Deliberately shy. One outstanding proposal at a time, one look per interval, and "nothing worth suggesting" is an
/// answer it is told explicitly to give — because a page that offers something every hour is a page that gets ignored,
/// and a declined suggestion costs more trust than a missed one.
/// </para>
/// </summary>
/// <summary>
/// An idea that was offered, and what became of it.
/// </summary>
public sealed class SuggestionRecord
{
    public string Title { get; set; } = "";
    public string Why { get; set; } = "";

    /// <summary>offered | accepted | declined.</summary>
    public string Outcome { get; set; } = "offered";

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// What has been offered before, and how it landed.
///
/// <para>
/// The memory used to be a set of declined titles held in RAM, which meant two things: an idea turned down on Monday
/// came back on Tuesday if the process had restarted in between, and nothing about what was ACCEPTED reached the prompt
/// at all. Both halves are signal — a decline says something about what this person does not want, an acceptance says
/// what they do — and throwing them away is what makes a suggester repeat itself.
/// </para>
/// </summary>
public sealed class SuggestionLog
{
    /// <summary>Enough to be informative without turning the prompt into a changelog.</summary>
    private const int Keep = 40;

    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<SuggestionRecord> _records = new();
    private readonly object _lock = new();

    public SuggestionLog(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path))
                _records = JsonSerializer.Deserialize<List<SuggestionRecord>>(File.ReadAllText(path), json) ?? new();
        }
        catch { _records = new(); }
    }

    public IReadOnlyList<SuggestionRecord> All()
    {
        lock (_lock) return _records.ToList();
    }

    public void Offered(string title, string why)
    {
        lock (_lock)
        {
            _records.RemoveAll(r => string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase));
            _records.Add(new SuggestionRecord
            {
                Title = title.Trim(),
                Why = why.Trim(),
                Outcome = "offered",
                At = DateTimeOffset.UtcNow,
            });
            if (_records.Count > Keep) _records.RemoveRange(0, _records.Count - Keep);
            Save();
        }
    }

    /// <summary>What the user did about it. An unknown title is ignored rather than invented.</summary>
    public void Answered(string title, bool accepted)
    {
        lock (_lock)
        {
            var record = _records.LastOrDefault(r => string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase));
            if (record is null) return;
            record.Outcome = accepted ? "accepted" : "declined";
            record.At = DateTimeOffset.UtcNow;
            Save();
        }
    }

    /// <summary>True when this exact idea has already been turned down.</summary>
    public bool WasDeclined(string title)
    {
        lock (_lock)
            return _records.Any(r => r.Outcome == "declined"
                                     && string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The history as the model should read it: decisions, not a diary.
    /// </summary>
    public string Describe()
    {
        lock (_lock)
        {
            var settled = _records.Where(r => r.Outcome is "accepted" or "declined").TakeLast(12).ToList();
            if (settled.Count == 0) return "";

            var lines = settled.Select(r =>
                $"- \"{r.Title}\" — {(r.Outcome == "accepted" ? "they said yes" : "they turned it down")}" +
                $" ({r.At.ToLocalTime():d MMM})");

            return "\n\nIdeas you have already put to them, and how each landed:\n" + string.Join("\n", lines) +
                   "\nDo not offer a declined idea again, even reworded. Read the pattern rather than the list: what " +
                   "they said yes to is the shape of thing worth offering, and what they turned down is a direction to " +
                   "stop going in.\n";
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_records, _json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[suggest] couldn't save the log: {ex.Message}"); }
    }
}

public sealed class WidgetSuggester
{
    private readonly WidgetStore _widgets;
    private readonly WidgetLibrary _kinds;
    private readonly IModelProvider _model;
    private readonly string _modelName;
    private readonly Func<string> _context;
    private readonly Action<string>? _trace;
    private readonly TimeSpan _every;

    /// <summary>What has been offered and how it landed. Persisted, so a restart does not forget a refusal.</summary>
    private readonly SuggestionLog _log;

    private DateTimeOffset _lastLooked = DateTimeOffset.MinValue;

    public WidgetSuggester(WidgetStore widgets, WidgetLibrary kinds, IModelProvider model, string modelName,
        Func<string> context, SuggestionLog log, TimeSpan? every = null, Action<string>? trace = null)
    {
        _widgets = widgets;
        _kinds = kinds;
        _log = log;
        _model = model;
        _modelName = modelName;
        _context = context;
        _every = every ?? TimeSpan.FromHours(4);
        _trace = trace;
    }

    /// <summary>
    /// Record what the user did about an offer, so the next look is better informed.
    /// </summary>
    /// <param name="why">
    /// The offer's own reason, used only when this proposal is not already in the log — a panel proposed before the log
    /// existed, or by something other than this suggester. The proposal was real and the answer was real, so recording
    /// both is accurate rather than invented, and without it a decline of such an offer is forgotten entirely.
    /// </param>
    public void Answered(string title, bool accepted, string? why = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        if (!_log.All().Any(r => string.Equals(r.Title, title.Trim(), StringComparison.OrdinalIgnoreCase)))
            _log.Offered(title, why ?? "");

        _log.Answered(title, accepted);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // A first look shortly after start rather than immediately: the panels and lists are still loading, and a
        // suggestion made from an empty page would be about nothing.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await LookAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _trace?.Invoke($"[suggest] {ex.Message}"); }

            try { await Task.Delay(TimeSpan.FromMinutes(20), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Consider suggesting something. Returns the proposal, or null when there is nothing worth offering.
    /// </summary>
    public async Task<Widget?> LookAsync(CancellationToken ct, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastLooked < _every) return null;

        // One at a time. An unanswered offer means the last one has not been read yet, and stacking them turns a
        // suggestion into a queue of chores.
        if (_widgets.All().Any(w => w.Status == WidgetStatus.Proposed)) return null;

        _lastLooked = DateTimeOffset.UtcNow;

        // Logged before the call, because a decision you cannot see the inputs to is a decision you cannot debug —
        // and the first time this declined, the only way to find out why was to reconstruct the context by hand.
        var context = _context();
        var history = _log.Describe();

        // Both halves in the trace, because a refusal you cannot see the inputs to is a refusal you cannot debug —
        // and the history is precisely the input meant to stop it repeating itself.
        _trace?.Invoke($"[suggest] looking at: {context.Replace('\n', '|')}");
        if (history.Length > 0) _trace?.Invoke($"[suggest] history: {history.Replace('\n', '|')}");

        var decision = await AskAsync(context, history, ct).ConfigureAwait(false);
        if (decision is null || !decision.Value.Suggest)
        {
            _trace?.Invoke("[suggest] nothing worth offering");
            return null;
        }

        var (_, title, why, size, kind) = decision.Value;

        // Made of words, not punctuation. The whitespace check let "..." through — a title and a reason that were
        // both literally an ellipsis — and the card rendered "Build ... for your home page?" with a "..." underneath.
        // This is validation, not reading intent: an offer nobody could act on is not an offer.
        if (!Sayable(title, 3) || !Sayable(why, 12))
        {
            _trace?.Invoke($"[suggest] discarded an empty offer (title {Quote(title)}, why {Quote(why)})");
            return null;
        }

        if (_log.WasDeclined(title!))
        {
            _trace?.Invoke($"[suggest] {Quote(title)} was turned down before — not offering it again");
            return null;
        }

        // Already there under another name is the common failure, so it is checked here rather than trusted to the
        // model: a second weather panel is not a suggestion.
        if (_widgets.All().Any(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase))) return null;

        var proposal = _widgets.Reserve(title.Trim(), size ?? WidgetSizes.Kpi, 60, why.Trim(), null, proposed: true);
        _log.Offered(title!, why!);

        // A kind it can be made from means accepting is instant rather than a build — worth knowing, and worth saying
        // in the offer.
        if (kind is { Length: > 0 } && _kinds.Get(kind) is not null)
            _trace?.Invoke($"[suggest] \"{title}\" — from the existing {kind} kind");
        else
            _trace?.Invoke($"[suggest] \"{title}\" — would need building");

        return proposal;
    }

    /// <summary>
    /// Is this something a person could read? Long enough, and containing actual letters.
    /// </summary>
    private static bool Sayable(string? text, int minimum)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length >= minimum && trimmed.Any(char.IsLetter);
    }

    private static string Quote(string? text) => text is null ? "null" : $"\"{text}\"";

    private async Task<(bool Suggest, string? Title, string? Why, string? Size, string? Kind)?> AskAsync(
        string context, string history, CancellationToken ct)
    {
        var request = new ModelRequest
        {
            Model = _modelName,
            Messages = new List<Message>
            {
                Message.System(
                    "You look after someone's home page: a grid of small panels, each showing one live thing. Decide " +
                    "whether to OFFER them one more panel.\n\n" +
                    "Here is what you know about them right now:\n" + context + "\n" +
                    // Both halves matter: a decline is a direction to stop going in, an acceptance is the
                    // shape of thing worth offering. Without them it has no memory and repeats itself.
                    history + "\n" +
                    "Suggest something ONLY if it is clearly useful to this person, today, given the above — their " +
                    "location, what is on their lists, what they have been doing. A panel is worth offering when it " +
                    "answers something they would otherwise check manually and repeatedly.\n\n" +
                    "The test is WHOSE it is, not what shape it is. A panel built from their own situation is the " +
                    "whole point: the dinner plan they are part-way through this week, a project with a date coming " +
                    "up, something they are selling, a journey they keep making. Any of those beats anything generic, " +
                    "and \"it is a list\" is not a reason to reject one — their list is not a stranger's list.\n" +
                    "Do NOT suggest: anything they already have a panel for, however differently worded; something " +
                    "that would suit anybody equally (a clock, a generic news feed, an empty to-do widget); anything " +
                    "you cannot imagine a live source for. Note that their own lists, projects and agenda ARE a live " +
                    "source — there is an internal feed for them, so a panel over their own data needs no external " +
                    "site at all and is the cheapest good suggestion there is.\n\n" +
                    "\"Nothing worth suggesting\" is a good answer and you should give it whenever nothing is " +
                    "CLEARLY useful — say suggest=false and nothing else. But judge it against what is actually in " +
                    "front of you: a nearly empty home page belonging to someone whose city you know almost always " +
                    "has one obvious gap worth offering, and declining then is not caution, it is just unhelpful. Be " +
                    "specific to them rather than safe.\n\n" +
                    "If you cannot name the thing concretely — an actual title and an actual reason in real words — " +
                    "then the answer is suggest=false. Say that and stop. Do not answer suggest=true with placeholder " +
                    "text: one that came back as a title of \"...\" and a reason of \"...\" rendered on the home page " +
                    "as \"Build ... for your home page?\", which is worse than no offer at all.\n" +
                    "If you do suggest something: give it a short title, one sentence on why it is useful TO THEM " +
                    "specifically (this is shown to them, so make it about their situation, not about panels), a size " +
                    "— kpi for one number, tall for a few rows, wide for a list or chart — and the name of an " +
                    "existing kind if one obviously fits."),
            },
            Think = false,
            ResponseFormat = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["suggest"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean" },
                    ["title"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                    ["why"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                    ["size"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                    ["kind"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                },
                // ONLY suggest is required.
                //
                // Marking title and why required meant a decision to decline still had to emit them, so it filled
                // them with placeholders — and a proposal reading "Build ... for your home page?" appeared on the
                // home page overnight. A field that is meaningless when the answer is no should not be demanded.
                ["required"] = new System.Text.Json.Nodes.JsonArray { "suggest" },
            },
            MaxOutputTokens = 300,
            TurnTimeout = TimeSpan.FromSeconds(60),
        };

        try
        {
            var answer = await _model.CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(answer.Content)) return null;

            int first = answer.Content.IndexOf('{'), last = answer.Content.LastIndexOf('}');
            if (first < 0 || last <= first) return null;

            using var doc = JsonDocument.Parse(answer.Content[first..(last + 1)]);
            var root = doc.RootElement;

            if (!root.TryGetProperty("suggest", out var suggest) ||
                suggest.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            string? Read(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()?.Trim()
                    : null;

            var size = Read("size");
            return (suggest.GetBoolean(), Read("title"), Read("why"),
                WidgetSizes.Known(size) ? size!.ToLowerInvariant() : WidgetSizes.Kpi, Read("kind"));
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[suggest] couldn't decide: {ex.Message}");
            return null;
        }
    }
}
