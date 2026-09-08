using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Something the chat can route to and scope context and jobs by. Two sorts, told apart by one field.
/// </summary>
/// <remarks>
/// <para>
/// One record for both, because everything AROUND them is identical: lists, files, runs, a dedicated chat, a slug
/// other things reference. Two stores would mean two copies of all of that and a slug namespace that could hold a
/// project and a topic of the same name, which is a collision waiting to be a bug.
/// </para>
/// <para>
/// What separates them is <see cref="Goal"/>, and it separates them completely: a project is a thing being driven
/// towards a stated outcome, and a topic is somewhere to file what accumulates about a subject. A favourite
/// restaurant is a topic. Selling the car is a project. The old model had only the second word available, so the
/// restaurant became a project and the whole notion of a goal quietly stopped meaning anything.
/// </para>
/// <para>
/// Note the class keeps its name while the vocabulary the user and the model see is project/topic. Renaming the type
/// touches around twelve hundred references, every stored slug, the routes and the specs — for a word that only ever
/// appears in code. The distinction that matters is enforced on the way in.
/// </para>
/// </remarks>
public sealed class Project
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "active";   // active | done | archived
    public DateTimeOffset Created { get; set; }

    /// <summary>
    /// What finishing this would mean. Set for a project, absent for a topic — and that is the whole distinction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required to create a project and refused if empty, because a container with no stated outcome was the thing
    /// going wrong: told a favourite restaurant, the model reached for the only container it had and made a project
    /// out of a preference. Nothing was broken by that except meaning — every question about what was on and what was
    /// finished then had a restaurant in the answer.
    /// </para>
    /// <para>
    /// A goal earns the word by being finishable. It is not a restatement of the title, which is why one that only
    /// repeats the title is refused: "Favourite restaurant" wanting to be a project because its goal is "favourite
    /// restaurant" is the exact failure, and it is catchable without reading either as prose.
    /// </para>
    /// </remarks>
    public string? Goal { get; set; }

    /// <summary>
    /// The day its goal was reached. Null while it is still going, and always null for a topic.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Status"/> because the date is the interesting part later: "when did we sell the car" is
    /// a question, "was its status done" is not.
    /// </remarks>
    public DateOnly? CompletedOn { get; set; }

    /// <summary>Whether this is somewhere to file things rather than something being driven at an outcome.</summary>
    public bool IsTopic => string.IsNullOrWhiteSpace(Goal);

    /// <summary>Whether its goal has been reached. Never true of a topic — there is nothing to reach.</summary>
    public bool Finished => !IsTopic && Status == "done";

    /// <summary>Which of the two this is, in the words the user and the model use.</summary>
    public string Sort => IsTopic ? "topic" : "project";
    public string? Summary { get; set; }              // short live narrative of where it stands (regenerated on touch)

    /// <summary>
    /// The window this project is ABOUT, when it has one. Not when it was created and not when work happened —
    /// the dates the thing itself covers.
    /// <para>
    /// Some projects are inherently bounded and lose their meaning outside those dates: meal prep for next week,
    /// a trip, a sprint. Ask for meal prep a fortnight later and last fortnight's plan is not the answer — but it
    /// is still the record of what was planned then, so it must be readable rather than deleted. Both nullable,
    /// because plenty of projects have no natural window (a house move drifts, "learn Spanish" never ends), and a
    /// required date would only get made up.
    /// </para>
    /// </summary>
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }

    /// <summary>
    /// How often this period comes round: "weekly", "fortnightly", "monthly", or empty for a project that happens
    /// once.
    ///
    /// <para>
    /// The distinction the model was missing. A window says a project is ABOUT a period; this says the period repeats,
    /// which is a different thing and the more common one for anything domestic — meals, bins, a standing order of
    /// chores. Without it, "week of dinners" ended on Sunday and everything built on it went stale, including a panel
    /// whose whole job was answering what is for dinner tonight.
    /// </para>
    /// </summary>
    public string? Repeat { get; set; }

    /// <summary>
    /// The standing instruction for each new period.
    ///
    /// <para>
    /// This is the part worth keeping: "no pork; each night a vegetarian base plus a protein" is the thing that would
    /// otherwise be re-typed every Sunday. With it, renewal can actually fill the new period rather than just moving
    /// the dates and leaving an empty week.
    /// </para>
    /// </summary>
    public string? RenewPrompt { get; set; }

    /// <summary>When it last rolled over, so a renewal is not run twice for the same period.</summary>
    public DateOnly? RenewedOn { get; set; }

    /// <summary>True when this period repeats rather than happening once.</summary>
    public bool Recurring => !string.IsNullOrWhiteSpace(Repeat) && StartsOn is not null && EndsOn is not null;

    /// <summary>Whether it covers a given day. False for either half of an open-ended window that excludes it.</summary>
    public bool Covers(DateOnly day) =>
        (StartsOn is null || day >= StartsOn) && (EndsOn is null || day <= EndsOn);

    /// <summary>True once its window has closed. An unbounded project is never over.</summary>
    public bool HasEnded(DateOnly today) => EndsOn is { } end && today > end;

    /// <summary>True while it is still ahead of us — planned, not yet begun.</summary>
    public bool NotYetStarted(DateOnly today) => StartsOn is { } start && today < start;

    /// <summary>
    /// The window in words, for the model and the UI: what it covers and where that sits relative to today. Empty
    /// when there is no window at all, so an unbounded project reads exactly as it did before.
    /// </summary>
    public string WindowNote(DateOnly today)
    {
        if (StartsOn is null && EndsOn is null) return "";

        var range = (StartsOn, EndsOn) switch
        {
            ({ } s, { } e) when s == e => s.ToString("d MMM yyyy"),
            ({ } s, { } e) => $"{s:d MMM} – {e:d MMM yyyy}",
            ({ } s, null) => $"from {s:d MMM yyyy}",
            (null, { } e) => $"until {e:d MMM yyyy}",
            _ => "",
        };

        // The relative part is the bit that has to land: "ran 18–24 Aug" invites the model to treat a finished
        // week as current, where "ENDED 12 days ago" does not.
        if (HasEnded(today))
        {
            var days = today.DayNumber - EndsOn!.Value.DayNumber;
            return $"covers {range} — ENDED {Ago(days)}; it is over, and anything asked about now is a NEW period";
        }
        if (NotYetStarted(today))
        {
            var days = StartsOn!.Value.DayNumber - today.DayNumber;
            return $"covers {range} — starts in {Ago(days)}, not yet under way";
        }
        return EndsOn is { } ending
            ? $"covers {range} — running now, {Ago(ending.DayNumber - today.DayNumber)} left"
            : $"covers {range} — running now";
    }

    private static string Ago(int days) => days switch
    {
        <= 0 => "today",
        1 => "1 day",
        < 14 => $"{days} days",
        < 60 => $"{days / 7} week{(days / 7 == 1 ? "" : "s")}",
        _ => $"{days / 30} month{(days / 30 == 1 ? "" : "s")}",
    };
}

