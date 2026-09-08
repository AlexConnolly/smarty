using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// One panel on the home page: a React component the system wrote, and a data feed it found to put in it.
///
/// <para>
/// The first version of this was a fixed set of six templates a model chose between, filled in by an agent that
/// re-read a web page every few hours. It failed on the first real request — "track this flight" — and the way it
/// failed is instructive: scraping a flight page is unreliable, re-scraping it every ten minutes is expensive and
/// unreliable, and a template picker cannot express a flight anyway. A boarding pass is not a headline.
/// </para>
/// <para>
/// So the two halves are both real work now, and both done ONCE, in the background, by a task:
/// </para>
/// <list type="bullet">
/// <item><b><see cref="Code"/></b> — a component, in JSX, using Tailwind and the kit of primitives the page
/// provides. Written for the data it is about, so a flight looks like a flight.</item>
/// <item><b><see cref="FeedUrl"/></b> — a JSON endpoint the task went and FOUND, verified, and wrote down. After
/// that, keeping the panel current is an HTTP GET on a timer: no model, no browser, no cost.</item>
/// </list>
/// <para>
/// That division is the whole thing. Building a panel is expensive and happens once; running it is free and
/// happens forever.
/// </para>
/// </summary>
public sealed class Widget
{
    public string Id { get; set; } = "";

    /// <summary>What it is, in a few words. Shown while it is still being built, and in the panel's menu.</summary>
    public string Title { get; set; } = "";

    /// <summary>kpi | tall | wide — a footprint on the bento grid, not pixels.</summary>
    public string Size { get; set; } = WidgetSizes.Kpi;

    /// <summary>
    /// How near the top it belongs, 0–100. Ordering IS the relevancy: a flight you asked about this morning
    /// outranks the project list, and next week it is gone.
    /// </summary>
    public int Priority { get; set; } = 50;

    /// <summary>The user said keep it. Pinned panels sort above everything.</summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// building | live | proposed | paused | failed.
    ///
    /// <para>
    /// "building" is its own state because making one of these is a job — find a feed, prove it works, write a
    /// component — and a panel that appears instantly showing nothing is what the last version did. It says it is
    /// being built, and by whom, until it isn't.
    /// </para>
    /// </summary>
    public string Status { get; set; } = WidgetStatus.Building;

    /// <summary>The background task building it, so the panel can show live progress and be cancelled.</summary>
    public string? TaskId { get; set; }

    /// <summary>Why it's on the page. Shown on a proposal, and in the menu.</summary>
    public string? Why { get; set; }

    /// <summary>What went wrong, when building or fetching failed. Shown on the panel rather than hidden.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The component rendering against made-up data, while the real feed is still being built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things at once, and the second is the reason it is worth the field. It lets the panel be SHOWN while it is
    /// still being made — dimmed, with what is happening written over it — instead of a progress line and the word
    /// "Building" for eight minutes. And it means the component has been rendered against its declared shape before any
    /// source exists, so a component that cannot render is caught in the first minute rather than the last.
    /// </para>
    /// <para>
    /// Already in the model's shape, exactly as <see cref="Data"/> is. The component cannot tell the difference, which
    /// is the point: if it renders this it will render the real thing.
    /// </para>
    /// </remarks>
    public string? Sample { get; set; }

    /// <summary>
    /// The component as it was designed, before there was anything real to put in it.
    /// </summary>
    /// <remarks>
    /// Held apart from <see cref="OwnCode"/> rather than reusing it, because the two mean opposite things to
    /// everything that reads them: own code is a panel that WORKS and has no library kind behind it, and a failed
    /// build with own code set is left live on purpose. A design is a promise. Confusing the two would leave a build
    /// that died halfway showing invented numbers as though they were the answer.
    /// </remarks>
    public string? Design { get; set; }

    /// <summary>
    /// What is happening to it right now, in the words to put on the panel.
    /// </summary>
    /// <remarks>
    /// Set where the build actually passes each milestone rather than reported by the thing doing the work, so it cannot
    /// be forgotten by a model concentrating on something else. Null once it is live — there is nothing in progress.
    /// </remarks>
    public string? Stage { get; set; }

    /// <summary>
    /// Its build died with the process, so it is owed another go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Error"/> because the two mean different things to whoever reads them next. An error is
    /// something about the panel that needs a person; this is nothing about the panel at all — the process went away
    /// mid-build — and the panel was left telling its owner to ask again for something no human decision was needed for.
    /// </para>
    /// <para>
    /// Which is a poor deal when the process restarts often. A dozen restarts in an afternoon meant a dozen manual
    /// nudges, and it read as a panel that would not stop failing.
    /// </para>
    /// </remarks>
    public bool Interrupted { get; set; }

    /// <summary>
    /// How many times a restart has already been made good for this panel.
    /// </summary>
    /// <remarks>
    /// Capped, because retrying on startup is exactly the shape of thing that turns a crash loop into a build loop. Past
    /// the cap it goes back to asking, which is the honest signal that something other than a restart is wrong.
    /// </remarks>
    public int RestartRetries { get; set; }

    /// <summary>
    /// How many times a broken component has been sent back to be fixed automatically.
    ///
    /// <para>
    /// A component that throws is a fault with a message attached, and the thing that can read that message is the
    /// same worker that wrote the code — so it goes back with the exception in its brief rather than waiting for
    /// someone to notice a dead panel. Counted because the obvious failure mode is a fix that doesn't fix it: two
    /// attempts, then it stays broken with the error showing, which is honest and cheap.
    /// </para>
    /// </summary>
    public int Fixes { get; set; }

    /// <summary>The exception the component last threw in the browser, if it did.</summary>
    public string? RenderError { get; set; }

    /// <summary>The last load failure, so the SAME failure twice can be told from one bad night on the network.</summary>
    public string? LastLoadError { get; set; }

    /// <summary>
    /// What its build has stopped to ask the user, if it has stopped to ask something.
    ///
    /// <para>
    /// Held HERE, on the panel, rather than only on the run in the control hub. The hub is a dashboard log and
    /// treats a question as disposable: a re-drive of the task, or a restart while it happened to be running,
    /// clears it on purpose — which is right for a log and fatal for a question, because the question was the only
    /// thing standing between the panel and working. The camera panel asked for its credentials, correctly, and the
    /// ask was gone by the next restart.
    /// </para>
    /// <para>
    /// The panel is the durable place for it, because the panel is where the user is looking and is the thing that
    /// cannot finish without an answer.
    /// </para>
    /// </summary>
    public string? Asks { get; set; }

