using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// A guide to doing a kind of job, written by the run that last did it.
///
/// <para>
/// Across 76 recorded runs the same shapes come round and round: seven eBay listings, five Gmail sends, two
/// Ocado shops. Every one worked the procedure out from nothing. One listing cost 417 tool calls and 7.4 million
/// tokens; one email cost 617 calls. The transcript knew the answer each time and was never asked.
/// </para>
/// <para>
/// The name is the generic shape — "list an item on eBay", never "list a signed vinyl record" — because the
/// whole point is that the next Switch listing finds what the record listing learned.
/// </para>
/// </summary>
public sealed class TaskGuide
{
    /// <summary>The generic job this covers. Also its identity: writing the same name again updates it.</summary>
    public string Name { get; set; } = "";

    /// <summary>How to do it and what to avoid, as one piece of prose a worker can read and follow.</summary>
    public string Description { get; set; } = "";

    /// <summary>The run it was last written or revised by, so a claim can be checked against what happened.</summary>
    public string LearnedFrom { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>How many runs have contributed to it. A guide revised five times has been through five sites.</summary>
    public int Revisions { get; set; }

    /// <summary>
    /// Runs that followed it and failed, since the last one that followed it and didn't.
    ///
    /// <para>
    /// A guide is a claim about a site, and sites change. Nothing else in here can notice that the claim has gone
    /// wrong: the write-up only runs on a success, so a guide that walks the next three runs into a wall is never
    /// revisited and is handed out again each time. One failure proves nothing — a run fails for its own reasons
    /// — so this counts, and a guide that has missed twice running is dropped. A wrong procedure is worse than
    /// no procedure, because a worker with none looks at the page.
    /// </para>
    /// </summary>
    public int Misses { get; set; }
}

/// <summary>The guides learned so far, on disk beside everything else this remembers.</summary>
public sealed class TaskHintStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<TaskGuide> _guides = new();
    private readonly object _lock = new();

