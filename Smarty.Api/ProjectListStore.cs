using System.Text.Json;

namespace Smarty.Api;

/// <summary>A named list belonging to a project — preferred amenities, must-sees, people to ask.</summary>
public sealed class ProjectList
{
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";          // slug
    public string Title { get; set; } = "";
    public List<string> Items { get; set; } = new();
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }

    /// <summary>
    /// Whether these items get worked THROUGH rather than just held.
    /// <para>
    /// Not every list wants ticking. "Preferred amenities: all-inclusive, pool" is criteria — a tick against it
    /// means nothing. "Shopping list: 35 items" is a job, and the only interesting question about it is which
    /// ones are done. Same shape, different purpose, so the purpose is stated rather than guessed.
    /// </para>
    /// </summary>
    public bool Checklist { get; set; }

    /// <summary>
    /// Which items are ticked, by their text.
    /// <para>
    /// A parallel set rather than a flag on each item, so every list written before checklists existed still
    /// loads: the JSON is unchanged and this arrives empty. Identity is the item's text, which is already how
    /// removal works ("you don't need the exact wording"), so there is one matching rule to understand rather
    /// than two.
    /// </para>
    /// </summary>
    public List<string> Done { get; set; } = new();

    public bool IsDone(string item) => Done.Contains(item, StringComparer.OrdinalIgnoreCase);

    /// <summary>How much of it is behind you — the thing a checklist exists to answer.</summary>
    public int DoneCount => Items.Count(IsDone);

    /// <summary>
    /// Which day an item is FOR, by its text. Absent for anything undated, which is most items.
    ///
    /// <para>
    /// A meal plan is the case that proves the need. Seven dinners went into a list and the answer to "what's for
    /// dinner tonight" was still nowhere — the data was there and nothing could ask it a question about today,
    /// because an ordered list of seven things does not say which one is Tuesday. Ordinal position won't do it
    /// either: the list gets reordered, an item gets added, and the whole week silently shifts by one.
    /// </para>
    /// <para>
    /// A date, not a time — "dinner is on Tuesday", not at 19:30. Something wanted at a specific moment is a
    /// <see cref="ScheduledTask"/>, which goes off and does something; this only says which day it belongs to.
    /// A parallel dictionary rather than a field on each item, for the same reason <see cref="Done"/> is one:
    /// every list written before dates existed still loads, and arrives with none.
    /// </para>
    /// </summary>
    public Dictionary<string, DateOnly> Dates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateOnly? DateOf(string item) => Dates.TryGetValue(item, out var on) ? on : null;

    /// <summary>Does any of it belong to a day? Cheap enough to ask of every list when building the day's view.</summary>
    public bool Dated => Dates.Count > 0;

    /// <summary>
    /// Which items name something the brain already knows, by their text.
    ///
    /// <para>
    /// The thing that makes a list worth asking questions of rather than just reading back. "Where should I go for
    /// dinner" is unanswerable from a list of words — but if the items are joined to the places themselves, then
    /// everything the graph knows about them comes too: that one has been talked about for months and never visited,
    /// that another is round the corner from the lido, that a third already has a booking. The list supplies the
    /// shortlist and the graph supplies the reason to pick one.
    /// </para>
    /// <para>
    /// Not every item, and that is the point. "Bin bags" and "not fussy about a pool" are words, and forcing them into
    /// nodes is exactly the junk that makes the one reliable filter useless. So an item is text by default and a node
    /// when it happens to name one — the same promotion the reconciler already does, where a value becomes a real
    /// connection the moment something with that name exists.
    /// </para>
    /// <para>
    /// A parallel dictionary for the same reason <see cref="Done"/> and <see cref="Dates"/> are ones: identity is the
    /// item's text, which is already the matching rule everywhere else here, and every list written before this existed
    /// still loads and simply arrives with none.
    /// </para>
    /// </summary>
    public Dictionary<string, string> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? NodeOf(string item) => Nodes.TryGetValue(item, out var id) ? id : null;

    /// <summary>Which of these are things rather than words. What a question walks into.</summary>
    public IEnumerable<string> Known => Items.Select(NodeOf).Where(id => id is { Length: > 0 })!;
}