    /// <summary>
    /// What this panel has seen before now.
    ///
    /// <para>
    /// The panel's own memory. Kept here rather than in a database because it is small, it is only ever read whole,
    /// and it belongs to the panel — delete the panel and the history goes with it, which is the behaviour anyone
    /// would expect.
    /// </para>
    /// </summary>
    public List<WidgetSample> History { get; set; } = new();

    /// <summary>When the build now claiming this panel started. The clock behind "this has been going too long".</summary>
    public DateTimeOffset? BuildingSince { get; set; }

    /// <summary>When a pair of eyes last checked it.</summary>
    public DateTimeOffset? LookedAt { get; set; }

    /// <summary>What was in the frame when they did. Kept because it is the most legible thing on the record.</summary>
    public string? Seen { get; set; }

    /// <summary>
    /// Resources the panel tried to load and couldn't, as the browser reported them.
    ///
    /// <para>
    /// The precise version of what the visual check finds by looking. A broken image is the most likely fault in a
    /// client-mode panel and the most silent — nothing throws, nothing fails to load server-side — but the browser
    /// knows exactly which url failed and says so. "GET https://… did not load" beats "a grey box with a torn-page
    /// symbol" for fixing something, and the two agree, which is how you know both are right.
    /// </para>
    /// </summary>
    public List<string> Faults { get; set; } = new();

    /// <summary>
    /// Something has been noticed about this panel that a repair could address — and nothing has been started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The replacement for repairing itself. Every check still runs and still records what it finds; this is the sum
    /// of them, and it exists so the panel's own menu can OFFER the fix. The user asked for exactly this after a panel
    /// they had just had made to look right rewrote itself twice in a row: once over a feed that answered oddly on the
    /// load straight after the build, and once over an error that only existed in a browser tab running yesterday's
    /// bundle. Both were gone within a minute. Neither needed a decision, and both got one.
    /// </para>
    /// <para>
    /// Live only. A panel mid-build has a task on it already, and a failed one has never worked, so neither is a thing
    /// with a fault to offer to mend.
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Ailing => Status == WidgetStatus.Live
                          && (RenderError is { Length: > 0 }
                              // Not this one. A page running an older bundle is a page to reload, and offering to
                              // "fix" the panel over it is how a panel that worked got rewritten twice in an hour.
                              || (Error is { Length: > 0 } && Error != WidgetStore.OlderBundle)
                              || Faults.Count > 0);

    /// <summary>What was noticed, in one line, for the menu and for the brief of a repair the user asks for.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Wrong =>
        RenderError is { Length: > 0 } threw ? threw
        : Error is { Length: > 0 } failed && failed != WidgetStore.OlderBundle ? failed
        : Faults.Count > 0 ? string.Join("; ", Faults)
        : null;

    // ---- what it is, and what it is about ----

    /// <summary>
    /// The library kind this is an instance of — "flight-status", "ebay-listing".
    ///
    /// <para>
    /// The component and the feed template live on the KIND, not here, so a second flight costs a parameter rather
    /// than another four-minute build. Null only on a panel reserved but not yet built.
    /// </para>
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// The values that make this instance about a particular thing: <c>{"flightNumber": "BA2490"}</c>.
    ///
    /// <para>
    /// Handed to the component as <c>params</c> as well as being substituted into the feed url, because a panel
    /// usually needs to SAY what it is about and the feed's response often doesn't contain it.
    /// </para>
    /// </summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A one-off component, for a panel that genuinely isn't an instance of anything.
    ///
    /// <para>
    /// Almost nothing should use this. It exists because the alternative is forcing a library entry for something
    /// that will never have a second instance, and a library full of those is a library nobody can search.
    /// </para>
    /// </summary>
    public string? OwnCode { get; set; }

    // ---- its data ----

    public DateTimeOffset? FetchedAt { get; set; }
    public DateTimeOffset? NextRefresh { get; set; }

    /// <summary>How often it reloads. Taken from the kind, overridable per panel.</summary>
    public string Refresh { get; set; } = "";

    /// <summary>
    /// Does the SERVER load this panel's data?
    ///
    /// <para>
    /// False for a client-mode panel, where the component fetches or embeds for itself. Recorded on the instance
    /// because the refresher has no library to ask, and without it a client-mode panel is queued for a server load
    /// it can never satisfy — which then fails with "No url to load", which the loader-repair reads as a broken
    /// loader and sends off to be rebuilt. A panel regenerating forever over a fault that was the absence of a
    /// fault.
    /// </para>
    /// </summary>
    public bool ServerLoads { get; set; } = true;

    /// <summary>
    /// The last load, in the kind's declared shape — already the object the component renders.
    ///
    /// <para>
    /// Stored rather than loaded on view: a panel that goes to the internet every time the home page opens is a
    /// panel that is blank for four seconds and costs money to look at. Shaped on the way in rather than on the way
    /// out, so every tab renders the same object and none of them has to know where it came from.
    /// </para>
    /// </summary>
    public string? Data { get; set; }

    public DateTimeOffset Created { get; set; }

    /// <summary>The conversation it came out of.</summary>
    public string? Session { get; set; }

    /// <summary>
    /// How the build of this panel is going, state by state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives on the panel because it belongs to the panel: delete one and the account of how it was made goes with
    /// it, and a panel that failed can be picked up at the state it died in rather than paying for its design a
    /// second time. The alternative — a run log in the control hub — is a dashboard that gets cleared, and the
    /// question "why is this panel not working" outlives any dashboard.
    /// </para>
    /// <para>
    /// Null on a panel built before the states existed, and on one that never needed a build.
    /// </para>
    /// </remarks>
    public PanelBuildRecord? Build { get; set; }
}

