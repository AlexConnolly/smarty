using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Smarty.Agents;

namespace Smarty.Brain;

/// <summary>
/// The whole brain, in three verbs: tell it something, take something back, ask it something.
/// </summary>
/// <remarks>
/// <para>
/// Everything above this line is free text, and that is the point. The caller does not name a subject, choose a kind,
/// pick a label or decide whether something is a new node — it says "we're going to Majorca in September with my wife"
/// and this works out that there is a trip, that there is a wife, that neither is on file, and that both need creating
/// and joining up. A tool surface that asked for those decisions would be asking the wrong participant: the model
/// writing the sentence knows what it means, and only the graph knows what already exists.
/// </para>
/// <para>
/// So resolution happens HERE, where both halves are available — the sentence, and everything currently on file. That is
/// the reconciliation job. Find the subject, find the things it connects to, create what is missing, and never guess
/// when two things answer to the same name.
/// </para>
/// <para>
/// Telling it something is instant and costs nothing: the sentence is filed and the work happens afterwards, because a
/// model call in the middle of a reply is a model call the person is sitting and waiting for. The queue is drained in
/// the background AND before any question is answered, so the delay can never be observed — say it, ask about it, it is
/// there.
/// </para>
/// </remarks>
public sealed class Memory
{
    /// <summary>How many times a sentence is reconsidered before it is given up on.</summary>
    /// <remarks>
    /// It has to be finite. One sentence the model cannot read would otherwise be retried forever, and — worse — sit at
    /// the head of the queue delaying everything said after it.
    /// </remarks>
    public const int MaxTries = 3;

    /// <summary>How many nodes are shown to the model when it reconciles or searches.</summary>
    /// <remarks>
    /// Everything, in any brain this is built for. A personal graph is hundreds of nodes, and hundreds of names is a
    /// couple of thousand tokens — cheap enough that no retrieval step is needed, which means no retrieval step can
    /// LOSE something. Retrieval before resolution is how you get a second Emma: the shortlist misses the first one, so
    /// the model correctly concludes she is new. Past this many the most connected win, and that is the point at which
    /// this design needs revisiting rather than tuning.
    /// </remarks>
    public const int Candidates = 400;

    private readonly Graph _graph;
    private readonly IModelProvider _model;
    private readonly string _modelName;
    private readonly string? _dir;
    private readonly JsonSerializerOptions _json;
    private readonly List<Pending> _pending = new();
    private readonly SemaphoreSlim _draining = new(1, 1);
    private readonly object _lock = new();
    private int _nextPending;

    public Memory(Graph graph, IModelProvider model, string modelName, string? dir = null,
        JsonSerializerOptions? json = null)
    {
        _graph = graph;
        _model = model;
        _modelName = modelName;
        _dir = dir;
        _json = json ?? new JsonSerializerOptions { WriteIndented = true };
        Load();

        // Who this belongs to is READ BACK from the graph, not waited on from the host.
        //
        // It was set only by the host at startup, from configuration — so saying it once through the app worked until the
        // next restart, when it was forgotten and the setup page appeared again over a brain that already knew. The graph
        // is the thing that persists and the thing that needs the answer; there is no reason for a second copy to exist
        // somewhere else and disagree.
        Self = _graph.Resolve("me").Node;
    }

    /// <summary>
    /// The graph underneath.
    /// </summary>
    /// <remarks>
    /// Exposed because plenty of reads need no judgement at all. "What is recorded about this project" already knows its
    /// subject, so putting a model in front of it would be paying for a decision nobody has to make.
    /// </remarks>
    public Graph Graph => _graph;

    /// <summary>
    /// What the nodes actually contain. Loaded only for what a question settled on.
    /// </summary>
    /// <remarks>
    /// Set by the host, and deliberately not required: a brain with nowhere to put files still records everything else. The
    /// graph never touches it — the order is walk, decide, then load, which is what keeps a hundred stored documents free
    /// to a question about none of them.
    /// </remarks>
    public Contexts? Contexts { get; set; }

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Somewhere to send a line about what happened, for the log.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Whether a reader in one audience may see a fact recorded in another. Given (the fact's audience, the reader's).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host's rule, because the host owns the notion of an audience. Left unset, everything is visible to everyone —
    /// correct for a graph serving one person, and a leak for one serving a workspace, which is why the host that serves
    /// a workspace has to say what its rule is rather than inherit a permissive default silently.
    /// </para>
    /// <para>
    /// Applied to every read a question makes, not to the answer at the end: a fact nobody may see must not be walked
    /// through either, or it leaks by being the reason a visible thing turned up.
    /// </para>
    /// </remarks>
    public Func<string?, string?, bool>? Sees { get; set; }

    /// <summary>
    /// Is that name one of the assistant's OWN things, rather than something in the world?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The map holds somebody's world. The assistant is not in it, and neither is anything the assistant made for them:
    /// a panel on their home page, a shape in its own library, the home page itself. Every one of those turned up as a
    /// thing joined to them — "Alex has Home Page", "Alex published Ebay Latest Sale" — and each is true, useless, and
    /// permanent.
    /// </para>
    /// <para>
    /// The host answers this because the host is what owns those things and knows their names; the graph has no way to
    /// tell a restaurant from a panel about one. Left unset nothing is excluded, which is right for a graph with no
    /// application around it.
    /// </para>
    /// <para>
    /// Only ever consulted when a node would be CREATED. Anything already on file stays — the host puts its projects and
    /// topics in here deliberately, and this must not start eating them.
    /// </para>
    /// </remarks>
    public Func<string, bool>? Ours { get; set; }

    /// <summary>
    /// The person whose memory this is.
    /// </summary>
    /// <remarks>
    /// Without this, "I'm going with my wife" has nothing to hang off — there is no "I" in the graph, so the wife can be
    /// created and then joined to nothing, and the sentence that made her worth recording is lost. Which is exactly what
    /// happened on the first empty graph: the brief said "me" is on file above, nobody was, and the model recorded
    /// nothing at all rather than guess. It was right to.
    /// <para>Set by the host, which is the only thing that knows whose machine this is.</para>
    /// </remarks>
    public Node? Self { get; private set; }

    /// <summary>
    /// Say who this memory belongs to, creating them or correcting the name already on file.
    /// </summary>
    /// <remarks>
    /// Renames rather than adds when somebody already answers to "me", because this can be called again the moment a real
    /// name is known — and creating a second person then would leave half the memory hanging off a placeholder and half
    /// off the real name, with no way to tell from the outside which was which.
    /// </remarks>
    public Node WhoAmI(string name, IEnumerable<string>? aliases = null)
    {
        // Everything anybody says for themselves, so the model never has to pick between "me" and a name.
        var known = new List<string> { "me", "i", "my", "myself", "self", "us", "we", "the user" };
        if (aliases is not null) known.AddRange(aliases);

        if (_graph.Resolve("me").Node is { } already)
        {
            _graph.Rename(already.Id, name);
            _graph.AddAliases(already.Id, known);
            return Self = _graph.Get(already.Id)!;
        }

        return Self = _graph.Upsert(name, Kinds.Person, known);
    }

    /// <summary>What has been said but not yet reconciled.</summary>
    public IReadOnlyList<Pending> Waiting
    {
        get { lock (_lock) return _pending.ToList(); }
    }

    /// <summary>
    /// Throw away everything waiting to be read.
    /// </summary>
    /// <remarks>
    /// Needed by a wipe and by nothing else. Left behind, the queue would be read against an empty graph a minute later
    /// and quietly rebuild a handful of the things that were just deleted — which reads as a wipe that did not work.
    /// </remarks>
    public void Clear()
    {
        lock (_lock)
        {
            _pending.Clear();
            _nextPending = 0;
            Save();
        }

        Self = null;
    }