/// <summary>
/// The handful of projects on the go. Deliberately small — projects are created explicitly (never as a
/// side-effect of delegating), so this stays a short, intentional list.
/// </summary>
public sealed class ProjectStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<Project> _projects = new();

    public ProjectStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    /// <summary>Create a project (deliberate, confirmed act). Returns a confirmation message and, when a
    /// project was actually created, its slug (null if it was a no-op — empty title or a near-duplicate).</summary>
    /// <summary>
    /// Make a topic: no goal named, and none being asked for.
    /// </summary>
    /// <remarks>
    /// A goal is what makes something a project, so a call that names none and does not ask for one gets the container
    /// that needs none. Note this is NOT the same as asking for a project and leaving the goal out — that is refused,
    /// which is the point, and it is why intent is a separate argument from the goal itself rather than being inferred
    /// from its absence.
    /// </remarks>
    public (string Message, string? Slug) Create(string title, string description) =>
        Create(title, description, null, null);

    /// <inheritdoc cref="Create(string,string)"/>
    public (string Message, string? Slug) Create(string title, string description, DateOnly? startsOn, DateOnly? endsOn)
        => Create(title, description, startsOn, endsOn, goal: null, filing: true);

    /// <summary>
    /// Make one, of whichever sort the goal decides.
    /// </summary>
    /// <param name="goal">
    /// What finishing it would mean. Required unless <paramref name="filing"/> — a project with no stated outcome is
    /// refused and sent back as a topic instead.
    /// </param>
    /// <param name="filing">
    /// True when the caller means a topic: somewhere to keep what accumulates about a subject, with nothing to finish.
    /// </param>
    /// <remarks>
    /// The refusal lives HERE rather than in what the model is told, because instructing against it had already been
    /// tried by implication — nothing ever said a project needed a goal, and nothing needed to, since there was no
    /// other container to offer. Now there are two, and the one that requires an outcome will not be created without
    /// one. A model that reaches for the wrong one is handed the right one by name.
    /// </remarks>
    public (string Message, string? Slug) Create(string title, string description, DateOnly? startsOn,
        DateOnly? endsOn, string? goal, bool filing)
    {
        title = title.Trim();
        goal = goal?.Trim();

        if (title.Length == 0) return ($"A {(filing ? "topic" : "project")} needs a title.", null);

        if (!filing)
        {
            if (goal is not { Length: > 0 })
                return ("A project needs a goal — what finishing it would mean. If there is nothing to finish and " +
                        "this is somewhere to keep what comes up about the subject, make it a topic instead.", null);

            // A goal that only says the title again is not a goal. Comparing two supplied fields, not reading either as
            // prose — which is the only sort of check worth making here, since anything cleverer would be guessing.
            if (Words(goal).Count > 0 && !Words(goal).Except(Words(title)).Any())
                return ($"\"{goal}\" restates the title rather than saying what finishing it would mean. If there is " +
                        "no outcome to reach, make it a topic instead.", null);
        }

        lock (_lock)
        {
            if (startsOn is { } s && endsOn is { } e && e < s)
                return ($"That {(filing ? "topic" : "project")} would end before it starts — check the dates.", null);

            // Anti-sprawl: refuse a near-duplicate of an existing project (share ≥2 meaningful words).
            //
            // A project whose window has CLOSED is not a duplicate of a new one, though: "meal prep w/c 11 Aug"
            // and "meal prep w/c 18 Aug" are the same words about different weeks, and blocking the second is
            // exactly how you end up editing last week's plan instead of making this week's.
            var today = DateOnly.FromDateTime(Now().Date);
            var words = Words(title);

            // Two words in common, OR every meaningful word of one of them.
            //
            // The second half is the fix for a hole this had all along: "The car" is one meaningful word once the
            // article is dropped, so it could never reach two and a second "The car" was created beside the first.
            // Short titles are the common case for a topic, which is exactly where sprawl hurts most. Sharing ALL of
            // a title's words is at least as strong a signal as sharing two of five, and it does not fire on "Meal
            // prep" against "Meal plan", where each keeps a word of its own.
            var dup = _projects.FirstOrDefault(p =>
            {
                if (p.Status != "active" || p.HasEnded(today)) return false;

                var theirs = Words(p.Title);
                var shared = theirs.Intersect(words).Count();
                return shared >= 2 || (shared > 0 && (shared == words.Count || shared == theirs.Count));
            });
            if (dup is not null)
                return ($"There's already a similar {dup.Sort}: \"{dup.Title}\" (slug: {dup.Slug})" +
                        (dup.WindowNote(today) is { Length: > 0 } w ? $", which {w}" : "") +
                        ". Use that one instead of creating a duplicate.", null);

            var slug = Slugify(title);
            string baseSlug = slug;
            for (int n = 2; _projects.Any(p => p.Slug == slug); n++) slug = $"{baseSlug}-{n}";

            _projects.Add(new Project
            {
                Slug = slug,
                Title = title,
                Description = description?.Trim() ?? "",
                Status = "active",
                Created = Now(),
                StartsOn = startsOn,
                EndsOn = endsOn,
                Goal = filing ? null : goal,
            });
            Save();

            var window = startsOn is null && endsOn is null
                ? ""
                : $" It {_projects[^1].WindowNote(today)}.";

            return filing
                ? ($"Topic created: \"{title}\" (slug: {slug}). File anything about it here — there is nothing to " +
                   $"finish.{window}", slug)
                : ($"Project created: \"{title}\" (slug: {slug}), done when {goal}. Tag work to it with that " +
                   $"slug.{window}", slug);
        }
    }

    public Project? Get(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        lock (_lock) return _projects.FirstOrDefault(p => p.Slug == slug);
    }

    /// <summary>Update a project's short live summary (regenerated in the background on each touch).</summary>
    public void SetSummary(string slug, string summary)
    {
        slug = slug.Trim().ToLowerInvariant();
        lock (_lock)
        {
            var p = _projects.FirstOrDefault(x => x.Slug == slug);
            if (p is null) return;
            p.Summary = summary;
            Save();
        }
    }

    public bool Exists(string slug) => Get(slug) is not null;

    /// <summary>
    /// Give a project the window it covers (or clear it by passing nulls). Separate from creation because the
    /// dates often arrive a turn later — "actually make that the week after next".
    /// </summary>
    public string SetDates(string slug, DateOnly? startsOn, DateOnly? endsOn)
    {
        slug = slug.Trim().ToLowerInvariant();
        if (startsOn is { } s && endsOn is { } e && e < s) return "That would end before it starts — check the dates.";

        lock (_lock)
        {
            var p = _projects.FirstOrDefault(x => x.Slug == slug);
            if (p is null) return $"There's no project \"{slug}\".";
            p.StartsOn = startsOn;
            p.EndsOn = endsOn;
            Save();

            var today = DateOnly.FromDateTime(Now().Date);
            return startsOn is null && endsOn is null
                ? $"\"{p.Title}\" is no longer tied to particular dates."
                : $"\"{p.Title}\" {p.WindowNote(today)}.";
        }
    }

    /// <summary>
    /// The projects on the go, for resolving a statement against them.
    /// <para>
    /// A project whose window has closed is deliberately NOT here. It stays readable through Get/find/summary —
    /// the record of what was planned is worth keeping — but it has no business steering a new conversation, and
    /// this list is what gets put in front of the model every turn.
    /// </para>
    /// </summary>
    /// <summary>
    /// Call a project done, against the goal it was created with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the goal is FOR, and until now nothing used it. <c>Status</c> was declared <c>active | done | archived</c>
    /// and never once set to anything but active in the whole codebase — the field existed, the word "project" implied
    /// it, and no path could ever reach it. A goal nothing can be measured against is decoration.
    /// </para>
    /// <para>
    /// It also does the work no write-time check can. Whether a stated goal is a real outcome or a restatement dressed
    /// up as one cannot be decided by reading it — but it becomes obvious the moment somebody is asked whether it has
    /// been reached, because a goal that was never finishable has no answer. So the weak ones surface here, in front of
    /// the person who can say, rather than being guessed at on the way in.
    /// </para>
    /// <para>
    /// Refused for a topic. There is nothing to finish, and letting one be marked done would put the word back to
    /// meaning nothing.
    /// </para>
    /// </remarks>
    public string Complete(string slug)
    {
        lock (_lock)
        {
            if (Get(slug) is not { } p) return $"There's no project \"{slug}\".";

            if (p.IsTopic)
                return $"\"{p.Title}\" is a topic — there is nothing to finish. Topics are somewhere to keep what " +
                       "comes up about a subject; they stay as long as the subject does.";

            if (p.Status == "done") return $"\"{p.Title}\" was already marked done.";

            p.Status = "done";
            p.CompletedOn = DateOnly.FromDateTime(Now().Date);
            Save();

            return $"\"{p.Title}\" is done — {p.Goal}. It stays readable, and it stops counting as current.";
        }
    }

    /// <summary>
    /// Remove it entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only operation here that erases rather than settling into a state. Finishing a project and deleting one are
    /// different things and both are needed: a goal reached is a fact worth keeping, while a container made by mistake —
    /// which is how most of them arrive — is one nothing should mention again.
    /// </para>
    /// <para>
    /// This removes the RECORD only. Its lists, its runs and whatever the brain holds about it live in other stores that
    /// know nothing about this one, so the caller joins them up; that is what the endpoint does, and doing it here would
    /// mean this store reaching into three others.
    /// </para>
    /// </remarks>
    public Project? Delete(string slug)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();

        lock (_lock)
        {
            var found = _projects.FirstOrDefault(p => p.Slug == slug);
            if (found is null) return null;

            _projects.Remove(found);
            Save();
            return found;
        }
    }

    /// <summary>Put a finished project back, when it turns out not to have been.</summary>
    public string Reopen(string slug)
    {
        lock (_lock)
        {
            if (Get(slug) is not { } p) return $"There's no project \"{slug}\".";
            if (p.Status != "done") return $"\"{p.Title}\" isn't marked done.";

            p.Status = "active";
            p.CompletedOn = null;
            Save();
            return $"\"{p.Title}\" is open again — {p.Goal}.";
        }
    }

    /// <summary>
    /// Make this period repeat, with a standing instruction for each new one.
    /// </summary>
    /// <param name="repeat">weekly, fortnightly, monthly — or empty to stop repeating.</param>
    /// <param name="renewPrompt">
    /// What to do each time it comes round. Without one the window still moves, but nothing fills it, so a recurring
    /// project with no instruction is an empty week rather than a planned one.
    /// </param>
    public string SetRepeat(string slug, string? repeat, string? renewPrompt)
    {
        lock (_lock)
        {
            if (Get(slug) is not { } project) return $"There's no project \"{slug}\".";

            if (string.IsNullOrWhiteSpace(repeat))
            {
                project.Repeat = null;
                project.RenewPrompt = null;
                Save();
                return $"\"{project.Title}\" no longer repeats.";
            }

            if (project.StartsOn is null || project.EndsOn is null)
                return $"\"{project.Title}\" has no dates yet, and a period can only repeat if it has one — set " +
                       "the dates it covers first.";

            project.Repeat = repeat.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(renewPrompt)) project.RenewPrompt = renewPrompt.Trim();
            Save();

            return $"\"{project.Title}\" now repeats {project.Repeat}" +
                   (project.RenewPrompt is { Length: > 0 } ? ", and each new period will be filled in automatically." : ".");
        }
    }

    /// <summary>
    /// Recurring projects whose window has closed and which have not yet been rolled into the current one.
    /// </summary>
    public IReadOnlyList<Project> DueForRenewal(DateOnly today)
    {
        lock (_lock)
            return _projects
                .Where(p => p.Status == "active" && p.Recurring && p.HasEnded(today))
                // RenewedOn is the guard against doing the same period twice — a restart, or two ticks a minute
                // apart, must not book the same planning task again.
                .Where(p => p.RenewedOn != today)
                .ToList();
    }

    /// <summary>Move a project onto its next period. Returns false if something else got there first.</summary>
    public bool Renew(string slug, DateOnly starts, DateOnly ends, DateOnly today)
    {
        lock (_lock)
        {
            if (Get(slug) is not { } project) return false;
            if (project.RenewedOn == today) return false;

            project.StartsOn = starts;
            project.EndsOn = ends;
            project.RenewedOn = today;
            Save();
            return true;
        }
    }

    public IReadOnlyList<(string Slug, string Title, string Description)> ActiveProjects()
    {
        var today = DateOnly.FromDateTime(Now().Date);
        lock (_lock)
            return _projects.Where(p => p.Status == "active" && !p.HasEnded(today))
                .Select(p => (p.Slug, p.Title, p.Description)).ToList();
    }

    /// <summary>Overridable clock, so the window logic can be tested without waiting for next week.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    public string List()
    {
        var today = DateOnly.FromDateTime(Now().Date);
        lock (_lock)
        {
            var active = _projects.Where(p => p.Status == "active").ToList();

            // Goals that were reached, listed rather than dropped. A project that vanishes on completion is one the
            // model will cheerfully create again next week, and "have we already done this" is a fair question.
            var done = _projects.Where(p => p.Finished)
                .OrderByDescending(p => p.CompletedOn ?? DateOnly.MinValue).ToList();

            if (active.Count == 0 && done.Count == 0) return "Nothing on the go yet.";

            // Running ones first, then the finished ones under their own heading — so a closed window is visible
            // as a fact about the project rather than something to be inferred from a date at the end of a line.
            var running = active.Where(p => !p.HasEnded(today)).ToList();
            var ended = active.Where(p => p.HasEnded(today)).ToList();

            var sb = new StringBuilder();

            // Under their own headings, because the two answer different questions: what is being driven at something,
            // and what is merely being kept. Mixed together, a preference reads as work in progress.
            if (running.Any(p => !p.IsTopic))
            {
                sb.Append("Projects — each has an outcome to reach:\n");
                foreach (var p in running.Where(p => !p.IsTopic)) Line(sb, p, today);
            }
            if (running.Any(p => p.IsTopic))
            {
                sb.Append(sb.Length > 0 ? "\n" : "");
                sb.Append("Topics — filing only, nothing to finish:\n");
                foreach (var p in running.Where(p => p.IsTopic)) Line(sb, p, today);
            }
            if (ended.Count > 0)
            {
                sb.Append(sb.Length > 0 ? "\n" : "");
                sb.Append("Out of date (their period has passed — readable, but do NOT treat as current; a new " +
                          "request about this subject needs a new one):\n");
                foreach (var p in ended) Line(sb, p, today);
            }
            if (done.Count > 0)
            {
                sb.Append(sb.Length > 0 ? "\n" : "");
                sb.Append("Done (their goal was reached — don't start these again):\n");
                foreach (var p in done) Line(sb, p, today);
            }
            return sb.ToString().TrimEnd();
        }

        static void Line(StringBuilder sb, Project p, DateOnly today)
        {
            sb.Append($"- {p.Slug}: {p.Title}");
            if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($" — {p.Description}");
            if (p.Goal is { Length: > 0 } goal)
                sb.Append(p.Finished
                    ? $" (done{(p.CompletedOn is { } on ? $" {on:d MMM yyyy}" : "")} — {goal})"
                    : $" (done when {goal})");
            if (p.WindowNote(today) is { Length: > 0 } window) sb.Append($" [{window}]");
            sb.Append('\n');
        }
    }

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "with", "for", "my", "our", "plan", "planning", "project", "trip", "get", "getting", "sort", "sorting" };

    private static HashSet<string> Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]{3,}")
            .Select(m => m.Value).Where(w => !Common.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Slugify(string title)
    {
        var s = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "project" : (s.Length > 40 ? s[..40].Trim('-') : s);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(_path), _json);
            if (loaded is not null) _projects.AddRange(loaded);
        }
        catch { /* a corrupt file shouldn't crash startup */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_projects, _json));
        }
        catch { /* best-effort */ }
    }
}

