using System.Text;
using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// One PARAMETER of a widget kind — the thing that makes it reusable rather than a one-off.
///
/// <para>
/// This is the whole distinction. "Track BA2490" asks for a flight tracker that takes a flight number, not for a
/// BA2490 tracker; "track my vinyl record on eBay" asks for an eBay listing panel that takes an item id. Build the
/// specific thing and the next request builds it again from nothing. Build the kind, and the next request is a
/// parameter.
/// </para>
/// </summary>
public sealed class WidgetParam
{
    public string Name { get; set; } = "";

    /// <summary>What it is, so the value can be recognised or gone and found — "the eBay item number, 12 digits".</summary>
    public string Description { get; set; } = "";

    /// <summary>A real example. What tells the difference between an item number and a listing URL.</summary>
    public string? Example { get; set; }

    public bool Required { get; set; } = true;
}

/// <summary>
/// A kind of panel: the component, the feed template, and the parameters that fill them in.
///
/// <para>
/// The library entry. Building one is expensive — a worker goes and finds a live feed, verifies it and writes a
/// component — so it is done once per KIND and then instantiated for free. Asking for a second flight is a lookup
/// and a parameter, not another four-minute build.
/// </para>
/// </summary>
public sealed class WidgetKind
{
    /// <summary>Slug, and identity: "flight-status", "ebay-listing". Republishing the same name revises it.</summary>
    public string Name { get; set; } = "";

    /// <summary>What it is, for a person: "Flight status".</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// What it shows, generically. This is what gets SEARCHED when a request arrives, so it is written to be
    /// found — the words someone would use for the job, not the words of the job it was first built for.
    /// </summary>
    public string Description { get; set; } = "";

    public List<WidgetParam> Params { get; set; } = new();

    /// <summary>
    /// PART ONE — the data model. What a panel of this kind is handed, field by field.
    ///
    /// <para>
    /// The contract, and the reason the other two parts can be fixed independently. A component written against a
    /// declared model cannot be broken by a source changing shape; that breaks the LOADER instead, which is
    /// server-side and testable. Before this existed the component dug through the raw response itself, and every
    /// shape change was a throw in the browser.
    /// </para>
    /// </summary>
    public List<WidgetField> Model { get; set; } = new();

    /// <summary>
    /// PART TWO — the loader. Where the data comes from and how it becomes the model. Server-side, always.
    /// </summary>
    public WidgetLoader Loader { get; set; } = new();

    /// <summary>
    /// Further feeds, for a question one source cannot answer on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not exotic, and not a way around writing one good loader. A great many sources split "what happened" from
    /// "what is coming" across two calls, because they are two different questions to a database. ESPN is the case
    /// that forced this: a team's schedule endpoint returns the completed match, and the SAME endpoint with
    /// <c>?fixture=true</c> returns the 45 upcoming ones, and there is no combination of parameters that returns
    /// both. A panel asked to show the last result until the day after, then count down to the next kick-off,
    /// needs both, and could previously only have one.
    /// </para>
    /// <para>
    /// Each feed maps only the fields IT supplies, and they merge into one object; the model contract is checked
    /// ONCE, at the end, against the merged result. That is what stops a second feed doubling a panel's fragility:
    /// a feed that fails only breaks the panel if the fields it was carrying were required. A panel whose extra
    /// feed is down shows the half it still has, which is the honest answer and usually most of the point.
    /// </para>
    /// <para>
    /// The first feed is <see cref="Loader"/>. These are the rest, and every one of them must carry a map, because
    /// merging is only possible if each feed says which fields are its own.
    /// </para>
    /// </remarks>
    public List<WidgetLoader> More { get; set; } = new();

    /// <summary>Every feed this kind reads, the primary first.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<WidgetLoader> Feeds => new[] { Loader }.Concat(More).ToList();

