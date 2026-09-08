using System.Text;
using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// What Proact did, and what it is allowed to do. See PROACT_SPEC.md.
///
/// <para>
/// Everything else in this system is PULLED. A panel refreshes when its timer says so, a task runs when it was
/// scheduled, a watcher fires when something arrives — even the two that look proactive are reactive underneath.
/// Proact is the first thing here that wakes up and asks what would help, with nothing having happened to prompt it.
/// </para>
/// <para>
/// Which is exactly why the interesting half of this file is <see cref="Proact"/> — the boundary. A loop that acts
/// unattended a hundred times a day is safe only because of one rule: <b>it may prepare, it may never commit.</b>
/// Write, never send. Gather, never spend. Draft, never dispatch. Anything a reasonable person would want a say in
/// becomes a proposal rather than an act.
/// </para>
/// </summary>
public static class ProactKinds
{
    /// <summary>A fact about their situation they would want to know. Often the most valuable, and has no artefact.</summary>
    public const string Noticed = "noticed";

    /// <summary>Did the work and left it ready: a document, a shortlist, a draft, a gathered set of numbers.</summary>
    public const string Prepared = "prepared";

    /// <summary>Something it will not do unattended, reduced to a tick or a cross. See <see cref="ProactAction.Plan"/>.</summary>
    public const string Proposed = "proposed";

    /// <summary>
    /// It looked properly and found nothing worth reporting.
    /// </summary>
    /// <remarks>
    /// Recorded rather than dropped for two reasons. A loop that only logs its wins reads as cleverer than it is, and
    /// "I already read that" is the only thing that stops it reading the same project every day for a week.
    /// </remarks>
    public const string Looked = "looked";

    public static readonly string[] All = { Noticed, Prepared, Proposed, Looked };

    public static bool Known(string? kind) =>
        All.Contains((kind ?? "").Trim().ToLowerInvariant());
}

/// <summary>How a proposal was answered.</summary>
public static class ProactAnswers
{
    public const string Pending = "pending";

    /// <summary>The tick. Which is the COMMIT — see <see cref="ProactAction.Plan"/>.</summary>
    public const string Ticked = "ticked";

    public const string Crossed = "crossed";

    /// <summary>Nobody answered in time and it stopped being worth answering.</summary>
    public const string Expired = "expired";
}

/// <summary>
/// One thing Proact did, with something to show for it.
///
/// <para>
/// Not "a thought" and not "a message". The record is made of these, the last 24 hours of them go into the next
/// tick's prompt, and the timeline is just this list rendered — so a field that isn't here is a thing the user can
/// never be told and the loop can never remember.
/// </para>
/// </summary>
public sealed class ProactAction
{
    public string Id { get; set; } = "";

    public DateTimeOffset At { get; set; }

    /// <summary>One of <see cref="ProactKinds"/>.</summary>
    public string Kind { get; set; } = ProactKinds.Noticed;

    /// <summary>
    /// The TITLE: one short line, in the user's terms rather than the system's.
    /// </summary>
    /// <remarks>
    /// Short because the timeline shows it on one line and truncates. The first real finding put three hundred
    /// characters in here — the group behind the restaurant, the address, the postcode, two sources and a caveat —
    /// which is a good paragraph and a terrible title, and on a list it became an ellipsis with the useful half
    /// hidden. The detail belongs in <see cref="Body"/>.
    /// </remarks>
    public string What { get; set; } = "";

    /// <summary>
    /// The whole of it, as markdown.
    /// </summary>
    /// <remarks>
    /// Rich on purpose: links inline, pictures inline, a list where a list is the answer. The timeline shows one
    /// truncated line of it and the dialog shows all of it, so this is where a finding is allowed to be as long as
    /// it needs to be — which is also what stops the title from trying to be both things at once.
    /// </remarks>
    public string Body { get; set; } = "";

    /// <summary>
    /// What about their situation prompted it.
    /// </summary>
    /// <remarks>
    /// The anti-corny test, made structural. "Hey, have you thought about a to-do list" has no answer to this;
    /// anything drawn from their actual week does. A blank one is the signal that the action should not have happened.
    /// </remarks>
    public string Why { get; set; } = "";

    /// <summary>The artefact, where there is one: a file name, a panel id, a finding. Null for a bare notice.</summary>
    public string? Produced { get; set; }

    /// <summary>
    /// Where it got this, as things worth actually clicking.
    /// </summary>
    /// <remarks>
    /// Not citations for their own sake. A finding about a place that is opening is worth far more with the operator's
    /// own page and the article that dated it, because the next thing a person does with a recommendation is go and
    /// look at it — and a timeline that makes them search for what it just told them about has made more work rather
    /// than less. Deliberately NOT verified: a link is followed by a human in a real browser, and plenty of sites
    /// answer a server request with 403 while working perfectly for a person.
    /// </remarks>
    public List<ProactLink> Links { get; set; } = new();

    /// <summary>
    /// Pictures, where a picture is the answer.
    /// </summary>
    /// <remarks>
    /// Verified before they are ever stored, which is the opposite of the rule for links and for a specific reason:
    /// these render INLINE. A link that is wrong costs a click; an image that is wrong is a broken-image icon sitting
    /// on the page, and this system has already lost a day to exactly that — a camera panel showing a broken image
    /// with every mechanism reporting it healthy. So an image nobody has fetched does not go on the timeline.
    /// </remarks>
    public List<ProactImage> Images { get; set; } = new();

    /// <summary>
    /// up | down | null. What they thought of it.
    /// </summary>
    /// <remarks>
    /// A different question from a proposal's tick. A cross says "not this thing"; a thumb says "not this KIND of
    /// thing", which is the more useful of the two and the only signal here that generalises. It is what
    /// <see cref="ProactStore.Taste"/> is made of.
    /// </remarks>
    public string? Verdict { get; set; }

    public DateTimeOffset? VotedAt { get; set; }