/// <summary>Tools for creating and listing projects. Creation is its own deliberate step.</summary>
public static class ProjectTools
{
    public static AgentTool CreateTool(ProjectStore store) => new(
        "create_project",
        // Defined by what it ACCUMULATES, not by a list of life-events. The old wording named a party, a trip and
        // a house move, and the model read that as the set of things that qualify — so a week of meal plans, which
        // has preferences to remember, a shopping list and a plan worth keeping, got none. The test isn't how big
        // or important the request sounds; it's whether anything survives the answer.
        // The split that fixes the actual failure. Told a favourite restaurant, the model made a project of it —
        // correctly, given a project was the only container on offer. Nothing about that was a judgement error; it was
        // a missing option. So there are two now, and the one that means "being driven at an outcome" cannot be created
        // without one, which leaves the other as the obvious home for a preference.
        "Start a project for something being driven towards a stated OUTCOME — it needs a goal, and it is refused " +
        "without one. Something the user will come back to that has nothing to finish is a TOPIC: use create_topic " +
        "for that instead. Propose it and confirm first; don't duplicate an existing one.\n" +
        // Measured: told only that dates were available, the model worked out the week correctly and then wrote it
        // into the TITLE — "Week of dinners (17–23 Aug)" with both date fields empty. A period in the title is
        // decoration; nothing can expire on it. Say where it goes.
        "IF IT COVERS A PERIOD, put that period in starts_on/ends_on — NOT in the title. \"next week\" means " +
        "actual dates: work them out from today's date and pass them. The title stays plain (\"Week of dinners\"), " +
        "and the dates are what make it stop counting as current once they pass.",
        new[]
        {
            ToolParameter.String("title", "Short title.", required: true),
            // The one thing a project cannot be created without, and the whole difference between the two containers.
            ToolParameter.String(
                "goal",
                "What finishing it would mean — the outcome that would let the user call it done. It has to say " +
                "something the title does not: a goal that only repeats the title is refused, because that means " +
                "there is nothing to finish and it belongs as a topic.",
                required: true),
            ToolParameter.String("description", "One line on what it's about.", required: false),
            // REQUIRED to answer, still optional to have — "none" is a valid answer.
            //
            // As optional parameters these were simply skipped: told plainly to put the period in the fields and
            // not the title, the model wrote a plain title and passed no dates at all. That is what optional
            // means to a model with its required arguments already satisfied. Required forces the question to be
            // ANSWERED every time, while "none" means it never has to invent a window it doesn't have.
            ToolParameter.String(
                "starts_on",
                "First day it covers, YYYY-MM-DD — or \"none\". Most have no dates at all and that is normal: a goal " +
                "is what makes something a project, never a window. Work \"next week\" out from today's date if it " +
                "does cover a period, and don't put that period in the title.",
                required: true),
            ToolParameter.String(
                "ends_on",
                "Last day it covers, YYYY-MM-DD — or \"none\". After this day it stops counting as current: still " +
                "readable, but a later request about the same subject means a NEW project for the new dates.",
                required: true),
        },
        (args, _) => Task.FromResult(ToolOutput.Ok(store.Create(
            args.GetString("title"),
            args.GetStringOrNull("description") ?? "",
            ParseDay(args.GetStringOrNull("starts_on")),
            ParseDay(args.GetStringOrNull("ends_on")),
            goal: args.GetStringOrNull("goal"),
            filing: false).Message
            + Unreadable(("starts_on", args.GetStringOrNull("starts_on")), ("ends_on", args.GetStringOrNull("ends_on"))))));

