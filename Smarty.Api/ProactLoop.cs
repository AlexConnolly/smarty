using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The loop that goes out and looks.
///
/// <para>
/// Two things have to be true at once and they pull in opposite directions. The user picks a cadence as fast as
/// every five minutes — 288 wakeups a day — and the loop must be affordable enough to leave on for months. So the
/// tick is a heartbeat, not a workload: the great majority of wakeups reach no model at all, because nothing has
/// changed since the last one, and a wakeup with nothing to think about should cost nothing rather than "cost a
/// little".
/// </para>
/// <para>
/// The order of the decision is the design, exactly as it is for feeds. Free first: is it on, is it due, has
/// anything changed. Only what survives all of that is read by a model, and only what the model thinks is worth
/// doing becomes a worker with tools. A quiet day costs approximately nothing.
/// </para>
/// </summary>
public sealed class ProactLoop
{
    private readonly ProactStore _store;
    private readonly IModelProvider _model;
    private readonly string _modelName;

    /// <summary>Who they are and what is going on — assembled by the caller, which is where the stores live.</summary>
    private readonly Func<string> _context;

    /// <summary>
    /// A cheap string that changes when their world does.
    /// </summary>
    /// <remarks>
    /// The whole economics of the fast dial rest on this. It must be assembled from things already in memory —
    /// counts, latest ids, the top of a list — and must never fetch anything, because it runs 288 times a day and
    /// its entire purpose is to cost nothing.
    /// </remarks>
    private readonly Func<string> _fingerprint;

    /// <summary>Sends a worker to do the thing, with Proact's bounded toolset. Returns the task id.</summary>
    private readonly Func<string, string, Task<(string Task, string Session)>> _dispatch;

    private readonly Action<string>? _trace;

    private string? _lastPrint;

    /// <summary>
    /// How rarely a deep dive happens, whatever the dial says.
    /// </summary>
    /// <remarks>
    /// Deliberately not on the user's interval. Somebody who sets five minutes wants to be attended to more closely;
    /// they do not want twelve times as many deep dives. There are only so many deep things to find in one person's
    /// week, and a Discover that runs hourly is a Discover that reports shallow things hourly.
    /// </remarks>
    private static readonly TimeSpan DiscoverEvery = TimeSpan.FromHours(6);

    /// <summary>How often the loop checks whether it is due. Not the cadence — the cadence comes from the dial.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(30);

    public ProactLoop(ProactStore store, IModelProvider model, string modelName,
        Func<string> context, Func<string> fingerprint,
        Func<string, string, Task<(string Task, string Session)>> dispatch, Action<string>? trace = null)
    {
        _store = store;
        _model = model;
        _modelName = modelName;
        _context = context;
        _fingerprint = fingerprint;
        _dispatch = dispatch;
        _trace = trace;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // A first look a couple of minutes in rather than immediately: the stores are still loading at startup and a
        // decision made from a half-loaded picture is a decision about nothing. Borrowed from the panel suggester,
        // which learned it the same way.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Recorded rather than swallowed. A Proact that quietly stopped is the worst outcome available, and
                // this system has shipped that bug twice — once on feeds, once on panels.
                _store.Ticked(new ProactTickRecord { Error = ex.Message });
                _trace?.Invoke($"[proact] tick failed: {ex.Message}");
            }

