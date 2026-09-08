using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The reconciliation job, written as the conversation that drives it.
///
/// <para>
/// The whole point is that nothing has to exist before you can talk about it. You say you are going on holiday and the
/// holiday appears; you say you are going with your wife and your wife appears and gets joined to it. Find the subject,
/// find the children, create what is missing — that sequence is the job, and these tests are it.
/// </para>
/// </summary>
public class TellingItThings
{
    private static (Memory Memory, Graph Graph) Fresh(Scripted model)
    {
        var graph = new Graph();
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        return (new Memory(graph, model, "test-model"), graph);
    }

    [Fact]
    public void Filing_something_costs_nothing_and_the_work_happens_after()
    {
        // The reason it is a queue at all: a sentence recorded mid-conversation must not put a model call in the way of
        // the reply. Three things said in one turn used to be three round trips.
        var model = new Scripted();
        var (memory, graph) = Fresh(model);

        memory.Note("we're going on holiday to Majorca in September");

        Assert.Equal(0, model.Calls);
        Assert.Single(memory.Waiting);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public async Task A_holiday_it_has_never_heard_of_becomes_a_holiday_it_knows()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "destination", "Mallorca", fromKind: "project", toKind: "place"),
            Scripted.Fact("Majorca trip", "when", "September")));

        var (memory, graph) = Fresh(model);
        memory.Note("we're going on holiday to Majorca in September");

        var done = await memory.ReconcileAsync();

        var trip = graph.Resolve("Majorca trip").Node;
        Assert.NotNull(trip);
        Assert.Equal(Kinds.Project, trip!.Kind);
        Assert.NotNull(graph.Resolve("Mallorca").Node);
        Assert.Contains("September", graph.Properties(trip.Id).Select(e => e.Value));

        Assert.Empty(memory.Waiting);
        Assert.Empty(done.Questions);
    }

    [Fact]
    public async Task Going_with_my_wife_creates_the_wife_and_joins_her_to_the_trip()
    {
        // The sequence in full, and the one that matters most: the trip is known, the wife is not, and BOTH edges have
        // to land — she is on the trip, and she is my wife. Miss the second and "what holidays am I on with my wife"
        // can never work.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "destination", "Mallorca", fromKind: "project", toKind: "place")));

        var (memory, graph) = Fresh(model);
        memory.Note("we're going on holiday to Majorca");
        await memory.ReconcileAsync();

        model.Then(Scripted.Plan(
            Scripted.Link("Majorca trip", "going on", "Emma", toKind: "person"),
            Scripted.Link("me", "wife", "Emma", toKind: "person")));

        memory.Note("I'm going with my wife Emma");
        await memory.ReconcileAsync();

        var emma = graph.Resolve("Emma").Node;
        Assert.NotNull(emma);
        Assert.Equal(Kinds.Person, emma!.Kind);

        var trip = graph.Resolve("Majorca trip").Node!;
        Assert.Contains(emma.Id, graph.Around(trip.Id).Select(s => s.To.Id));
        Assert.Contains("wife", graph.Around(graph.Resolve("me").Node!.Id).Select(s => s.Edge.Label));

        // And only ONE trip, because the second statement named a trip that already existed.
        Assert.Single(graph.Nodes.Where(n => n.Kind == Kinds.Project));
    }

    [Fact]
    public async Task What_holidays_am_I_going_on_with_my_wife()
    {
        // Two stages, and the second one is the point: resolve the wife loosely, then walk to the projects she is on.
        // Nothing in the graph says "holiday" anywhere.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "going on", "Emma", fromKind: "project", toKind: "person"),
            Scripted.Link("me", "wife", "Emma", toKind: "person")));

        var (memory, graph) = Fresh(model);
        memory.Note("going to Majorca with my wife Emma");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Emma", wanting: Kinds.Project, hops: 1, anchors: new[] { "me" }));

        var answer = await memory.RecallAsync("what holidays am I going on with my wife?");

        Assert.Contains("Majorca trip", answer);
        // And it says HOW it got there, so the reply can be checked rather than trusted.
        Assert.Contains("Emma", answer);
    }

    [Fact]
    public async Task Whos_going_on_the_holiday_finds_people_through_the_trip()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "going on", "Emma", fromKind: "project", toKind: "person"),
            Scripted.Link("Majorca trip", "guest", "Dave", toKind: "person"),
            Scripted.Link("me", "wife", "Emma", toKind: "person"),
            Scripted.Link("me", "old friend from uni", "Dave", toKind: "person")));

        var (memory, graph) = Fresh(model);
        memory.Note("Emma and Dave are coming to Majorca");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Majorca trip", wanting: Kinds.Person, hops: 1,
            anchors: new[] { "me" }, exclude: new[] { "me" }));

        var answer = await memory.RecallAsync("who's going on the Majorca holiday?");

        Assert.Contains("Emma", answer);
        Assert.Contains("Dave", answer);
        // Two different labels on the trip, and how I know each of them, in the recorded words.
        Assert.Contains("me: wife", answer);
        Assert.Contains("me: old friend from uni", answer);
        Assert.DoesNotContain("Alex", answer);
    }

    [Fact]
    public async Task Asking_straight_after_telling_never_reads_stale()
    {
        // The one thing a queue must not cost you. Filing is instant, so a question asked in the very next breath has to
        // settle what is waiting before it answers, or the system looks like it wasn't listening.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "going on", "Emma", fromKind: "project", toKind: "person")));

        var (memory, graph) = Fresh(model);
        memory.Note("Emma is coming to Majorca");

        Assert.Single(memory.Waiting);

        model.Then(Scripted.Query(about: "Majorca trip", wanting: Kinds.Person, hops: 1));
        var answer = await memory.RecallAsync("who's coming to Majorca?");

        Assert.Empty(memory.Waiting);
        Assert.Contains("Emma", answer);
    }

    [Fact]
    public async Task The_model_is_shown_what_already_exists_or_it_could_never_reuse_it()
    {
        // Not a test of the model — a test of what it is handed. Reconciliation that does not show the model the nodes
        // already on file cannot reuse them, and the resulting pile of duplicates looks like a model fault from outside.
        var model = new Scripted(Scripted.Plan(Scripted.Fact("Majorca trip", "when", "September")));
        var (memory, graph) = Fresh(model);
        graph.Upsert("Majorca trip", Kinds.Project, new[] { "the holiday" });
        graph.Upsert("Emma", Kinds.Person);

        memory.Note("the holiday is in September");
        await memory.ReconcileAsync();

        var prompt = Assert.Single(model.Prompts);
        Assert.Contains("majorca-trip", prompt);
        Assert.Contains("the holiday", prompt);
        Assert.Contains("emma", prompt);
        Assert.Contains("the holiday is in September", prompt);
    }

    [Fact]
    public async Task A_name_already_known_under_another_kind_is_reused_not_duplicated()
    {
        // The duplicate this design keeps producing if you let it. Mallorca is on file as a place; a plan that calls it
        // a topic must land on the SAME node, because what exists beats a guess about its kind.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "destination", "Mallorca", fromKind: "project", toKind: "topic")));

        var (memory, graph) = Fresh(model);
        var mallorca = graph.Upsert("Mallorca", Kinds.Place, new[] { "majorca" });

        memory.Note("the trip is to Mallorca");
        await memory.ReconcileAsync();

        Assert.Single(graph.Nodes.Where(n => Graph.Normalise(n.Name) == "mallorca"));
        Assert.Equal(Kinds.Place, graph.Get(mallorca.Id)!.Kind);
        Assert.Contains(mallorca.Id, graph.Around(graph.Resolve("Majorca trip").Node!.Id).Select(s => s.To.Id));
    }

    [Fact]
    public async Task An_ambiguous_name_becomes_a_question_and_the_rest_still_lands()
    {
        // Two Emmas and a sentence that says "Emma". Guessing is the one thing that must not happen — but neither should
        // the whole statement be thrown away, because everything else in it was perfectly clear.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "going on", "Emma", fromKind: "project", toKind: "person"),
            Scripted.Fact("Majorca trip", "when", "September")));

        var (memory, graph) = Fresh(model);
        graph.Upsert("Emma", Kinds.Person, new[] { "emma@home.example" });
        graph.Add("Emma", Kinds.Person, new[] { "emma@work.example" });

        memory.Note("Emma is coming in September");
        var done = await memory.ReconcileAsync();

        var question = Assert.Single(done.Questions);
        Assert.Contains("Emma", question);

        var trip = graph.Resolve("Majorca trip").Node!;
        Assert.Contains("September", graph.Properties(trip.Id).Select(e => e.Value));
        Assert.Empty(graph.Around(trip.Id));
        Assert.Empty(memory.Waiting);
    }

    [Fact]
    public async Task A_well_connected_thing_still_says_what_it_knows_about_itself()
    {
        // The failure this fixes, and it got worse the more the memory held: a subject with more neighbours than the detail
        // threshold lost its OWN facts, so asking about a flight named the airports and dropped the seat, the date and the
        // departure time. One node's properties are bounded no matter how crowded the answer is.
        var model = new Scripted(Scripted.Plan(
            Scripted.Fact("BA2604", "departs at", "06:55", fromKind: "thing"),
            Scripted.Fact("BA2604", "seat", "14A"),
            Scripted.Link("me", "flying on", "BA2604", toKind: "thing")));

        var (memory, graph) = Fresh(model);
        memory.Note("flight details");
        await memory.ReconcileAsync();

        // Crowd the owner out past the threshold, so the flight is reached alongside a great many other things.
        var me = graph.Resolve("me").Node!;
        for (var n = 0; n < Recall.DetailUpTo + 4; n++)
            graph.Link(me.Id, "knows", graph.Upsert($"Person {n}", Kinds.Person).Id);

        model.Then(Scripted.Query(about: "BA2604", hops: 2));
        var answer = await memory.RecallAsync("tell me about BA2604");

        Assert.Contains("departs at: 06:55", answer);
        Assert.Contains("seat: 14A", answer);
    }

    [Fact]
    public void A_run_cannot_file_anything_at_all()
    {
        // The rule that replaced a whole apparatus. There used to be a second way in for work — Learned() — with a prune
        // behind it that deleted anything a run filed which was not directly joined to the owner, because runs filed a
        // café's opening hours, a restaurant's Instagram handle and the fact that a panel had been published.
        //
        // A worker cannot judge what is worth remembering: it holds the page it just read and knows nothing about what
        // its owner cares about. So there is one door now, and it belongs to the half with the conversation in front of
        // it. Nothing to prune, because nothing untrusted gets in.
        Assert.DoesNotContain("Learned",
            typeof(Memory).GetMethods().Select(m => m.Name));
    }

    [Fact]
    public async Task A_person_saying_something_is_taken_at_their_word()
    {
        // The same rule must NOT apply to what somebody tells you. Pruning their statements on this test was tried and took
        // real facts with it: a sentence that recorded something true, whose plan simply forgot to say what it had to do
        // with them, lost the fact instead of gaining the connection.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "destination", "Mallorca", fromKind: "project", toKind: "place")));

        var (memory, graph) = Fresh(model);
        memory.Note("we are going to Mallorca");
        await memory.ReconcileAsync();

        Assert.NotNull(graph.Resolve("Majorca trip").Node);
        Assert.NotNull(graph.Resolve("Mallorca").Node);
    }

    [Fact]
    public async Task What_two_things_have_in_common_is_askable()
    {
        // The question the rest of this could not express, and an ordinary one: what do the two of us do together. Without
        // it the best available reading is "things connected to me", which answers something else and comes back sounding
        // like nothing was ever recorded — over a graph holding the answer plainly.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("me", "brother", "Tom", toKind: "person"),
            Scripted.Link("me", "member of", "Fenwick Harriers", toKind: "club"),
            Scripted.Link("Tom", "trains with", "Fenwick Harriers", fromKind: "person", toKind: "club"),
            Scripted.Link("me", "swims at", "Marlow Lido", toKind: "place")));

        var (memory, graph) = Fresh(model);
        memory.Note("a few things");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "me", with: "Tom", hops: 2));
        var answer = await memory.RecallAsync("what do my brother and I do together?");

        Assert.Contains("Fenwick Harriers", answer);
        // Only what they SHARE. The lido is mine alone, and returning it is the failure being fixed — including at two
        // hops, where without a wall between them everything I touch is two steps from Tom by way of me.
        Assert.DoesNotContain("Marlow Lido", answer);

        // And it says how the other one relates to the thing they share, which is the interesting half.
        Assert.Contains("trains with", answer);
    }

    [Fact]
    public async Task The_other_subject_is_never_the_one_thing_a_shared_question_cannot_return()
    {
        // A walk never includes where it started, so intersecting two neighbourhoods drops the second subject — the one
        // node guaranteed to be connected to both. Asked who somebody's brother is, planned as a question about them
        // shared with me, it reported knowing nothing while holding exactly that.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("me", "brother", "Tom", toKind: "person")));

        var (memory, graph) = Fresh(model);
        memory.Note("Tom is my brother");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Tom", with: "me", hops: 1));
        var answer = await memory.RecallAsync("who is Tom's brother?");

        Assert.Contains("Alex", answer);
    }

    [Fact]
    public async Task A_thing_cannot_be_its_own_anything()
    {
        // Written the moment a model had free rein over labels. It reads as a fact, carries no information, and turns up
        // as a neighbour of itself in every walk that passes through.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Fenwick Harriers", "is running club", "Fenwick Harriers", fromKind: "club", toKind: "club"),
            Scripted.Link("me", "member of", "Fenwick Harriers", toKind: "club")));

        var (memory, graph) = Fresh(model);
        memory.Note("something about the club");
        await memory.ReconcileAsync();

        var club = graph.Resolve("Fenwick Harriers").Node!;
        Assert.DoesNotContain(club.Id, graph.Around(club.Id).Select(s => s.To.Id));

        // And the rest of the sentence still lands.
        Assert.Contains(club.Id, graph.Around(graph.Resolve("me").Node!.Id).Select(s => s.To.Id));
        Assert.Throws<ArgumentException>(() => graph.Link(club.Id, "is", club.Id));
    }

    [Fact]
    public async Task Taking_something_back_keeps_the_reason()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Majorca trip", "chosen hotel", "Stella Island", fromKind: "project", toKind: "place")));

        var (memory, graph) = Fresh(model);
        memory.Note("we're booking Stella Island");
        await memory.ReconcileAsync();

        model.Then(Scripted.Retraction("Majorca trip", "chosen hotel", "Stella Island", "too expensive"));
        memory.Forget("we're not going with Stella Island, too expensive");
        await memory.ReconcileAsync();

        var trip = graph.Resolve("Majorca trip").Node!;
        Assert.Empty(graph.Around(trip.Id));

        var ended = Assert.Single(graph.History(trip.Id).Where(e => e.State == EdgeState.Ended));
        Assert.Equal("too expensive", ended.Because);
    }

    [Fact]
    public async Task Nothing_worth_recording_settles_rather_than_retrying_forever()
    {
        // "thanks!" is a real thing to say and produces no facts. An empty plan has to count as done, or the queue fills
        // with pleasantries being reconsidered every few seconds.
        var model = new Scripted(Scripted.Nothing);
        var (memory, graph) = Fresh(model);

        memory.Note("thanks, that's great");
        var done = await memory.ReconcileAsync();

        Assert.Empty(memory.Waiting);
        Assert.Single(done.Settled);
        Assert.Empty(done.Settled[0].Wrote);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public async Task A_statement_that_keeps_failing_is_dropped_with_its_reason()
    {
        // Retried, but not forever, and never silently. A sentence the model cannot read is a thing to be told about,
        // not a poison pill that stalls every reconciliation behind it.
        var model = new Scripted("not json", "still not json", "nope");
        var (memory, graph) = Fresh(model);

        memory.Note("something it cannot read");

        for (var attempt = 0; attempt < Memory.MaxTries; attempt++) await memory.ReconcileAsync();

        Assert.Empty(memory.Waiting);
        Assert.Equal(Memory.MaxTries, model.Calls);
    }

    [Fact]
    public void What_goes_into_a_conversation_is_a_count_and_never_a_name()
    {
        // A node's name is often the sensitive part rather than a pointer to it — an account handle, a person, where
        // somebody lives. The names used to go into every turn, which put the most identifying material in the memory into
        // the context of every request, and let an answer be read straight off the list without looking anything up.
        var model = new Scripted();
        var (memory, graph) = Fresh(model);

        graph.Upsert("2010techgeeks", Kinds.Thing);
        graph.Link(graph.Resolve("me").Node!.Id, "owns", graph.Resolve("2010techgeeks").Node!.Id);
        graph.Link(graph.Resolve("me").Node!.Id, "brother", graph.Upsert("Tom", Kinds.Person).Id);

        var shape = memory.Shape();

        Assert.DoesNotContain("2010techgeeks", shape);
        Assert.DoesNotContain("Tom", shape);

        // It still says there is something worth searching, and how much.
        Assert.Contains("1 thing", shape);
        Assert.Contains("1 person", shape);
    }

    [Fact]
    public void The_index_names_what_it_knows_so_it_cannot_forget_to_look()
    {
        // The always-on line. It is not the facts — it is the fact that there ARE facts, which is what makes a model
        // decide to search at all. It cannot look up a holiday it has no idea you have.
        var model = new Scripted();
        var (memory, graph) = Fresh(model);

        var trip = graph.Upsert("Majorca trip", Kinds.Project);
        var emma = graph.Upsert("Emma", Kinds.Person);
        graph.Upsert("Reykjavik someday", Kinds.Project);
        graph.Link(trip.Id, "going on", emma.Id);
        graph.Link(graph.Resolve("me").Node!.Id, "wife", emma.Id);

        var index = memory.Index();

        Assert.Contains("Majorca trip", index);
        Assert.Contains("Emma", index);
        Assert.Contains("Reykjavik someday", index);

        // Most connected first, because a cap has to cut something and the hubs are what questions are about.
        Assert.True(index.IndexOf("Emma", StringComparison.Ordinal)
                    < index.IndexOf("Reykjavik someday", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_brain_says_so_rather_than_pretending()
    {
        var model = new Scripted();
        var graph = new Graph();
        var memory = new Memory(graph, model, "test-model");

        Assert.Empty(memory.Index());

        model.Then(Scripted.Query(about: "the holiday"));
        var answer = await memory.RecallAsync("what holiday am I going on?");

        Assert.Contains("nothing", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_queue_survives_a_restart()
    {
        // Filing is instant precisely because the work happens later — so the queue has to outlive the process, or a
        // restart between the telling and the reconciling loses what you said.
        var dir = Path.Combine(Path.GetTempPath(), "smarty-brain-" + Guid.NewGuid().ToString("n"));
        try
        {
            var first = new Memory(new Graph(dir), new Scripted(), "test-model", dir);
            first.Note("Emma is coming to Majorca");
            Assert.Single(first.Waiting);

            var model = new Scripted(Scripted.Plan(
                Scripted.Link("Majorca trip", "going on", "Emma", fromKind: "project", toKind: "person")));
            var graph = new Graph(dir);
            var second = new Memory(graph, model, "test-model", dir);

            Assert.Single(second.Waiting);
            await second.ReconcileAsync();

            Assert.NotNull(graph.Resolve("Emma").Node);
            Assert.Empty(second.Waiting);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