    /// <summary>
    /// Put away, and not shown again.
    /// </summary>
    /// <remarks>
    /// Different from <see cref="Seen"/>, which only says somebody's eyes passed over it. This is a decision: read,
    /// finished with, go away. Without it the only way for the list to get shorter is for things to age out, and a
    /// surface that accumulates is one that stops being opened.
    ///
    /// <para>
    /// It never dismisses an unanswered proposal — that would silently discard a decision the user still owes.
    /// </para>
    /// </remarks>
    public bool Dismissed { get; set; }

    /// <summary>Which mode produced it — attend | discover. So a timeline can show where the deep dives went.</summary>
    public string Mode { get; set; } = ProactModes.Attend;

    /// <summary>
    /// What this was ABOUT, in two or three words: "dinner spots", "the Lisbon trip", "the boiler".
    /// </summary>
    /// <remarks>
    /// The field that makes breadth enforceable instead of merely requested. The 24-hour log already forbids
    /// repeating an ACTION, and that turned out not to be the problem: it went back to the same restaurant project
    /// twice in a row with a genuinely different finding each time, so nothing was repeated and the effect was still
    /// an assistant that had stopped looking around. A follow-up is not a new thing.
    ///
    /// <para>
    /// Coarse on purpose. Two or three words is enough for "have I just been here", and anything finer would let
    /// "dinner spots" and "restaurants nearby" read as different subjects — which is exactly the evasion it exists
    /// to prevent.
    /// </para>
    /// </remarks>
    public string Subject { get; set; } = "";

    // ---- the proposal half, null on every other kind -----------------------------------------------------

    /// <summary>
    /// The steps that will actually run when the user ticks it.
    /// </summary>
    /// <remarks>
    /// NOT explaining copy. The instinct is to write a paragraph about how Proact works; nobody needs that. This is
    /// the real sequence, short enough to read in five seconds, and its whole job is to make ticking SAFE — a person
    /// has to know what they are authorising before they authorise it.
    /// </remarks>
    public string? Plan { get; set; }

    /// <summary>
    /// What cannot be undone, and by when it has to be decided.
    /// </summary>
    /// <remarks>
    /// Mandatory on anything irreversible, and the one place a proposal is allowed to slow the user down. A tick on
    /// something that cannot be taken back must show that before the click rather than after it.
    /// </remarks>
    public string? Catch { get; set; }

    /// <summary>When this stops being worth answering. "Book for tonight" is meaningless tomorrow.</summary>
    public DateTimeOffset? Expires { get; set; }