    // ---- telling it ----

    /// <summary>File something you have been told. Instant: no model call, no graph change yet.</summary>
    public Pending Note(string text, string? source = null, string? audience = null)
        => File(text, retract: false, source, audience);

    /// <summary>
    /// File what somebody just said, without anybody having decided it was worth filing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Because deciding was the weakest link. Whether a stated fact reached the memory at all depended on a model choosing
    /// to call a tool mid-reply, and it chose inconsistently: one sentence about an evening out was filed, an identically
    /// shaped sentence about an appointment was answered warmly and filed nowhere. From the outside those are the same
    /// conversation, and the second one is a memory that silently did not happen.
    /// </para>
    /// <para>
    /// So the choice is removed. Everything the person says goes on the queue, and the reconciler — the one place that can
    /// see both the sentence and the whole graph — decides whether it states anything. It already answers "nothing here"
    /// for a greeting or a question, which is the behaviour this leans on rather than a new one.
    /// </para>
    /// <para>
    /// Marked as overheard, which buys one thing: no second reading when the plan comes back empty. That retry exists for
    /// sentences somebody deliberately filed, where emptiness is more likely a misread than the truth — and every "thanks"
    /// would otherwise cost two model calls to conclude what it concluded the first time.
    /// </para>
    /// </remarks>
    public Pending Overhear(string text, string? source = null, string? audience = null)
    {
        var pending = File(text, retract: false, source, audience);
        pending.Overheard = true;
        Save();
        return pending;
    }

    /// <summary>
    /// Take something off the queue before it is read, because it turned out not to be worth reading.
    /// </summary>
    /// <remarks>
    /// The counterpart to overhearing everything. Filing every sentence is only safe if there is a way to take one back
    /// the moment the host learns something the words could not tell it — that the turn was a request rather than a
    /// statement, say. Silent about an id that has already been drained: by then it is a matter for the graph, not the
    /// queue, and pretending otherwise would be the caller's problem rather than a fact about what happened.
    /// </remarks>
    public bool Discard(string pendingId)
    {
        if (pendingId is not { Length: > 0 }) return false;

        lock (_lock)
        {
            var gone = _pending.RemoveAll(p => p.Id == pendingId) > 0;
            if (gone) Save();
            return gone;
        }
    }

    /// <summary>
    /// Say the same thing better, replacing what was overheard rather than filing it twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Because both paths now fire on one sentence: everything said is overheard, and the model may ALSO file its own
    /// reading of it, usually a better one — "Thursday at 9.30am" comes back as a resolved date. Filing both means
    /// reconciling the same fact twice, and the second reading is free to choose a different label for it. Once that
    /// happens there are two edges saying one thing, which is the duplication this whole design exists to avoid, and it
    /// only did not happen the first time by luck.
    /// </para>
    /// <para>
    /// So the deliberate version replaces the overheard one, by id, while it is still waiting. Nothing is compared —
    /// matching two wordings of the same sentence is exactly the guesswork worth not doing. If the queue has already been
    /// drained then the overheard reading is on file and this is a genuine second statement, which is filed normally.
    /// </para>
    /// </remarks>
    public Pending Reword(string? overheardId, string text, string? source = null, string? audience = null)
    {
        if (overheardId is { Length: > 0 })
            lock (_lock)
            {
                var waiting = _pending.FirstOrDefault(p => p.Id == overheardId && p.Overheard);
                if (waiting is not null)
                {
                    waiting.Text = (text ?? "").Trim().Length == 0 ? waiting.Text : text!.Trim();
                    waiting.Overheard = false;
                    Save();
                    return waiting;
                }
            }

        return File(text, retract: false, source, audience);
    }

    /// <summary>
    /// File something you have been told, with a document that belongs to it.
    /// </summary>
    /// <remarks>
    /// The file waits on the queue with the sentence, because which THING it belongs to is not knowable until the sentence
    /// has been read against the graph — "here's my flight ticket" has to become a ticket joined to a person before there
    /// is anywhere to put the ticket.
    /// </remarks>
    public Pending Keep(string text, string path, string? name = null, string? source = null, string? audience = null)
    {
        var pending = File(text, retract: false, source, audience);

        // Read NOW, so the sentence and the document are settled together. "Here's my flight ticket" on its own yields a
        // thing called a flight ticket and nothing else — while the ticket itself names the airports, the date and the
        // flight, every one of which is a fact worth having. The document is the better half of what was just said.
        var pulled = FileText.Extract(path);

        lock (_lock)
        {
            pending.FilePath = path;
            pending.FileName = name ?? Path.GetFileName(path);
            pending.FileText = pulled.Ok && pulled.Text.Trim().Length > 0
                ? Shorten(pulled.Text.Trim(), Contexts.Excerpt)
                : null;
            Save();
        }

        Trace?.Invoke($"[brain] filed {pending.FileName} with {Quote(text)}" +
                      (pending.FileText is null ? " (nothing readable in it)" : $" ({pending.FileText.Length} chars read)"));
        return pending;
    }

    private static string Shorten(string text, int most) =>
        text.Length <= most ? text : text[..most] + $"\n… ({text.Length - most:N0} more characters)";

    /// <summary>File something that is no longer true. Reconciled into an ending, with its reason kept.</summary>
    public Pending Forget(string text, string? source = null, string? audience = null)
        => File(text, retract: true, source, audience);

    private Pending File(string text, bool retract, string? source, string? audience)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Nothing to record.", nameof(text));