    public TaskHintStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path))
                _guides = JsonSerializer.Deserialize<List<TaskGuide>>(File.ReadAllText(path), json) ?? new();
        }
        catch { _guides = new(); }
    }

    public IReadOnlyList<TaskGuide> All()
    {
        lock (_lock) return _guides.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A guide's whole budget, in characters.
    ///
    /// <para>
    /// About six short steps. The first one written ran to 540 characters and spent most of them on the particular
    /// job it came from — which holidays were in the table, which of them Scotland does differently — none of which
    /// is true of the next job and all of which has to be read before the useful sentence is reached. Past this it
    /// stops being a head start and becomes a document, and a document at the top of a prompt is a document nobody
    /// reads. Held rather than trimmed, the way <see cref="Brain"/> holds a fact: half a procedure is worse than
    /// none, because it reads as complete.
    /// </para>
    /// </summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>Guides that miss this many runs running are dropped rather than handed out again.</summary>
    public const int MaxMisses = 2;

    /// <summary>
    /// Write a guide, replacing one of the same name. False if it was refused for length.
    /// <para>
    /// Name is identity on purpose: a run that has just done the job again knows more than the one before it,
    /// and two guides for the same job is how a worker ends up choosing between a fresh answer and a stale one.
    /// A revision starts the miss count over — whatever was wrong about it has just been rewritten.
    /// </para>
    /// </summary>
    public bool Upsert(string name, string description, string learnedFrom)
    {
        name = (name ?? "").Trim();
        description = (description ?? "").Trim();
        if (name.Length == 0 || description.Length == 0) return false;
        if (description.Length > MaxDescriptionLength) return false;

        lock (_lock)
        {
            var existing = _guides.FirstOrDefault(g =>
                string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Description = description;
                existing.LearnedFrom = learnedFrom;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.Revisions++;
                existing.Misses = 0;
            }
            else
            {
                _guides.Add(new TaskGuide
                {
                    Name = name,
                    Description = description,
                    LearnedFrom = learnedFrom,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Revisions = 1,
                });
            }
            Save();
        }
        return true;
    }

    /// <summary>The guide by name, or null. For showing a write-up what it is revising.</summary>
    public TaskGuide? Get(string name)
    {
        lock (_lock)
            return _guides.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A run followed these guides and failed. Returns the ones that were dropped for missing once too often.
    /// <para>
    /// No model is asked whether the guide was to blame, because the counter answers it more cheaply: a run that
    /// failed for its own reasons leaves one miss behind and the next success clears it, while a guide that is
    /// genuinely wrong about the site keeps failing and is gone in two.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Missed(IEnumerable<string> names)
    {
        var dropped = new List<string>();
        lock (_lock)
        {
            foreach (var name in names)
            {
                var g = _guides.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (g is null) continue;
                if (++g.Misses < MaxMisses) continue;

                _guides.Remove(g);
                dropped.Add(g.Name);
            }
            if (names.Any()) Save();
        }
        return dropped;
    }

    /// <summary>A run followed these guides and succeeded, so whatever went wrong before wasn't them.</summary>
    public void Held(IEnumerable<string> names)
    {
        lock (_lock)
        {
            bool changed = false;
            foreach (var name in names)
            {
                var g = _guides.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (g is null || g.Misses == 0) continue;
                g.Misses = 0;
                changed = true;
            }
            if (changed) Save();
        }
    }

    /// <summary>
    /// The guides worth handing to a worker about to do this job.
    ///
    /// <para>
    /// Scored on words shared with the task, not asked of a model: matching "list the Switch on eBay" to "list an
    /// item on eBay" is what a scorer is for, and a round trip to decide whether to save a round trip is a poor
    /// trade. Service names and verbs carry the signal, so a shared uncommon word counts for much more than a
    /// shared common one.
    /// </para>
    /// </summary>
    public IReadOnlyList<TaskGuide> Relevant(string task, int limit = 2)
    {
        if (string.IsNullOrWhiteSpace(task)) return Array.Empty<TaskGuide>();

        var wanted = Words(task);
        if (wanted.Count == 0) return Array.Empty<TaskGuide>();

        lock (_lock)
        {
            return _guides
                .Select(g => (Guide: g, Name: Words(g.Name)))
                .Select(x => (x.Guide, x.Name, Score: Overlap(wanted, x.Name)))
                .Where(x => x.Score >= Enough(x.Name.Count))
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => x.Guide)
                .ToList();
        }
    }

    /// <summary>
    /// How much of a guide's OWN name has to turn up before it counts as the same job.
    /// </summary>
    /// <remarks>
    /// Two shared words used to be the bar, and that reads as a subject only when the thing being matched is a
    /// sentence. It isn't always: a panel build hands its whole step brief in as the description, and against
    /// two hundred words almost any guide clears two. That is how a job to restyle a vacuum panel was handed
    /// "find things to do in London on a specific date" and "find a place on Google Maps" — which is not merely
    /// wasted prompt, it is a worker being pointed at a browser it had no reason to open.
    /// <para>
    /// Scoring against the GUIDE's length instead of the task's makes the test independent of how much text it
    /// is matched against: most of what a guide is called has to be there. Still two at minimum, so a short
    /// name isn't matched on a single word.
    /// </para>
    /// </remarks>
    private static int Enough(int nameWords) => Math.Max(2, (nameWords + 1) / 2);

    /// <summary>Words that carry meaning. "the", "an", "on" match everything and so distinguish nothing.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "on", "in", "to", "for", "of", "and", "or", "with", "at", "by", "from",
        "user", "users", "their", "them", "this", "that", "it", "its", "is", "are", "be", "into",
        "please", "then", "using", "use", "new", "one", "item", "items",
    };

    private static List<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(w => w.Length > 2 && !Noise.Contains(w))
            .Distinct()
            .ToList();

    private static int Overlap(List<string> a, List<string> b) =>
        b.Count(w => a.Contains(w, StringComparer.OrdinalIgnoreCase));

    /// <summary>What a worker is shown before it starts. Empty when nothing matches, so it costs nothing.</summary>
    public string NoteFor(string task) => Render(Relevant(task));

    /// <summary>Guides as the text a planner or a worker reads.</summary>
    public static string Render(IReadOnlyList<TaskGuide> matched)
    {
        if (matched.Count == 0) return "";

        var sb = new StringBuilder(
            "\n\nWhat was learned last time a job like this was done. It is a head start, not an instruction: " +
            "follow it while it matches what you see, and abandon it the moment it doesn't — a site may have " +
            "changed since. It carries no decisions about prices, policies or terms; those come from the task.\n");

        foreach (var g in matched)
            sb.Append($"\n### {g.Name}\n{g.Description}\n");

        return sb.ToString();
    }

    /// <summary>
    /// The names of every guide, for a worker deciding whether one of them is about the job it now has.
    /// <para>
    /// Names only. The point of the tool is that the library can grow past what belongs in a prompt, and a list
    /// of one-line summaries is the same problem one step later.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Names()
    {
        lock (_lock) return _guides.Select(g => g.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_guides, _json));
        }
        catch { /* a guide is an optimisation; losing one must never break a run */ }
    }
}