    /// <summary>
    /// Somewhere to file what accumulates about a subject, with nothing to finish.
    /// </summary>
    /// <remarks>
    /// The container that was missing, and its absence is the whole bug: everything a project offers — lists, files, a
    /// summary, its own chat — applies just as well to a subject nobody is trying to complete, and without this the
    /// only way to get any of it was to declare an outcome that did not exist.
    /// </remarks>
    public static AgentTool TopicTool(ProjectStore store) => new(
        "create_topic",
        "Start a topic for a subject the user keeps coming back to that has nothing to finish — somewhere to file " +
        "what accumulates about it: what they like, what they have decided, files, lists. If there IS an outcome to " +
        "reach then it is a project and needs a goal, so use create_project. Propose it and confirm first; don't " +
        "duplicate an existing one.",
        new[]
        {
            ToolParameter.String("title", "Short title — the subject itself.", required: true),
            ToolParameter.String("description", "One line on what it's about.", required: false),
        },
        (args, _) => Task.FromResult(ToolOutput.Ok(store.Create(
            args.GetString("title"),
            args.GetStringOrNull("description") ?? "",
            startsOn: null,
            endsOn: null,
            goal: null,
            filing: true).Message)));

    /// <summary>
    /// Mark a project's goal as reached.
    /// </summary>
    /// <remarks>
    /// The tool that gives a goal a point. Without it a project could be created with a stated outcome and never be
    /// measured against it, which is how the word "project" came to mean nothing in the first place — and it is also
    /// the only place a goal that was never really a goal can be caught, since it is unanswerable here in a way it
    /// never is at the moment it is written.
    /// </remarks>
    public static AgentTool CompleteTool(ProjectStore store) => new(
        "finish_project",
        "Mark a project done once its goal has been reached — the user has to say so, never assume it. It stays " +
        "readable afterwards and stops counting as current. Topics cannot be finished: there is nothing to finish, " +
        "so this is refused for them.",
        new[]
        {
            ToolParameter.String("project", "The project's slug.", required: true),
            ToolParameter.Boolean("reopen", "True to put a finished project back if it turns out not to have been.",
                required: false),
        },
        (args, _) => Task.FromResult(ToolOutput.Ok(
            args.GetBoolOrNull("reopen") == true
                ? store.Reopen(args.GetString("project"))
                : store.Complete(args.GetString("project")))));