    /// <summary>
    /// PART THREE — the component. Renders the model; knows nothing about where it came from.
    /// </summary>
    /// <summary>
    /// The fields worth remembering, as paths into the loaded data.
    ///
    /// <para>
    /// Declared on the KIND because what is worth tracking is a property of the shape, not of the instance: every
    /// eBay listing wants its views and its price kept, and every listing panel then gets a history without anyone
    /// asking for one twice. Empty means the panel is a readout — the weather does not need remembering, because
    /// nobody wants a graph of what the forecast used to say.
    /// </para>
    /// </summary>
    public List<string> Track { get; set; } = new();

    public string Code { get; set; } = "";

    /// <summary>Default cadence for instances of this kind.</summary>
    public string Refresh { get; set; } = "";

    public string DefaultSize { get; set; } = WidgetSizes.Kpi;

    /// <summary>How many panels have been made from it. The measure of whether the library is doing its job.</summary>
    public int Uses { get; set; }

    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }

    /// <summary>The panel whose build produced it, so a claim can be traced to the run that made it.</summary>
    public string? LearnedFrom { get; set; }

    public bool Fetches => Loader.Fetches;

    /// <summary>Parameters with no value given. What has to be found before an instance can work.</summary>
    public IReadOnlyList<WidgetParam> Missing(IReadOnlyDictionary<string, string>? values) =>
        Params.Where(p => p.Required &&
                          (values is null || !values.TryGetValue(p.Name, out var v) || string.IsNullOrWhiteSpace(v)))
              .ToList();
}

