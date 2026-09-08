using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// What the memory is FOR, which turned out to be the thing that was wrong.
///
/// <para>
/// A graph of five nodes held: a tracker somebody had asked for, a panel that had been published, the home page, and
/// "active projects". Alex was the fifth. Every one of the four is true and every one is worthless, and each arrived
/// after a guard was written against the shape of the last one — an empty verb, then a property a run had looked up.
/// The guards were a list of wrong answers and the list has no end.
/// </para>
/// <para>
/// The map holds somebody's WORLD: who is in it, what is theirs, how those connect. Not what happened, not what was
/// asked for, not what is being worked on. So the question asked of every fact is how long it lasts — which is factual,
/// and which a request answers honestly: it ends when it is served. The code drops those. Nobody is asked to judge
/// whether a fact is "relevant", because that was the impossible part: a request somebody made really is about them.
/// </para>
/// </summary>
public class AMapNotALog
{
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static (Memory Memory, Graph Graph) Fresh(Scripted model)
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        return (new Memory(graph, model, "test-model") { Now = () => Tuesday }, graph);
    }

    [Fact]
    public async Task Something_they_asked_for_is_not_a_memory()
    {
        // The exact fact that was on file: "Alex — wants — Latest Sale Tracker". True the moment it was said, and still
        // sitting there long after the panel was built, deleted and forgotten.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "wants", "Latest Sale Tracker", fromKind: Kinds.Person, toKind: Kinds.Thing,
                lasts: Lifetimes.UntilItIsDone)));

        var (memory, graph) = Fresh(model);
        memory.Note("can you track my latest eBay sale on the homepage");
        var done = await memory.ReconcileAsync();

        Assert.Empty(done.Wrote);
        Assert.Empty(graph.Edges);

        // And not as a question either. Nothing went wrong — this is simply not what the map holds.
        Assert.Empty(done.Questions);
    }

    [Fact]
    public async Task The_thing_a_dropped_request_invented_goes_with_it()
    {
        // Half a fix is worse than none: refusing the fact and keeping the node leaves the tracker on file, joined to
        // nothing, and on the list shown to every future write — where it invites a second edge to reconnect it.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "wants", "Latest Sale Tracker", fromKind: Kinds.Person, toKind: Kinds.Thing,
                lasts: Lifetimes.UntilItIsDone)));

        var (memory, graph) = Fresh(model);
        memory.Note("can you track my latest eBay sale on the homepage");
        await memory.ReconcileAsync();

        Assert.Null(graph.Resolve("Latest Sale Tracker").Node);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public async Task What_lasts_is_kept_from_the_same_sentence()
    {
        // The gate has to be per fact, not per sentence. One sentence usually says something about their world AND asks
        // for something, and dropping the whole reading because part of it was a request loses the half worth having.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "sister of", "Niamh", fromKind: Kinds.Person, toKind: Kinds.Person,
                lasts: Lifetimes.Forever),
            Scripted.Link("Alex", "wants", "Family Tree Panel", fromKind: Kinds.Person, toKind: Kinds.Thing,
                lasts: Lifetimes.UntilItIsDone)));

        var (memory, graph) = Fresh(model);
        memory.Note("my sister is called Niamh — could you put a family tree on my home page");
        var done = await memory.ReconcileAsync();

        Assert.Single(done.Wrote);
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "niamh");
        Assert.Null(graph.Resolve("Family Tree Panel").Node);
    }

    [Fact]
    public async Task A_state_that_holds_until_it_changes_is_kept()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "lives in", "Bristol", fromKind: Kinds.Person, toKind: Kinds.Place,
                lasts: Lifetimes.WhileItHolds)));

        var (memory, graph) = Fresh(model);
        memory.Note("I live in Bristol");
        var done = await memory.ReconcileAsync();

        Assert.Single(done.Wrote);
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "bristol");
    }

    [Fact]
    public async Task A_plan_that_says_nothing_about_how_long_is_still_recorded()
    {
        // The field is new, and dropping everything that omits it would mean one missing field silently emptying the
        // memory. Unstated is kept — the gate is for what actively says it ends with the work.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "brother of", "Tom", fromKind: Kinds.Person, toKind: Kinds.Person)));

        var (memory, graph) = Fresh(model);
        memory.Note("Tom is my brother");
        var done = await memory.ReconcileAsync();

        Assert.Single(done.Wrote);
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "tom");
    }

    [Fact]
    public async Task A_value_that_ends_with_the_job_is_dropped_the_same_way()
    {
        // Not only links. "Alex — needs — the item number found" is a property, and it ends the moment somebody finds it.
        var model = new Scripted(Scripted.Plan(
            Scripted.Fact("Alex", "needs", "the item number for the vinyl listing", fromKind: Kinds.Person,
                lasts: Lifetimes.UntilItIsDone)));

        var (memory, graph) = Fresh(model);
        memory.Note("find the item number for my vinyl listing");
        var done = await memory.ReconcileAsync();

        Assert.Empty(done.Wrote);
        Assert.Empty(graph.Edges);
    }

    // ── what nearly got lost ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_preference_is_a_fact_even_when_what_is_preferred_is_a_day()
    {
        // Live, and the complaint that found it: "my favourite day to relax is Sunday" was filed, reconciled, and
        // recorded NOTHING — twice, because a second reading asked the identical question and got the identical empty
        // answer. A day of the week is not a thing in its own right and nothing about it is scheduled, so the day goes in
        // a value and the preference belongs. Said once, in the one brief, for every sentence.
        var model = new Scripted(Scripted.Plan(
            Scripted.Fact("Alex", "favourite day to relax", "Sunday", fromKind: Kinds.Person,
                lasts: Lifetimes.WhileItHolds)));

        var (memory, graph) = Fresh(model);
        memory.Note("My favourite day to relax and do my own things is Sunday.");
        var done = await memory.ReconcileAsync();

        Assert.Contains(graph.Properties("alex"), e => e.Value == "Sunday");
        Assert.Single(done.Wrote);

        // One reading. There is no second attempt at an empty plan any more: a retry that has to say something different
        // to work is an admission that the first prompt was wrong, and it hides the defect.
        Assert.Equal(1, model.Calls);
        Assert.Contains("A PREFERENCE IS A FACT", model.Prompts[0]);
    }

    [Fact]
    public async Task A_sentence_with_no_subject_is_about_the_person_whose_memory_it_is()
    {
        // "I love the beach!" reached the memory as "Loves the beach." — the subject stripped by whatever paraphrased it.
        // There is exactly one person a subjectless statement in somebody's own memory can be about.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "loves", "The Beach", fromKind: Kinds.Person, toKind: Kinds.Place,
                lasts: Lifetimes.WhileItHolds)));

        var (memory, graph) = Fresh(model);
        memory.Note("Loves the beach.");
        await memory.ReconcileAsync();

        Assert.Contains(graph.Around("alex"), step => step.To.Name == "The Beach");
        Assert.Contains("NO subject", model.Prompts[0]);
    }

    [Fact]
    public async Task A_statement_naming_nobody_is_about_the_owner_even_when_the_reading_says_otherwise()
    {
        // The live one, and worse than losing the fact: "Loves the beach." was attached to MATTHEW — the last person
        // anybody had mentioned — which is a fact about the wrong person's life that reads perfectly plausibly for ever.
        // There is exactly one person a subjectless statement in somebody's own memory can be about.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Matthew", "loves", "The Beach", fromKind: Kinds.Person, toKind: Kinds.Place,
                lasts: Lifetimes.WhileItHolds)));

        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        graph.Upsert("Matthew", Kinds.Person);
        var memory = new Memory(graph, model, "test-model") { Now = () => Tuesday };

        memory.Note("Loves the beach.");
        await memory.ReconcileAsync();

        Assert.Contains(graph.Around("alex"), step => step.To.Name == "The Beach");
        Assert.Empty(graph.Around("matthew"));
    }

    [Fact]
    public async Task A_statement_that_does_name_somebody_is_left_where_it_was_put()
    {
        // The rule has to stay off every sentence that names a person, or a memory with two people in it starts
        // attributing everything to its owner.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Matthew", "loves", "The Beach", fromKind: Kinds.Person, toKind: Kinds.Place,
                lasts: Lifetimes.WhileItHolds)));

        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        graph.Upsert("Matthew", Kinds.Person);
        var memory = new Memory(graph, model, "test-model") { Now = () => Tuesday };

        memory.Note("Matthew loves the beach.");
        await memory.ReconcileAsync();

        Assert.Contains(graph.Around("matthew"), step => step.To.Name == "The Beach");
        Assert.Empty(graph.Around("alex"));
    }

    // ── the backstop: our own furniture ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_of_the_assistant_s_own_becomes_a_thing()
    {
        // "Alex — has — Home Page" was on file, and by the lifetime test it is honestly permanent: the home page will
        // sit there for ever. It is still not part of his world. What the assistant owns is knowable, so the host says
        // so, and the map stays about people, places and things.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "has", "Home Page", fromKind: Kinds.Person, toKind: Kinds.Thing,
                lasts: Lifetimes.Forever)));

        var (memory, graph) = Fresh(model);
        memory.Ours = name => name.Equals("Home Page", StringComparison.OrdinalIgnoreCase);
        memory.Note("my home page looks good");
        await memory.ReconcileAsync();

        Assert.Null(graph.Resolve("Home Page").Node);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task A_place_it_has_never_heard_of_is_still_created()
    {
        // The backstop must stay narrow. It is a list of what the assistant owns, not a list of names nobody may use.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "swims at", "London Fields Lido", fromKind: Kinds.Person, toKind: Kinds.Place,
                lasts: Lifetimes.WhileItHolds)));

        var (memory, graph) = Fresh(model);
        memory.Ours = name => name.Equals("Home Page", StringComparison.OrdinalIgnoreCase);
        memory.Note("I swim at London Fields Lido");
        await memory.ReconcileAsync();

        Assert.NotNull(graph.Resolve("London Fields Lido").Node);
    }

    [Fact]
    public void The_writer_is_told_which_lifetimes_there_are()
    {
        // Read off the same vocabulary the code gates on. Stated in the brief but enforced elsewhere, the two drift and
        // the model spends every call proposing a word the schema rejects.
        var brief = Reconcile.Brief("anything", retract: false, known: "", self: "alex", kinds: "", wording: "",
            today: Tuesday);

        foreach (var lifetime in Lifetimes.All) Assert.Contains(lifetime, brief);
    }
}