    /// <summary>One of <see cref="ProactAnswers"/>. Null on anything that is not a proposal.</summary>
    public string? Answer { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>
    /// The task a tick dispatched, and where it got to.
    /// </summary>
    /// <remarks>
    /// The most expensive non-safety failure in the design is the tick that goes nowhere: the user spent trust on one
    /// click and nothing visibly happened, which teaches them never to click again. So acceptance is not a state
    /// change — it is a handover, and the handover's outcome comes back here and onto the timeline.
    /// </remarks>
    public string? TaskId { get; set; }

    public string? Outcome { get; set; }

    // ---- the record ---------------------------------------------------------------------------------------

    /// <summary>Has anybody looked at it. What makes the surface honest, and an unseen action is not licence to repeat it.</summary>
    public bool Seen { get; set; }

    public long Tokens { get; set; }

    public int Seconds { get; set; }

    /// <summary>The conversation a ticked proposal's work runs in, so the timeline can link to it.</summary>
    public string? Session { get; set; }

    public bool IsProposal => string.Equals(Kind, ProactKinds.Proposed, StringComparison.OrdinalIgnoreCase);

    public bool Outstanding =>
        IsProposal && string.Equals(Answer, ProactAnswers.Pending, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Something worth clicking, and what it is.</summary>
public sealed class ProactLink
{
    /// <summary>What it is, in a few words. "Their booking page", not "click here" and not the bare url.</summary>
    public string Label { get; set; } = "";

    public string Url { get; set; } = "";
}

/// <summary>A picture that has been fetched at least once.</summary>
public sealed class ProactImage
{
    public string Url { get; set; } = "";

    public string? Caption { get; set; }
}

/// <summary>What a thumb means.</summary>
public static class ProactVerdicts
{
    public const string Up = "up";
    public const string Down = "down";

    public static bool Known(string? v) =>
        v is Up or Down;
}

public static class ProactModes
{
    /// <summary>Cheap, on the user's interval, usually silent.</summary>
    public const string Attend = "attend";

    /// <summary>Expensive, rare, one thing properly — and deliberately NOT on the user's interval.</summary>
    public const string Discover = "discover";

    public static readonly string[] All = { Attend, Discover };
}

/// <summary>
/// One wakeup, whether it did anything or not.
/// </summary>
/// <remarks>
/// Kept separately from actions because of the arithmetic. At the fast end of the dial this is 288 rows a day, nearly
/// all of them "nothing had changed" — so ticks are what prove the loop is alive, and actions are what reach the
/// prompt. Conflating them would either crowd the prompt with noise or leave a stopped Proact looking identical to a
/// quiet one, and this system has already made the second mistake twice.
/// </remarks>
public sealed class ProactTickRecord
{
    public DateTimeOffset At { get; set; }
    public string Mode { get; set; } = ProactModes.Attend;

    /// <summary>Did anything change since the last look? False means the tick cost nothing at all.</summary>
    public bool Changed { get; set; }

    /// <summary>How many actions came out of it. Usually zero, and that is the design working.</summary>
    public int Acted { get; set; }

    /// <summary>Why it did nothing, when it looked and decided not to. The most useful line in the log.</summary>
    public string? Note { get; set; }

    /// <summary>Kept, because a Proact that quietly stopped is the worst outcome available.</summary>
    public string? Error { get; set; }

    public long Tokens { get; set; }
}

/// <summary>
/// One day's roundup: what it did, and what is still waiting.
///
/// <para>
/// The only thing about Proact that reaches out rather than waiting to be visited, and the reason it is a digest
/// rather than a notification is arithmetic. One message a day is one message a day whether it did two things or
/// twenty, so the roundup can never become the noise it exists to summarise. A per-action alert could.
/// </para>
/// <para>
/// Assembled from the record rather than written by a model. "At 08:40 I noticed X" is a fact this store already
/// holds, and paying for prose to restate it would add cost, latency and a chance of being wrong about what
/// happened. The one thing a model could add — a nicer sentence — is worth less than the guarantee that the
/// roundup and the timeline can never disagree.
/// </para>
/// </summary>
public sealed class ProactRoundup
{
    /// <summary>The local day it covers, as yyyy-MM-dd. One per day, ever.</summary>
    public string Day { get; set; } = "";

    public DateTimeOffset At { get; set; }

    /// <summary>The whole thing, ready to read.</summary>
    public string Text { get; set; } = "";

    public int Did { get; set; }

    /// <summary>Decisions still owed when the roundup was made.</summary>
    public int Waiting { get; set; }

    /// <summary>How many times it looked — the honest denominator under everything else.</summary>
    public int Looks { get; set; }

    public bool Seen { get; set; }
}

/// <summary>The toggle and the dial. Two controls, because they answer different questions.</summary>
public sealed class ProactSettings
{
    /// <summary>
    /// The hour, local, at which the day gets rounded up. Null turns the roundup off without turning Proact off.
    /// </summary>
    /// <remarks>
    /// A fixed hour rather than one inferred from when they stop working. Inferring it sounds cleverer and is worse:
    /// the roundup would arrive at a different time every day, which is exactly the property that makes a thing feel
    /// like an interruption rather than a routine.
    /// </remarks>
    public int? RoundupHour { get; set; } = 18;

    /// <summary>
    /// Off by default. On is a deliberate act.
    /// </summary>
    /// <remarks>
    /// "Is this running at all" is a trust decision, which is why it is not folded into the interval as an extra
    /// option. The reason to switch it off is almost never the reason to slow it down.
    /// </remarks>
    public bool On { get; set; }

    /// <summary>How often it goes out. One of <see cref="ProactIntervals"/>, stored as the words a feed uses.</summary>
    public string Every { get; set; } = "every 10 minutes";

    /// <summary>Not now, rather than never. Nothing runs until this passes.</summary>
    public DateTimeOffset? PausedUntil { get; set; }

    public bool Running(DateTimeOffset nowUtc) => On && (PausedUntil is not { } until || until <= nowUtc);
}

/// <summary>
/// The four cadences on offer.
/// </summary>
/// <remarks>
/// Four fixed choices rather than free text, for two reasons. Nobody can ask for every thirty seconds; and the
/// strings are the ones <see cref="ScheduleStore.TryParseRepeat"/> already parses for feeds and schedules, so the
/// dial needed no parser of its own.
///
/// <para>
/// It carries more weight than a preference. There is no daily cap on actions, so the interval IS the rate control —
/// sixty minutes instead of five is a twelvefold reduction in chances to act, decided by the person who has to read
/// the output rather than by a constant in this file.
/// </para>
/// </remarks>
public static class ProactIntervals
{
    public static readonly string[] All =
    {
        "every 5 minutes", "every 10 minutes", "every 30 minutes", "every 60 minutes",
    };

    public static bool Known(string? every) =>
        All.Contains((every ?? "").Trim().ToLowerInvariant());
}

/// <summary>
/// What Proact may hold.
///
/// <para>
/// The boundary is the TOOLSET, not the prompt. A capability it does not have cannot be talked into, and that is the
/// whole reason the one rule is enforceable — asking a model to be careful with a booking tool is not a safeguard,
/// it is a hope.
/// </para>
/// <para>
/// This has to be a whitelist because of how workers are assembled. A persona looks like the natural place to bound
/// one, and it is not: after a persona's capabilities are gathered, every tool from the default worker set that is
/// missing gets added back — deliberately, so that one bad routing decision costs some specialism rather than the
/// ability to work at all. Correct for a delegated task, fatal here. So Proact narrows the same way a panel build
/// does, with a named filter applied after assembly.
/// </para>
/// </summary>
public static class Proact
{
    /// <summary>
    /// The browser tools that can ACT on a page, and therefore the ones that could book a table.
    /// </summary>
    /// <remarks>
    /// The sharpest edge in the whole design. Reading the web is most of Proact's value, and a browser that can click
    /// and type is a browser that can buy, book, send and cancel — signed in as the user, on their own machine. So
    /// the browser comes in read-only, and these are named individually rather than caught by a prefix so that a new
    /// chrome tool arriving in future is refused by default rather than silently allowed.
    ///
    /// <para>
    /// chrome_javascript is on this list and it is the one that looks like it should not be. It is only "reading" in
    /// the sense that a shell is only text: arbitrary script in a page signed in as the user can click anything.
    /// Reading a page is what chrome_read_page is for.
    /// </para>
    /// </remarks>
    public static readonly string[] CanAct =
    {
        "chrome_click", "chrome_type_text", "chrome_press_key", "chrome_form_input",
        "chrome_file_upload", "chrome_javascript",
    };

    /// <summary>Everything Proact is allowed, by name.</summary>
    /// <remarks>
    /// A ceiling, on purpose. Elsewhere in this system an allow-list turned out to be a ceiling nobody wanted — the
    /// closed palette that made every picture icon-sized, the five feed names that could not spell a plugin. Here the
    /// ceiling IS the feature, and the cost of it being too low is that Proact cannot do something useful and says
    /// so, which is recoverable. The cost of it being too high is a booking nobody asked for.
    /// </remarks>
    public static readonly string[] Allowed =
    {
        // Its own record. Without these it can think but never act.
        "proact_noticed", "proact_prepared", "proact_propose", "read_their_lists",

        // Read what the user has.
        // search_memory, not "recall" — the name matters and getting it wrong is invisible. The whitelist said
        // "recall", the tool is called search_memory, so the first live run quietly dropped the one capability this
        // whole feature leans on hardest: knowing who the person is. It read as a working boundary because a
        // whitelist that is too tight fails silently and looks identical to one that is right.
        "read_file", "file_summary", "find_in_file", "list_files", "search_memory",

        // Write into its own workspace and hand things over. Not the user's own files.
        "write_file", "edit_file", "make_document", "download",

        // Research, read-only.
        //
        // Web research IS the browser in this system — the fetch-and-extract pair was deliberately removed because
        // it answered from snippets and dead HTML, so there is no search tool to allow and nothing missing from this
        // list. Going out to find something means navigating and reading, which is exactly what these do, and it is
        // also why the acting half of the browser had to be taken away by name rather than the whole of it.
        "chrome_tabs_context", "chrome_tabs_create", "chrome_tabs_close", "chrome_navigate",
        "chrome_read_page", "chrome_find", "chrome_scroll", "chrome_screenshot",
        "chrome_images", "chrome_grab_image", "chrome_inspect", "chrome_changes",
        "chrome_read_console", "chrome_read_network", "chrome_read_dialogs",

        // A panel is reversible, visible, and already gated by its own publish proof.
        "widget_design", "widget_publish", "widget_try", "widget_fill",
    };

    /// <summary>
    /// The conversations Proact opens, named so they can be told apart from a person's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every run needs a conversation to hold its task — that is how a task gets a workspace, a trace, a spend
    /// figure and recovery after a restart, and inventing a second mechanism for Proact would have meant giving up
    /// all four. The side effect is a conversation per run, and since nobody ever types in one it has no first user
    /// message to take a title from, so it lands in the chat list as "Untitled chat". A dozen of those a day is the
    /// sidebar becoming unusable.
    /// </para>
    /// <para>
    /// So the ids carry the prefix, and the chat list filters on it. Minted here rather than as a format string at
    /// each call site, because a listing that filters on a prefix nothing guarantees is a listing that breaks the
    /// day somebody writes the string slightly differently.
    /// </para>
    /// </remarks>
    public static string Conversation(string mode, DateTimeOffset at) =>
        $"proact-{mode}-{at:yyyyMMddHHmmss}";

    /// <summary>Where a ticked proposal's work happens.</summary>
    public static string ConversationForTicked(string actionId) => $"proact-do-{actionId}";

    /// <summary>Which proposal is this conversation carrying out, if any? Null when it is not one.</summary>
    /// <remarks>
    /// The reverse of <see cref="ConversationForTicked"/>, and the only link back from the work to the offer that
    /// caused it. Without it a tick could be dispatched and never resolved: <see cref="ProactStore.Finished"/> had
    /// existed unused since the day it was written, so every proposal the user ever ticked sat there reading as
    /// still in flight, however long ago it had finished or failed. The tick is the one commit this whole feature
    /// makes — it is the last thing that should be unauditable.
    /// </remarks>
    public static string? TickedIn(string? sessionId)
    {
        const string prefix = "proact-do-";
        var id = (sessionId ?? "").Trim();

        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && id.Length > prefix.Length
            ? id[prefix.Length..]
            : null;
    }

    /// <summary>
    /// Is this one of Proact LOOKING, rather than doing something the user asked for?
    /// </summary>
    /// <remarks>
    /// The line is who asked. A look is Proact's own errand and belongs on its timeline, not in a list of the
    /// person's conversations. A ticked proposal is work they authorised with a click — that one stays visible,
    /// because they may want to follow it, and it can ask them something.
    /// </remarks>
    public static bool Looking(string? sessionId)
    {
        var id = (sessionId ?? "").Trim();
        return ProactModes.All.Any(mode =>
            id.StartsWith($"proact-{mode}-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>May Proact hold this tool?</summary>
    public static bool Keep(string? name)
    {
        var tool = (name ?? "").Trim();
        if (tool.Length == 0) return false;

        // Denied first and explicitly, so that a name appearing on both lists is refused rather than allowed. The
        // ordering is the safe one and it should stay the safe one.
        if (CanAct.Contains(tool, StringComparer.OrdinalIgnoreCase)) return false;

        return Allowed.Contains(tool, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What was taken away, for the trace — a bounded worker should say what it is not holding.</summary>
    public static IReadOnlyList<string> Dropped(IEnumerable<string> offered) =>
        offered.Where(t => !Keep(t)).ToList();
}

/// <summary>Proact's record, on disk.</summary>
public sealed class ProactStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly object _lock = new();

    private ProactSettings _settings = new();
    private List<ProactAction> _actions = new();
    private List<ProactTickRecord> _ticks = new();
    private List<ProactRoundup> _roundups = new();

    /// <summary>Enough for the user to answer "is this any good", without the file growing for ever.</summary>
    private const int ActionsKept = 500;

    /// <summary>Three days at the fastest dial. Liveness is a recent question.</summary>
    private const int TicksKept = 900;

    /// <summary>Three weeks of roundups. Long enough to answer "is this actually any good?" — which it must be.</summary>
    private const int RoundupsKept = 21;

    /// <summary>
    /// How many proposals may be waiting at once.
    /// </summary>
    /// <remarks>
    /// Not one. That rule came from the panel suggester, where accepting meant filling in a form; a tick is one
    /// click, so a few clear decisions are reasonable where a queue of forms was not. But they are still decisions
    /// the user OWES, and an unpaid pile of them makes the whole surface feel like work — so it is low, and expiry
    /// does most of the job anyway.
    /// </remarks>
    public const int OutstandingProposals = 3;

    public ProactStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Kept>(File.ReadAllText(path), json) is { } kept)
            {
                _settings = kept.Settings ?? new();
                _actions = kept.Actions ?? new();
                _ticks = kept.Ticks ?? new();
                _roundups = kept.Roundups ?? new();
            }
        }
        catch { /* a corrupt file starts empty rather than stopping the app */ }
    }

    public ProactSettings Settings
    {
        get { lock (_lock) return Copy(_settings); }
    }

    /// <summary>Turn it on or off, set the dial, or pause it for a while.</summary>
    /// <remarks>
    /// A change to the interval takes effect on the NEXT tick rather than at the far end of the old one — the same
    /// courtesy a feed gets when its cadence is edited, and for the same reason: somebody who has just turned it up
    /// wants to see that it did something.
    /// </remarks>
    public ProactSettings Set(bool? on = null, string? every = null, DateTimeOffset? pausedUntil = null,
        bool clearPause = false, int? roundupHour = null)
    {
        lock (_lock)
        {
            if (on is { } running) _settings.On = running;
            if (every is { Length: > 0 } dial && ProactIntervals.Known(dial))
                _settings.Every = dial.Trim().ToLowerInvariant();
            if (clearPause) _settings.PausedUntil = null;
            else if (pausedUntil is { } until) _settings.PausedUntil = until;
            // 0-23 sets the hour; -1 turns the roundup off without turning Proact off.
            if (roundupHour is { } hour) _settings.RoundupHour = hour is >= 0 and <= 23 ? hour : null;
            Save();
            return Copy(_settings);
        }
    }

    public ProactAction Add(ProactAction action)
    {
        lock (_lock)
        {
            action.Id = string.IsNullOrWhiteSpace(action.Id) ? Guid.NewGuid().ToString("N")[..8] : action.Id;
            if (action.At == default) action.At = DateTimeOffset.UtcNow;
            if (action.IsProposal && string.IsNullOrWhiteSpace(action.Answer))
                action.Answer = ProactAnswers.Pending;

            _actions.Insert(0, action);
            if (_actions.Count > ActionsKept) _actions.RemoveRange(ActionsKept, _actions.Count - ActionsKept);
            Save();
            return action;
        }
    }

    public IReadOnlyList<ProactAction> Actions(int take = 100)
    {
        lock (_lock) return _actions.Take(take).ToList();
    }

    public ProactAction? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock) return _actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ProactAction> Since(DateTimeOffset from)
    {
        lock (_lock) return _actions.Where(a => a.At >= from).ToList();
    }

    /// <summary>Proposals still waiting for a tick or a cross.</summary>
    public IReadOnlyList<ProactAction> Waiting()
    {
        lock (_lock) return _actions.Where(a => a.Outstanding).ToList();
    }

    /// <summary>Is there room to offer another? See <see cref="OutstandingProposals"/>.</summary>
    public bool RoomToPropose()
    {
        lock (_lock) return _actions.Count(a => a.Outstanding) < OutstandingProposals;
    }

    /// <summary>
    /// Retire proposals nobody answered in time.
    /// </summary>
    /// <remarks>
    /// Done on every tick rather than lazily on read, because a stale proposal is worse than a missing one: it
    /// invites a click that would do the wrong thing, and a timeline of dead offers is a timeline nobody opens.
    /// </remarks>
    public int Expire(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var gone = 0;
            foreach (var a in _actions.Where(a => a.Outstanding && a.Expires is { } by && by <= nowUtc))
            {
                a.Answer = ProactAnswers.Expired;
                a.AnsweredAt = nowUtc;
                gone++;
            }
            if (gone > 0) Save();
            return gone;
        }
    }

    /// <summary>The tick or the cross. Returns the proposal, or null if it is not one or is already answered.</summary>
    public ProactAction? Answered(string id, bool ticked, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var a = _actions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (a is null || !a.Outstanding) return null;

            a.Answer = ticked ? ProactAnswers.Ticked : ProactAnswers.Crossed;
            a.AnsweredAt = nowUtc;
            a.Seen = true;
            Save();
            return a;
        }
    }

    /// <summary>Where a ticked proposal's work went, and what came of it.</summary>
    public void Dispatched(string id, string sessionId, string taskId)
    {
        lock (_lock)
        {
            if (_actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } a) return;
            a.Session = sessionId;
            a.TaskId = taskId;
            Save();
        }
    }