/// <summary>
/// The library of kinds.
///
/// <para>
/// Searched before anything is built, which is the point: "track this flight" should find the flight kind and cost
/// a parameter, and only an genuinely new SHAPE of thing should cost a build. Scored on words shared with the
/// request rather than asked of a model, the same way task guides are matched — a round trip to decide whether to
/// save a round trip is a poor trade.
/// </para>
/// </summary>
public sealed class WidgetLibrary
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<WidgetKind> _kinds = new();
    private readonly object _lock = new();

    public WidgetLibrary(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path))
                _kinds = JsonSerializer.Deserialize<List<WidgetKind>>(File.ReadAllText(path), json) ?? new();
        }
        catch { _kinds = new(); }
    }

    public IReadOnlyList<WidgetKind> All()
    {
        lock (_lock) return _kinds.OrderByDescending(k => k.Uses).ThenBy(k => k.Name).ToList();
    }

    public WidgetKind? Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
            return _kinds.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Write a kind, replacing one of the same name. Name is identity: a rebuild improves the entry.</summary>
    /// <summary>
    /// Whether republishing this kind would CHANGE ITS SHAPE, rather than just fix it.
    ///
    /// <para>
    /// The distinction that keeps a library trustworthy. A kind in use is a contract: panels hold values for its
    /// parameters and a feed url is built from them. Rewriting the component under a live panel is a fix and is the
    /// entire point of having a library — one repair mends every flight panel at once. Renaming its parameters is not
    /// a fix; it is a different kind wearing the same name, and every existing instance is silently orphaned.
    /// </para>
    /// <para>
    /// That is not hypothetical. A 7-day weather build republished <c>city-weather</c> taking latitude/longitude/city
    /// where the existing panel held lat/lon/place. The url it was then expected to fill kept the literal
    /// "{latitude}" in it for ever, every refresh failed, and the panel showed a red "stale" beside an empty box with
    /// nothing anywhere explaining why. Two panels then took turns orphaning each other.
    /// </para>
    /// </summary>
    /// <returns>The parameter names that differ, or an empty list when the shape is unchanged.</returns>
    public IReadOnlyList<string> ShapeChange(WidgetKind proposed)
    {
        lock (_lock)
        {
            var existing = _kinds.FirstOrDefault(k =>
                string.Equals(k.Name, Slug(proposed.Name), StringComparison.OrdinalIgnoreCase));
            if (existing is null) return Array.Empty<string>();

            var was = existing.Params.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = proposed.Params.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Symmetric difference: a parameter that has gone is as breaking as one that has appeared.
            return was.Except(now, StringComparer.OrdinalIgnoreCase)
                .Concat(now.Except(was, StringComparer.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public WidgetKind Upsert(WidgetKind kind)
    {
        lock (_lock)
        {
            kind.Name = Slug(kind.Name);
            var existing = _kinds.FirstOrDefault(k =>
                string.Equals(k.Name, kind.Name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Title = kind.Title;
                existing.Description = kind.Description;
                existing.Params = kind.Params;
                existing.Model = kind.Model;
                existing.Loader = kind.Loader;
                existing.Code = kind.Code;
                existing.Refresh = kind.Refresh;
                existing.DefaultSize = kind.DefaultSize;
                existing.LearnedFrom = kind.LearnedFrom ?? existing.LearnedFrom;
                existing.Updated = DateTimeOffset.UtcNow;
                Save();
                return existing;
            }

            kind.Created = kind.Updated = DateTimeOffset.UtcNow;
            _kinds.Add(kind);
            Save();
            return kind;
        }
    }

    public void Used(string name)
    {
        lock (_lock)
        {
            if (Get(name) is { } k) { k.Uses++; Save(); }
        }
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            var gone = _kinds.RemoveAll(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (gone) Save();
            return gone;
        }
    }

    /// <summary>
    /// Kinds that might already do what's being asked for. Two shared meaningful words is a subject; one is a
    /// coincidence — the same threshold task guides use, for the same reason.
    /// </summary>
    public IReadOnlyList<WidgetKind> Relevant(string request, int limit = 4)
    {
        if (string.IsNullOrWhiteSpace(request)) return Array.Empty<WidgetKind>();
        var wanted = Words(request);
        if (wanted.Count == 0) return Array.Empty<WidgetKind>();

        lock (_lock)
            return _kinds
                .Select(k => (Kind: k, Score: Overlap(wanted, Words($"{k.Name} {k.Title} {k.Description}"))))
                .Where(x => x.Score >= 2)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Kind.Uses)
                .Take(limit)
                .Select(x => x.Kind)
                .ToList();
    }

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "on", "in", "to", "for", "of", "and", "or", "with", "at", "by", "from", "my", "me",
        "user", "users", "this", "that", "it", "its", "is", "are", "be", "into", "please", "then", "using",
        "use", "new", "show", "shows", "track", "tracking", "panel", "widget", "home", "page", "add", "put",
    };

    private static List<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(w => w.Length > 2 && !Noise.Contains(w))
            .Distinct()
            .ToList();

    private static int Overlap(List<string> a, List<string> b) =>
        b.Count(w => a.Contains(w, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The library as the model reads it before deciding whether to build anything.
    ///
    /// <para>
    /// Everything, not just the near matches: the request that should reuse a kind is often worded nothing like the
    /// kind's own description, and a shortlist of four hides the entry that would have fitted. It stays small
    /// because a library of shapes is small even when the number of panels isn't.
    /// </para>
    /// </summary>
    public string Describe()
    {
        var all = All();
        if (all.Count == 0) return "";

        var sb = new StringBuilder(
            "\n\nPanel kinds already in the library. Prefer widget_add with one of these over building anything — " +
            "it is instant and costs nothing, where a build is minutes of work:\n");

        foreach (var k in all)
        {
            var ps = k.Params.Count == 0
                ? "no parameters"
                : string.Join(", ", k.Params.Select(p =>
                    $"{p.Name}{(p.Required ? "" : "?")} ({p.Description}{(p.Example is { Length: > 0 } e ? $", e.g. {e}" : "")})"));
            var fields = k.Model.Count == 0 ? "" : $"; shows {string.Join(", ", k.Model.Select(f => f.Name))}";
            sb.Append($"- {k.Name}: {k.Description} — takes {ps}{fields}\n");
        }

        sb.Append("Only use widget_build when nothing here covers the SHAPE of what's wanted. A different flight, " +
                  "listing or city is the same shape.\n");
        return sb.ToString();
    }

    private static string Slug(string value) =>
        System.Text.RegularExpressions.Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_kinds, _json));
        }
        catch { /* a library is an optimisation; losing it must never break a run */ }
    }
}