/// <summary>
/// The three footprints a panel can have.
///
/// <para>
/// Three, because four were one too many and the extra one was doing harm. The old set had a 2×1 and a 4×1, and a
/// box one row high is not enough room for anything with a sentence in it — a quote panel on the home page read
/// "Non-violence and truth are inseparable and presuppose one another" and then simply stopped, because the box ran
/// out. Offering a size that cannot hold prose guarantees that prose is eventually put in it.
/// </para>
/// <para>
/// So the short one is now explicitly a KPI: one number, its label, and at most a trend beside it. Anything with
/// words in it takes <see cref="Tall"/> or <see cref="Wide"/>, which have the room. The constraint is the feature —
/// the previous version's small panels all looked broken for the same reason, and it was never the components'
/// fault.
/// </para>
/// </summary>
public static class WidgetSizes
{
    /// <summary>2 cols × 1 row. ONE number and its label. Not a list, not a paragraph, not three stats.</summary>
    public const string Kpi = "kpi";

    /// <summary>2 × 2 — one across, two down. A few rows, a small chart, a short paragraph.</summary>
    public const string Tall = "tall";

    /// <summary>4 × 2 — the full width. A real chart, a table, several things at once.</summary>
    public const string Wide = "wide";

    public static readonly string[] All = { Kpi, Tall, Wide };

    public static bool Known(string? size) =>
        size is { Length: > 0 } && All.Contains(size, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An old size in the new set.
    ///
    /// <para>
    /// "small" was the same 2×1 box, so it becomes a KPI unchanged. "large" was 4×2 and is now called wide. The old
    /// "wide" was 4×1 — the flat letterbox that could not hold what it was given — and it gains a row rather than
    /// keeping a shape that was the problem.
    /// </para>
    /// </summary>
    public static string Migrate(string? size) => (size ?? "").ToLowerInvariant() switch
    {
        "small" => Kpi,
        "large" => Wide,
        "wide" => Wide,
        "tall" => Tall,
        var known when Known(known) => known,
        _ => Kpi,
    };
}

public static class WidgetStatus
{
    /// <summary>A task is out finding a feed and writing the component.</summary>
    public const string Building = "building";
    public const string Live = "live";
    public const string Proposed = "proposed";
    public const string Paused = "paused";

    /// <summary>The build gave up. Kept, with the reason, rather than vanishing — a panel that silently never
    /// appeared is the failure that made the last version feel broken.</summary>
    public const string Failed = "failed";
}

/// <summary>Feeds resolved in-process from what the system already holds.</summary>
/// <summary>
/// What a build is doing, in the words that go on the panel.
/// </summary>
/// <remarks>
/// Written as the user would read them rather than as the code thinks of them, and kept here so the thing that sets a
/// stage and the thing that displays it cannot drift into two different vocabularies.
/// </remarks>
public static class Stages
{
    public const string Designing = "Designing it";
    public const string Feed = "Building the data feed";
    public const string Testing = "Testing it";
}

public static class WidgetInternals
{
    public const string Agenda = "agenda";
    public const string Lists = "lists";
    public const string Schedule = "schedule";
    public const string Tasks = "tasks";
    public const string Projects = "projects";

    public static readonly string[] All = { Agenda, Lists, Schedule, Tasks, Projects };

    /// <summary>An installed plugin's command, as a feed: "plugin:roborock/status".</summary>
    public const string PluginPrefix = "plugin:";

    /// <summary>A granted folder, as a feed: "source:photos".</summary>
    public const string SourcePrefix = "source:";