/// <summary>
/// Asking for a guide by name, mid-run.
///
/// <para>
/// The automatic lookup happens once, against the task as it was WORDED, and scores on shared words. That is the
/// right default — it costs nothing and needs no decision — but it can only match what the task already said. A
/// job that arrives as "sort out the spare Switch" and turns into an eBay listing three steps in shares no words
/// with "list an item on eBay" and never will. Only the worker, having seen the page, knows which job it is
/// actually doing, so it gets to ask.
/// </para>
/// <para>
/// Names first, then the guide, deliberately: the library grows without bound and the whole reason not to inject
/// all of it is that a prompt is not a filing cabinet.
/// </para>
/// </summary>
public static class TaskGuideTools
{
    public static AgentTool Tool(TaskHintStore store, Action<string>? followed = null) => new(
        "task_guides",
        "How jobs like this were done before, learned from runs that finished. Call with no argument for the " +
        "list of names; call with `name` for that guide. Worth a look when you find out what the job actually " +
        "is — the name you want may be nothing like how the task was worded. What comes back is a head start, " +
        "not an instruction: follow it while it matches the page in front of you, and drop it when it doesn't.",
        new[] { ToolParameter.String("name", "The guide to read, exactly as listed. Omit to list them.", required: false) },
        (args, _) =>
        {
            var name = (args.GetStringOrNull("name") ?? "").Trim();
            if (name.Length == 0)
            {
                var names = store.Names();
                return Task.FromResult(ToolOutput.Ok(names.Count == 0
                    ? "No guides have been learned yet."
                    : "Guides that exist:\n" + string.Join("\n", names.Select(n => "- " + n))));
            }

            var guide = store.Get(name);
            if (guide is null)
            {
                var names = store.Names();
                // A dead end, not a retry: guessing at a name it has already been given the whole list of is a
                // loop, and there is nothing to correct beyond reading the list.
                return Task.FromResult(ToolOutput.DeadEnd(
                    $"No guide called \"{name}\". " + (names.Count == 0
                        ? "None have been learned yet."
                        : "The ones that exist are: " + string.Join("; ", names))));
            }

            // Read on purpose, so it counts the same as one handed over automatically: if the run then fails,
            // this guide wears the miss, and if it succeeds its record is cleared.
            followed?.Invoke(guide.Name);
            return Task.FromResult(ToolOutput.Ok($"### {guide.Name}\n{guide.Description}"));
        });
}