    /// <summary>Set or clear the window a project covers, when the dates arrive after it was created.</summary>
    public static AgentTool DatesTool(ProjectStore store) => new(
        "set_project_dates",
        "Give a project the dates it covers, or clear them. Use it when a project turns out to be tied to a " +
        "period — the week it's for, the days of a trip — or when those dates change. Once the end date passes " +
        "the project stops being treated as current.",
        new[]
        {
            ToolParameter.String("project", "The project's slug.", required: true),
            ToolParameter.String("starts_on", "First day (YYYY-MM-DD). Omit both to clear the dates.", required: false),
            ToolParameter.String("ends_on", "Last day (YYYY-MM-DD).", required: false),
        },
        (args, _) => Task.FromResult(ToolOutput.Ok(store.SetDates(
            args.GetString("project"),
            ParseDay(args.GetStringOrNull("starts_on")),
            ParseDay(args.GetStringOrNull("ends_on")))
            + Unreadable(("starts_on", args.GetStringOrNull("starts_on")), ("ends_on", args.GetStringOrNull("ends_on"))))));

    /// <summary>
    /// Make a project's period repeat, with a standing instruction for each new one.
    /// </summary>
    public static AgentTool RepeatTool(ProjectStore store) => new(
        "set_project_repeat",
        "Make a dated project RECUR, so it rolls into its next period automatically instead of ending. Use it for " +
        "anything that is a period rather than a goal: a week of dinners, a fortnightly shop, a monthly review. " +
        "Without this such a project reads as finished on its last day, and anything built on it — a panel showing " +
        "what is for dinner tonight — goes stale and stays stale.\n" +
        "The `each_period` instruction is the important half: it is the standing brief that fills the NEW period, so " +
        "write down what would otherwise be re-typed every time (\"no pork; each night a vegetarian base plus a " +
        "protein\"). Existing items are never removed — they are dated, so they fall out of view on their own.",
        new[]
        {
            ToolParameter.String("project", "The project's slug. It must already have dates.", required: true),
            ToolParameter.String("repeat",
                "weekly, fortnightly or monthly — or \"none\" to stop it repeating. Anything else is treated as the " +
                "same length as its current window, repeated.", required: true),
            ToolParameter.String("each_period",
                "What to do each time it comes round: the standing instruction for filling the new period. Omit only " +
                "if the dates should move with nothing being planned.", required: false),
        },
        (args, _) =>
        {
            var repeat = args.GetString("repeat");
            if (string.Equals(repeat?.Trim(), "none", StringComparison.OrdinalIgnoreCase)) repeat = null;
            return Task.FromResult(ToolOutput.Ok(
                store.SetRepeat(args.GetString("project"), repeat, args.GetStringOrNull("each_period"))));
        });

    /// <summary>
    /// A date the model supplied. "none" (and anything else unreadable) means no window — a wrong date is worse
    /// than none. Whether it was understood is reported back in the tool's reply, so a mangled date isn't
    /// silently dropped and then wondered about later.
    /// </summary>
    public static DateOnly? ParseDay(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return DateOnly.TryParse(text, out var day) ? day : null;
    }

    /// <summary>Names a supplied date that couldn't be read, so the reply can say so rather than swallow it.</summary>
    private static string Unreadable(params (string Name, string? Value)[] supplied)
    {
        var bad = supplied
            .Where(s => ParseDay(s.Value) is null
                        && !string.IsNullOrWhiteSpace(s.Value)
                        && !s.Value!.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            .Select(s => $"{s.Name}=\"{s.Value}\"")
            .ToList();

        return bad.Count == 0
            ? ""
            : $" (couldn't read {string.Join(" or ", bad)} — no dates recorded; pass YYYY-MM-DD to set them.)";
    }

    public static AgentTool ListTool(ProjectStore store) => new(
        "list_projects",
        "List the projects currently on the go (with their slugs).",
        Array.Empty<ToolParameter>(),
        (_, _) => Task.FromResult(ToolOutput.Ok(store.List())));
}