            try { await Task.Delay(Beat, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Is it time, and if so what should happen? Public so "look now" on the page runs the real thing.</summary>
    /// <param name="deep">
    /// Ask for the deep look specifically. A manual poke is an Attend by default — somebody pressing a button wants
    /// a quick answer — but there has to be a way to say "go and have a proper dig", and without one the deep look
    /// is only ever reachable by waiting.
    /// </param>
    public async Task<bool> TickAsync(CancellationToken ct, bool force = false, bool deep = false)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = _store.Settings;
        if (!force && !settings.Running(now)) return false;

        if (!force && !Due(settings, now)) return false;

        // Not while one is still out. A manual poke gets through, because somebody pressing the button means it.
        if (!force && _store.Busy(now))
        {
            _trace?.Invoke("[proact] still working on the last one; leaving it be");
            return false;
        }

        // Before anything else, because a stale proposal invites a click that would do the wrong thing.
        if (_store.Expire(now) is > 0 and var gone)
            _trace?.Invoke($"[proact] {gone} proposal(s) expired unanswered");

        // The day's roundup, once, when its hour comes.
        //
        // Riding on the tick rather than having a timer of its own: the tick already runs every few minutes, and a
        // second loop would be a second thing that can silently stop. The store refuses a duplicate for the same
        // local day, so it does not matter how many ticks pass through here after the hour.
        if (_store.RoundupDue(DateTimeOffset.Now) && _store.Roundup(DateTimeOffset.Now) is { } roundup)
            _trace?.Invoke($"[proact] rounded up {roundup.Day}: {roundup.Did} thing(s) from {roundup.Looks} look(s)");

        var discovering = deep || Discovering(now, force);
        var mode = discovering ? ProactModes.Discover : ProactModes.Attend;

        // The free gate, and the reason a five-minute dial is affordable. A Discover is exempt: going deep on
        // something is worth doing precisely when nothing on the surface has moved.
        var print = Safe(_fingerprint);
        if (!discovering && !force && print is { } p && p == _lastPrint)
        {
            _store.Ticked(new ProactTickRecord { At = now, Mode = mode, Changed = false, Note = "nothing had changed" });
            return false;
        }
        _lastPrint = print;

        var context = Safe(_context) ?? "";
        // Where it has been, as information rather than as an instruction it must obey. Telling it costs nothing and
        // is the difference between judgement and amnesia; ASSIGNING it somewhere — which was tried — replaces the
        // judgement that is the whole point of having a model in this loop at all.
        var history = _store.Describe(now)
                      + Breadth(_store.RecentSubjects(now))
                      + _store.Taste();

        // BOTH halves in the trace, and this comment used to say that while the code traced only the first.
        //
        // Which mattered the moment it was needed: the history carries the 24-hour log, the subjects already covered
        // and what the user has thumbed up or down, and those are precisely the inputs meant to change the answer —
        // so a decision that looks wrong is undebuggable without them. A trace that claims to show the inputs and
        // shows half of them is worse than one that shows none, because it is believed.
        _trace?.Invoke($"[proact] {mode}: looking at {context.Replace('\n', '|')}");
        if (history is { Length: > 0 }) _trace?.Invoke($"[proact] {mode}: knowing {history.Replace('\n', '|')}");

        var decision = await AskAsync(mode, context, history, ct).ConfigureAwait(false);

        if (decision is null)
        {
            _store.Ticked(new ProactTickRecord
            {
                At = now, Mode = mode, Changed = true, Error = "couldn't decide",
            });
            return false;
        }

        var (act, doing) = decision.Value;

        if (!act || string.IsNullOrWhiteSpace(doing))
        {
            // The commonest outcome, and the design working rather than failing. Its reason is recorded because
            // "why did it do nothing all afternoon" has to be answerable.
            _store.Ticked(new ProactTickRecord
            {
                At = now, Mode = mode, Changed = true,
                Note = doing is { Length: > 0 } ? doing : "nothing worth doing",
            });
            _trace?.Invoke($"[proact] {mode}: nothing worth doing — {doing}");
            return false;
        }

        // Recorded BEFORE the worker starts, and this ordering matters for the deep look: the pacing is measured
        // from the tick, so a Discover that dispatched and then failed still counts as having used its slot. The
        // alternative is a broken deep dive retrying every few minutes for six hours.
        _store.Ticked(new ProactTickRecord
        {
            At = now, Mode = mode, Changed = true, Acted = 1, Note = doing,
        });

        _trace?.Invoke($"[proact] {mode}: going to {Snip(doing, 120)}");

        // The worker records the action itself, with one of the three tools. The loop deliberately does not — an
        // action written by the loop would be an action nobody actually did.
        var brief = ProactTools.Brief(mode, doing, context, history);
        try
        {
            var (started, where) = await _dispatch(mode, brief).ConfigureAwait(false);
            _store.Working(started, where);
        }
        catch (Exception ex)
        {
            _store.Ticked(new ProactTickRecord { At = now, Mode = mode, Changed = true, Error = $"couldn't start: {ex.Message}" });
            _trace?.Invoke($"[proact] couldn't start the work: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Has the dial's interval elapsed since the last look?</summary>
    private bool Due(ProactSettings settings, DateTimeOffset now) =>
        _store.LastTick is not { } last || now - last >= Every(settings);

    /// <summary>The dial, as a length of time.</summary>
    /// <remarks>
    /// The dial's words are the ones a feed uses, so the same parser reads them. A cadence that will not parse falls
    /// back to ten minutes rather than to never — a typo should slow it down, not silence it.
    /// </remarks>
    private static TimeSpan Every(ProactSettings settings)
    {
        var every = ScheduleStore.TryParseRepeat(settings.Every, DateTimeOffset.Now, out var next)
            ? next - DateTimeOffset.Now
            : TimeSpan.FromMinutes(10);

        return every <= TimeSpan.Zero ? TimeSpan.FromMinutes(10) : every;
    }

    /// <summary>When it next goes out. Null when it is switched off, because there is no answer then.</summary>
    /// <remarks>
    /// Shares <see cref="Every"/> with <see cref="Due"/> deliberately. A countdown worked out separately from the
    /// check it is counting down to is a countdown that reaches zero and then sits there, which is worse than no
    /// countdown at all: it reads as something being stuck.
    /// </remarks>
    public DateTimeOffset? NextAt(DateTimeOffset nowUtc)
    {
        var settings = _store.Settings;
        if (!settings.On) return null;

        // Paused beats due. Whatever the dial says, nothing happens before the pause is up.
        if (settings.PausedUntil is { } until && until > nowUtc) return until;

        return _store.LastTick is not { } last ? nowUtc : last + Every(settings);
    }

    private bool Discovering(DateTimeOffset now, bool force) =>
        !force && (_store.LastDiscover is not { } last || now - last >= DiscoverEvery);

    /// <summary>
    /// Ask whether anything is worth doing. One cheap call, structured, no tools.
    /// </summary>
    /// <remarks>
    /// Separated from the doing on purpose. Deciding is a paragraph of context and a yes or no; doing needs a
    /// browser, a workspace and minutes. Running them together would mean paying for a worker on every tick that
    /// turned out to have nothing to do — which, by design, is nearly all of them.
    /// </remarks>
    private async Task<(bool Act, string? Doing)?> AskAsync(
        string mode, string context, string history, CancellationToken ct)
    {
        var request = new ModelRequest
        {
            Model = _modelName,
            Messages = new List<Message>
            {
                Message.System(
                    // WHAT IT IS FOR, which this prompt never said.
                    //
                    // It opened with "decide whether there is anything worth doing", which is a monitor's question:
                    // it asks whether a condition holds, and the honest answer to it is usually no. Sent to a robot
                    // vacuum on the strength of it, the line it wrote was "checking your robot vacuum's status and
                    // whether it needs attention" — it went to INSPECT a machine, found it charged and idle,
                    // and correctly reported that no attention was needed. Nobody buys a vacuum so that somebody
                    // can watch it.
                    //
                    // The word missing from the whole prompt was "chores". Naming what it is for is not a nudge
                    // toward any particular errand; it is the difference between an assistant and a dashboard.
                    "You are someone's assistant, and what you are for is GETTING THINGS DONE for them — chores, " +
                    "errands, the admin nobody enjoys, the research they have not got round to, the ordinary " +
                    "business of a life. Nobody has asked you anything: you have woken up on your own to decide " +
                    "what to do for them right now.\n\n" +
                    "Here is what you know about them and their situation:\n" + context + "\n" +
                    history + "\n" +
                    (mode == ProactModes.Discover
                        ? "THIS IS THE DEEP LOOK, and it comes round about four times a day. It is the one slot " +
                          "where real work is affordable, so the bar for declining it is HIGH — much higher than " +
                          "for a quick look. Use it.\n" +
                          // THE PRECEDENCE, because these two were fighting and the wrong one kept winning.
                          //
                          // "Use the slot" and "everything is covered" are both true when somebody has one project
                          // and it has been done. Left unranked it resolved toward acting, so a deep look went back
                          // to the same restaurants for the fifth time. The high bar is a bar for FINDING something
                          // new, never a licence to revisit something old.
                          "BUT THAT BAR DOES NOT OVERRIDE WHERE YOU HAVE ALREADY BEEN. If every part of their life " +
                          "you can reach has been covered below, the right answer is act=false — not a fifth visit " +
                          "to whichever of them went best. A quiet week is a quiet week, and saying so costs you " +
                          "nothing; going round again costs the only thing that matters, which is them still " +
                          "bothering to read you.\n" +
                          "Depth is not urgency. \"Nothing is due and nothing has changed\" is the normal state of " +
                          "affairs and is NOT a reason to decline this.\n" +
                          // WHAT THIS USED TO SAY, AND WHY IT WAS THE BUG.
                          //
                          // "Their own projects and lists ... are almost never where the answer is. The value is in
                          // going OUT and coming back with something they did not have — what has opened, what has
                          // changed out there."
                          //
                          // Which is where "checking what else has opened near you in Fitzrovia" came from, five
                          // deep looks running. Not a rut it fell into on its own: a recital of the loudest sentence
                          // in its instructions. And the sentence after it — "reading their material and reporting
                          // it back is worth nothing" — was meant to ban reading a list ALOUD, and instead banned
                          // the whole category of doing something about one, because it never drew the line between
                          // handing their notes back and acting on them.
                          //
                          // So getting a thing DONE for them was ruled out twice before it was ranked third. The
                          // same mistake in the same words was already found and fixed in the worker's brief (see
                          // ProactTools.Brief); this copy survived because nothing makes the decider and the worker
                          // share a sentence.
                          "WHAT WINS IS WHATEVER HELPS THEM MOST, and there are three kinds of it. Something off " +
                          "their plate — a thing they are carrying that you could carry instead, worked on and left " +
                          "further along than you found it. Something of theirs nobody has asked — a device, a " +
                          "state, a number they cannot see from where they sit. Or something from outside they did " +
                          "not have. RANK THEM BY WHAT IT IS WORTH TO THEM rather than by which is the more " +
                          "interesting trip, and the first of the three is most often worth the most: a job " +
                          "advanced beats a fact delivered, because they wanted the job done and nobody asked for " +
                          "the fact.\n" +
                          "What is worth nothing is handing their own material back UNCHANGED — their list read " +
                          "out to them, their project restated. That is not the same as doing something about what " +
                          "is in it. Those are opposites, and the difference is whether they are further along " +
                          "afterwards.\n" +
                          "Say which you picked and what you are going to do about it. Decline only if there is " +
                          "genuinely nothing worth the trip.\n\n"
                        : "This is a quick look, which happens every few minutes.\n" +
                          "DOING NOTHING IS THE RIGHT ANSWER ALMOST EVERY TIME. Only act on something that has " +
                          "genuinely changed or is genuinely close. An assistant who produces something every few " +
                          "minutes is one who gets switched off in a week; one who produces a single genuinely " +
                          "useful thing a day is invaluable. Be quiet by default and specific when you do speak.\n\n") +
                    // ORDER IS THE RANKING. Whatever stands first here, argued longest, is what comes back — so
                    // the list opens on the kind of work they actually wanted, and the outward trip keeps its place
                    // without keeping its precedence.
                    "What counts as worth doing, BEST FIRST:\n" +
                    "  - SOMETHING GOT DONE. Work you can simply do for them and leave ready — gathered, drafted, " +
                    "worked out, written up, or a thing of theirs put in the state they would want it in. This is " +
                    "the top of the list because it is the only kind that leaves them with LESS to do than they had " +
                    "this morning; everything below it leaves them with more to read. If something they are " +
                    "carrying could be carried further without asking them anything, that beats any fact you could " +
                    "go and fetch.\n" +
                    "  - a fact about their own week they would not have spotted (two things clashing, a deadline " +
                    "nearer than it looks, a consequence of two separate arrangements)\n" +
                    "  - A STANDING INTEREST, TAKEN FURTHER. Anything they have shown they care about — a project, " +
                    "a list, a subject they keep coming back to — and something about it they do not know yet. " +
                    "NOTHING HAS TO HAVE CHANGED ON THEIR SIDE for this to be worth doing: the world moved, or you " +
                    "simply have not looked into it yet. Worth doing, and easy to miss because it needs no prompt " +
                    "and no deadline.\n" +
                    "  - something worth offering that you will not do unattended, as a tick-or-cross proposal\n\n" +
                    "Do not wait to be given a problem. A subject they keep is a standing invitation to bring them " +
                    "something new about it, and \"nothing is wrong and nothing is due\" is the normal state of a " +
                    "week rather than a reason to do nothing.\n\n" +
                    "What does NOT count, ever:\n" +
                    "  - anything that would be equally true of a stranger. The test is whether it is about THIS " +
                    "person; if you cannot point at the thing in their situation that prompted it, the answer is no\n" +
                    // No named example, deliberately. A negative one primes the noun exactly as well as a positive
                    // one does: an illustration about restaurants, however disparaging, is still the word
                    // "restaurants" sitting in the prompt — and this loop has already spent a day proving it reaches
                    // for whatever is most salient in front of it.
                    "  - the obvious answer to a standing interest. The best-known example of whatever they keep a " +
                    "list of is not research, it is a guess they could have made themselves. Taking an interest " +
                    "further means something SPECIFIC and genuinely new — recent, local, particular, and checked\n" +
                    "  - advice they did not ask for, wellbeing suggestions, reminders to relax, generic tips\n" +
                    "  - anything already on the list of what you have done in the last 24 hours, however reworded\n" +
                    "  - checking in, saying hello, or offering to help\n\n" +
                    // THE LAST RUNG BEFORE NO.
                    //
                    // "Nothing new on your lists" was being treated as a finished answer, and it is not: it is one
                    // place looked at. Their devices are listed above and were being walked straight past — a vacuum
                    // that has not run in four days is a real, specific, useful thing to say, and it is knowable for
                    // the price of one question. So declining now has a step before it rather than being the default
                    // exit, and the step points at the one part of their world that can always be interrogated.
                    // YOU ARE DECIDING, NOT DOING — and this is the sentence that was missing.
                    //
                    // This call has no tools. Told to "go and look at the devices before saying no", the model did
                    // the only thing it could: reasoned about them from the names alone, found nothing, and declined
                    // with "nothing new on your lists or devices". Which was honest — it cannot see a vacuum's state
                    // from a list of command names. It never needed to: not knowing IS the reason to send somebody.
                    "YOU HAVE NO TOOLS IN THIS CALL. You cannot read a page, query a device or check a state right " +
                    "now — you are deciding whether it is worth going, and something else does the going.\n" +
                    // Named generically, and the names come from the context rather than from here. An example with
                    // a real device in it is a prompt that only fits one person's house — and this one had a vacuum
                    // written into it, which would have read as a nudge toward hoovering to somebody who owns a
                    // thermostat. What each of them is called is listed above, from what is actually installed.
                    // AND THE TRIP IS NOT AN INSPECTION, which is what this clause used to describe.
                    //
                    // Every payoff it offered was a thing to SAY: "worth telling them", "something that wants
                    // attention", "a state they would want to know". So the decision it produced was "checking
                    // whether it needs attention" — a faithful reading of the only kind of win on offer. The
                    // second half, the one that matters for a machine, was never written down: that you might go
                    // there and simply DO something.
                    "So NOT KNOWING IS NOT A REASON TO DECLINE. It is the reason to go. And going is worth it for " +
                    "either of two things, not one: something to TELL them — a state they cannot see from where " +
                    "they sit, something that has not happened when it usually does — or SOMETHING TO DO — a " +
                    "chore you could get out of the way, a job of theirs that is due, something set wrong you could " +
                    "set right. THE SECOND IS USUALLY THE BETTER ANSWER. They did not buy the thing so that " +
                    "somebody could watch it.\n" +
                    "So say act=true, name the thing in `doing` using its own name from the list, and say what you " +
                    "are going there TO DO rather than what you are going to check. You find out when you arrive, " +
                    "and if there turns out to be nothing, nothing is recorded and nothing is lost.\n" +
                    "\"Nothing new on their lists\" is not an answer while there is something of theirs nobody has " +
                    "asked. " +
                    "Decline because you have thought about it and there is genuinely nothing worth the trip — never " +
                    "because you cannot see the answer from here.\n" +
                    "Put ONE SHORT LINE in `doing`, and nothing else anywhere. If you are acting it is what you " +
                    "are about to go and look at; if you are not, it is why not, in a few words.\n" +
                    "WRITE IT TO THEM, IN THE SECOND PERSON. This line appears on their own page — \"nothing new on " +
                    "your lists\", \"checking what else has opened near you\". NEVER \"they\" or \"their\": a note " +
                    "about somebody, visible to that somebody, reads as being talked about behind their back, and it " +
                    "is the one thing here that would make an assistant feel like surveillance rather than help.\n" +
                    "Keep it short: a long answer gets cut off before it is finished and the decision is lost."),
            },
            // THINKING ON, and this is the one call in the whole feature where it earns its keep.
            //
            // It was off to save money, and that was the wrong economy: with no room to reason it pattern-matched to
            // whatever was most salient in the prompt, which was always the project it had most recently talked
            // about. Hence three looks in a row at the same restaurants. "What would actually help this person right
            // now" is a judgement over their whole situation — their week, their projects, their devices, what it has
            // already told them — and a judgement made without thinking is just the nearest available association.
            Think = true,
            ResponseFormat = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                // THE ORDER OF THESE IS LOAD-BEARING, which is not obvious and cost two live runs to learn.
                //
                // A model generates the fields in the order the schema lists them, so the order decides what survives
                // an answer that gets cut off. With `why` second, it explained its reasoning at length, ran out
                // mid-sentence, and `doing` — the one field the loop cannot work without — was never written at all.
                // The decision was recovered, read as having no instruction, and recorded as "nothing worth doing":
                // it had decided to act and the loop could not tell.
                //
                // So the load-bearing field goes first. `why` is last because it is the one that can afford to be
                // lost, being for the record rather than for the work.
                // Bare types, no per-property descriptions — the same shape the panel suggester uses, which is the
                // one known to work against this provider. Adding descriptions here came back as five characters of
                // punctuation, so whatever carries them is not carrying them well; the prompt above says the same
                // things and is not part of the schema.
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["act"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean" },
                    // Before `doing`, and short, so it survives a truncated answer — the loop cannot check a
                    // collision it never received.
                    ["doing"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                },
                // Only `act` is required, for the reason the panel suggester found out the hard way: demanding the
                // other fields when the answer is no means they come back filled with placeholder text, and a
                // placeholder that reaches a surface is worse than no offer at all.
                ["required"] = new System.Text.Json.Nodes.JsonArray { "act" },
            },
            MaxOutputTokens = 2000,
            TurnTimeout = TimeSpan.FromSeconds(120),
        };

        try
        {
            var answer = await _model.CompleteAsync(request, ct).ConfigureAwait(false);

            // What came back, when it could not be read.
            //
            // Every one of these paths used to return a bare null, which the tick recorded as "couldn't decide" —
            // safe, visible on the timeline, and completely undiagnosable. On a five-minute dial a model that has
            // stopped honouring the response format would produce that line 288 times a day and there would be no
            // way to tell it from a model that was merely slow. The decision's inputs are traced; its output has to
            // be too.
            if (string.IsNullOrWhiteSpace(answer.Content))
            {
                _trace?.Invoke("[proact] the model returned nothing at all");
                return null;
            }

            return Decide(answer.Content) ?? Scavenge(answer.Content);
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"[proact] couldn't decide: {ex.Message}");
            return null;
        }
    }

    /// <summary>The clean path: it came back as JSON and parsed.</summary>
    private static (bool, string?)? Decide(string content)
    {
        int first = content.IndexOf('{'), last = content.LastIndexOf('}');
        if (first < 0 || last <= first) return null;

        try
        {
            using var doc = JsonDocument.Parse(content[first..(last + 1)]);
            var root = doc.RootElement;

            if (!root.TryGetProperty("act", out var act)
                || act.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            string? Read(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()?.Trim()
                    : null;

            return (act.GetBoolean(), Read("doing"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Read the decision out of an answer that never finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not defensive padding — this was earned. The moment the decision got interesting, the model started explaining
    /// itself at length and its JSON arrived cut off mid-string: <c>{"act":true,"why":"They keep a list of dinner
    /// spots around Fitzrovia; the world moves on openings…</c> and no closing brace. It had reached exactly the right
    /// answer and the parser threw the whole thing away, so the loop recorded "couldn't decide" and did nothing. Twice.
    /// </para>
    /// <para>
    /// The shape of the thing being read is what makes this safe rather than sloppy: a boolean and two short strings,
    /// with the boolean first. There is no structure to get subtly wrong — either the word is there or it is not — and
    /// a decision that is legible but unparseable is far better used than discarded. Raising the token ceiling helps
    /// and does not fix it, because there is no ceiling at which a model cannot be verbose.
    /// </para>
    /// </remarks>
    private (bool, string?)? Scavenge(string content)
    {
        var act = System.Text.RegularExpressions.Regex.Match(content, @"""act""\s*:\s*(true|false)");
        if (!act.Success)
        {
            _trace?.Invoke($"[proact] couldn't read a decision out of this ({content.Length} chars): {Snip(content, 1200)}");
            return null;
        }

        string? Field(string name)
        {
            // Up to the closing quote, or to the end when it was cut off there.
            var m = System.Text.RegularExpressions.Regex.Match(
                content, $@"""{name}""\s*:\s*""((?:[^""\\]|\\.)*)");
            return m.Success && m.Groups[1].Value.Trim() is { Length: > 0 } text ? text.Replace("\\\"", "\"").Trim() : null;
        }

        _trace?.Invoke($"[proact] the answer was cut off; read the decision out of it anyway");
        return (act.Groups[1].Value == "true", Field("doing"));
    }

    /// <summary>
    /// Where it has already been, and the instruction to go elsewhere.
    /// </summary>
    /// <remarks>
    /// The 24-hour log forbids repeating an action and that was not enough. It went back to the same restaurant
    /// project on consecutive deep looks with a genuinely different finding each time — nothing repeated, and the
    /// effect was still an assistant with one interest. A follow-up feels like new work from the inside and reads as
    /// narrowness from the outside, so the subjects have to be named and ruled out explicitly.
    /// </remarks>
    private static string Breadth(IReadOnlyList<string> subjects)
    {
        if (subjects.Count == 0) return "";

        return "\n--- corners of their life you have already been in ---\n" +
               string.Join(", ", subjects) + "\n" +
               "GO SOMEWHERE ELSE. Picking one of those again is a follow-up, not a new thing, however different " +
               "the finding turns out to be — and an assistant with one interest is one they stop reading.\n" +
               // Pointing at the context rather than describing categories. It declined a whole deep look having
               // reasoned only about the subject it had just covered, with a vacuum it could have asked about
               // sitting four lines above in the same prompt — "their life is wider than this" is true and did not
               // tell it where to look.
               "EVERYTHING ELSE IN FRONT OF YOU IS FAIR GAME, and it is all listed above: the devices and services " +
               "you can operate, their other lists and projects, where they are, the days ahead. Work down that " +
               "list rather than stopping at the first thing you recognise. \"The one subject I know about is " +
               "covered\" is not the same answer as \"there is nothing worth doing\".\n" +
               "Only return to one of the covered subjects if it is genuinely time-critical now.\n";
    }

    /// <summary>
    /// Anything the caller supplies, called without letting it take the loop down.
    /// </summary>
    /// <remarks>
    /// These delegates read half the stores in the process, and a loop that dies because one list was mid-write is a
    /// loop that stops for good.
    /// </remarks>
    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }

    private static string Snip(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}
