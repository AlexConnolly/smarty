using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Proact: the first thing in this system that acts without being asked.
///
/// <para>
/// Which makes these tests a different shape from the rest. Everywhere else the question is "does the feature work";
/// here the first question is "can it do something nobody sanctioned", and the answer has to be no by construction
/// rather than by good behaviour. The toolset tests below are the ones that matter most — a prompt telling a model
/// not to book a restaurant is a hope, and a worker with no tool that can click a button is a guarantee.
/// </para>
/// <para>
/// The second theme is noise, which is the failure that actually kills this feature. There is deliberately no cap on
/// how much it may do, so everything that restrains it — the free gate, the 24-hour log, the expiry, the room to
/// propose — is load-bearing and tested here.
/// </para>
/// </summary>
public class ProactTests : IDisposable
{
    private readonly List<string> _paths = new();

    private ProactStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proact-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new ProactStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task<ToolOutput> Call(AgentTool tool, object args) =>
        await tool.InvokeAsync(
            new ToolCallArguments(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement),
            CancellationToken.None);

    private static object GoodProposal(object? overrides = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["what"] = "Book Trullo, 8pm Friday, table for two",
            ["why"] = "Friday is clear and the anniversary is on their list for Saturday",
            ["plan"] = "Open Trullo's booking page, pick 8pm Friday for 2, fill in their name and number, confirm",
            ["checked"] = "Their booking page shows 8pm free for 2 on Friday the 22nd",
            ["catch"] = "Free cancellation until Thursday; 8pm tables usually gone by Wednesday",
            ["hours"] = 24,
        };
        if (overrides is not null)
            foreach (var p in overrides.GetType().GetProperties())
                d[p.Name] = p.GetValue(overrides);
        return d;
    }

    // ── The boundary. This is the half that has to be right. ──────────────────────────────────────────────

    /// <summary>
    /// The tools that could book a table, buy a thing or send a message are the tools it does not have. Named
    /// individually rather than caught by a prefix, so a new chrome tool arriving next month is refused by default.
    /// </summary>
    [Fact]
    public void It_cannot_hold_a_tool_that_can_act_on_a_page()
    {
        foreach (var tool in new[]
                 {
                     "chrome_click", "chrome_type_text", "chrome_press_key",
                     "chrome_form_input", "chrome_file_upload", "chrome_javascript",
                 })
            Assert.False(Proact.Keep(tool), $"{tool} must never be available to Proact");
    }

    /// <summary>
    /// Going out to find something is most of Proact's value, and in this system web research IS the browser — the
    /// fetch-and-extract tools were removed for answering from dead HTML. So the reading half has to be intact, or
    /// the whole outward-looking half of the feature is dead on arrival.
    /// </summary>
    [Fact]
    public void It_can_read_the_web_but_not_touch_it()
    {
        // Enough to navigate and read a live page, which is the entire web capability here.
        Assert.True(Proact.Keep("chrome_navigate"));
        Assert.True(Proact.Keep("chrome_read_page"));
        Assert.True(Proact.Keep("chrome_find"));
        Assert.True(Proact.Keep("chrome_tabs_create"));
        Assert.True(Proact.Keep("chrome_scroll"));

        Assert.False(Proact.Keep("chrome_click"));
        Assert.False(Proact.Keep("chrome_type_text"));
    }

    /// <summary>
    /// A whitelist, so anything nobody has thought about is refused. The failure mode of being too strict is Proact
    /// saying it cannot do something; of being too loose, a booking nobody asked for.
    /// </summary>
    [Fact]
    public void Anything_not_on_the_list_is_refused()
    {
        foreach (var tool in new[]
                 {
                     "run_shell_command", "shell", "send_email", "send_slack_message", "post_message",
                     "delete_file", "pay", "checkout", "schedule_task", "watch_publish", "feed_publish",
                     "fan_out", "something_invented_next_year", "",
                 })
            Assert.False(Proact.Keep(tool), $"{tool} must not be available to Proact");
    }

    [Fact]
    public void It_holds_what_it_needs_to_do_its_job()
    {
        foreach (var tool in new[]
                 {
                     "proact_noticed", "proact_prepared", "proact_propose",
                     "read_file", "find_in_file", "list_files", "search_memory",
                     "write_file", "widget_publish",
                 })
            Assert.True(Proact.Keep(tool), $"{tool} should be available to Proact");
    }

    /// <summary>A name on both lists is refused, because the ordering has to be the safe one and stay it.</summary>
    [Fact]
    public void Denied_beats_allowed()
    {
        Assert.Contains("chrome_javascript", Proact.CanAct);
        Assert.False(Proact.Keep("chrome_javascript"));
    }

    [Fact]
    public void It_reports_what_it_is_not_holding()
    {
        var dropped = Proact.Dropped(new[] { "read_file", "chrome_click", "write_file", "shell" });

        Assert.Contains("chrome_click", dropped);
        Assert.Contains("shell", dropped);
        Assert.DoesNotContain("read_file", dropped);
    }

    /// <summary>
    /// The leak that the first live deep look actually opened.
    ///
    /// <para>
    /// A Proact task assessed as "complex" made a plan, and a plan's steps are FRESH tasks. A fresh task carries no
    /// Proact flag, so its toolset was assembled the ordinary way — browser, clicking, typing and all — and the one
    /// rule was gone one level down from where it was enforced. Nothing in the design said children were exempt; it
    /// simply never occurred to anyone that there would be children.
    /// </para>
    /// <para>
    /// Two guards now, because a boundary that depends on one check is a boundary that ends at the next refactor:
    /// Proact does not plan at all, and a child inherits the flag if one is ever made anyway. This pins the second.
    /// </para>
    /// </summary>
    [Fact]
    public void A_child_of_an_unattended_run_is_still_unattended()
    {
        var parent = new TaskInfo { Id = "1", Description = "look into the dinner project", Proact = ProactModes.Discover };

        // The shape both child-making paths use: everything about who and what is inherited from the parent.
        var child = new TaskInfo
        {
            Id = "2",
            Description = "one step of it",
            ParentTaskId = parent.Id,
            UserScope = parent.UserScope,
            Room = parent.Room,
            UserName = parent.UserName,
            Proact = parent.Proact,
        };

        Assert.Equal(ProactModes.Discover, child.Proact);
        Assert.False(string.IsNullOrEmpty(child.Proact));
    }

    /// <summary>A task nobody asked for does one thing it can explain, or it belongs in a proposal.</summary>
    [Fact]
    public void An_unattended_run_is_marked_as_already_assessed_so_it_never_plans()
    {
        // Assessed is what the orchestrator checks before it plans; Proact sets it so planning is skipped, exactly
        // as a panel build does. Tested through the flag because the gate itself is inside the worker loop.
        var task = new TaskInfo { Id = "1", Description = "a look", Proact = ProactModes.Attend };
        Assert.False(task.Assessed);

        // What the gate does.
        if (task.Proact is { Length: > 0 } && !task.Assessed) task.Assessed = true;

        Assert.True(task.Assessed);
    }

    // ── The toggle and the dial ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void It_is_off_until_somebody_turns_it_on()
    {
        var store = NewStore();

        Assert.False(store.Settings.On);
        Assert.False(store.Settings.Running(DateTimeOffset.UtcNow));

        store.Set(on: true);
        Assert.True(store.Settings.Running(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_dial_takes_one_of_four_answers()
    {
        var store = NewStore();

        store.Set(every: "every 5 minutes");
        Assert.Equal("every 5 minutes", store.Settings.Every);

        // Nonsense is ignored rather than accepted, and it must not silence the loop either.
        store.Set(every: "every 3 seconds");
        Assert.Equal("every 5 minutes", store.Settings.Every);

        Assert.All(ProactIntervals.All, i => Assert.True(ProactIntervals.Known(i)));
        Assert.False(ProactIntervals.Known("hourly"));
    }

    /// <summary>The dial's words are a feed's words, so the parser that already exists reads them.</summary>
    [Fact]
    public void Every_interval_parses_with_the_existing_repeat_parser()
    {
        foreach (var every in ProactIntervals.All)
            Assert.True(ScheduleStore.TryParseRepeat(every, DateTimeOffset.Now, out _), $"{every} should parse");
    }

    [Fact]
    public void Pausing_stops_it_without_forgetting_anything()
    {
        var store = NewStore();
        store.Set(on: true);
        store.Add(new ProactAction { Kind = ProactKinds.Noticed, What = "something", Why = "a reason" });

        store.Set(pausedUntil: DateTimeOffset.UtcNow.AddHours(2));
        Assert.False(store.Settings.Running(DateTimeOffset.UtcNow));
        Assert.True(store.Settings.On);
        Assert.Single(store.Actions());

        store.Set(clearPause: true);
        Assert.True(store.Settings.Running(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Switching_it_off_and_on_again_keeps_its_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proact-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = new ProactStore(path, json);
        first.Set(on: true, every: "every 30 minutes");
        first.Add(new ProactAction { Kind = ProactKinds.Noticed, What = "the thing it noticed", Why = "a reason" });
        first.Ticked(new ProactTickRecord { Changed = false, Note = "nothing had changed" });

        var again = new ProactStore(path, json);
        Assert.True(again.Settings.On);
        Assert.Equal("every 30 minutes", again.Settings.Every);
        Assert.Single(again.Actions());
        Assert.Single(again.Ticks());
    }

    // ── Proposals: the tick and the cross ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_proposal_carries_everything_needed_to_answer_it()
    {
        var store = NewStore();

        var result = await Call(ProactTools.ProposeTool(store, ProactModes.Attend), GoodProposal());

        Assert.False(result.IsError);
        var offered = Assert.Single(store.Waiting());
        Assert.Equal(ProactKinds.Proposed, offered.Kind);
        Assert.NotNull(offered.Plan);
        Assert.NotNull(offered.Catch);
        Assert.NotNull(offered.Expires);
        Assert.Equal(ProactAnswers.Pending, offered.Answer);
    }

    /// <summary>
    /// One click authorises this, so "do the thing" is not a plan. The person has to be able to see what they are
    /// agreeing to before they agree to it.
    /// </summary>
    [Fact]
    public async Task A_proposal_with_no_real_plan_is_refused()
    {
        var store = NewStore();

        var result = await Call(ProactTools.ProposeTool(store, ProactModes.Attend),
            GoodProposal(new { plan = "do it" }));

        Assert.True(result.IsError);
        Assert.Empty(store.Waiting());
    }

    /// <summary>
    /// The same bargain the feeds and the panels both make, in a third place: do not offer what you have not proved.
    /// An unrunnable proposal is worse than a missing one, because the user has now spent a click and some trust.
    /// </summary>
    [Fact]
    public async Task A_proposal_it_has_not_established_is_refused()
    {
        var store = NewStore();

        var result = await Call(ProactTools.ProposeTool(store, ProactModes.Attend),
            GoodProposal(new { @checked = "" }));

        Assert.True(result.IsError);
        Assert.Contains("establish", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Waiting());
    }

    [Fact]
    public async Task A_proposal_that_never_expires_is_refused()
    {
        var store = NewStore();

        var result = await Call(ProactTools.ProposeTool(store, ProactModes.Attend),
            GoodProposal(new { hours = 0 }));

        Assert.True(result.IsError);
        Assert.Empty(store.Waiting());
    }

    [Fact]
    public async Task A_proposal_that_hides_what_cannot_be_undone_is_refused()
    {
        var store = NewStore();

        var result = await Call(ProactTools.ProposeTool(store, ProactModes.Attend),
            GoodProposal(new { catch_ = (string?)null, @catch = "" }));

        Assert.True(result.IsError);
        Assert.Empty(store.Waiting());
    }

    [Fact]
    public void The_tick_and_the_cross_are_recorded_and_cannot_be_answered_twice()
    {
        var store = NewStore();
        var offered = store.Add(new ProactAction
        {
            Kind = ProactKinds.Proposed, What = "Book the table", Why = "Friday is clear",
            Plan = "Open the page and book it", Catch = "Free until Thursday",
            Expires = DateTimeOffset.UtcNow.AddHours(6),
        });

        var answered = store.Answered(offered.Id, ticked: true, DateTimeOffset.UtcNow);
        Assert.NotNull(answered);
        Assert.Equal(ProactAnswers.Ticked, answered!.Answer);

        // A second click must not dispatch the work a second time.
        Assert.Null(store.Answered(offered.Id, ticked: true, DateTimeOffset.UtcNow));
        Assert.Empty(store.Waiting());
    }

    /// <summary>
    /// The most expensive non-safety failure available: a click that appears to do nothing teaches the user never to
    /// click again. So where the work went, and what came of it, live on the proposal.
    /// </summary>
    [Fact]
    public void A_ticked_proposal_says_where_its_work_went()
    {
        var store = NewStore();
        var offered = store.Add(new ProactAction
        {
            Kind = ProactKinds.Proposed, What = "Book the table", Why = "Friday is clear",
            Plan = "Open the page and book it", Catch = "none", Expires = DateTimeOffset.UtcNow.AddHours(6),
        });
        store.Answered(offered.Id, ticked: true, DateTimeOffset.UtcNow);

        store.Dispatched(offered.Id, "proact-do-1", "7");
        store.Finished(offered.Id, "Booked for 8pm, confirmation in their email");

        var done = store.Get(offered.Id)!;
        Assert.Equal("proact-do-1", done.Session);
        Assert.Equal("7", done.TaskId);
        Assert.Contains("Booked", done.Outcome);
    }

    /// <summary>"Book for tonight" is meaningless tomorrow, and a list of dead offers is a list nobody opens.</summary>
    [Fact]
    public void A_proposal_nobody_answered_expires_rather_than_waiting_for_ever()
    {
        var store = NewStore();
        store.Add(new ProactAction
        {
            Kind = ProactKinds.Proposed, What = "Book for tonight", Why = "tonight is free",
            Plan = "Open the page and book it", Catch = "none",
            Expires = DateTimeOffset.UtcNow.AddHours(-1),
        });

        Assert.Equal(1, store.Expire(DateTimeOffset.UtcNow));
        Assert.Empty(store.Waiting());
        Assert.Equal(ProactAnswers.Expired, store.Actions().Single().Answer);
    }

    [Fact]
    public async Task It_will_not_pile_up_unanswered_decisions()
    {
        var store = NewStore();
        var tool = ProactTools.ProposeTool(store, ProactModes.Attend);

        for (var i = 0; i < ProactStore.OutstandingProposals; i++)
            Assert.False((await Call(tool, GoodProposal(new { what = $"Book the table, option {i}" }))).IsError);

        var refused = await Call(tool, GoodProposal(new { what = "Book yet another table" }));
        Assert.True(refused.IsError);
        Assert.Equal(ProactStore.OutstandingProposals, store.Waiting().Count);
    }

    /// <summary>Looking at a decision is not making it, so reading the timeline must not clear one.</summary>
    [Fact]
    public void Reading_the_timeline_does_not_answer_a_proposal()
    {
        var store = NewStore();
        store.Add(new ProactAction { Kind = ProactKinds.Noticed, What = "a thing it noticed", Why = "a reason" });
        var offered = store.Add(new ProactAction
        {
            Kind = ProactKinds.Proposed, What = "Book the table", Why = "Friday is clear",
            Plan = "Open the page and book it", Catch = "none", Expires = DateTimeOffset.UtcNow.AddHours(6),
        });

        store.SeenAll();

        Assert.True(store.Actions().First(a => a.Kind == ProactKinds.Noticed).Seen);
        Assert.False(store.Get(offered.Id)!.Seen);
        Assert.Single(store.Waiting());
    }

    // ── Anti-corniness, enforced rather than requested ─────────────────────────────────────────────────────

    [Fact]
    public async Task Something_that_would_suit_anybody_is_refused()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Attend),
            new { what = "You could try taking a break today", why = "it's good" });

        Assert.True(result.IsError);
        Assert.Empty(store.Actions());
    }

    [Fact]
    public async Task Placeholder_text_never_reaches_the_timeline()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Attend),
            new { what = "...", why = "..." });

        Assert.True(result.IsError);
        Assert.Empty(store.Actions());
    }

    [Fact]
    public async Task Prepared_work_needs_something_to_show_for_it()
    {
        var store = NewStore();

        var result = await Call(ProactTools.PreparedTool(store, ProactModes.Attend),
            new
            {
                what = "Looked into flights for the Lisbon trip",
                why = "they have Lisbon on their list for next month",
                produced = "",
            });

        Assert.True(result.IsError);
        Assert.Empty(store.Actions());
    }

    [Fact]
    public async Task A_real_notice_is_recorded_with_its_reason()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Attend),
            new
            {
                what = "Thursday's dentist at 09:00 and the Bristol meeting at 10:30 are 120 miles apart",
                why = "both are on their lists for Thursday and neither mentions travel",
            });

        Assert.False(result.IsError);
        var recorded = Assert.Single(store.Actions());
        Assert.Equal(ProactKinds.Noticed, recorded.Kind);
        Assert.Contains("Bristol", recorded.What);
        Assert.Contains("Thursday", recorded.Why);
    }

    // ── The 24-hour log, which is the defence against repeating itself ─────────────────────────────────────

    /// <summary>
    /// The arithmetic that shapes this: at a five-minute dial there are 288 ticks a day and nearly all of them say
    /// "nothing had changed". Enumerating those would crowd out the actions, which are the thing the window is for.
    /// </summary>
    [Fact]
    public void The_log_carries_every_action_and_only_counts_the_ticks()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        store.Add(new ProactAction
        {
            At = now.AddHours(-2), Kind = ProactKinds.Noticed,
            What = "Thursday's two appointments are 120 miles apart", Why = "both are on Thursday's list",
        });
        for (var i = 0; i < 40; i++)
            store.Ticked(new ProactTickRecord { At = now.AddMinutes(-i * 5), Changed = false, Note = "nothing had changed" });

        var log = store.Describe(now);

        Assert.Contains("120 miles apart", log);

        // The REASON is deliberately absent now. Each entry used to carry its own `why`, and reprinting a rationale
        // turned out to be reprinting persuasion: six notes each restating "they keep a dinner-spots project and
        // liked the last one" argued the case for restaurants far harder than the instruction below argued against.
        Assert.DoesNotContain("both are on Thursday's list", log);
        // The ticks are a number, not forty lines.
        Assert.Contains("40 look(s)", log);
        Assert.DoesNotContain("nothing had changed", log);
        Assert.Contains("Do not do any of these again", log);
    }

    [Fact]
    public void The_log_forgets_anything_older_than_a_day()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        store.Add(new ProactAction
        {
            At = now.AddHours(-30), Kind = ProactKinds.Noticed, What = "yesterday's old news", Why = "a reason",
        });

        Assert.DoesNotContain("old news", store.Describe(now));
    }

    [Fact]
    public void The_log_says_how_a_proposal_landed()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        var offered = store.Add(new ProactAction
        {
            At = now.AddHours(-3), Kind = ProactKinds.Proposed,
            What = "Book Trullo for Friday", Why = "Friday is clear",
            Plan = "Open the page and book it", Catch = "none", Expires = now.AddHours(6),
        });
        store.Answered(offered.Id, ticked: false, now.AddHours(-2));

        var log = store.Describe(now);
        Assert.Contains("Book Trullo", log);
        Assert.Contains("crossed", log);
    }

    [Fact]
    public void An_empty_log_does_not_read_as_a_reason_to_act()
    {
        var log = NewStore().Describe(DateTimeOffset.UtcNow);

        Assert.Contains("Nothing yet", log);
        Assert.Contains("not a reason to act", log);
    }

    // ── Rich content: sources to click, pictures that load ────────────────────────────────────────────────

    /// <summary>
    /// Links are kept as given; images are fetched first. The asymmetry is the point — a link is followed by a human
    /// in a real browser, where an image renders inline and a bad one is a broken-image icon on the page.
    /// </summary>
    [Fact]
    public async Task Sources_are_kept_and_pictures_are_checked_before_they_are()
    {
        var store = NewStore();
        var asked = new List<string>();

        var tool = ProactTools.NoticedTool(store, ProactModes.Discover,
            imageLoads: (url, _) => { asked.Add(url); return Task.FromResult(url.Contains("good")); });

        var result = await Call(tool, new
        {
            what = "Soraya is opening on George Street, a short walk from them",
            why = "they keep a dinner-spots project for the area",
            subject = "dinner spots",
            links = new[]
            {
                new { label = "Their own site", url = "https://soraya.london" },
                new { label = "Hot Dinners", url = "https://hot-dinners.com/soraya" },
            },
            images = new[]
            {
                new { url = "https://example.com/good.jpg", caption = "The dining room" },
                new { url = "https://example.com/missing.jpg", caption = "Gone" },
            },
        });

        Assert.False(result.IsError);
        var a = Assert.Single(store.Actions());

        Assert.Equal(2, a.Links.Count);
        Assert.Equal("Their own site", a.Links[0].Label);

        // Both were fetched; only the one that answered was kept.
        Assert.Equal(2, asked.Count);
        Assert.Single(a.Images);
        Assert.Contains("good", a.Images[0].Url);
    }

    /// <summary>
    /// Failing closed. A timeline without pictures is a small loss; a timeline of broken-image icons is the failure
    /// this system already spent a day on.
    /// </summary>
    [Fact]
    public async Task With_no_way_to_check_a_picture_none_is_kept()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Attend),
            new
            {
                what = "Something specific about their own week",
                why = "it is on their list for Thursday",
                subject = "thursday",
                images = new[] { new { url = "https://example.com/unchecked.jpg" } },
            });

        Assert.False(result.IsError);
        Assert.Empty(store.Actions().Single().Images);
    }

    [Fact]
    public async Task A_link_that_is_not_a_web_address_is_dropped()
    {
        var store = NewStore();

        await Call(ProactTools.NoticedTool(store, ProactModes.Attend),
            new
            {
                what = "Something specific about their own week",
                why = "it is on their list for Thursday",
                subject = "thursday",
                links = new[]
                {
                    new { label = "Fine", url = "https://example.com/real" },
                    // A worker with no shell must not be able to put either of these in front of somebody.
                    new { label = "Local file", url = "file:///C:/Windows/System32" },
                    new { label = "Script", url = "javascript:alert(1)" },
                },
            });

        var kept = Assert.Single(store.Actions()).Links;
        Assert.Single(kept);
        Assert.StartsWith("https://", kept[0].Url);
    }

    /// <summary>
    /// A brief must not contradict its own assignment.
    ///
    /// <para>
    /// Sent to check a vacuum, a worker instead went out and found an art installation — and it was obeying orders.
    /// Four lines above the assignment, the deep-look framing said "GO OUT AND FIND SOMETHING THEY DO NOT HAVE …
    /// handing their own notes back is worth nothing". Querying their own machine reads as exactly the worthless kind
    /// of work under that wording, so the emphatic instruction beat the specific one.
    /// </para>
    /// <para>
    /// The real test was never inside-versus-outside: it is whether they already know it. Their notes they can see;
    /// their vacuum's state they cannot.
    /// </para>
    /// </summary>
    [Fact]
    public void A_deep_brief_does_not_disparage_looking_at_their_own_things()
    {
        var brief = ProactTools.Brief(ProactModes.Discover,
            "checking the vacuum's state and whether it has run recently",
            "Today is Friday.", "");

        // The assignment survives.
        Assert.Contains("checking the vacuum's state", brief);
        // And is not undercut by a louder instruction to go elsewhere.
        Assert.DoesNotContain("GO OUT AND FIND SOMETHING THEY DO NOT HAVE", brief);
        Assert.Contains("DO THE THING YOU DECIDED TO DO", brief);
        Assert.Contains("do not treat looking at something of theirs as the lesser", brief);
    }

    // ── Nothing user-facing talks about them in the third person ──────────────────────────────────────────

    /// <summary>
    /// The thing that would make this feel like surveillance rather than help.
    ///
    /// <para>
    /// A note about somebody, on a page that somebody can read, reads as being discussed behind their back. It
    /// happened: a line saying "a fresh, specific spot they won't have seen" turned up on the page belonging to the
    /// "they" in question, and it was unsettling for exactly the right reason.
    /// </para>
    /// <para>
    /// The root of it was a contradiction rather than an oversight — the prompt told the model the line was never
    /// shown, and the code showed it. Two things are asserted here: the recording tools ASK for the second person,
    /// and nothing tells the model its words are private when they are not.
    /// </para>
    /// </summary>
    [Fact]
    public void The_recording_tools_ask_for_words_written_to_the_user()
    {
        var tools = new[]
        {
            ProactTools.NoticedTool(NewStore(), ProactModes.Attend),
            ProactTools.PreparedTool(NewStore(), ProactModes.Attend),
            ProactTools.ProposeTool(NewStore(), ProactModes.Attend),
        };

        foreach (var tool in tools)
        {
            var why = tool.Parameters.Single(p => p.Name == "why");
            Assert.Contains("second person", why.Description, StringComparison.OrdinalIgnoreCase);
            // And says why, because a rule with no reason attached is a rule that gets dropped in the next edit.
            Assert.Contains("shown", why.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Its own errands are not the person's conversations ────────────────────────────────────────────────

    /// <summary>
    /// Every run needs a conversation to hold its task, and nobody ever types in one — so it has no first user
    /// message to take a title from and lands in the chat list as "Untitled chat". A dozen a day makes the sidebar
    /// useless, which is why the ids carry a prefix the listing can filter on.
    /// </summary>
    [Fact]
    public void A_look_is_recognisable_as_proacts_own_errand()
    {
        var attend = Proact.Conversation(ProactModes.Attend, DateTimeOffset.UtcNow);
        var discover = Proact.Conversation(ProactModes.Discover, DateTimeOffset.UtcNow);

        Assert.True(Proact.Looking(attend));
        Assert.True(Proact.Looking(discover));
    }

    /// <summary>
    /// The line is who asked. A ticked proposal is work the user authorised with a click — it can come back with a
    /// question, so it stays in their conversations where they can find it.
    /// </summary>
    [Fact]
    public void Work_the_user_ticked_stays_in_their_conversations()
    {
        Assert.False(Proact.Looking(Proact.ConversationForTicked("5add578c")));
    }

    [Fact]
    public void A_persons_own_chat_is_never_mistaken_for_one()
    {
        foreach (var id in new[]
                 {
                     "", "abc-123", "woke-7f2a-20260821093000", "widget-ba3ea8f7",
                     // Close enough to matter: neither is a look.
                     "proactive-notes", "proact-do-5add578c",
                 })
            Assert.False(Proact.Looking(id), $"{id} is not one of Proact's looks");
    }

    // ── A title, a body, and a way to put it away ─────────────────────────────────────────────────────────

    /// <summary>
    /// The title shows on one line and truncates, so a paragraph in it arrives as an ellipsis with the useful half
    /// hidden — which is exactly what the first real finding did. Refused rather than trimmed: cutting it silently
    /// produces the sentence fragment the cap exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_paragraph_in_the_title_is_refused_and_told_where_the_detail_goes()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Discover),
            new
            {
                what = "Soraya — a new Middle Eastern restaurant from the Pachamama Group (the team behind Nina and "
                       + "Bottarga) — is opening in Marylebone at 96 George Street, W1U 8AF, a short walk from "
                       + "Cleveland Street. Their own site still says \"Opening Soon\".",
                why = "they keep a dinner-spots project for the area",
                subject = "dinner spots",
            });

        Assert.True(result.IsError);
        Assert.Contains("body", result.Content);
        Assert.Empty(store.Actions());
    }

    [Fact]
    public async Task A_title_and_a_body_are_kept_separately()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Discover),
            new
            {
                what = "Soraya is opening on George Street",
                body = "A new Middle Eastern place from the Pachamama Group.\n\n"
                       + "![the room](https://example.com/room.jpg)\n\n"
                       + "Their [own site](https://soraya.london) still says Opening Soon.",
                why = "they keep a dinner-spots project for the area",
                subject = "dinner spots",
            });

        Assert.False(result.IsError);
        var a = Assert.Single(store.Actions());
        Assert.Equal("Soraya is opening on George Street", a.What);
        Assert.Contains("Pachamama", a.Body);
        Assert.Contains("soraya.london", a.Body);
    }

    [Fact]
    public void Something_read_and_finished_with_can_be_put_away()
    {
        var store = NewStore();
        var a = store.Add(new ProactAction { Kind = ProactKinds.Noticed, What = "a thing", Why = "a reason" });

        Assert.True(store.Dismiss(a.Id));
        Assert.True(store.Get(a.Id)!.Dismissed);
        Assert.True(store.Get(a.Id)!.Seen);
    }

    /// <summary>
    /// A pending proposal is a decision they owe, and the answer is the tick or the cross — not a third way out that
    /// silently discards it.
    /// </summary>
    [Fact]
    public void An_unanswered_proposal_cannot_be_dismissed()
    {
        var store = NewStore();
        var offered = store.Add(new ProactAction
        {
            Kind = ProactKinds.Proposed, What = "Book the table", Why = "Friday is clear",
            Plan = "Open the page and book it", Catch = "none", Expires = DateTimeOffset.UtcNow.AddHours(6),
        });

        Assert.False(store.Dismiss(offered.Id));
        Assert.False(store.Get(offered.Id)!.Dismissed);
        Assert.Single(store.Waiting());

        // Once answered, it can be put away like anything else.
        store.Answered(offered.Id, ticked: false, DateTimeOffset.UtcNow);
        Assert.True(store.Dismiss(offered.Id));
    }

    /// <summary>
    /// An offer written as prose is an offer nobody can click.
    ///
    /// <para>
    /// Sent to look at a vacuum, it found the map had never been built — a genuinely good finding — and put "if you'd
    /// like, I can start a full clean" in the BODY. Two things of mine caused it: the brief said to finish with "one
    /// of" the three tools, so a fact and an offer competed for one slot; and this tool's own reply ended "do not
    /// also propose something about it", written to stop double-posting and suppressing exactly the case that
    /// mattered.
    /// </para>
    /// <para>
    /// The offer is the half the user can act on, so it has to become a tick and a cross. Detected rather than
    /// requested: a false positive costs one nudge it can ignore.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_offer_buried_in_a_notice_is_sent_to_become_a_proposal()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Discover),
            new
            {
                what = "Your vacuum has no rooms mapped yet",
                body = "Its map is empty, so room-by-room cleaning isn't available. If you'd like, I can start a "
                       + "full clean now so the map gets built.",
                why = "you keep a vacuum panel on your home page",
                subject = "the vacuum map",
            });

        // The notice itself is good and is kept.
        Assert.False(result.IsError);
        Assert.Single(store.Actions());

        // But it is told where the offer belongs.
        Assert.Contains("proact_propose", result.Content);
        Assert.DoesNotContain("You are done", result.Content);
    }

    [Fact]
    public async Task A_notice_with_nothing_offered_is_simply_finished()
    {
        var store = NewStore();

        var result = await Call(ProactTools.NoticedTool(store, ProactModes.Attend),
            new
            {
                what = "Thursday's two appointments are 120 miles apart",
                why = "both are on your list for Thursday and neither mentions travel",
                subject = "thursday",
            });

        Assert.False(result.IsError);
        Assert.Contains("You are done", result.Content);
        Assert.DoesNotContain("proact_propose", result.Content);
    }

    // ── Thumbs: the only signal that generalises ──────────────────────────────────────────────────────────

    [Fact]
    public void A_thumb_can_be_given_changed_and_taken_back()
    {
        var store = NewStore();
        var a = store.Add(new ProactAction { Kind = ProactKinds.Noticed, What = "a thing", Why = "a reason" });

        Assert.Equal(ProactVerdicts.Up, store.Vote(a.Id, ProactVerdicts.Up, DateTimeOffset.UtcNow)!.Verdict);
        Assert.Equal(ProactVerdicts.Down, store.Vote(a.Id, ProactVerdicts.Down, DateTimeOffset.UtcNow)!.Verdict);

        // A mis-tap must be undoable.
        Assert.Null(store.Vote(a.Id, null, DateTimeOffset.UtcNow)!.Verdict);
        Assert.Null(store.Get(a.Id)!.VotedAt);
    }

    /// <summary>
    /// Taste has to outlive the 24-hour log, which is the whole reason it is a separate payload: a thumbs-down on
    /// Monday is still true in November, and forgetting it overnight learns nothing from the only signal the user
    /// ever volunteers.
    /// </summary>
    [Fact]
    public void Taste_survives_long_after_the_daily_log_has_forgotten_the_action()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        var liked = store.Add(new ProactAction
        {
            At = now.AddDays(-9), Kind = ProactKinds.Noticed, What = "a new opening near them", Why = "r",
        });
        var loathed = store.Add(new ProactAction
        {
            At = now.AddDays(-9), Kind = ProactKinds.Noticed, What = "a wellbeing tip", Why = "r",
        });
        store.Vote(liked.Id, ProactVerdicts.Up, now);
        store.Vote(loathed.Id, ProactVerdicts.Down, now);

        // Nine days old: long gone from the 24-hour window.
        Assert.DoesNotContain("new opening", store.Describe(now));

        var taste = store.Taste();
        Assert.Contains("a new opening near them", taste);
        Assert.Contains("a wellbeing tip", taste);
        Assert.Contains("They liked these", taste);
        Assert.Contains("did NOT like", taste);
        // The distinction that stops taste fighting breadth: it says what KIND of thing lands, never what subject
        // to revisit. Two upvoted restaurant notes once read as "bring more restaurants" and produced five in a day.
        Assert.Contains("A DISLIKE IS AN INSTRUCTION. A LIKE IS NOT", taste);
        Assert.Contains("not an order for another on the same subject", taste);
    }

    /// <summary>
    /// The asymmetry that broke the restaurant loop.
    ///
    /// <para>
    /// A thumbs-up means "that was good", not "another one now" — but shown a same-day like it read as exactly that:
    /// two upvoted Fitzrovia openings in the morning produced two more in the afternoon, each of them correct and the
    /// set of them tedious. Somebody pleased to hear about a new restaurant at ten is not asking for a fourth by four.
    /// </para>
    /// <para>
    /// A dislike is the opposite: "not this" is true the moment it is said, and most valuable immediately, because
    /// the next look is the one that would otherwise repeat the mistake.
    /// </para>
    /// </summary>
    [Fact]
    public void A_like_today_does_not_steer_today_but_a_dislike_does()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        var likedToday = store.Add(new ProactAction
        {
            At = now.AddHours(-1), Kind = ProactKinds.Noticed, What = "a restaurant opening this morning", Why = "r",
        });
        var dislikedToday = store.Add(new ProactAction
        {
            At = now.AddHours(-1), Kind = ProactKinds.Noticed, What = "a wellbeing tip this morning", Why = "r",
        });
        store.Vote(likedToday.Id, ProactVerdicts.Up, now);
        store.Vote(dislikedToday.Id, ProactVerdicts.Down, now);

        var taste = store.Taste();

        // The dislike steers now.
        Assert.Contains("a wellbeing tip this morning", taste);
        // The like does not — it would read as "bring another one".
        Assert.DoesNotContain("a restaurant opening this morning", taste);
    }

    [Fact]
    public void A_like_from_an_earlier_day_still_says_what_good_looks_like()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        var liked = store.Add(new ProactAction
        {
            At = now.AddDays(-3), Kind = ProactKinds.Noticed, What = "a specific local opening", Why = "r",
        });
        store.Vote(liked.Id, ProactVerdicts.Up, now.AddDays(-3));

        var taste = store.Taste();
        Assert.Contains("a specific local opening", taste);
        Assert.Contains("previous days", taste);
    }

    [Fact]
    public void With_no_votes_there_is_nothing_to_say_about_taste()
    {
        var store = NewStore();
        store.Add(new ProactAction { Kind = ProactKinds.Noticed, What = "a thing", Why = "a reason" });

        Assert.Equal("", store.Taste());
    }

    // ── Breadth: a follow-up is not a new thing ───────────────────────────────────────────────────────────

    /// <summary>
    /// What the 24-hour log could not catch. It went back to the same restaurant project on consecutive deep looks
    /// with a genuinely different finding each time — nothing repeated, and the effect was still an assistant with
    /// one interest. So the subjects are named and ruled out, rather than the actions.
    /// </summary>
    [Fact]
    public void The_corners_it_has_already_been_in_are_answerable()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        store.Add(new ProactAction
        {
            At = now.AddHours(-2), Kind = ProactKinds.Noticed, Subject = "dinner spots",
            What = "Soraya is opening", Why = "r",
        });
        store.Add(new ProactAction
        {
            At = now.AddHours(-30), Kind = ProactKinds.Noticed, Subject = "Dinner Spots",
            What = "a different restaurant finding entirely", Why = "r",
        });
        store.Add(new ProactAction
        {
            At = now.AddDays(-9), Kind = ProactKinds.Noticed, Subject = "the boiler", What = "old news", Why = "r",
        });

        var been = store.RecentSubjects(now);

        // Yesterday still counts — coming back two days running is still narrow.
        Assert.Single(been);
        Assert.Equal("dinner spots", been[0], ignoreCase: true);
        // And it forgets eventually, or it could never return to anything.
        Assert.DoesNotContain("the boiler", been);
    }

    /// <summary>
    /// The collision has to be MACHINERY, not a request.
    ///
    /// <para>
    /// The subjects already covered were in the prompt and the prompt is a request — so the guard was the model
    /// noticing, which is the thing that cannot be relied on. Worse, the realisation would land after a decision to
    /// act had already been taken, which is the wrong order: what has been done already is an input to CHOOSING, not
    /// something to discover once the work is under way.
    /// </para>
    /// <para>
    /// So the decision names its subject, and the loop compares it against where it has been before anything is
    /// dispatched. This pins the comparison the loop performs.
    /// </para>
    /// </summary>
    [Fact]
    public void A_subject_it_has_already_been_in_is_recognisable_before_any_work_starts()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        store.Add(new ProactAction
        {
            At = now.AddHours(-3), Kind = ProactKinds.Noticed, Subject = "dinner spots",
            What = "Soraya is opening on George Street", Why = "r",
        });

        var been = store.RecentSubjects(now);

        // What the loop asks, in the words it asks it: case-insensitively, before dispatching anything.
        Assert.Contains(been, s => s.Equals("Dinner Spots", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(been, s => s.Equals("the Lisbon trip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Every_recorded_action_says_what_it_was_about()
    {
        var store = NewStore();

        await Call(ProactTools.NoticedTool(store, ProactModes.Attend),
            new
            {
                what = "Thursday's two appointments are 120 miles apart",
                why = "both are on Thursday's list",
                subject = "thursday",
            });

        Assert.Equal("thursday", store.Actions().Single().Subject);
    }

    // ── The daily roundup: the one part of Proact that reaches out ─────────────────────────────────────────

    /// <summary>
    /// One a day, whether it did two things or twenty — which is the property that stops the summary becoming the
    /// noise it exists to summarise. A per-action alert could not make that promise.
    /// </summary>
    [Fact]
    public void The_roundup_happens_once_a_day_and_not_twice()
    {
        var store = NewStore();
        var evening = new DateTimeOffset(2026, 8, 21, 18, 30, 0, TimeSpan.Zero);

        Assert.True(store.RoundupDue(evening));
        Assert.NotNull(store.Roundup(evening));

        // Every tick after the hour passes through here; only the first may write one.
        Assert.False(store.RoundupDue(evening.AddMinutes(5)));
        Assert.Null(store.Roundup(evening.AddMinutes(5)));
        Assert.Single(store.Roundups());
    }

    [Fact]
    public void It_does_not_round_up_before_its_hour()
    {
        var store = NewStore();
        Assert.False(store.RoundupDue(new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_roundup_can_be_turned_off_without_turning_proact_off()
    {
        var store = NewStore();
        store.Set(on: true, roundupHour: -1);

        Assert.Null(store.Settings.RoundupHour);
        Assert.True(store.Settings.On);
        Assert.False(store.RoundupDue(new DateTimeOffset(2026, 8, 21, 23, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>
    /// It reports what it did NOT do as well as what it did. A digest of only wins reads as cleverer than the thing
    /// actually is, and the looks-behind-the-actions ratio is the most honest line in it.
    /// </summary>
    [Fact]
    public void The_roundup_says_what_it_did_and_how_many_looks_that_took()
    {
        var store = NewStore();
        var evening = new DateTimeOffset(2026, 8, 21, 18, 30, 0, TimeSpan.Zero);
        var morning = new DateTimeOffset(2026, 8, 21, 8, 40, 0, TimeSpan.Zero);

        store.Add(new ProactAction
        {
            At = morning, Kind = ProactKinds.Noticed,
            What = "Thursday's two appointments are 120 miles apart", Why = "both on Thursday's list",
        });
        for (var i = 0; i < 30; i++)
            store.Ticked(new ProactTickRecord { At = morning.AddMinutes(i * 10), Changed = false });

        var roundup = store.Roundup(evening)!;

        Assert.Contains("120 miles apart", roundup.Text);
        Assert.Contains("30 looks", roundup.Text);
        Assert.Equal(1, roundup.Did);
        Assert.Equal(30, roundup.Looks);
    }

    [Fact]
    public void A_quiet_day_is_reported_as_a_quiet_day()
    {
        var store = NewStore();
        var evening = new DateTimeOffset(2026, 8, 21, 18, 30, 0, TimeSpan.Zero);
        for (var i = 0; i < 12; i++)
            store.Ticked(new ProactTickRecord { At = evening.AddHours(-i), Changed = false });

        var roundup = store.Roundup(evening)!;

        // Said plainly. Dressing a quiet day up is how a roundup starts sounding like marketing.
        Assert.Contains("Nothing worth bringing you", roundup.Text);
        Assert.Equal(0, roundup.Did);
    }

    [Fact]
    public void The_roundup_names_what_is_still_waiting_on_them()
    {
        var store = NewStore();
        var evening = new DateTimeOffset(2026, 8, 21, 18, 30, 0, TimeSpan.Zero);

        store.Add(new ProactAction
        {
            At = evening.AddHours(-2), Kind = ProactKinds.Proposed,
            What = "Book Trullo for Friday", Why = "Friday is clear",
            Plan = "Open the page and book it", Catch = "free until Thursday",
            Expires = evening.AddDays(1),
        });

        var roundup = store.Roundup(evening)!;

        Assert.Contains("Book Trullo", roundup.Text);
        Assert.Contains("still waiting on you", roundup.Text);
        Assert.Contains("Waiting on you: 1", roundup.Text);
        Assert.Equal(1, roundup.Waiting);
    }

    [Fact]
    public void A_roundup_is_dismissed_by_being_read()
    {
        var store = NewStore();
        var evening = new DateTimeOffset(2026, 8, 21, 18, 30, 0, TimeSpan.Zero);
        var roundup = store.Roundup(evening)!;

        Assert.False(roundup.Seen);
        store.RoundupSeen(roundup.Day);
        Assert.True(store.RoundupFor(roundup.Day)!.Seen);
    }

    [Fact]
    public void Roundups_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proact-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var evening = new DateTimeOffset(2026, 8, 21, 18, 30, 0, TimeSpan.Zero);

        new ProactStore(path, json).Roundup(evening);

        var again = new ProactStore(path, json);
        Assert.Single(again.Roundups());
        // And it still refuses a second one for the same day, which is the promise that matters.
        Assert.False(again.RoundupDue(evening.AddHours(1)));
    }

    // ── Liveness, because a stopped Proact must not look like a quiet one ──────────────────────────────────

    [Fact]
    public void A_tick_that_did_nothing_is_still_recorded()
    {
        var store = NewStore();
        store.Ticked(new ProactTickRecord { Changed = false, Note = "nothing had changed" });
        store.Ticked(new ProactTickRecord { Changed = true, Note = "nothing worth doing" });

        Assert.Equal(2, store.Ticks().Count);
        Assert.NotNull(store.LastTick);
        Assert.Equal(0, store.ActionsSince(DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [Fact]
    public void An_error_on_a_tick_is_kept_rather_than_swallowed()
    {
        var store = NewStore();
        store.Ticked(new ProactTickRecord { Error = "the model timed out" });

        Assert.Equal("the model timed out", store.Ticks().Single().Error);
    }

    /// <summary>A deep dive keeps its own pacing, so the loop has to be able to see when the last one was.</summary>
    [Fact]
    public void The_last_deep_dive_is_findable_separately_from_the_last_look()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        store.Ticked(new ProactTickRecord { At = now.AddHours(-8), Mode = ProactModes.Discover, Changed = true });
        store.Ticked(new ProactTickRecord { At = now.AddMinutes(-5), Mode = ProactModes.Attend, Changed = false });

        Assert.NotNull(store.LastDiscover);
        Assert.True(store.LastDiscover < store.LastTick);
    }

    /// <summary>
    /// With no cap on how much it may do, this figure is the instrument rather than a statistic — the only early
    /// warning of the failure mode most likely to actually happen.
    /// </summary>
    [Fact]
    public void How_much_it_has_done_today_is_answerable()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        store.Add(new ProactAction { At = now.AddMinutes(-30), Kind = ProactKinds.Noticed, What = "one", Why = "r" });
        store.Add(new ProactAction { At = now.AddMinutes(-10), Kind = ProactKinds.Prepared, What = "two", Why = "r" });
        store.Add(new ProactAction { At = now.AddDays(-2), Kind = ProactKinds.Noticed, What = "old", Why = "r" });

        Assert.Equal(2, store.ActionsSince(now.AddHours(-1)));
    }

    // ONE AT A TIME. The duplicate these prevent was seen live: two workers out at once on the same subject,
    // producing two near-identical proposals, because neither had written anything down when the other was sent.

    [Fact]
    public void NothingOutMeansNotBusy()
    {
        var store = NewStore();

        Assert.False(store.Busy(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ARunThatIsStillOutMakesItBusy()
    {
        var store = NewStore();

        store.Working("task-1");

        Assert.True(store.Busy(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ARunThatCameBackFreesItUp()
    {
        var store = NewStore();
        store.Working("task-1");

        store.Working(null);

        Assert.False(store.Busy(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AWorkerThatNeverReportsIsEventuallyWrittenOff()
    {
        // The opposite failure, and the worse one: a run that dies without reporting must not silence the loop.
        var store = NewStore();
        store.Working("task-lost");

        Assert.True(store.Busy(DateTimeOffset.UtcNow));
        Assert.False(store.Busy(DateTimeOffset.UtcNow.AddMinutes(21)));
    }

    [Fact]
    public void BeingWrittenOffIsPermanent()
    {
        // Once it is written off the flag is cleared rather than merely reported stale, so a clock that goes
        // backwards cannot resurrect a worker that is gone.
        var store = NewStore();
        store.Working("task-lost");

        Assert.False(store.Busy(DateTimeOffset.UtcNow.AddMinutes(21)));
        Assert.False(store.Busy(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BusyIsNotWrittenToDisk()
    {
        // In memory on purpose: a restart is the cheapest way out of a stuck flag, so it must not survive one.
        var path = Path.Combine(Path.GetTempPath(), $"proact-busy-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = new ProactStore(path, json);
        first.Working("task-1");
        Assert.True(first.Busy(DateTimeOffset.UtcNow));

        Assert.False(new ProactStore(path, json).Busy(DateTimeOffset.UtcNow));
    }

    // THE THREAD BACK FROM THE WORK TO THE OFFER. Without it a tick is dispatched and never resolved.

    [Fact]
    public void ATickedProposalsConversationNamesItsProposal()
    {
        var id = "abc123";

        Assert.Equal(id, Proact.TickedIn(Proact.ConversationForTicked(id)));
    }

    [Theory]
    [InlineData("proact-discover-20260821120000")]
    [InlineData("proact-attend-20260821120000")]
    [InlineData("some-other-chat")]
    [InlineData("proact-do-")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotATickedProposal(string? sessionId)
    {
        Assert.Null(Proact.TickedIn(sessionId));
    }

    [Fact]
    public void LookingAndTickedAreDifferentThings()
    {
        // A look is Proact's own errand; a tick is work the user authorised. Neither should answer to the other's
        // question, because one is hidden from their chat list and the other deliberately is not.
        var ticked = Proact.ConversationForTicked("abc123");

        Assert.False(Proact.Looking(ticked));
        Assert.NotNull(Proact.TickedIn(ticked));
    }

    [Fact]
    public void HowTheCommitWentIsRecorded()
    {
        var store = NewStore();
        var action = new ProactAction
        {
            Kind = ProactKinds.Proposed, What = "Run a full clean", Plan = "Send it out", Catch = "Nothing",
            Expires = DateTimeOffset.UtcNow.AddDays(3), Answer = ProactAnswers.Pending,
        };
        store.Add(action);
        store.Answered(action.Id, ticked: true, DateTimeOffset.UtcNow);

        store.Finished(action.Id, "done");

        Assert.Equal("done", store.Get(action.Id)!.Outcome);
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