        lock (_lock)
        {
            var pending = new Pending
            {
                Id = $"p{++_nextPending}",
                Text = trimmed,
                Retract = retract,
                Source = source,
                // Carried on the QUEUE, not read at reconciliation time. A sentence said in one conversation may be
                // worked through minutes later while somebody else is talking, and stamping it with whoever happens to
                // be around then would hand a private fact to the wrong room.
                Audience = audience,
                Filed = Now(),
            };

            _pending.Add(pending);
            Save();
            return pending;
        }
    }

    // ---- asking it ----

    /// <summary>
    /// Answer a question from the graph.
    /// </summary>
    /// <remarks>
    /// Two stages, because one is not enough. "What friends are coming on the Majorca trip" has no row anywhere: the
    /// trip has to be found first, loosely, and only then can the people on it be walked to. A single pass over the text
    /// would be looking for the word "friend" in a graph that says "wife" and "old friend from uni" and "guest".
    /// </remarks>
    public async Task<string> RecallAsync(string question, string? audience = null, CancellationToken ct = default)
    {
        if (_graph.Nodes.Count == 0) return "I have nothing on file yet — nothing has been recorded.";

        // Never answer over a stale graph. Whatever was said a moment ago has to land before the question is read.
        await ReconcileAsync(ct).ConfigureAwait(false);

        var plan = await AskModelAsync(Recall.Brief(question, Describe(), Whom(), Vocabulary()), Recall.Schema, ct).ConfigureAwait(false);
        if (plan is null) return "I couldn't work out what to look up.";

        var about = plan["about"]?.GetValue<string>() ?? "";
        if (about.Trim().Length == 0) return "I have nothing on file about that.";

        var ask = new Ask
        {
            About = about,
            Visible = Allowed(audience),
            Wanting = Wanted(plan["wanting"]?.GetValue<string>()),
            With = plan["with"]?.GetValue<string>(),
            Hops = Whole(plan["hops"]) ?? 2,
        };

        foreach (var anchor in Strings(plan["anchors"])) ask.Anchors.Add(anchor);
        foreach (var out_ in Strings(plan["exclude"])) ask.Exclude.Add(Without.Node(out_));

        var answer = _graph.Ask(ask);

        // A kind filter that finds nothing is a filter that was wrong, not an answer. Asked who somebody competes with
        // when the graph records a club rather than its members, the planner filters for people and returns none — while
        // the answer sits one edge away under a different kind. The code can see that happen; the model cannot, so it is
        // decided here rather than asked for.
        if (answer.Found.Count == 0 && ask.Wanting is { Length: > 0 })
        {
            ask.Wanting = null;
            answer = _graph.Ask(ask);
        }

        // Whether to bring the detail is decided here too, on the size of what came back, rather than asked of the model —
        // which got it wrong in the one direction that matters, returning the thing and leaving behind the fact about it
        // that was the whole question. The second walk costs nothing: same graph, same process, no model involved.
        if (answer.Found.Count <= Recall.DetailUpTo)
        {
            ask.WithProperties = true;
            answer = _graph.Ask(ask);
        }
        Trace?.Invoke($"[brain] \"{question}\" → about {Quote(about)}, wanting {ask.Wanting ?? "anything"}" +
                      (ask.With is { Length: > 0 } both ? $", shared with {Quote(both)}" : "") + ", " +
                      $"{answer.Found.Count} found");

        // The second phase. Nothing is loaded until the walk has settled which nodes matter, and then only for those —
        // and only when the answer is narrow enough that reading a document into it is still an answer.
        return Recall.Say(question, about, answer, Self?.Id,
            Contexts is null || answer.Found.Count > Recall.DetailUpTo
                ? null
                : id => Contexts.For(id).Select(c => (c, Contexts.TextOf(c))).ToList());
    }

    /// <summary>
    /// The line that goes in every prompt: WHAT it knows about, not what it knows.
    /// </summary>
    /// <remarks>
    /// Names and kinds only, and it exists to solve a problem no amount of instruction solves: a model cannot decide to
    /// look something up if it has no idea the thing exists. Told there is a Majorca trip on file, it asks about the
    /// Majorca trip. Told nothing, it either invents an answer or says it doesn't know — and both are wrong when the
    /// answer was sitting right there.
    /// <para>The detail deliberately stays out. Guessing which facts matter before the model has said what it wants is
    /// the retrieval-shaped mistake this replaces.</para>
    /// </remarks>
    /// <summary>
    /// How much is in here, by sort, and not one word of what it says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what goes into a conversation, and <see cref="Index"/> is not. The index was, and it was a mistake: a node's
    /// NAME is frequently the sensitive part rather than a pointer to it — an account handle, a person, a place somebody
    /// lives. Putting the list in front of every turn meant the most identifying material in the memory was in the context
    /// of every request, whether or not anything needed it, and it let an answer be given straight off the list without
    /// looking anything up. Told it was a table of contents, it read a username off it and answered.
    /// </para>
    /// <para>
    /// A count cannot leak and cannot be answered from, and it still does the job the index was there for: something that
    /// knows a memory exists and how big it is has a reason to search it. The names are still seen — by the lookup itself,
    /// inside the memory, where they are needed and where they do not travel.
    /// </para>
    /// </remarks>
    public string Shape(string? audience = null)
    {
        var allowed = Allowed(audience);

        var counts = _graph.Nodes
            .Where(n => allowed is null
                        || n.Id == Self?.Id
                        || _graph.Around(n.Id, visible: allowed).Count > 0
                        || _graph.Properties(n.Id, allowed).Count > 0)
            .Where(n => n.Id != Self?.Id)
            .GroupBy(n => n.Kind, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}")
            .ToList();

        return counts.Count == 0 ? "" : string.Join(", ", counts);
    }

    public string Index(int most = 24, string? audience = null)
    {
        var allowed = Allowed(audience);

        // A node with nothing visible about it is not named. Scoping the facts and then listing every name would leak
        // the most sensitive part of a private remark — that the person or thing was mentioned at all.
        var names = Ranked()
            .Where(n => allowed is null
                        || n.Id == Self?.Id
                        || _graph.Around(n.Id, visible: allowed).Count > 0
                        || _graph.Properties(n.Id, allowed).Count > 0)
            .Take(Math.Max(1, most))
            .Select(n => n.Name)
            .ToList();

        return names.Count == 0 ? "" : string.Join(", ", names);
    }

    // ---- the reconciliation job ----

    /// <summary>
    /// Work through everything waiting: read each sentence against what is on file, then write the graph.
    /// </summary>
    public async Task<Reconciled> ReconcileAsync(CancellationToken ct = default)
    {
        var settled = new List<Settled>();
        var dropped = new List<Pending>();

        // One drainer at a time. The background loop and a question arriving would otherwise reconcile the same sentence
        // twice, and "going on holiday with Emma" applied twice is survivable only by luck.
        await _draining.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Pending? next;
                lock (_lock) next = _pending.FirstOrDefault();
                if (next is null) break;

                ct.ThrowIfCancellationRequested();

                var outcome = await SettleAsync(next, ct).ConfigureAwait(false);
                if (outcome is not null)
                {
                    settled.Add(outcome);
                    Remove(next);
                    continue;
                }

                // Unreadable. Count the attempt, and give up rather than block everything filed behind it.
                next.Tried++;
                if (next.Tried >= MaxTries)
                {
                    next.Error ??= "couldn't be read";
                    Trace?.Invoke($"[brain] giving up on {Quote(next.Text)} after {next.Tried} tries");
                    dropped.Add(next);
                    Remove(next);
                }
                else
                {
                    Save();
                    break;
                }
            }
        }
        finally
        {
            _draining.Release();
        }

        return new Reconciled { Settled = settled, Dropped = dropped };
    }

    private async Task<Settled?> SettleAsync(Pending pending, CancellationToken ct)
    {
        var outcome = await ReadAsync(pending, ct).ConfigureAwait(false);

        // The graph refused something, so ask again WITH the refusal.
        //
        // Telling the writer the rule up front did not work — shown that a link to a person may not be "has", it wrote
        // "has" anyway, and a sister became a question instead of a fact. Which is the whole argument against fixing
        // behaviour by explaining: what changes an answer is being handed back the specific thing that was wrong with
        // the specific thing you wrote. The machinery for one more reading already exists for an empty plan; this is the
        // same call with the refusals appended, and the sentence still in front of it.
        if (outcome is { Refused.Count: > 0 } && !pending.Retried)
        {
            pending.Retried = true;
            Trace?.Invoke($"[brain] {Quote(pending.Text)} → {outcome.Refused.Count} refused; asking again with why");

            var again = await ReadAsync(pending, ct, outcome.Refused, outcome.Wrote).ConfigureAwait(false);
            if (again is { Wrote.Count: > 0 }) outcome = again;
        }

        // There is no second attempt at an empty plan, deliberately.
        //
        // There used to be: emptiness looked like an unlucky reading, so it was asked again. Then the retry had to be
        // told something DIFFERENT to work — and that is the tell. A retry that changes the question is not a retry, it
        // is an admission that the first prompt was wrong, and it hides the defect: every sentence the brief handles
        // badly quietly gets a second bite and the brief never gets fixed. Two real facts were lost that way while both
        // readings returned nothing. What the second attempt used to say now lives in the one brief, for every sentence.
        //
        // The refusal retry above is a different animal and stays: it fires only when the GRAPH rejected something
        // specific, and it hands that back rather than re-asking the same question and hoping.

        return outcome;
    }

    private async Task<Settled?> ReadAsync(Pending pending, CancellationToken ct,
        IReadOnlyList<string>? refused = null, IReadOnlyList<string>? already = null)
    {
        var plan = await AskModelAsync(
            Reconcile.Brief(pending.Text, pending.Retract, Describe(), Whom(), Vocabulary(), Wording(), Now(),
                pending.FileName ?? (pending.FilePath is { Length: > 0 } p ? Path.GetFileName(p) : null),
                says: pending.FileText, refused: refused, already: already),
            pending.Retract ? Reconcile.RetractSchema : Reconcile.Schema,
            ct).ConfigureAwait(false);

        if (plan is null)
        {
            pending.Error = "the model didn't answer with a usable plan";
            return null;
        }

        var wrote = new List<string>();
        var questions = new List<string>();
        var turned = new List<string>();
        var before = _graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filed = Now();

        // The things first, so a name declared with its other names is on file before any write reaches for it.
        foreach (var thing in Array(plan["things"]))
            Raise(thing, wrote, questions);

        foreach (var write in Array(plan["writes"]))
            Apply(write, plan, pending, before, wrote, questions, turned);

        foreach (var take in Array(plan["retract"]))
            Undo(take, pending.Audience, wrote, questions);

        if (pending.FilePath is { Length: > 0 }) Attach(pending, plan, wrote, questions);
        NothingJoinedToAnything(before);

        // A sentence that settles into nothing is either genuinely empty ("thanks") or a misread, and from the outside
        // those look identical — so when nothing lands, what came back gets logged. Being unable to tell the two apart
        // is how a memory quietly stops working while every call reports success.
        if (wrote.Count == 0 && questions.Count == 0)
            Trace?.Invoke($"[brain] {Quote(pending.Text)} → nothing to record; the plan was {plan.ToJsonString()}");
        else
            // What it wrote, not just how many. "4 written" and a graph with nothing in it is a fault nobody can even
            // begin to diagnose — the facts are one line each and the count on its own hid exactly that.
            Trace?.Invoke($"[brain] {Quote(pending.Text)} → {wrote.Count} written" +
                          (questions.Count > 0 ? $", {questions.Count} to ask about" : "") +
                          (wrote.Count > 0 ? ": " + string.Join(" / ", wrote) : "") +
                          (questions.Count > 0 ? " — asking: " + string.Join(" / ", questions) : ""));

        return new Settled { Text = pending.Text, Wrote = wrote, Questions = questions, Refused = turned };
    }

    /// <summary>
    /// Take away anything this reading created that ended up joined to nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thing with no connections is not a memory: nothing can reach it, no question can find it, and it still appears
    /// on the list handed to every future write, where it invites more of the same.
    /// </para>
    /// <para>
    /// They appear as soon as anything else is dropped. A statement whose only fact ends with the job — a tracker
    /// somebody asked for — is refused as a fact, and without this the tracker itself would stay behind as a thing,
    /// which is the whole problem wearing a different hat. Scoped to what this reading made, so a node left bare by an
    /// earlier retraction is left alone.
    /// </para>
    /// </remarks>
    private void NothingJoinedToAnything(IReadOnlySet<string> before)
    {
        foreach (var node in _graph.Nodes.Where(n => !before.Contains(n.Id)).ToList())
        {
            if (_graph.Edges.Any(e => e.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                                   || (e.To ?? "").Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (_graph.Forget(node.Id))
                Trace?.Invoke($"[brain] \"{node.Name}\" is joined to nothing, so it is nothing — taken back out");
        }
    }

    /// <summary>
    /// Bring a thing into being with the shape its kind requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the writes because a shaped thing exists in its own right: an appointment is on the calendar whether
    /// or not the sentence also said who is going. Everything else about it — who, where, what it is for — arrives as
    /// ordinary edges pointing at it by name.
    /// </para>
    /// <para>
    /// A kind that requires a field and does not get one becomes a QUESTION rather than a refusal. Refusing loses the
    /// whole thing over a missing detail, which is the trade that costs most: an appointment with no time is still worth
    /// having on file if somebody can be asked what time it is.
    /// </para>
    /// </remarks>
    private void Raise(JsonNode? thing, List<string> wrote, List<string> questions)
    {
        var name = (thing?["name"]?.GetValue<string>() ?? "").Trim();
        if (name.Length == 0) return;

        var kind = Kinds.Clean(thing?["kind"]?.GetValue<string>());

        if (Landing(name, kind, questions) is not { } node) return;

        // Other names it answers to. The cheapest identity mechanism there is and the one nothing could state before:
        // "my brother Matthew — I call him Matt" used to record Matthew, and then invent a second person the first time
        // anybody said Matt. One list, and every later mention lands in the right place.
        if (Names(thing?["also"]) is { Count: > 0 } also
            && _graph.AddAliases(node.Id, also) is { Count: > 0 } added)
            // Only what was actually new. Reporting the whole list meant every reading claimed to have recorded the
            // nine names Alex already answers to, which buries whatever the sentence really said.
            wrote.Add($"{node.Name} — also known as — {string.Join(", ", added)}");

        // A description, replaced by the name. Rename keeps the old one as an alias, so this adds a better title
        // and takes away no way of finding her.
        if ((thing?["called"]?.GetValue<string>() ?? "").Trim() is { Length: > 0 } called
            && !string.Equals(called, node.Name, StringComparison.OrdinalIgnoreCase))
        {
            var was = node.Name;
            if (_graph.Rename(node.Id, called)) wrote.Add($"{was} — is called — {called}");
        }


    }

    private void Apply(JsonNode? write, JsonObject plan, Pending pending, IReadOnlySet<string> before,
        List<string> wrote, List<string> questions, List<string> turned)
    {
        var audience = pending.Audience;

        var label = (write?["label"]?.GetValue<string>() ?? "").Trim();
        var fromName = (write?["from"]?.GetValue<string>() ?? "").Trim();
        if (label.Length == 0 || fromName.Length == 0) return;

        // Dropped BEFORE anything is resolved, so a fact that ends with the work never even creates the thing it was
        // about. Checked first for exactly that reason: "a tracker for my latest sale" used to arrive as a node, and the
        // node outlived the request that invented it.
        //
        // Not a question and not a refusal — nothing has gone wrong. The writer answered correctly and the answer was
        // that this is work, not world.
        var lasts = write?["lasts"]?.GetValue<string>();
        if (!Lifetimes.Keep(lasts))
        {
            Trace?.Invoke($"[brain] not a memory, it ends with the job: \"{fromName} — {label} — " +
                          $"{(write?["to"]?.GetValue<string>() ?? write?["value"]?.GetValue<string>())}\"");
            return;
        }

        var to = (write?["to"]?.GetValue<string>() ?? "").Trim();
        var value = (write?["value"]?.GetValue<string>() ?? "").Trim();
        var note = write?["note"]?.GetValue<string>();

        // Neither an other end nor a value is not a fact, it is half a sentence.
        if (to.Length == 0 && value.Length == 0) return;

        var from = Landing(fromName, write?["fromKind"]?.GetValue<string>(), questions);
        if (from is null) return;

        // Both ends get the same check, and the near one is where it was first missed: "Matt has just bought a camper
        // van" puts the name in doubt at the FROM end, so checking only the far end let it through.
        if (WhoWasMeant(from, write?["fromKind"]?.GetValue<string>(), pending, before, questions, turned,
                mayRefuse: false) is { } meant) from = meant;

        if (to.Length > 0)
        {
            // A time, an amount or a description written as a thing rather than as a value — the one write that keeps
            // coming back wrong, and no amount of asking has fixed it.
            //
            // So it stops being a decision. If the far end does not exist yet, nothing else in this statement mentions
            // it, and it was called a topic, then it is a value: nothing in the graph could ever reach it except the one
            // edge about to be made, which is the definition of a value rather than a thing. Nothing is lost either way,
            // because the graph promotes a value the moment it earns nodehood: create a node with that name later and
            // every text edge holding it becomes a real connection.
            if (LooksLikeAValue(to, write?["toKind"]?.GetValue<string>(), plan))
            {
                _graph.State(from.Id, label, to, note, audience: audience);
                wrote.Add(Reads(from.Name, label, to));
                return;
            }

            var target = Landing(to, write?["toKind"]?.GetValue<string>(), questions);
            if (target is null) return;

            if (WhoWasMeant(target, write?["toKind"]?.GetValue<string>(), pending, before, questions, turned)
                is not { } meantThere) return;
            target = meantThere;

            // Both ends the same thing. It reads as a fact and is not one, and the graph refuses it — caught here so one
            // bad line in a plan does not throw away everything else the sentence said.
            if (target.Id.Equals(from.Id, StringComparison.OrdinalIgnoreCase))
            {
                Trace?.Invoke($"[brain] skipped \"{from.Name} — {label} — itself\"");
                return;
            }

            // The graph refuses an edge that says nothing — the same label back the other way, or an empty verb pointed
            // at a person. Caught here for the same reason the self-link is caught above: one refused line must not take
            // the rest of the sentence with it. The first version of this guard threw, the plan died mid-way, and a
            // sister arrived as a node with nothing joining her to anybody — which is worse than the bad label was.
            //
            // Recorded as a question rather than swallowed, because the sentence DID state a relationship and the writer
            // dropped it. Somebody should be asked which one it was.
            try
            {
                _graph.Link(from.Id, label, target.Id, note, audience: audience);
            }
            catch (ArgumentException refused)
            {
                turned.Add($"\"{from.Name} — {label} → {target.Name}\" was refused: {refused.Message}");
                questions.Add($"How are {from.Name} and {target.Name} related? " +
                              $"I was told \"{label}\", which doesn't say.");
                Trace?.Invoke($"[brain] refused \"{from.Name} — {label} — {target.Name}\": {refused.Message}");
                return;
            }

            wrote.Add(Reads(from.Name, label, target.Name));
            return;
        }

        _graph.State(from.Id, label, value, note, audience: audience);
        wrote.Add(Reads(from.Name, label, value));
    }

    /// <summary>
    /// Put the document that came with a sentence onto the thing it belongs to.
    /// </summary>
    /// <remarks>
    /// The plan says which thing, because only the reading of the sentence knows. Falling back to the subject of the first
    /// write when it does not say: a file kept against roughly the right thing is findable, and one kept nowhere is a
    /// document the person handed over and can never get back.
    /// </remarks>
    private void Attach(Pending pending, JsonObject plan, List<string> wrote, List<string> questions)
    {
        if (Contexts is null)
        {
            questions.Add($"I was given \"{pending.FileName ?? pending.FilePath}\" but have nowhere to keep files.");
            return;
        }

        var named = plan["attachTo"]?.GetValue<string>()
                    ?? Array(plan["writes"]).FirstOrDefault()?["from"]?.GetValue<string>();

        if (named is not { Length: > 0 } || Existing(named, questions) is not { } node)
        {
            questions.Add($"Where should \"{pending.FileName ?? "that file"}\" be kept? I couldn't tell what it belongs to.");
            return;
        }

        // The text is handed over rather than pulled again — it was already read to build the plan.
        var held = Contexts.Keep(node.Id, pending.FilePath!, pending.FileName, pending.Source, pending.Audience,
            pending.FileText);
        wrote.Add(held is null
            ? $"couldn't keep the file against {node.Name}"
            : $"{node.Name} — keeps — {held.Name}{(held.Readable ? " (readable)" : "")}");
    }

    /// <summary>
    /// Which thing the sentence actually meant, when the plan reached for something already on file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two live failures, one method. "My brother is Matthew, he lives in Leeds" was recorded as Matthew living in
    /// BRISTOL, because Bristol was on file and the name resolved perfectly — a confident lie about where somebody
    /// lives, with nothing anywhere reporting a fault. And "Matt has just bought a camper van" was written straight
    /// against Matthew, which may well be right and is nobody's guess to make silently.
    /// </para>
    /// <para>
    /// They are the same mistake with different endings. A name the thing it reached for could be a longer form of is a
    /// QUESTION: the fact lands on what was said, as provisional, and somebody settles it once. Anything else is a
    /// substitution, and a substituted fact is about somebody else's life — refused, and asked about.
    /// </para>
    /// <para>Returns the node to use, or null to drop this one write.</para>
    /// </remarks>
    /// <param name="mayRefuse">
    /// Whether a substitution here is worth dropping the write over. True at the FAR end, where the sentence is saying
    /// something about a thing and naming the wrong one is a lie. False at the near end, because the near end is usually
    /// whatever the conversation was already about — "I'm going with my wife Emma" states a fact about a trip named two
    /// messages ago, and refusing that would refuse the reconciler's whole job.
    /// </param>
    private Node? WhoWasMeant(Node target, string? statedKind, Pending pending, IReadOnlySet<string> before,
        List<string> questions, List<string> turned, bool mayRefuse = true)
    {
        if (Elsewhere(target, pending, before) is not { } wrong) return target;

        if (wrong.Spoken is { Length: > 0 } spoken && Graph.CouldBeSame(spoken, target.Name))
        {
            var itsKind = Kinds.Clean(statedKind ?? target.Kind);
            var born = _graph.Upsert(Readable(spoken, itsKind), itsKind, provisional: true);
            if (born.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)) return target;

            if (_graph.Might(born.Id, target.Id))
                questions.Add($"Is {born.Name} the same {itsKind} as {target.Name}, or someone else?");

            Trace?.Invoke($"[brain] the sentence said \"{spoken}\" and the plan said \"{target.Name}\" — recorded " +
                          "against what was said, and asked about");
            return born;
        }

        // A statement that names NOBODY, in somebody's own memory, is about them. Not a guess: there is exactly one
        // person it can be about. "I love the beach!" reached the memory as "Loves the beach." with its subject
        // stripped, and the reading attached it to Matthew — the last person anybody had mentioned — which is a fact
        // about the wrong person's life and reads perfectly plausibly for ever.
        //
        // Only for a PERSON at the near end, and only when no other person the memory knows is named. A project, place
        // or thing carried in from earlier in the conversation is ordinary — "I'm going with my wife Emma" states a fact
        // about a trip named two messages ago — and rewriting that to the owner would be the same mistake mirrored.
        if (Kinds.NamesMayRepeat(target.Kind) && Self is { } owner
            && !owner.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)
            && !AnybodyNamed(pending.Text))
        {
            Trace?.Invoke($"[brain] {Quote(pending.Text)} names nobody, so it is about {owner.Name} — not {target.Name}");
            return owner;
        }

        if (!mayRefuse) return target;

        turned.Add(wrong.Why);
        questions.Add($"\"{pending.Text}\" doesn't mention {target.Name}. What was meant there?");
        Trace?.Invoke($"[brain] refused a write to \"{target.Name}\": {wrong.Why}");
        return null;
    }

    /// <summary>Does the statement name a person this memory already knows?</summary>
    private bool AnybodyNamed(string? text)
    {
        var said = text ?? "";
        if (said.Length == 0) return false;

        return _graph.Nodes
            .Where(n => Kinds.NamesMayRepeat(n.Kind))
            .SelectMany(n => n.AllNames())
            .Any(name => name.Length > 2 && said.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A node the statement never mentions, when the plan reached for it by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer is handed everything already on file so that it can REUSE it, and the same list is a menu of confident
    /// wrong answers: shown Bristol, a plan about Leeds can quietly say Bristol and every check passes, because Bristol
    /// is a real place that a real person really lives in.
    /// </para>
    /// <para>
    /// So a name at the far end has to appear in what was actually said — as itself, or as one of the names that thing
    /// answers to. Two exemptions, both necessary: an ID is the writer doing exactly what it was told (ids are slugs and
    /// nobody says them out loud), and the person whose memory this is arrives as "me" or "I". A document's text counts
    /// as said, because that is where its facts are.
    /// </para>
    /// <para>
    /// Returns why it is wrong and what the sentence said instead, or null when it is fine. The caller decides which of
    /// those two it is: a shorter form of the same name is a question, and anything else is a refusal.
    /// </para>
    /// </remarks>
    private (string Why, string? Spoken)? Elsewhere(Node target, Pending pending, IReadOnlySet<string> before)
    {
        // Anything this reading itself created. Coining a name for what was described — "Fonda booking" from "a table at
        // Fonda on Friday" — is the reconciler working, and the coined name is not in the sentence by definition. The
        // claim worth checking is the other one: that this is a thing ALREADY on file.
        if (!before.Contains(target.Id)) return null;

        // Themselves, however they were referred to.
        if (Self is { } owner && owner.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)) return null;

        var said = pending.Text + " " + (pending.FileText ?? "");
        if (target.AllNames().Any(name => name.Length > 2 && said.Contains(name, StringComparison.OrdinalIgnoreCase)))
            return null;

        // And only when the sentence names something the graph does NOT know, which is the substitution pattern itself:
        // an unfamiliar name in, a familiar one out. Without this the check refuses honest resolving — "the printers"
        // becoming Ace Printing, a document's facts, a thing the sentence refers to obliquely — all of which name
        // nothing the graph could have matched and are exactly what the reconciler is for.
        var unknown = Words(pending.Text)
            .Where(word => word.Length > 2 && char.IsUpper(word[0]) && !Ordinary.Contains(word))
            .Where(word => _graph.Resolve(word).Node is null && _graph.Get(word) is null)
            .ToList();

        if (unknown.Count == 0) return null;

        // The one it most likely meant: a name the thing it reached for could be a longer form of.
        var spoken = unknown.FirstOrDefault(word => Graph.CouldBeSame(word, target.Name)) ?? unknown[0];

        return ($"\"{target.Name}\" is on file but the statement never mentions it — it says " +
                $"{string.Join(", ", unknown.Take(3))}", spoken);
    }

    /// <summary>
    /// Words that start a sentence with a capital letter and name nothing.
    /// </summary>
    /// <remarks>
    /// The first word cannot simply be skipped, which was the first attempt: "Matt has just bought a camper van" puts the
    /// only name in the sentence at the front, so skipping it made the check blind to exactly the case it exists for.
    /// </remarks>
    private static readonly HashSet<string> Ordinary = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "but", "so", "then", "my", "our", "his", "her", "their", "its", "your",
        "i", "we", "he", "she", "they", "it", "you", "this", "that", "these", "those", "there", "here",
        "who", "what", "when", "where", "why", "how", "is", "was", "are", "were", "has", "have", "had",
        "just", "also", "now", "yes", "no", "ok", "okay", "please", "thanks", "hi", "hey", "hello",
    };

    /// <summary>The words of a sentence, stripped of punctuation.</summary>
    private static IEnumerable<string> Words(string? text) =>
        (text ?? "")
        .Split(new[] { ' ', ',', '.', ';', ':', '!', '?', '\n', '\r', '\t', '(', ')', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The other names a thing answers to, as stated.</summary>
    private static List<string> Names(JsonNode? node)
    {
        var names = new List<string>();
        if (node is not JsonArray list) return names;

        foreach (var item in list)
        {
            var name = item?.GetValue<string>()?.Trim();
            if (name is { Length: > 0 } && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }


    /// <summary>How a write reads back, with its moment when it has one — the confirmation a person actually checks.</summary>
    // No moment to print any more: a fact tied to a time is not a memory here, so there is never one to show.
    private static string Reads(string from, string label, string other) => $"{from} — {label} — {other}";

    private void Undo(JsonNode? take, string? audience, List<string> wrote, List<string> questions)
    {
        var label = (take?["label"]?.GetValue<string>() ?? "").Trim();
        var fromName = (take?["from"]?.GetValue<string>() ?? "").Trim();
        var toName = (take?["to"]?.GetValue<string>() ?? "").Trim();
        var because = (take?["because"]?.GetValue<string>() ?? "").Trim();
        if (label.Length == 0 || fromName.Length == 0 || toName.Length == 0) return;

        // Nothing is created on the way to removing something — a retraction naming things that were never here is a
        // misreading, and inventing the nodes to then end an edge between them would record a fiction.
        var from = Existing(fromName, questions);
        var to = Existing(toName, questions);
        if (from is null || to is null) return;

        // A reason is required by the graph, and a missing one means the story is lost rather than the edge.
        if (because.Length == 0) because = "no longer the case";

        if (_graph.End(from.Id, label, to.Id, because))
            wrote.Add($"ended {from.Name} — {label} — {to.Name} ({because})");
        else
            wrote.Add($"nothing on file to end between {from.Name} and {to.Name}");
    }

    /// <summary>
    /// The node this name means, creating it if nothing answers to it.
    /// </summary>
    /// <remarks>
    /// The heart of the job, and the order matters. A name is resolved BEFORE it is treated as an id, because two people
    /// called Emma have ids <c>emma</c> and <c>emma-2</c> — look the string up as an id and the first one wins silently,
    /// which is exactly the confident wrong answer the graph refuses to give. Resolving first turns that into a question.
    /// <para>
    /// The stated kind is only ever used for something genuinely new. Anything already on file wins outright: Mallorca
    /// is a place, and a plan calling it a topic must land on the place rather than fork a second Mallorca. What exists
    /// beats a guess about what it is.
    /// </para>
    /// </remarks>
    private Node? Landing(string nameOrId, string? kind, List<string> questions)
    {
        var resolved = _graph.Resolve(nameOrId);
        if (resolved.Node is { } found) return found;

        if (resolved.IsAmbiguous)
        {
            questions.Add(Ambiguity(nameOrId, resolved.Ambiguous));
            return null;
        }

        if (_graph.Get(nameOrId) is { } byId) return byId;

        try
        {
            // Cleaned, not vetted against the built-in five. Vetting here was the gate that quietly undid coining: a
            // freshly named kind was accepted by the schema, written into the plan, and then flattened to the catch-all
            // on the way into the graph — so the vocabulary could never actually grow.
            // A name that is nothing but a moment is not a thing, whatever kind it was given. Times and dates kept
            // arriving as nodes — an opening hour, a reporting window, a departure date — and each one is a description of
            // when something happened, holding no connections of its own and crowding the list shown on every write.
            //
            // This is recognising a timestamp, not reading intent out of prose: either the string parses as a moment or it
            // does not. Told three times in words not to do it and doing it anyway, the refusal belongs in the code.
            if (IsAMoment(nameOrId)) return null;

            // One of ours, not one of theirs. A panel, a panel kind, the page it sits on: things that exist because the
            // assistant made them, which is the opposite of what this map is for.
            if (Ours?.Invoke(nameOrId) == true)
            {
                Trace?.Invoke($"[brain] \"{nameOrId}\" is one of ours, not part of their world — not recording it");
                return null;
            }

            var settled = Kinds.Clean(kind);

            // Nothing answers to this name. Before it becomes a second person, ask whether it is the first one under a
            // shorter name: "my brother is Matthew" and then "Matt says he can drive" is one person or two, and the
            // graph cannot tell. What it can do is notice, land the fact anyway, and carry the doubt where somebody
            // can settle it once.
            var might = _graph.MightBe(nameOrId, settled);
            var born = _graph.Upsert(Readable(nameOrId, settled), settled, provisional: might is not null);

            if (might is not null && _graph.Might(born.Id, might.Id))
            {
                questions.Add($"Is {born.Name} the same {settled} as {might.Name}, or someone else?");
                Trace?.Invoke($"[brain] \"{born.Name}\" might be \"{might.Name}\" — recorded as a question");
            }

            return born;
        }
        catch (AmbiguousName ambiguous)
        {
            questions.Add(Ambiguity(nameOrId, ambiguous.Candidates));
            return null;
        }
    }

    /// <summary>
    /// Whether the far end of a write is really a value wearing a node's clothes.
    /// </summary>
    /// <remarks>
    /// Three conditions, all of them structural, so this never has to judge what the words mean. It must be new — anything
    /// already on file is a thing, whatever it is called. It must be called a topic — the catch-all is where a description
    /// lands when nothing else fits, and every other kind is a claim that it is a real thing. And nothing else in the same
    /// statement may mention it, because a second mention is a thing being related to twice.
    /// </remarks>
    private bool LooksLikeAValue(string name, string? kind, JsonObject plan)
    {
        if (!Kinds.Clean(kind).Equals(Kinds.Topic, StringComparison.OrdinalIgnoreCase)) return false;
        if (_graph.Resolve(name).Found || _graph.Get(name) is not null) return false;

        // Already written down once as a value by some earlier statement. Two facts sharing the same words is the second
        // mention, arriving across a gap rather than inside one sentence — and only a node can be shared, so it graduates.
        if (_graph.MentionedAsValue(name)) return false;

        var mentions = Array(plan["writes"]).Count(w =>
            Same(w?["from"]?.GetValue<string>(), name) || Same(w?["to"]?.GetValue<string>(), name));

        return mentions <= 1;
    }

    private static bool Same(string? a, string? b) =>
        a is { Length: > 0 } && b is { Length: > 0 } && Graph.Normalise(a) == Graph.Normalise(b);

    /// <summary>
    /// A new node's name, in the form somebody would actually read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Told to reference existing things by their id, the model writes id-SHAPED strings for new things too, so a shop
    /// arrives as "bespoke-cycles" and a person as "tom". Nothing about the graph is wrong afterwards — ids are slugged
    /// either way, so resolution is unaffected — but every answer built from it reads like a database dump, and this is a
    /// memory whose whole output is prose read back to a person.
    /// </para>
    /// <para>
    /// Only applied to names that are unmistakably slugs, and never to topics: hobbies, subjects and ideas are genuinely
    /// lowercase, and capitalising them would be an error rather than a tidy-up.
    /// </para>
    /// </remarks>
    private static string Readable(string name, string kind)
    {
        var trimmed = name.Trim();
        if (kind.Equals(Kinds.Topic, StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (trimmed.Length == 0 || trimmed.Any(char.IsUpper)) return trimmed;

        // Hyphens AND spaces. Skipping anything with a space in it left "marlow lido" exactly as written, which is the
        // same database-dump reading as the slug it was guarding against — the separator was never the point.
        var words = trimmed.Split(new[] { '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return trimmed;

        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>
    /// Whether this name is only a point or span of time.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: the WHOLE name has to parse as one. Anything with a real name in it as well — a dated event, a
    /// season somebody calls something — is a thing that happens to mention a time, and keeping those is the point.
    /// </remarks>
    private static bool IsAMoment(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return false;

        if (DateTime.TryParse(trimmed, out _) || DateTimeOffset.TryParse(trimmed, out _)) return true;

        // A bare clock time, which no parser agrees on: a number, optional minutes, optional am or pm, and nothing else.
        return System.Text.RegularExpressions.Regex.IsMatch(
            trimmed, @"^\d{1,2}([:.]\d{2})?\s*([ap]\.?m\.?)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>The node this name means, or nothing. Never creates.</summary>
    private Node? Existing(string nameOrId, List<string> questions)
    {
        var resolved = _graph.Resolve(nameOrId);
        if (resolved.Node is { } found) return found;

        if (resolved.IsAmbiguous)
        {
            questions.Add(Ambiguity(nameOrId, resolved.Ambiguous));
            return null;
        }

        return _graph.Get(nameOrId);
    }

    private static string Ambiguity(string name, IReadOnlyList<Node> candidates) =>
        $"Which {name} — there are {candidates.Count} " +
        $"({string.Join(", ", candidates.Select(c => $"{c.Name} [{c.Id}]"))})?";

    // ---- what the model is shown ----

    /// <summary>
    /// Everything on file, by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ids included, because that is how a plan points at something that already exists unambiguously; aliases included,
    /// because "the holiday" is how it will actually be referred to.
    /// </para>
    /// <para>
    /// And how each one stands to the owner, which was the omission that produced the worst write yet. Shown a bare list
    /// of people and a sentence saying "my wife", a model has to pick one, and it picked a neighbour — recording a
    /// marriage that does not exist to somebody who lives nearby. It was not being careless: nothing on the page said
    /// who anybody was, and nothing said that no wife was on file, which is the fact that should have made it create
    /// her instead of reaching for the nearest name.
    /// </para>
    /// </remarks>
    private string Describe()
    {
        var sb = new StringBuilder();
        var ties = Ties();

        foreach (var node in Ranked().Take(Candidates))
        {
            sb.Append("  ").Append(node.Id).Append(" | ").Append(node.Name).Append(" (").Append(node.Kind).Append(')');
            if (node.Aliases.Count > 0) sb.Append(" — also: ").Append(string.Join(", ", node.Aliases));
            if (ties.TryGetValue(node.Id, out var tie)) sb.Append(" — ").Append(tie);
            sb.AppendLine();
        }

        return sb.Length == 0 ? "  (nothing on file yet)\n" : sb.ToString();
    }

    /// <summary>
    /// How each thing directly connected to the owner stands to them.
    /// </summary>
    /// <remarks>
    /// The whole triple, redundant name and all, for the reason <see cref="Step.Sentence"/> gives: the labels are free
    /// prose, so any attempt to shorten this into a phrase reads wrongly for some of them, and a tie stated backwards is
    /// worse than one stated at length. Two at most per thing — enough to place somebody, short of turning the list of
    /// what exists into a second copy of the graph.
    /// </remarks>
    private Dictionary<string, string> Ties()
    {
        var ties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (Self is not { } owner) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Unfiltered, exactly as the list of what exists above it is: the writer is shown everything on file because it
        // has to point at what is already there. Ties add labels to names already listed, so this changes no boundary —
        // and a host that wants the writer walled would have to wall both together, not one of them.
        foreach (var step in _graph.Around(owner.Id))
        {
            if (step.To.Id.Equals(owner.Id, StringComparison.OrdinalIgnoreCase)) continue;

            var lines = ties.TryGetValue(step.To.Id, out var held) ? held : ties[step.To.Id] = new List<string>();
            if (lines.Count < 2) lines.Add(step.Sentence());
        }

        return ties.ToDictionary(pair => pair.Key, pair => string.Join("; ", pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The kinds in this graph, with counts, as the model is shown them.
    /// </summary>
    /// <remarks>
    /// Handed to BOTH sides, and that symmetry is the whole point of an open vocabulary. The writer needs it to reuse a
    /// kind instead of coining a near-duplicate; the reader needs it to filter on a kind that exists. Show it to only one
    /// of them and the two halves drift apart — a question asking for a kind nobody ever wrote returns an empty answer
    /// that reads as "nothing is recorded" rather than "you asked for a sort of thing that isn't a sort of thing here".
    /// </remarks>
    private string Vocabulary() => string.Join(
        Environment.NewLine,
        _graph.KindsInUse().Select(k => $"    {k.Kind} ({k.Count})"));

    /// <summary>
    /// The labels already written, as the writer is shown them.
    /// </summary>
    /// <remarks>
    /// Given only to the writer, not the reader — deliberately, and it is the one asymmetry here. A question must never
    /// filter on a label, so showing a reader the list would invite exactly the mistake the whole design avoids. A writer
    /// needs it for the opposite reason: without it, it invents a fresh phrase for something already recorded, and a fact
    /// whose label nothing else shares can never be found by its sort.
    /// </remarks>
    private string Wording()
    {
        var labels = _graph.LabelsInUse();
        return labels.Count == 0
            ? "    (nothing recorded yet)"
            : string.Join(", ", labels.Select(l => l.Label));
    }

    /// <summary>
    /// The kind a question may filter on: one this graph actually holds, or nothing.
    /// </summary>
    /// <remarks>
    /// Checked against the graph rather than against the built-in list, because the vocabulary is open — gate it on the
    /// system kinds and every coined one becomes unfilterable, which is the same silent miss the coining was meant to
    /// fix. A kind nobody has used is dropped rather than honoured: filtering on it can only return nothing.
    /// </remarks>
    private string? Wanted(string? kind)
    {
        if (kind is not { Length: > 0 }) return null;

        var cleaned = Kinds.Clean(kind);
        return _graph.KindsInUse().Any(k => k.Kind.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            ? cleaned
            : null;
    }

    /// <summary>
    /// Things in the graph with no path back to the owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported, never removed. This is a memory of one person's world, so something nothing connects to them is almost
    /// always information about the world that arrived while looking something up — a place that was suggested, its
    /// address, its opening hours. True of the world, and already known to anybody who can search.
    /// </para>
    /// <para>
    /// "Almost always" is why it is a report. Deleting on this test was tried and it took real facts with it: a statement
    /// that recorded something true about them, whose plan simply forgot to say what it had to do with them, lost the fact
    /// rather than gaining the connection. A pile that is visible while it is small is the whole of what is needed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Node> Stranded()
    {
        if (Self is not { } owner) return System.Array.Empty<Node>();

        return _graph.Nodes
            .Where(n => !n.Id.Equals(owner.Id, StringComparison.OrdinalIgnoreCase))
            .Where(n => !_graph.Reaches(n.Id, owner.Id))
            .ToList();
    }

    /// <summary>The predicate for one reader, or null when the host has no rule and everything is visible.</summary>
    private Func<Edge, bool>? Allowed(string? audience) =>
        Sees is null ? null : edge => Sees(edge.Audience, audience);

    /// <summary>How to refer to the owner, or nothing if the host never said who they are.</summary>
    private string? Whom() => Self is { } self ? $"{self.Name} [{self.Id}]" : null;

    /// <summary>Most connected first — if a cap has to cut something, the hubs are what questions are about.</summary>
    private List<Node> Ranked() => _graph.Nodes
        .OrderByDescending(n => _graph.Around(n.Id).Count)
        .ThenByDescending(n => n.Created)
        .ToList();

    // ---- the model call ----

    private async Task<JsonObject?> AskModelAsync(string brief, JsonNode schema, CancellationToken ct)
    {
        try
        {
            var response = await _model.CompleteAsync(new ModelRequest
            {
                Model = _modelName,
                Messages = new List<Message> { Message.System(brief) },
                Think = false,
                ResponseFormat = schema,
            }, ct).ConfigureAwait(false);

            var content = (response.Content ?? "").Trim();
            if (content.Length == 0) return null;

            return JsonNode.Parse(content) as JsonObject;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[brain] the model call failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>A number the model wrote, however it wrote it — some emit "2" as a string.</summary>
    private static int? Whole(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<int>(); } catch { }
        return int.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static IEnumerable<JsonNode?> Array(JsonNode? node) =>
        node is JsonArray array ? array : Enumerable.Empty<JsonNode?>();

    private static IEnumerable<string> Strings(JsonNode? node) => Array(node)
        .Select(n => n?.GetValue<string>() ?? "")
        .Where(s => s.Trim().Length > 0)
        .Select(s => s.Trim());

    private static string Quote(string? text) =>
        text is null ? "nothing" : $"\"{(text.Length > 60 ? text[..57] + "..." : text)}\"";

    // ---- the queue on disk ----

    private void Remove(Pending pending)
    {
        lock (_lock)
        {
            _pending.RemoveAll(p => p.Id == pending.Id);
            Save();
        }
    }

    private string PendingPath => Path.Combine(_dir!, "brain-pending.json");

    private void Load()
    {
        if (_dir is null || !System.IO.File.Exists(PendingPath)) return;

        try
        {
            _pending.AddRange(JsonSerializer.Deserialize<List<Pending>>(
                System.IO.File.ReadAllText(PendingPath), _json) ?? new());

            _nextPending = _pending
                .Select(p => int.TryParse(p.Id.TrimStart('p'), out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();
        }
        catch
        {
            _pending.Clear();
        }
    }

    private void Save()
    {
        if (_dir is null) return;

        try
        {
            Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(PendingPath, JsonSerializer.Serialize(_pending, _json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[brain] couldn't save what's waiting: {ex.Message}");
        }
    }
}

/// <summary>Something said, waiting to be reconciled into the graph.</summary>
public sealed class Pending
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Whether this takes something back rather than adding it.</summary>
    public bool Retract { get; set; }

    public string? Source { get; set; }

    /// <summary>The audience of the conversation this was said in, stamped onto every fact it becomes.</summary>
    public string? Audience { get; set; }

    /// <summary>Whether a piece of work filed this, rather than a person saying it. Held to a stricter rule.</summary>
    public bool FromRun { get; set; }

    /// <summary>
    /// Whether this was taken from what somebody said rather than deliberately filed.
    /// </summary>
    /// <remarks>
    /// Most conversation states no facts, so an empty plan here is the expected answer and is believed first time. A
    /// sentence somebody filed on purpose gets a second reading before emptiness is accepted.
    /// </remarks>
    public bool Overheard { get; set; }

    /// <summary>Whether one more reading has already been spent on a refusal, so a bad label cannot loop.</summary>
    public bool Retried { get; set; }

    /// <summary>A document handed over with the sentence, waiting for the sentence to say what it belongs to.</summary>
    public string? FilePath { get; set; }

    public string? FileName { get; set; }

    /// <summary>
    /// What the document says, read when it was filed.
    /// </summary>
    /// <remarks>
    /// On the queue rather than fetched at reconciliation time, so the file is read once and the sentence and the document
    /// are always judged together — and so a document that has since been cleaned up still contributes what it said.
    /// </remarks>
    public string? FileText { get; set; }

    public DateTimeOffset Filed { get; set; }
    public int Tried { get; set; }
    public string? Error { get; set; }
}

/// <summary>What one sentence turned into.</summary>
public sealed class Settled
{
    public required string Text { get; init; }

    /// <summary>Each fact written, in words, so the change is readable rather than inferred from the graph.</summary>
    public required IReadOnlyList<string> Wrote { get; init; }

    /// <summary>
    /// Edges the graph would not accept, and why, in the words the refusal used.
    /// </summary>
    /// <remarks>
    /// Kept so the sentence can be read once more with the objection in hand. Explaining a rule in advance does not
    /// change what gets written; being handed back the specific edge that was rejected does.
    /// </remarks>
    public IReadOnlyList<string> Refused { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Things that could not be recorded without guessing.
    /// </summary>
    /// <remarks>
    /// Two Emmas and a sentence saying "Emma". Worth surfacing as a question rather than dropping, because the person
    /// can settle it in one word — and the rest of the sentence still lands, since the ambiguity of one name is no
    /// reason to lose everything else that was said.
    /// </remarks>
    public required IReadOnlyList<string> Questions { get; init; }
}

/// <summary>The outcome of a drain.</summary>
public sealed class Reconciled
{
    public IReadOnlyList<Settled> Settled { get; init; } = System.Array.Empty<Settled>();

    /// <summary>Sentences given up on. Never silent — being told is the whole point of keeping them.</summary>
    public IReadOnlyList<Pending> Dropped { get; init; } = System.Array.Empty<Pending>();

    public IReadOnlyList<string> Questions => Settled.SelectMany(s => s.Questions).ToList();

    public IReadOnlyList<string> Wrote => Settled.SelectMany(s => s.Wrote).ToList();
}