    /// <summary>
    /// Whether this is a well-formed feed name — the SHAPE only. See <see cref="Fault"/> for whether it names
    /// anything that exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An allow-list is also a ceiling. This one held five fixed names, and the two feeds that carry an ARGUMENT
    /// — a plugin's command, a granted folder — could not be spelled in a way it accepted. The route that serves
    /// them knew about both; this, which is what publish asks, did not. So a panel binding to a vacuum was refused
    /// with "use one of: agenda, lists, schedule, tasks, projects", and the build did the only thing left to it:
    /// declared the design's invented values as PARAMETERS and shipped a panel that renders 82% for ever. It looks
    /// built. It is a photograph. One place decides what a feed can be called now, and this is it.
    /// </para>
    /// <para>
    /// Opening that ceiling then cost the opposite mistake, and it is the reason this is now only half the question.
    /// The fix went from a closed list straight to a check on the SYNTAX — anything after "source:" passed — so
    /// <c>source:any</c> was accepted by publish on a machine with no granted folders at all, and the panel it broke
    /// failed every five minutes for nine hours. A name being spellable is not a name meaning something. Shape here,
    /// existence in <see cref="Fault"/>, and publish asks both.
    /// </para>
    /// </remarks>
    public static bool Known(string? name)
    {
        // Trimmed first: a stray space around the name is a typo, not a different feed, and refusing it sends the
        // builder off to find a feed that does not exist.
        name = name?.Trim();
        if (name is not { Length: > 0 }) return false;
        if (All.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        if (name.StartsWith(PluginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // A plugin AND one of its commands: half of it addresses nothing.
            var wanted = name[PluginPrefix.Length..].Split('/', 2);
            return wanted.Length == 2 && wanted.All(part => part.Trim().Length > 0);
        }

        return name.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase)
               && name[SourcePrefix.Length..].Trim().Length > 0;
    }

    /// <summary>
    /// What is wrong with this feed name, asking the registries whether it points at anything real — or null when
    /// there is nothing wrong with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check that was missing. <see cref="Known"/> answers whether a name is SPELLED like a feed; only the thing
    /// holding the folders and the plugins can answer whether the feed is there. So the naming rules stay here, next
    /// to the prefixes they belong to, and the registries are handed in by whoever has them.
    /// </para>
    /// <para>
    /// A resolver left null means "cannot ask" and passes, deliberately: a caller with no plugin host has no business
    /// refusing every plugin feed, and the loader that runs at publish will catch it a moment later anyway. Our own
    /// five names never need asking about — they are produced by this process.
    /// </para>
    /// </remarks>
    /// <param name="sourceExists">Given a granted folder's name, whether it is granted.</param>
    /// <param name="pluginHas">Given a plugin id and a command, whether that plugin declares it.</param>
    public static string? Fault(string? name, Func<string, bool>? sourceExists = null,
        Func<string, string, bool>? pluginHas = null)
    {
        name = name?.Trim();
        if (!Known(name)) return $"An internal loader needs one of: {Options}.";
        if (All.Contains(name!, StringComparer.OrdinalIgnoreCase)) return null;

        if (name!.StartsWith(PluginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (pluginHas is null) return null;
            var parts = name[PluginPrefix.Length..].Split('/', 2);
            return pluginHas(parts[0].Trim(), parts[1].Trim())
                ? null
                : $"No installed plugin answers \"{name}\". A plugin feed is \"{PluginPrefix}<plugin>/<command>\" " +
                  "and both halves have to be one it actually declares — check the plugin's commands rather than " +
                  "guessing the name.";
        }

        if (sourceExists is null) return null;
        var wanted = name[SourcePrefix.Length..].Trim();
        return sourceExists(wanted)
            ? null
            : $"There's no granted folder called \"{wanted}\", so \"{name}\" can never be read. A folder has to be " +
              "granted before a panel can use it. If what you meant was a plugin's data, that is " +
              $"\"{PluginPrefix}<plugin>/<command>\"; if it is ours, it is one of: {string.Join(", ", All)}.";
    }

    /// <summary>The name as it should be STORED, or null if it names nothing.</summary>
    /// <remarks>
    /// The fixed names are ours and case-insensitive. An argument is somebody else's — a folder they named, a
    /// command a plugin declared — so it is kept exactly as given rather than folded to lower case.
    /// </remarks>
    public static string? Store(string? name) =>
        !Known(name) ? null
        : All.Contains(name!.Trim(), StringComparer.OrdinalIgnoreCase) ? name!.Trim().ToLowerInvariant()
        : name!.Trim();

    /// <summary>What to say when somebody asks for a feed that isn't one, including the two shapes.</summary>
    public static string Options =>
        $"{string.Join(", ", All)}, \"{PluginPrefix}<plugin>/<command>\" for an installed plugin's command, or " +
        $"\"{SourcePrefix}<name>\" for a granted folder";
}

/// <summary>
/// Where a panel's own work happens.
/// </summary>
/// <remarks>
/// <para>
/// A panel used to be worked on in the conversation it was ASKED FOR in, and a conversation is the unit everything
/// else isolates by: its workspace is a folder named after it, and its files area is shared across every task in it
/// so a later task can re-read what an earlier one fetched. All of which is right for a chat thread and wrong for a
/// panel, because two panels asked for in one chat then share both.
/// </para>
/// <para>
/// That is not hypothetical. A weather panel asked for in the vacuum's chat inherited its session, so nine hours
/// later the worker sent to repair the weather panel was reading the vacuum's command contract and a stale forecast
/// dump out of a shared files pile, paging through them four thousand characters at a time. It was not stuck and it
/// was not slow — it was working on the wrong panel's material.
/// </para>
/// <para>
/// So the work session is derived from the PANEL, always, and the chat that asked is kept as provenance rather than
/// used as an address. One line, because the whole fault was that this was a <c>??</c> in four different routes.
/// </para>
/// </remarks>
public static class WidgetWork
{
    public static string SessionFor(string panelId) => $"widget-{panelId}";
}

/// <summary>The panels, on disk. Ordering is the relevancy.</summary>
public sealed class WidgetStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<Widget> _widgets = new();
    private readonly object _lock = new();

    public WidgetStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path))
                _widgets = JsonSerializer.Deserialize<List<Widget>>(File.ReadAllText(path), json) ?? new();
        }
        catch { _widgets = new(); }

        // Sizes written under the old four-name set, brought forward. Done on load rather than by a migration
        // script because there is exactly one copy of this data and it is read through here.
        lock (_lock)
        {
            foreach (var w in _widgets)
            {
                var migrated = WidgetSizes.Migrate(w.Size);
                if (!string.Equals(migrated, w.Size, StringComparison.Ordinal))
                {
                    Console.WriteLine($"[widget] {w.Id} \"{w.Title}\" size {w.Size} -> {migrated}");
                    w.Size = migrated;
                }
            }
        }

        /*
         * A job that was in flight when the process died is never going to finish — its task is gone with the process.
         *
         * What matters here is WHICH job it was, and getting that wrong is what wrecked a working panel. A panel being
         * BUILT has nothing behind it, so a restart leaves it owed another attempt. A panel being ADJUSTED or repaired
         * is already live with a component and a feed that work, and it is marked building only because somebody asked
         * for a change — so the honest outcome is the panel it already was, minus the change. Treating those the same
         * meant an hourly weather panel that had worked for days came back from a restart as "the build was interrupted",
         * was rebuilt from its original one-line description, and lost everything it had been adjusted into.
         *
         * The task id goes in both cases, and that was the other half of it. Left pointing at a task from a dead
         * process, the panel says a job is attached to it: the menu offers to cancel a build nobody is doing, the page
         * shows progress that will never move, and nothing can be started because something is apparently already
         * running.
         */
        lock (_lock)
        {
            // A recorded fault that this version can see was never a fault: a browser complaining about a name the
            // scope HAS was a tab running an older bundle, and the guard for that only knew Chrome's phrasing until
            // now. Left on the panel, each one is an invitation to rewrite something that works — which is the exact
            // thing the panel's menu is there to offer, so the offer has to be true.
            foreach (var w in _widgets)
            {
                if (w.RenderError is not { Length: > 0 } threw) continue;
                if (Stale(threw) is not { } name || !ScopeNames.Contains(name)) continue;

                w.RenderError = null;
                if (string.Equals(w.Error, threw, StringComparison.Ordinal)) w.Error = OlderBundle;
            }

            foreach (var w in _widgets.Where(w => w.Status == WidgetStatus.Building))
            {
                // A build that STOPPED TO ASK was not interrupted by anything: it is waiting on the user, and the
                // question it recorded is the proof. It keeps its task and its question, because the answer is what
                // restarts it and the panel is where that answer gets given.
                if (w.Asks is { Length: > 0 }) continue;

                // Whatever else it was doing, nobody is doing it now.
                w.TaskId = null;
                // And its design goes with it. Left up, a panel would sit there dimmed with a spinner over invented
                // numbers, looking like a build in progress, when the run that drew it died with the process.
                w.Design = null;
                w.Sample = null;
                w.Stage = null;
                w.Asks = null;

                // It already works. The change it was in the middle of is lost; the panel is not.
                if (w.Kind is { Length: > 0 } || w.OwnCode is { Length: > 0 })
                {
                    w.Status = WidgetStatus.Live;
                    w.Interrupted = false;
                    w.Error = null;
                    // Due a load, because whatever it has on screen is as old as the process that died.
                    w.NextRefresh = w.ServerLoads ? DateTimeOffset.UtcNow : null;
                    Console.WriteLine($"[widget] {w.Id} \"{w.Title}\" was mid-change when the process went — " +
                                      "it is still the panel it was, and the change is dropped");
                    continue;
                }

                w.Status = WidgetStatus.Failed;
                w.Interrupted = w.RestartRetries < RestartRetryLimit;
                w.Error = w.Interrupted
                    ? "The build was interrupted by a restart. Picking it up again."
                    : "The build keeps being interrupted. Ask again to rebuild it.";
            }
        }
    }

    /// <summary>How many restarts will be made good automatically before it goes back to asking.</summary>
    public const int RestartRetryLimit = 3;

    /// <summary>
    /// Panels whose build a restart killed, and which are owed another attempt.
    /// </summary>
    /// <remarks>
    /// Claimed rather than listed: the retry count goes up and the flag comes off here, so two callers cannot both
    /// decide to rebuild the same panel and a restart during the retry does not reset the cap.
    /// </remarks>
    public IReadOnlyList<Widget> ClaimInterrupted()
    {
        lock (_lock)
        {
            var owed = _widgets.Where(w => w.Interrupted).ToList();
            foreach (var w in owed)
            {
                w.Interrupted = false;
                w.RestartRetries++;
            }

            if (owed.Count > 0) Save();
            return owed;
        }
    }

    public IReadOnlyList<Widget> All()
    {
        lock (_lock)
            return _widgets
                .OrderByDescending(w => w.Pinned)
                .ThenByDescending(w => w.Priority)
                .ThenByDescending(w => w.Created)
                .ToList();
    }

    public Widget? Get(string id)
    {
        lock (_lock) return _widgets.FirstOrDefault(w => w.Id == id);
    }

    /// <summary>
    /// Reserve a place on the page for something being built.
    ///
    /// <para>
    /// The panel exists before it works, on purpose. Building one takes a minute or two of real work, and the
    /// alternative is the user asking for something and the page not changing — which reads as nothing having
    /// happened. It shows what it is and that it is being built.
    /// </para>
    /// </summary>
    public Widget Reserve(string title, string size, int priority, string? why, string? session, bool proposed)
    {
        lock (_lock)
        {
            // Asking twice about the same thing rebuilds that panel rather than growing a second one beside it.
            var existing = _widgets.FirstOrDefault(w =>
                string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));

            var w = existing ?? new Widget
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Created = DateTimeOffset.UtcNow,
            };

            w.Title = title;
            w.Size = WidgetSizes.Migrate(size);
            w.Priority = Math.Clamp(priority, 0, 100);
            w.Why = why ?? w.Why;
            w.Session = session ?? w.Session;
            w.Status = proposed ? WidgetStatus.Proposed : WidgetStatus.Building;
            w.Error = null;

            if (existing is null) _widgets.Add(w);
            Save();
            return w;
        }
    }

    /// <summary>
    /// Its build has stopped to ask something — or has stopped needing to. Null clears the ask.
    /// </summary>
    public void Asked(string id, string? question)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return;

            var text = question?.Trim();
            var next = string.IsNullOrEmpty(text) ? null : text;
            if (string.Equals(w.Asks, next, StringComparison.Ordinal)) return;

            w.Asks = next;
            Save();
        }
    }

    /// <summary>Note which task is building it, so the panel can show its progress.</summary>
    public void Building(string id, string taskId)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return;
            w.TaskId = taskId;
            w.Status = WidgetStatus.Building;
            w.BuildingSince = DateTimeOffset.UtcNow;
            // Whatever a previous attempt drew is not this attempt's design.
            w.Design = null;
            w.Sample = null;
            w.Stage = null;
            Save();
        }
    }

    /// <summary>
    /// Show the component early, rendering made-up data, and say what is happening to it.
    /// </summary>
    /// <remarks>
    /// The panel becomes watchable at this point: the design is on screen, dimmed, with the stage written over it, and
    /// the rest of the build happens underneath. Deliberately does NOT go live — the data is invented, and a panel that
    /// looked finished while showing numbers nobody fetched would be worse than one that admits it is still working.
    /// </remarks>
    public bool Designed(string id, string code, string? sample, string? stage = null)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return false;

            w.Design = code;
            w.Sample = sample;
            w.Stage = stage ?? Stages.Feed;
            w.Status = WidgetStatus.Building;
            Save();
            return true;
        }
    }

    /// <summary>Move a build on to its next stage, for the words on the panel.</summary>
    public void Reached(string id, string stage)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null || w.Status != WidgetStatus.Building) return;
            if (string.Equals(w.Stage, stage, StringComparison.Ordinal)) return;

            w.Stage = stage;
            Save();
        }
    }

    // ── the build, state by state ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start this panel's build record, or hand back the one it already has.
    /// </summary>
    /// <remarks>
    /// A record survives the run that made it, so a build picked up after a restart re-enters at the state it died
    /// in. Only a genuinely new ask — different words on the same panel — starts again from the top, because the
    /// first state's whole job is to agree what those words mean.
    /// </remarks>
    public PanelBuildRecord Begin(string id, string shows)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return new PanelBuildRecord { PanelId = id, Shows = shows };

            if (w.Build is { } existing && string.Equals(existing.Shows, shows, StringComparison.Ordinal)
                && existing.Step is not (PanelStep.Done or PanelStep.Blocked))
                return existing;

            w.Build = new PanelBuildRecord { PanelId = id, Shows = shows };
            Save();
            return w.Build;
        }
    }

    public PanelBuildRecord? BuildOf(string id)
    {
        lock (_lock) return _widgets.FirstOrDefault(x => x.Id == id)?.Build;
    }

    /// <summary>Write down whatever a state produced. The record is the panel's, so saving it is the store's job.</summary>
    public bool Produced(string id, Action<PanelBuildRecord> write)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w?.Build is not { } record) return false;
            write(record);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Point a panel at a library kind with these parameter values. The moment it becomes real.
    ///
    /// <para>
    /// The instance stores only what makes it particular — which kind, which values, and the feed url those values
    /// produce. The component itself stays on the kind, so a fix to one flight panel fixes every flight panel, and
    /// a second flight is a row in a file rather than a build.
    /// </para>
    /// </summary>
    public bool Attach(string id, WidgetKind kind, IReadOnlyDictionary<string, string>? values, string? refresh = null)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return false;

            w.Kind = kind.Name;
            w.ServerLoads = LoaderModes.ServerLoads(kind.Loader.Mode);
            w.Params = values is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            w.OwnCode = null;

            // Published is not the same as working, and for a few seconds it is the difference between a panel with a
            // number on it and a panel saying "Loading…". So a panel that has a design keeps showing it, and says it
            // is being tested, until a load actually produces something. Nothing loads a client-mode panel here — the
            // page does that for itself — so there is nothing to wait for and the real thing goes up at once.
            w.Design = w.ServerLoads ? w.Design : null;
            w.Sample = w.ServerLoads ? w.Sample : null;
            w.Stage = w.Design is { Length: > 0 } ? Stages.Testing : null;
            w.Refresh = (refresh ?? kind.Refresh ?? "").Trim();
            w.Status = WidgetStatus.Live;
            w.Error = null;
            w.TaskId = null;
            w.Asks = null;
            // New code hasn't thrown yet. Clearing this is what lets a fix be judged on its own merits rather than
            // inheriting the last version's fault forever.
            w.RenderError = null;
            w.Fixes = 0;
            // Due now: the kind's feed was verified when it was built, so the first fetch should land immediately
            // rather than at the far end of a "daily at 07:00".
            // Due now: the kind's loader was verified when it was built, so the first load should land immediately
            // rather than at the far end of a "daily at 07:00". Nothing to wait for in client mode.
            w.NextRefresh = w.ServerLoads ? DateTimeOffset.UtcNow : null;
            Save();
            return true;
        }
    }

    /// <summary>The build couldn't be done. Kept with the reason — a panel that silently never appears is worse.</summary>
    /// <remarks>
    /// <para>
    /// The design stays up, and that is a deliberate reversal. It used to come down on every ending, on the argument
    /// that invented numbers must never be mistaken for the answer — right about the numbers, wrong about what was
    /// left behind. A build that drew the panel, showed it to the user and then couldn't find a feed took the whole
    /// thing away and replaced it with two lines of red text, so the user lost the one part that had worked and had
    /// nothing to react to.
    /// </para>
    /// <para>
    /// It stays because it is not pretending: the stage goes, so nothing spins, and the reason sits over it. The
    /// panel says "this is what you asked for, and here is what it still needs" instead of "no".
    /// </para>
    /// <para>
    /// The exception is a REBUILD that failed. There a working panel is underneath, and the working panel is the
    /// better thing to show — a half-built design over a live one would be a step backwards.
    /// </para>
    /// </remarks>
    public void Failed(string id, string why)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return;
            // A rebuild that fails leaves the working version alone: half a panel is worse than an old one.
            if (w.Kind is { Length: > 0 } || w.OwnCode is { Length: > 0 })
            {
                w.Status = WidgetStatus.Live;
                w.Error = why;
                w.Design = null;
                w.Sample = null;
            }
            else
            {
                w.Status = WidgetStatus.Failed;
                w.Error = why;
            }
            w.TaskId = null;
            // A build that gave up is not waiting on the user any more, whatever it asked on the way.
            w.Asks = null;
            // Nothing is happening to it, so nothing says anything is. This is what stops the kept design reading as
            // a build still in progress.
            w.Stage = null;
            Save();
        }
    }

    /// <summary>
    /// Panels that have been "being built" for longer than any build plausibly takes.
    ///
    /// <para>
    /// The backstop. A build is supposed to report its own end, and every path that can end one is supposed to say
    /// so — but a path that forgets leaves a progress bar turning for ever, and the user finds that before the
    /// author does. A timed-out build once left the camera panel saying "Fixing…" for forty minutes with nothing
    /// alive behind it, because the cleanup sat below an early return. This does not care why: past the deadline
    /// with no publish, the panel stops pretending.
    /// </para>
    /// <para>
    /// Deliberately generous. A real build reads pages, drives a browser and writes a component; twenty minutes is
    /// long enough that a slow one is never cut off, and short enough that nobody sits watching a dead bar.
    /// </para>
    /// </summary>
    public IReadOnlyList<Widget> Stalled(DateTimeOffset now, TimeSpan? after = null)
    {
        var deadline = after ?? TimeSpan.FromMinutes(20);
        lock (_lock)
            return _widgets
                .Where(w => w.Status == WidgetStatus.Building)
                // No stamp at all is itself suspicious — it predates the stamp or was written by hand — so those are
                // judged on when the panel was created instead of being given a free pass forever.
                .Where(w => now - (w.BuildingSince ?? w.Created) > deadline)
                // Except one that is waiting on the user: it is not stalled, it is being patient, and killing it
                // would throw away the answer it is about to get.
                .Where(w => string.IsNullOrWhiteSpace(w.Asks))
                .ToList();
    }

    /// <summary>Every panel made from a kind — what a fix to that kind is about to change.</summary>
    public IReadOnlyList<Widget> Instances(string kindName)
    {
        lock (_lock)
            return _widgets.Where(w => string.Equals(w.Kind, kindName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// A kind has been revised, so every panel made from it takes the new feed template and fetches again.
    ///
    /// <para>
    /// Without this, fixing the flight kind would fix the panel whose build fixed it and leave its siblings on the
    /// old broken component — which is the opposite of the point of having a library.
    /// </para>
    /// </summary>
    public int Requalify(WidgetKind kind)
    {
        lock (_lock)
        {
            var mine = _widgets
                .Where(w => string.Equals(w.Kind, kind.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var w in mine)
            {
                w.ServerLoads = LoaderModes.ServerLoads(kind.Loader.Mode);
                w.RenderError = null;
                w.Fixes = 0;

                // A republish can RENAME the kind's parameters, and then the values this panel has stopped fitting
                // the url it is now expected to fill. It happened between two weather panels: a 7-day build
                // republished city-weather with latitude/longitude/city, the older panel still held lat/lon/place,
                // and its feed url kept the literal "{latitude}" in it for ever. Every load failed, the footer said
                // "stale" in red, the box was empty, and nothing anywhere said why.
                //
                // Adopting the new names by guesswork would be worse than useless — lat is obviously latitude and
                // "place" is arguably anything — so the panel says plainly that it needs its values again, which is
                // a thing a person or a fill task can act on. Silence was the only unacceptable option.
                var missing = kind.Missing(w.Params);
                if (missing.Count > 0)
                {
                    w.Status = WidgetStatus.Failed;
                    w.Error = $"The \"{kind.Name}\" kind was rebuilt and now takes " +
                              $"{string.Join(", ", missing.Select(m => m.Name))}, which this panel doesn't have " +
                              $"(it has {(w.Params.Count == 0 ? "nothing" : string.Join(", ", w.Params.Keys))}). " +
                              "Ask for it again to give it the values.";
                    w.NextRefresh = null;
                    continue;
                }

                if (w.Status is WidgetStatus.Live or WidgetStatus.Failed)
                {
                    w.Status = WidgetStatus.Live;
                    w.Error = null;
                    w.NextRefresh = w.ServerLoads ? DateTimeOffset.UtcNow : null;
                }
            }

            if (mine.Count > 0) Save();
            return mine.Count;
        }
    }

    /// <summary>
    /// The names a component is given, so a stale browser can be told apart from a real fault.
    /// </summary>
    /// <remarks>
    /// Read out of the kit rather than listed here. The hand-kept version of this list had eight names in it and the
    /// kit had twenty-three, so the guard covered a third of the cases it was written for — and the missing two-thirds
    /// each cost a working panel a needless rewrite.
    /// </remarks>
    private static readonly HashSet<string> ScopeNames = new(WidgetScope.Names, StringComparer.Ordinal);

    /// <summary>
    /// What a panel says when the fault reported about it belongs to the browser rather than to the panel.
    ///
    /// <para>
    /// A constant because two places have to agree on it: the one that records it, and <see cref="Widget.Ailing"/>,
    /// which must NOT count it as something to mend. It is advice to the reader, not a defect in the thing.
    /// </para>
    /// </summary>
    internal const string OlderBundle = "This page is running an older version — reload it.";

    /// <summary>
    /// The component threw in the browser. Recorded, and nothing else.
    /// </summary>
    /// <returns>True if this is new information, for the log.</returns>
    /// <remarks>
    /// <para>
    /// This used to answer "and should it be sent back to be repaired?", and the answer was yes, automatically, from
    /// whichever tab happened to report first. That is now the user's call — see <see cref="Ailing"/>.
    /// </para>
    /// </remarks>
    public bool Broke(string id, string error)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return false;

            var message = (error ?? "").Trim();
            if (message.Length > 400) message = message[..400] + "…";

            // A browser running an older bundle than the server reports faults about names the CURRENT scope has, and
            // every browser words it differently: Chrome says "Panel is not defined", Safari says "Can't find
            // variable: Panel". A name the scope provides cannot be missing from code the server just served, so this
            // is a page to reload, not a panel to rewrite. Both phrasings, because matching only Chrome's is how a
            // panel that had just been made to look right was sent straight back to be rebuilt.
            if (Stale(message) is { } name && ScopeNames.Contains(name))
            {
                w.Error = OlderBundle;
                Save();
                return false;
            }

            // The same fault reported again by a second render is one fault, not two.
            bool isNew = !string.Equals(w.RenderError, message, StringComparison.Ordinal);
            w.RenderError = message;
            w.Error = message;
            Save();
            return isNew;
        }
    }

    /// <summary>The name a "no such variable" message is complaining about, however the browser phrases it.</summary>
    internal static string? Stale(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            message, @"^(?:(\w+) is not defined|Can't find variable: (\w+)|(\w+) is not defined\b)");
        if (!match.Success) return null;
        for (var group = 1; group < match.Groups.Count; group++)
            if (match.Groups[group].Success && match.Groups[group].Value.Length > 0)
                return match.Groups[group].Value;
        return null;
    }

    public bool Edit(string id, string? title = null, string? size = null, int? priority = null,
        bool? pinned = null, string? status = null, string? refresh = null)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return false;

            if (title is { Length: > 0 }) w.Title = title.Trim();
            if (WidgetSizes.Known(size)) w.Size = size!.ToLowerInvariant();
            if (priority is not null) w.Priority = Math.Clamp(priority.Value, 0, 100);
            if (pinned is not null) w.Pinned = pinned.Value;

            if (refresh is not null)
            {
                var trimmed = refresh.Trim();
                if (trimmed.Length > 0 && !ScheduleStore.TryParseRepeat(trimmed, DateTimeOffset.Now, out _))
                    return false;
                w.Refresh = trimmed;
                w.NextRefresh = trimmed.Length == 0 ? null : DateTimeOffset.UtcNow;
            }

            if (status is WidgetStatus.Live or WidgetStatus.Paused)
            {
                w.Status = status;
                if (status == WidgetStatus.Live) w.NextRefresh = DateTimeOffset.UtcNow;
            }

            Save();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var gone = _widgets.RemoveAll(w => w.Id == id) > 0;
            if (gone) Save();
            return gone;
        }
    }

    /// <summary>Panels due a reload. Only live ones attached to a kind.</summary>
    public IReadOnlyList<Widget> Due(DateTimeOffset nowUtc)
    {
        lock (_lock)
            return _widgets
                .Where(w => w.Status == WidgetStatus.Live && w.Kind is { Length: > 0 } && w.ServerLoads
                         && w.NextRefresh is { } next && next <= nowUtc)
                .ToList();
    }

    /// <summary>
    /// Someone opened the home page. Anything with nothing to show, or overdue, is marked due now.
    ///
    /// <para>
    /// The page is the trigger, which is the robust arrangement: a panel is refreshed because it is being LOOKED at,
    /// not only because a timer said so. It removes a whole class of dead panel — one whose cadence is tomorrow
    /// morning, one whose next-run was never set, one that lost its data to a rename — because opening the page is
    /// enough to fix it. What is on screen while that happens is whatever it last knew, which is the right trade:
    /// stale and honest about its age beats a spinner.
    /// </para>
    /// <para>
    /// Learned the hard way. A field rename dropped the stored data from nine panels, and because each one's next
    /// load was hours off, the whole home page sat on "Loading…" with no way to hurry it along.
    /// </para>
    /// </summary>
    /// <returns>How many were woken.</returns>
    public int Nudge(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var stir = _widgets
                .Where(w => w.Status == WidgetStatus.Live && w.Kind is { Length: > 0 } && w.ServerLoads)
                .Where(w => w.Data is null or "" || w.NextRefresh is null || w.NextRefresh <= nowUtc)
                .ToList();

            foreach (var w in stir) w.NextRefresh = nowUtc;
            if (stir.Count > 0) Save();
            return stir.Count;
        }
    }

    /// <summary>
    /// A load has failed the same way twice — the fault is the loader, not the network.
    /// </summary>
    /// <remarks>
    /// Recorded, not acted on. This used to send the panel off to be rewritten, and the reasoning was sound as far as
    /// it went: the same message twice is not a bad night on the network. What it missed is that "the same twice" is
    /// still routinely transient — a page read a moment too early, a source half-rendered — and the cost of being wrong
    /// is a working panel replaced by a guess. So it marks the panel as worth a look and stops there.
    /// </remarks>
    /// <returns>True the first time this particular failure repeats, so it is logged once rather than hourly.</returns>
    public bool LoaderStuck(string id)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null || w.Error is not { Length: > 0 } error) return false;
            if (w.Status != WidgetStatus.Live) return false;

            if (!string.Equals(w.LastLoadError, error, StringComparison.Ordinal))
            {
                w.LastLoadError = error;
                Save();
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The page reported resources it couldn't load. Recorded; the verdict is drawn elsewhere.
    /// </summary>
    /// <returns>True if this is new information.</returns>
    public bool Faulted(string id, IReadOnlyList<string> faults)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return false;

            var trimmed = faults
                .Select(f => (f ?? "").Trim())
                .Where(f => f.Length is > 0 and < 400)
                .Distinct(StringComparer.Ordinal)
                .Take(6)
                .ToList();

            if (trimmed.Count == 0) return false;
            if (trimmed.SequenceEqual(w.Faults, StringComparer.Ordinal)) return false;

            w.Faults = trimmed;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Somebody looked at the panel. Records what they saw.
    /// </summary>
    /// <remarks>
    /// It used to also decide, by itself, that a bad verdict meant a rebuild. One transient — a feed that answered
    /// oddly on the load right after a build, a tab running yesterday's bundle — and a panel that was working got
    /// rewritten from scratch, which is how a good panel becomes a worse one. What it saw is recorded; what to do
    /// about it is the user's, from the panel's own menu.
    /// </remarks>
    public void Looked(string id, bool working, string seen)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return;

            w.LookedAt = DateTimeOffset.UtcNow;
            w.Seen = seen;

            if (working)
            {
                // A panel that has been seen working has nothing held against it. This is the only check that can
                // clear the record, because it is the only one that tests what the user actually experiences.
                w.Error = null;
                w.RenderError = null;
                w.LastLoadError = null;
                w.Faults = new();
            }
            else
            {
                w.Error = seen;
            }

            Save();
        }
    }

    /// <summary>A load finished. Records what it produced and books the next one.</summary>
    /// <summary>
    /// A panel that has just shown something for the very first time.
    /// </summary>
    /// <remarks>
    /// This is when a panel becomes worth looking at, and the only moment that is true of. Photographing one at the end
    /// of its BUILD races the first load and loses it whenever loading is not instant — a panel whose data lives behind
    /// the user's own logged-in session took three minutes, so the photograph caught its loading state, the judge
    /// correctly reported what it saw, and a working panel was marked broken and sent to be rebuilt.
    /// <para>Raised outside the lock, because whatever handles it goes and drives a browser.</para>
    /// </remarks>
    public Action<Widget>? FirstShowed { get; set; }

    public void Loaded(string id, string? dataJson, string? error, IReadOnlyList<string>? track = null)
    {
        Widget? showed = null;

        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null) return;

            // Its first sight of anything: no data before, data now, and never yet looked at.
            if (error is null && dataJson is { Length: > 0 } && w.Data is not { Length: > 0 } && w.LookedAt is null)
                showed = w;

            w.FetchedAt = DateTimeOffset.UtcNow;
            w.Error = error;
            // Whatever came back, the design has been overtaken by it. A failed first load shows its error rather
            // than sitting behind made-up data that would read as the answer.
            w.Design = null;
            w.Sample = null;
            w.Stage = null;
            // A failed load keeps the last good data: a panel that was right ten minutes ago beats a blank one, as
            // long as it admits its age, which the footer does.
            if (dataJson is { Length: > 0 }) w.Data = dataJson;
            // A load that worked clears the record, so two failures a week apart are never mistaken for a pattern.
            if (error is null) w.LastLoadError = null;

            // And it is remembered. Only on a load that produced something: recording the last good value again
            // every time a source is down would draw a flat line through the outage and call it data.
            if (error is null && dataJson is { Length: > 0 } && track is { Count: > 0 })
            {
                var values = WidgetMemory.Read(dataJson, track);
                if (values.Count > 0) w.History = WidgetMemory.Append(w.History, values, DateTimeOffset.UtcNow);
            }

            w.NextRefresh = w.Refresh.Length > 0 &&
                            ScheduleStore.TryParseRepeat(w.Refresh, DateTimeOffset.Now, out var next)
                ? next
                : null;
            Save();
        }

        if (showed is not null) FirstShowed?.Invoke(showed);
    }

    public bool Wake(string id)
    {
        lock (_lock)
        {
            var w = _widgets.FirstOrDefault(x => x.Id == id);
            if (w is null || w.Kind is not { Length: > 0 } || !w.ServerLoads) return false;
            w.NextRefresh = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    /// <summary>What the model is told is on the page, so it revises rather than piling on.</summary>
    public string Describe()
    {
        var all = All();
        if (all.Count == 0) return "";

        var lines = all.Select(w =>
            $"- {w.Id} \"{w.Title}\" [{w.Size}, priority {w.Priority}, {w.Status}" +
            (w.Pinned ? ", pinned" : "") + "]" +
            (w.Kind is { Length: > 0 } k ? $" kind {k}" : "") +
            (w.Params.Count > 0 ? $" ({string.Join(", ", w.Params.Select(kv => $"{kv.Key}={Head(kv.Value, 30)}"))})" : "") +
            (w.Refresh.Length > 0 ? $", {w.Refresh}" : "") +
            (w.Error is { Length: > 0 } e ? $" — PROBLEM: {Head(e, 80)}" : ""));

        return "\n\nOn the user's home page (highest first):\n" + string.Join("\n", lines) +
               "\nRe-rank or resize one of these with widget_update rather than building a second about the same " +
               "thing; something that has stopped mattering should be removed. Rebuild one only if its feed has " +
               "broken or the user wants it to show something different.\n";
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_widgets, _json));
        }
        catch { /* a layout is not worth breaking a run over */ }
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