/// <summary>Pulling what a finished run can teach out of what it actually did.</summary>
public static class RunLessons
{
    /// <summary>Tools that only LOOK. A call that reads a page tells the next run nothing about what to do.</summary>
    private static readonly HashSet<string> Observers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome_read_page", "chrome_images", "chrome_grab_image", "chrome_find", "chrome_inspect",
        "chrome_tabs_context", "chrome_read_console", "chrome_read_network", "chrome_read_dialogs",
        "chrome_screenshot", "chrome_changes", "chrome_scroll",
        "describe_image", "web_search", "search_memory",
        "list_files", "read_file", "file_summary", "find_in_file",
        "code_tree", "code_search", "code_read",
    };

    /// <summary>
    /// Did a browser action actually do anything?
    ///
    /// <para>
    /// Only the browser needs asking. A click on a dead element and a click that opened a dialog are identical
    /// from here except for what they reported changing, and a page is full of things that look clickable and
    /// aren't. Everywhere else the question doesn't arise: write_file that returned without an error wrote the
    /// file. Requiring this proof of every tool is what left a run that wrote six files and sent an email with
    /// an empty procedure and nothing learned.
    /// </para>
    /// </summary>
    private static bool Moved(string? result) =>
        result is { Length: > 0 } r &&
        (Reported(r, "changed") || Reported(r, "navigated") || Reported(r, "uploaded"));

    /// <summary>
    /// The tool reported this key, and reported something real under it.
    /// <para>
    /// A click on a dead element still comes back with a changed key — the browser answers
    /// <c>"changed": "nothing on the page changed"</c>, because a missing key has to mean "couldn't tell" rather
    /// than "did nothing". Matching the key alone therefore counts every dead click as a step, and a page is
    /// full of things that look clickable and aren't. That procedure would be mostly wrong turnings.
    /// </para>
    /// </summary>
    private static bool Reported(string result, string key)
    {
        int at = result.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (at < 0) return false;

        int i = at + key.Length + 2;
        while (i < result.Length && (char.IsWhiteSpace(result[i]) || result[i] == ':')) i++;

        var rest = result.AsSpan(i);
        return !(rest.Length == 0 ||
                 rest.StartsWith("[]") || rest.StartsWith("{}") || rest.StartsWith("null") ||
                 rest.StartsWith("\"\"") || rest.StartsWith("false") ||
                 rest.StartsWith("\"nothing", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Browser(string tool) => tool.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase);

    private static bool Failed(string? result) =>
        result is { Length: > 0 } r &&
        (r.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
         (r.Contains("Error:", StringComparison.Ordinal) &&
          r.IndexOf("Error:", StringComparison.Ordinal) < 60));

    /// <summary>
    /// The steps that actually moved something.
    /// <para>
    /// The filter is the whole trick: drop the calls that only looked, the calls that were refused, and the
    /// browser actions that reported changing nothing. On a 417-call listing run this leaves 29 steps, and they
    /// read as the eBay listing procedure.
    /// </para>
    /// </summary>
    public static List<string> Spine(IEnumerable<RunStep> steps)
    {
        var spine = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (step.Kind != "tool" || step.Tool is not { Length: > 0 } tool) continue;
            if (Observers.Contains(tool) || Failed(step.Result)) continue;
            if (Browser(tool) && !Moved(step.Result)) continue;

            var (target, value) = Describe(step.Args);
            var line = $"{tool} {target}".Trim();
            // The same step twice is one step: a run that clicked "next" seven times learned it once.
            if (!seen.Add(line)) continue;
            spine.Add(value.Length > 0 ? $"{line}  ← {value}" : line);
        }

        return spine;
    }

    /// <summary>What was refused, and what was done instead. The next run's list of what not to try.</summary>
    public static List<string> Gotchas(IReadOnlyList<RunStep> steps)
    {
        var gotchas = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Kind != "tool" || !Failed(step.Result) || step.Tool is not { Length: > 0 } tool) continue;

            var (target, _) = Describe(step.Args);
            var why = Flatten(step.Result!, 150);
            var key = $"{tool}|{why[..Math.Min(50, why.Length)]}";
            if (!seen.Add(key)) continue;

            // What worked next is the useful half — a refusal alone says avoid, not what to do instead.
            var recovery = "";
            for (int j = i + 1; j < Math.Min(i + 4, steps.Count); j++)
            {
                if (steps[j].Kind != "tool" || Failed(steps[j].Result)) continue;
                var (t2, _) = Describe(steps[j].Args);
                recovery = $"{steps[j].Tool} {t2}".Trim();
                break;
            }

            gotchas.Add($"tried {tool} {target} → {why}" +
                        (recovery.Length > 0 ? $" — worked instead: {recovery}" : ""));
        }

        return gotchas;
    }

    /// <summary>The interesting part of a call's arguments: what it acted on, and what it put there.</summary>
    private static (string Target, string Value) Describe(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return ("", "");
        try
        {
            var root = JsonDocument.Parse(args).RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", "");

            string Get(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";

            // In rough order of how much it says about the step. The file and shell names are here because
            // without them every write_file in a run reduced to the bare word "write_file", the de-dupe below
            // then collapsed six of them into one, and a run that built six documents looked like a run that
            // did nothing three times.
            var target = Get("find");
            if (target.Length == 0) target = Get("url");
            if (target.Length == 0) target = Get("query");
            if (target.Length == 0) target = Get("ref");
            if (target.Length == 0) target = Get("name");
            if (target.Length == 0) target = Get("path");
            if (target.Length == 0) target = Get("file");
            if (target.Length == 0) target = Get("to");
            if (target.Length == 0) target = Flatten(Get("command"), 60);
            // A script is the whole step and there is no shorter name for it. Its head is enough to tell two
            // apart, which is the point: a run that fetched a JSON endpoint, reshaped it and wrote a file made
            // four run_python calls, and with nothing to tell them apart the de-dupe below made them one — so
            // the lesson (that the endpoint exists at all) came out as a single word, "run_python".
            if (target.Length == 0) target = Flatten(Get("code"), 60);
            if (target.Length == 0) target = Flatten(Get("expression"), 60);

            var value = Get("value");
            if (value.Length == 0) value = Get("subject");
            return (Flatten(target, 70), Flatten(value, 40));
        }
        catch { return ("", ""); }
    }

    private static string Flatten(string text, int max)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>What the write-up is given: small, factual, and impossible to wander away from.</summary>
    public static string Evidence(string task, IReadOnlyList<RunStep> steps)
    {
        var sb = new StringBuilder($"TASK: {Flatten(task, 300)}\n\n");

        sb.Append("STEPS THAT CHANGED SOMETHING (in order):\n");
        foreach (var line in Spine(steps).Take(40)) sb.Append("  ").Append(line).Append('\n');

        var gotchas = Gotchas(steps);
        if (gotchas.Count > 0)
        {
            sb.Append("\nWHAT FAILED, AND WHAT WORKED INSTEAD:\n");
            foreach (var line in gotchas.Take(15)) sb.Append("  ").Append(line).Append('\n');
        }

        return sb.ToString();
    }
}