    public void Finished(string id, string outcome)
    {
        lock (_lock)
        {
            if (_actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } a) return;
            a.Outcome = outcome;
            Save();
        }
    }

    public void SeenAll()
    {
        lock (_lock)
        {
            var touched = false;
            // A proposal is not "seen" by looking at the list — it is seen by being answered. Marking it read here
            // would quietly clear the thing the user still owes a decision on.
            foreach (var a in _actions.Where(a => !a.Seen && !a.Outstanding)) { a.Seen = true; touched = true; }
            if (touched) Save();
        }
    }

    public void Ticked(ProactTickRecord tick)
    {
        lock (_lock)
        {
            if (tick.At == default) tick.At = DateTimeOffset.UtcNow;
            _ticks.Insert(0, tick);
            if (_ticks.Count > TicksKept) _ticks.RemoveRange(TicksKept, _ticks.Count - TicksKept);
            Save();
        }
    }

    public IReadOnlyList<ProactTickRecord> Ticks(int take = 60)
    {
        lock (_lock) return _ticks.Take(take).ToList();
    }

    public DateTimeOffset? LastTick
    {
        get { lock (_lock) return _ticks.FirstOrDefault()?.At; }
    }

    public DateTimeOffset? LastDiscover
    {
        get
        {
            lock (_lock)
                return _ticks.FirstOrDefault(t => t.Mode == ProactModes.Discover)?.At;
        }
    }

    /// <summary>How many actions today, which is the figure that says whether the quality gate is holding.</summary>
    public int ActionsSince(DateTimeOffset from)
    {
        lock (_lock) return _actions.Count(a => a.At >= from);
    }

    /// <summary>
    /// The last 24 hours, as the next tick needs to read it.
    /// </summary>
    /// <remarks>
    /// The single most important defence against repetition, and the reason it is shaped like this: ACTIONS go in
    /// full with their times, because "I did that at 08:40" is what stops the same notice arriving twice; TICKS go in
    /// only as a count, because at a five-minute dial there are 288 of them a day and nearly all say "nothing had
    /// changed". Enumerating those would crowd out the very thing the window is for.
    ///
    /// <para>
    /// The count is not padding either. A model that can see it has acted twice in forty-one looks behaves
    /// differently from one that believes it has just woken up for the first time.
    /// </para>
    /// </remarks>
    public string Describe(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var from = nowUtc.AddHours(-24);
            var actions = _actions.Where(a => a.At >= from).OrderBy(a => a.At).ToList();
            var ticks = _ticks.Count(t => t.At >= from);

            var text = new StringBuilder();
            text.Append($"--- what you have done in the last 24 hours ({actions.Count} action(s) across {ticks} look(s)) ---\n");

            if (actions.Count == 0)
            {
                text.Append("Nothing yet. That is normal and is not a reason to act now.\n");
                return text.ToString();
            }

            // GROUPED BY SUBJECT, AND WITHOUT THE REASONS.
            //
            // This list exists to prevent repetition and was causing it. Each entry carried its own `why`, and six of
            // nine said some version of "they keep a Dinner spots project and liked the Soraya note" — so the log was
            // a wall of text arguing, in its own prior words, that restaurants are what this person wants, followed
            // by two lines asking it not to. The examples buried the instruction, and worse, each new restaurant note
            // cited the earlier ones, so the case got more persuasive every time it acted.
            //
            // Reprinting a rationale is reprinting persuasion. What it actually needs is what it already SAID, which
            // is the title — and grouping by subject makes the narrowness legible: "Fitzrovia dinner spots (4)" reads
            // as a rut, where four separate entries read as four interesting facts.
            foreach (var group in actions
                         .GroupBy(a => a.Subject is { Length: > 0 } ? a.Subject : "(unfiled)",
                             StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(g => g.Count()))
            {
                text.Append($"{group.Key} ({group.Count()}):\n");
                foreach (var a in group.OrderBy(a => a.At))
                {
                    text.Append($"  {a.At.ToLocalTime():HH:mm} {a.What}");
                    if (a.IsProposal && a.Answer is { Length: > 0 } answer && answer != ProactAnswers.Pending)
                        text.Append($" (they {answer} it)");
                    else if (a.Outstanding) text.Append(" (still waiting for an answer)");
                    text.Append('\n');
                }
            }

            // Said out loud rather than left to be inferred from the list, because "do not repeat yourself" is the
            // instruction the list exists to support.
            text.Append("Do not do any of these again, and do not reword one and do it again.\n" +
                        // Spelled out because "again" did not cover it. Told about a restaurant opening in the
                        // morning, it spent the afternoon checking whether the opening date had firmed up — which is
                        // not the same action, is not a rewording of it, and is still the same corner of their life
                        // twice in a day. A follow-up feels like new work from the inside and reads as an assistant
                        // with one interest from the outside.
                        "AND DO NOT FOLLOW UP ON ANY OF THEM. Going back to see whether something you already told " +
                        "them about has progressed, firmed up, been reviewed or been dated is NOT new work — it is " +
                        "the same subject a second time. If they want to know how one of those turned out they will " +
                        "ask. Go and look at a different part of their life.\n" +
                        "If the most useful thing you can think of is on that list, or is a continuation of " +
                        "something on it, the answer is to do nothing.\n" +
                        "AND LOOK AT THE COUNTS. A subject with more than one against it today is a rut you are " +
                        "already in, not a seam you have found. The fact that the last few went well there is the " +
                        "reason to leave it alone, not the reason to go back.\n");
            return text.ToString();
        }
    }

    /// <summary>
    /// What it has been on about lately, so it can be told to go somewhere else.
    /// </summary>
    /// <remarks>
    /// Longer than the 24-hour window on purpose. Coming back to the same subject two days running is still a narrow
    /// assistant, and the whole value of this thing is that it looks at parts of somebody's life they were not
    /// thinking about.
    /// </remarks>
    public IReadOnlyList<string> RecentSubjects(DateTimeOffset nowUtc, int days = 3)
    {
        // Dismissed ones count. Putting a notice away means "read, finished with it" — not "you may now spend
        // another deep look on the same subject".
        lock (_lock)
            return _actions
                .Where(a => a.At >= nowUtc.AddDays(-days) && a.Subject is { Length: > 0 })
                .Select(a => a.Subject.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
    }

    /// <summary>
    /// Put one away. Refuses an unanswered proposal, which is a decision rather than a notice.
    /// </summary>
    /// <param name="undo">
    /// Put it back.
    /// </param>
    /// <remarks>
    /// Reversible, because the alternative is a one-tap irreversible action sitting next to a thumb on a phone. The
    /// thumbs already clear by pressing them again; dismiss having no way back was an inconsistency waiting to lose
    /// somebody something they wanted — and it did, twice, to the person testing it.
    /// </remarks>
    public bool Dismiss(string id, bool undo = false)
    {
        lock (_lock)
        {
            var a = _actions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            // Dismissing a pending proposal would throw away something they still owe an answer on — and the answer
            // is the tick or the cross, not a third way out.
            if (a is null || (!undo && a.Outstanding)) return false;

            a.Dismissed = !undo;
            if (!undo) a.Seen = true;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Wipe the record from a moment onward. Returns how much went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A start-again, and it earns its place because of how the restraints work. Everything that stops this thing
    /// repeating itself is built on what it did earlier — the 24-hour log, the subjects covered, the votes. That is
    /// right in normal use and it means a bad morning suppresses a good afternoon: once a subject is covered, it stays
    /// covered, whether the coverage was any use or not.
    /// </para>
    /// <para>
    /// Ticks go too. They are the honest denominator ("2 actions across 38 looks") and leaving them behind a cleared
    /// day would report a count against nothing.
    /// </para>
    /// <para>
    /// Destructive and deliberately not on the timeline — this belongs with the settings, next to the switch, where
    /// somebody has gone looking for a control rather than reading their day.
    /// </para>
    /// </remarks>
    /// <summary>How long a dispatched run is believed to still be going before it is written off as lost.</summary>
    private static readonly TimeSpan LostWorker = TimeSpan.FromMinutes(20);

    private (string Task, string Session, DateTimeOffset At)? _out;

    /// <summary>Remember that a run is out, or that it has come back. Pass null when it finishes.</summary>
    /// <remarks>
    /// ONE DISCOVERY RUN AT A TIME, which nothing enforced.
    /// <para>
    /// The dispatch is fire-and-forget — <c>RunProactAsync</c> starts a task and returns its id — so nothing
    /// stopped the next tick sending a second worker out while the first was still gone. On the five-minute dial
    /// with a worker that takes longer than five minutes that is not hypothetical, and what it produces is two
    /// near-identical proposals about the same thing. The 24-hour record cannot prevent it, because the first worker
    /// has not written anything down yet: at the moment the second decision is made there is genuinely nothing to
    /// see.
    /// </para>
    /// <para>
    /// In memory rather than on disk, and it expires, because the failure to avoid is the opposite one: a worker
    /// that dies without reporting must not silence the loop for ever. A restart clears it and so does time.
    /// </para>
    /// </remarks>
    public void Working(string? taskId, string? sessionId = null)
    {
        lock (_lock)
            _out = string.IsNullOrWhiteSpace(taskId) ? null : (taskId!, sessionId ?? "", DateTimeOffset.UtcNow);
    }

    /// <summary>The run that is out right now, if there is one, so the page can watch it happen.</summary>
    /// <remarks>
    /// Carries the CONVERSATION rather than just the task, because that is what can be followed: a Proact run has
    /// its own conversation and it is hidden from the person's chat list, so without this the only way to see what
    /// it is doing was to wait for it to finish and read what it filed. Pressing a button and being told "gone" is
    /// not the same as being shown the work.
    /// </remarks>
    public (string Task, string Session)? Out(DateTimeOffset nowUtc) =>
        Busy(nowUtc) && _out is { } o && o.Session.Length > 0 ? (o.Task, o.Session) : null;

    /// <summary>Is a run still out? False once it has been gone long enough to be written off.</summary>
    public bool Busy(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            if (_out is not { } o) return false;
            if (nowUtc - o.At < LostWorker) return true;
            _out = null;
            return false;
        }
    }

    public (int Actions, int Ticks) Clear(DateTimeOffset from)
    {
        lock (_lock)
        {
            var actions = _actions.RemoveAll(a => a.At >= from);
            var ticks = _ticks.RemoveAll(t => t.At >= from);
            if (actions > 0 || ticks > 0) Save();
            return (actions, ticks);
        }
    }

    /// <summary>Thumbs up or down. Null clears it, because a mis-tap should be undoable.</summary>
    public ProactAction? Vote(string id, string? verdict, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var a = _actions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (a is null) return null;

            a.Verdict = ProactVerdicts.Known(verdict) ? verdict : null;
            a.VotedAt = a.Verdict is null ? null : nowUtc;
            a.Seen = true;
            Save();
            return a;
        }
    }

    /// <summary>
    /// What they like and what they don't, for the prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Describe"/> and it has to be, because the two answer different questions over
    /// different spans. The 24-hour log stops it repeating itself and is worthless after a day. Taste is the opposite:
    /// a thumbs-down on Monday is still true in November, and forgetting it overnight would mean learning nothing at
    /// all from the only signal the user ever volunteers.
    /// </para>
    /// <para>
    /// Both halves go in, because both are informative — a down says what direction to stop going in, an up says the
    /// shape of thing worth doing again. The panel suggester learned this the same way and says so in its own prompt:
    /// keeping only the rejections teaches a system to be timid rather than to be right.
    /// </para>
    /// </remarks>
    public string Taste()
    {
        lock (_lock)
        {
            // THE TWO THUMBS ARE NOT SYMMETRICAL IN TIME, and treating them as though they were is what caused the
            // whole restaurant loop.
            //
            // A thumbs-up means "that was good". It does not mean "another one now" — but shown a same-day like it
            // read exactly that way: two upvoted Fitzrovia openings in the morning produced two more in the
            // afternoon, each correct, the set of them tedious. Somebody pleased to hear about a new restaurant at
            // ten is not asking for a fourth by four o'clock.
            //
            // A thumbs-DOWN is the opposite. "Not this" is true the moment it is said and most valuable immediately,
            // because the next look is the one that would otherwise repeat the mistake.
            //
            // So: dislikes from any time, including today. Likes only from BEFORE today, where they inform the form
            // of a good finding without reading as an instruction to go round again.
            var today = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset);

            var up = _actions
                .Where(a => a.Verdict == ProactVerdicts.Up && a.At < today)
                .Take(12).ToList();
            var down = _actions
                .Where(a => a.Verdict == ProactVerdicts.Down)
                .Take(12).ToList();

            if (up.Count == 0 && down.Count == 0) return "";

            var text = new StringBuilder("--- what they have told you about your judgement ---\n");

            if (down.Count > 0)
            {
                // First, because it is the half that should change what happens next.
                text.Append("They did NOT like these — steer away from this sort of thing:\n");
                foreach (var a in down) text.Append($"  - {a.What}\n");
            }

            if (up.Count > 0)
            {
                text.Append("They liked these on previous days — for the FORM only, not the subject:\n");
                foreach (var a in up) text.Append($"  + {a.What}\n");
            }

            // THE KIND OF THING, NOT THE SUBJECT — and this distinction is the whole value of the block.
            //
            // Without it, this fought the instruction to go somewhere new and won. Two notes about Fitzrovia
            // restaurants got thumbed up, "the shape of thing worth doing again" read as "bring more restaurants",
            // and it produced five of them in a day while a separate block was telling it the subject was covered.
            // Taste is about form; breadth is about subject. They are different axes and only one of them is a
            // licence to repeat.
            text.Append("A DISLIKE IS AN INSTRUCTION. A LIKE IS NOT.\n" +
                        "What they turned down is a direction to stop going in, and it applies now — a near-identical " +
                        "version of something they marked down is worse than doing nothing at all.\n" +
                        "What they liked is only evidence about FORM: how specific, how current, how well checked, " +
                        "how close to home a good finding is for them. Being pleased to hear something is not a " +
                        // Named nothing, for the same reason the other blocks name nothing: the examples above are
                        // the user's real subjects, and a sentence explaining that they are not a shopping list is
                        // one more mention of them.
                        "request to hear more of it — being pleased with one of the above is not an order for another " +
                        "on the same subject, it is a standard to meet on a different one. If you take the subject " +
                        "from the liked list, you have misread it.\n");
            return text.ToString();
        }
    }

    // ---- the daily roundup ---------------------------------------------------------------------------------

    /// <summary>The roundups, newest first. Kept for a few weeks so "is this any good?" is answerable.</summary>
    public IReadOnlyList<ProactRoundup> Roundups(int take = 21)
    {
        lock (_lock) return _roundups.Take(take).ToList();
    }

    public ProactRoundup? RoundupFor(string day)
    {
        lock (_lock) return _roundups.FirstOrDefault(r => r.Day == day);
    }

    /// <summary>
    /// Is today's roundup due, and not already made?
    /// </summary>
    /// <remarks>
    /// Checked against LOCAL time because the hour is a human one — six in the evening means six where they are, and
    /// a roundup that lands at 6pm UTC is a roundup that lands at 7pm in summer.
    /// </remarks>
    public bool RoundupDue(DateTimeOffset nowLocal)
    {
        lock (_lock)
        {
            if (_settings.RoundupHour is not { } hour) return false;
            if (nowLocal.Hour < hour) return false;

            var day = nowLocal.ToString("yyyy-MM-dd");
            return !_roundups.Any(r => r.Day == day);
        }
    }

    /// <summary>
    /// Write today's roundup from the record. Returns it, or null if there is already one.
    /// </summary>
    /// <remarks>
    /// It reports what it did NOT do as well as what it did. A digest of only wins reads as cleverer than the thing
    /// actually is, and the number of looks behind two actions is the most honest line in it — that ratio is what
    /// tells the user whether this is working or just busy.
    /// </remarks>
    public ProactRoundup? Roundup(DateTimeOffset nowLocal)
    {
        lock (_lock)
        {
            var day = nowLocal.ToString("yyyy-MM-dd");
            if (_roundups.Any(r => r.Day == day)) return null;

            var from = new DateTimeOffset(nowLocal.Date, nowLocal.Offset);
            var mine = _actions.Where(a => a.At >= from).OrderBy(a => a.At).ToList();
            var looks = _ticks.Count(t => t.At >= from);
            var owed = _actions.Where(a => a.Outstanding).ToList();

            var text = new StringBuilder();

            if (mine.Count == 0)
            {
                // Said plainly rather than dressed up. A quiet day is the normal case and pretending otherwise is
                // how a roundup starts sounding like marketing.
                text.Append(looks == 0
                    ? "I didn't get out at all today."
                    : $"Nothing worth bringing you today. I looked {looks} time{(looks == 1 ? "" : "s")} and there was nothing I could usefully do.");
            }
            else
            {
                text.Append($"Today I did {mine.Count} thing{(mine.Count == 1 ? "" : "s")}, out of {looks} look{(looks == 1 ? "" : "s")}.\n");

                foreach (var a in mine)
                {
                    text.Append($"\n{a.At.ToLocalTime():HH:mm} — {a.What}");
                    if (a.Kind == ProactKinds.Prepared && a.Produced is { Length: > 0 } made)
                        text.Append($" ({made})");
                    if (a.IsProposal)
                        text.Append(a.Answer switch
                        {
                            ProactAnswers.Ticked => " — you said yes",
                            ProactAnswers.Crossed => " — you said no",
                            ProactAnswers.Expired => " — expired before you saw it",
                            _ => " — still waiting on you",
                        });
                }
            }

            if (owed.Count > 0)
                text.Append($"\n\nWaiting on you: {owed.Count} thing{(owed.Count == 1 ? "" : "s")} to say yes or no to.");

            var roundup = new ProactRoundup
            {
                Day = day, At = DateTimeOffset.UtcNow, Text = text.ToString(),
                Did = mine.Count, Waiting = owed.Count, Looks = looks,
            };

            _roundups.Insert(0, roundup);
            if (_roundups.Count > RoundupsKept) _roundups.RemoveRange(RoundupsKept, _roundups.Count - RoundupsKept);
            Save();
            return roundup;
        }
    }

    public void RoundupSeen(string day)
    {
        lock (_lock)
        {
            if (_roundups.FirstOrDefault(r => r.Day == day) is not { Seen: false } r) return;
            r.Seen = true;
            Save();
        }
    }

    private static ProactSettings Copy(ProactSettings s) => new()
    {
        On = s.On, Every = s.Every, PausedUntil = s.PausedUntil, RoundupHour = s.RoundupHour,
    };

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(
                new Kept { Settings = _settings, Actions = _actions, Ticks = _ticks, Roundups = _roundups }, _json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[proact] couldn't save: {ex.Message}");
        }
    }

    private sealed class Kept
    {
        public ProactSettings? Settings { get; set; }
        public List<ProactAction>? Actions { get; set; }
        public List<ProactTickRecord>? Ticks { get; set; }
        public List<ProactRoundup>? Roundups { get; set; }
    }
}