/// <summary>One dated thing, and where it came from — the shape the day's view is built out of.</summary>
public sealed record AgendaEntry(
    string ListId,
    string ListTitle,
    string Project,
    string Item,
    DateOnly On,
    bool Done);

/// <summary>
/// Lists, as a thing a project remembers.
/// <para>
/// A key/value memory can hold "destination = Mallorca" and cannot hold "preferred amenities: a pool, several
/// restaurants, all-inclusive" — not really. Stuffing that into one value makes editing it a rewrite, so "actually
/// I'm not fussy about a pool" means restating the whole thing and hoping nothing else changed. A list is the shape
/// that fits: named, ordered, and edited an item at a time by either side.
/// </para>
/// <para>
/// Deliberately not built on the brain. A brain edge is a claim with an audience and a staleness half-life, which
/// is right for "they live in London" and wrong for a working list the user reorders on a whim.
/// </para>
/// </summary>
public sealed class ProjectListStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<ProjectList> _lists = new();
    private readonly object _lock = new();

    public ProjectListStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;

        try
        {
            if (File.Exists(path))
                _lists = JsonSerializer.Deserialize<List<ProjectList>>(File.ReadAllText(path), json) ?? new();
        }
        catch (Exception)
        {
            _lists = new(); // a corrupt file is not worth refusing to start over
        }
    }

    /// <summary>
    /// How to tell whether an item names something the brain knows. Supplied by the host.
    /// </summary>
    /// <remarks>
    /// Given rather than reached for, so this store keeps knowing nothing about the graph — the same arrangement as the
    /// brain being handed a way to read files rather than opening them itself. Left unset, items are just words, which is
    /// what they were before.
    /// <para>
    /// Re-run on every read rather than only when an item is added, because the answer changes without the list changing:
    /// a place written down as words in March becomes a real place the day somebody mentions going there. Resolving once
    /// at write time would leave the earliest items — the ones talked about longest, which are precisely the interesting
    /// ones — as text for ever.
    /// </para>
    /// </remarks>
    public Func<string, string?>? Resolve { get; set; }

    public IReadOnlyList<ProjectList> ForProject(string slug)
    {
        slug = Slug(slug);
        lock (_lock)
        {
            var found = _lists.Where(l => l.Project == slug).OrderBy(l => l.Created).ToList();
            foreach (var list in found) Recognise(list);
            return found;
        }
    }

    /// <summary>
    /// Join up whatever the graph has learned to recognise since last time.
    /// </summary>
    /// <remarks>
    /// Only ever adds. An item that stops resolving — because a node was merged away — keeps the id it had, since the
    /// graph follows a merge tombstone and would find the same thing anyway.
    /// </remarks>
    private void Recognise(ProjectList list)
    {
        if (Resolve is null) return;

        var changed = false;
        foreach (var item in list.Items)
        {
            if (list.Nodes.ContainsKey(item)) continue;
            if (Resolve(item) is not { Length: > 0 } id) continue;

            list.Nodes[item] = id;
            changed = true;
        }

        if (changed) Save();
    }

    public ProjectList? Get(string id)
    {
        lock (_lock)
        {
            var found = _lists.FirstOrDefault(l => l.Id == id);
            if (found is not null) Recognise(found);
            return found;
        }
    }

    /// <summary>Every list, newest first. For a home-page panel, which isn't about one project.</summary>
    public IReadOnlyList<ProjectList> All()
    {
        lock (_lock)
        {
            var found = _lists.OrderByDescending(l => l.Updated).ToList();
            foreach (var list in found) Recognise(list);
            return found;
        }
    }

    /// <summary>Create a list, or return the existing one of that name — asking twice for "amenities" means the
    /// same list, not a second one beside it.</summary>
    public ProjectList Create(string project, string title, IEnumerable<string>? items = null,
        bool? checklist = null)
    {
        var slug = Slug(project);
        var clean = Clean(items);

        lock (_lock)
        {
            var existing = _lists.FirstOrDefault(l =>
                l.Project == slug && string.Equals(l.Title, title.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                foreach (var item in clean)
                    if (!existing.Items.Contains(item, StringComparer.OrdinalIgnoreCase))
                        existing.Items.Add(item);
                // A return visit may promote a list to a checklist; it never quietly demotes one that is
                // already being worked through.
                if (checklist == true) existing.Checklist = true;
                existing.Updated = DateTimeOffset.UtcNow;
                Save();
                return existing;
            }

            var list = new ProjectList
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Project = slug,
                Title = title.Trim(),
                Items = clean.ToList(),
                Checklist = checklist == true,
                Created = DateTimeOffset.UtcNow,
                Updated = DateTimeOffset.UtcNow,
            };
            _lists.Add(list);
            Save();
            return list;
        }
    }

    /// <summary>
    /// Add and remove items in one go, because that is how a change actually arrives: "not fussy about a pool, but
    /// a spa would be nice" is one edit. Removal matches loosely — an item is identified by the words a person
    /// would use for it, not by an index they never saw.
    /// </summary>
    public ProjectList? Update(string id, IEnumerable<string>? add = null, IEnumerable<string>? remove = null,
        string? title = null)
    {
        lock (_lock)
        {
            var list = _lists.FirstOrDefault(l => l.Id == id);
            if (list is null) return null;

            foreach (var item in Clean(remove))
            {
                list.Items.RemoveAll(existing => Same(existing, item));
                // A tick for something no longer on the list is a ghost that keeps counting towards the total.
                list.Done.RemoveAll(ticked => Same(ticked, item));
                // And a date for something no longer on the list is a ghost that keeps turning up in the day's
                // view, where there is nothing left to click on and no way to work out where it came from.
                foreach (var dated in list.Dates.Keys.Where(k => Same(k, item)).ToList()) list.Dates.Remove(dated);
            }

            foreach (var item in Clean(add))
                if (!list.Items.Contains(item, StringComparer.OrdinalIgnoreCase))
                    list.Items.Add(item);

            if (!string.IsNullOrWhiteSpace(title)) list.Title = title.Trim();

            list.Updated = DateTimeOffset.UtcNow;
            Save();
            return list;
        }
    }

    /// <summary>
    /// Put items on days, or take them off one. Matched the loose way everything else here is matched, and stored
    /// against the item's own text so the date still lines up when the list is read back.
    /// </summary>
    /// <param name="on">Item text → the day it is for. A null value clears that item's date.</param>
    /// <returns>The list and how many items actually moved, or null if there is no such list.</returns>
    public (ProjectList List, int Changed)? Schedule(string id, IReadOnlyDictionary<string, DateOnly?> on)
    {
        lock (_lock)
        {
            var list = _lists.FirstOrDefault(l => l.Id == id);
            if (list is null) return null;

            int changed = 0;
            foreach (var (wanted, day) in on)
            {
                var text = (wanted ?? "").Trim();
                if (text.Length == 0) continue;

                foreach (var item in list.Items.Where(i => Same(i, text)).ToList())
                {
                    if (day is null)
                    {
                        if (list.Dates.Remove(item)) changed++;
                    }
                    else if (list.DateOf(item) != day)
                    {
                        list.Dates[item] = day.Value;
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                list.Updated = DateTimeOffset.UtcNow;
                Save();
            }
            return (list, changed);
        }
    }

    /// <summary>
    /// Everything with a day attached, in a window, in day order.
    ///
    /// <para>
    /// Across every list and every project on purpose. "What's on today" is not a question about a project — the
    /// meal plan, the jobs before the trip and the bins all land on the same Tuesday, and a view that made you
    /// pick a project first would be the same wasted step as the one that made this invisible in the first place.
    /// </para>
    /// </summary>
    public IReadOnlyList<AgendaEntry> Agenda(DateOnly from, int days = 1)
    {
        var until = from.AddDays(Math.Max(1, days) - 1);
        lock (_lock)
            return _lists
                .Where(l => l.Dated)
                .SelectMany(l => l.Items
                    .Select(i => (Item: i, On: l.DateOf(i)))
                    .Where(x => x.On is { } d && d >= from && d <= until)
                    .Select(x => new AgendaEntry(l.Id, l.Title, l.Project, x.Item, x.On!.Value, l.IsDone(x.Item))))
                .OrderBy(e => e.On)
                .ThenBy(e => e.ListTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>
    /// Tick or untick items, matched the same loose way removal matches them — a worker that just added
    /// "Cherry tomatoes (250g)" to a basket should be able to tick "cherry tomatoes" without the two having to
    /// agree character for character.
    /// </summary>
    /// <returns>The list and how many items the call actually moved, or null if there is no such list.</returns>
    public (ProjectList List, int Changed)? Check(string id, IEnumerable<string>? done, IEnumerable<string>? undone)
    {
        lock (_lock)
        {
            var list = _lists.FirstOrDefault(l => l.Id == id);
            if (list is null) return null;

            int changed = 0;

            foreach (var wanted in Clean(undone))
                changed += list.Done.RemoveAll(ticked => Same(ticked, wanted));

            foreach (var wanted in Clean(done))
                foreach (var item in list.Items.Where(i => Same(i, wanted)))
                    if (!list.IsDone(item))
                    {
                        // Stored as the item's own text, not as whatever the caller typed, so the tick still
                        // lines up with the list when someone reads it back.
                        list.Done.Add(item);
                        changed++;
                    }

            // Ticking something is what a checklist IS. Rather than refuse, take the tick as the answer to
            // whether this list was one — a shopping list written before anyone thought to say so becomes one
            // the moment work starts on it.
            if (changed > 0 && !list.Checklist) list.Checklist = true;

            if (changed > 0)
            {
                list.Updated = DateTimeOffset.UtcNow;
                Save();
            }
            return (list, changed);
        }
    }

    /// <summary>The one matching rule: exact, or either containing the other. Used by removing and by ticking so
    /// there is a single notion of "that item" to learn.</summary>
    private static bool Same(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
        b.Contains(a, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Remove every list belonging to something, and say how many went.
    /// </summary>
    /// <remarks>
    /// For when the thing they belong to is deleted. Lists are the one part of a project with no other way in — no slug
    /// of their own that anything else holds — so a project removed without them leaves rows nothing can ever reach or
    /// name again.
    /// </remarks>
    public int DeleteFor(string slug)
    {
        slug = Slug(slug);

        lock (_lock)
        {
            var went = _lists.RemoveAll(l => l.Project == slug);
            if (went > 0) Save();
            return went;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _lists.RemoveAll(l => l.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>The project's lists as a line or two for a prompt. Empty when there are none.</summary>
    public string Describe(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return "";
        var lists = ForProject(project);
        if (lists.Count == 0) return "";

        // Dates shown inline, because a list read back without them invites re-deciding which dinner is Tuesday
        // and then setting the whole week again over the top of what was already there.
        return string.Join("\n", lists.Select(l =>
            $"- {l.Title} ({l.Id}): " +
            (l.Items.Count == 0 ? "empty" : string.Join(", ", l.Items.Select(i => Label(l, i))))));
    }

    /// <summary>An item as it reads with its day, when it has one: "Tue 18 Aug — chicken traybake".</summary>
    public static string Label(ProjectList list, string item) =>
        list.DateOf(item) is { } on ? $"{on:ddd d MMM} — {item}" : item;

    private static IEnumerable<string> Clean(IEnumerable<string>? items) =>
        (items ?? Array.Empty<string>())
            .Select(i => (i ?? "").Trim())
            .Where(i => i.Length is > 0 and < 200)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Slug(string value) => (value ?? "").Trim().ToLowerInvariant();

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_lists, _json));
        }
        catch (Exception)
        {
            // In-memory stays correct; the next write may well succeed.
        }
    }
}
