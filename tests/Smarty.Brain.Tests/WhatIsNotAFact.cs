using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The three ways junk got in, none of which was a durability problem.
///
/// <para>
/// A graph of eleven nodes contained a thing called "Try again", a thing whose name was an entire user message
/// including a URL, a restaurant's Instagram handle, the fact that its menu page returns a 404, and one fact written
/// twice in opposite directions. The durability rule was working the whole time. What was missing was any notion of
/// whether the input was a STATEMENT, whether a job may DESCRIBE the world, and whether an edge says anything at all.
/// </para>
/// </summary>
public class WhatIsNotAFact
{
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static (Memory Memory, Graph Graph) Fresh(Scripted model)
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        return (new Memory(graph, model, "test-model") { Now = () => Tuesday }, graph);
    }

    [Fact]
    public async Task Something_overheard_can_be_taken_back_before_it_is_read()
    {
        // The counterpart to overhearing everything. Filing every sentence is only safe if the host can withdraw one the
        // moment it learns something the words could not tell it — here, that the turn was a request.
        var model = new Scripted(Scripted.Plan(
            Scripted.Fact("Alex", "has", "try again", fromKind: Kinds.Person)));

        var (memory, graph) = Fresh(model);
        var heard = memory.Overhear("Try again");

        Assert.True(memory.Discard(heard.Id));
        Assert.Empty(memory.Waiting);

        await memory.ReconcileAsync();

        Assert.Equal(0, model.Calls);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void Discarding_something_already_read_says_so_rather_than_pretending()
    {
        var (memory, _) = Fresh(new Scripted());
        Assert.False(memory.Discard("p99"));
        Assert.False(memory.Discard(""));
    }

    [Fact]
    public async Task What_a_stranger_can_change_is_still_refused_however_it_arrives()
    {
        // This used to be about what a RUN filed, policed after the fact by a prune. Runs cannot file at all now, so the
        // question is what happens when the same thing arrives the only way left: somebody saying it. The answer is the
        // same and the reason is unchanged — an Instagram handle and a page's status code move without anybody being
        // told, so a copy here goes quietly wrong.
        //
        // Kept as a test of the DEFINITION rather than of the plumbing: the writer is told this, and the lifetime it must
        // state is the mechanism. A fact that only outside forces can change is not one that lasts.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "favourite restaurant", "Fonda", fromKind: Kinds.Person, toKind: Kinds.Place,
                lasts: Lifetimes.WhileItHolds),
            Scripted.Fact("Fonda", "has /menu page", "returns a 404", fromKind: Kinds.Place,
                lasts: Lifetimes.UntilItIsDone)));

        var (memory, graph) = Fresh(model);
        memory.Note("Fonda is my favourite restaurant, though their menu page is a 404 at the moment");
        await memory.ReconcileAsync();

        var fonda = graph.Resolve("Fonda").Node;
        Assert.NotNull(fonda);
        Assert.Contains(graph.Around("alex"), step => step.To.Id == fonda!.Id);
        Assert.Empty(graph.Properties(fonda!.Id));
    }

    [Fact]
    public async Task What_they_say_themselves_keeps_its_properties()
    {
        // The prune above must only ever touch what a RUN filed. A person saying something is taken at their word.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "is going to", "Fonda", fromKind: Kinds.Person, toKind: Kinds.Place),
            Scripted.Fact("Fonda", "table booked for", "four", fromKind: Kinds.Place)));

        var (memory, graph) = Fresh(model);
        memory.Note("I've booked Fonda for four on Friday");
        await memory.ReconcileAsync();

        var fonda = graph.Resolve("Fonda").Node!;
        Assert.Single(graph.Properties(fonda.Id));
    }

    [Fact]
    public void One_fact_written_twice_in_opposite_directions_is_refused()
    {
        // Not two facts. One fact written twice by a writer reaching for the emptiest verb available — and the pair is
        // the tell that the label carries no direction, and so no meaning.
        // Between two THINGS, so only the reciprocal rule is in play — a link to a person is refused earlier, by the
        // guard below, and would prove the wrong thing here.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Places List", Kinds.Thing);
        graph.Upsert("Dinner Spots", Kinds.Thing);

        graph.Link("places-list", "contains", "dinner-spots");

        var refused = Assert.Throws<ArgumentException>(() => graph.Link("dinner-spots", "contains", "places-list"));
        Assert.Contains("says nothing more", refused.Message);
        Assert.Single(graph.Edges.Where(e => e.State == EdgeState.Active));
    }

    [Fact]
    public void Nobody_has_a_person()
    {
        // It arrived as "my sister is called Niamh" and was recorded as though she were an object he owned. Possession is
        // a real relationship for a thing and a discarded one for a person: the sentence always said which relation it
        // was, and this label is the writer throwing away the only part that mattered.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person);
        graph.Upsert("Niamh", Kinds.Person);

        var refused = Assert.Throws<ArgumentException>(() => graph.Link("alex", "has", "niamh"));
        Assert.Contains("says nothing about how", refused.Message);

        // And the real relationship is accepted.
        graph.Link("alex", "brother of", "niamh");
        Assert.Single(graph.Edges.Where(e => e.State == EdgeState.Active));
    }

    [Fact]
    public async Task A_refused_label_loses_that_edge_and_nothing_else()
    {
        // The first version of this guard threw and the plan died mid-way: a sister arrived as a node with nothing
        // joining her to anybody, and the place she lives was never created at all. Worse than the bad label was.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "has", "Niamh", fromKind: Kinds.Person, toKind: Kinds.Person),
            Scripted.Link("Niamh", "lives in", "Bristol", fromKind: Kinds.Person, toKind: Kinds.Place)));

        var (memory, graph) = Fresh(model);
        memory.Note("my sister is called Niamh and she lives in Bristol");
        var done = await memory.ReconcileAsync();

        // The rest of the sentence landed.
        Assert.NotNull(graph.Resolve("Bristol").Node);
        Assert.Contains(graph.Around("niamh"), step => step.To.Id == "bristol");

        // The empty one did not, and it is asked about rather than lost silently.
        Assert.DoesNotContain(graph.Around("alex"), step => step.To.Id == "niamh");
        Assert.Contains(done.Questions, q => q.Contains("Niamh") && q.Contains("related"));
    }

    [Fact]
    public async Task A_refusal_is_handed_back_and_the_second_reading_gets_it_right()
    {
        // Explaining the rule in advance did not work: shown that a link to a person may not be "has", it wrote "has"
        // anyway and a sister became a question. What changes an answer is being handed the specific edge that was
        // rejected, so the refusal goes back with the sentence and one more reading is spent on it.
        var model = new Scripted(
            Scripted.Plan(Scripted.Link("Alex", "has", "Niamh", fromKind: Kinds.Person, toKind: Kinds.Person)),
            Scripted.Plan(Scripted.Link("Alex", "sister of", "Niamh", fromKind: Kinds.Person, toKind: Kinds.Person)));

        var (memory, graph) = Fresh(model);
        memory.Note("my sister is called Niamh");
        var done = await memory.ReconcileAsync();

        Assert.Equal(2, model.Calls);
        Assert.Contains("refused", model.Prompts[1]);
        Assert.Contains("sister of", Assert.Single(done.Wrote));
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "niamh");
    }

    [Fact]
    public async Task Only_one_more_reading_is_spent_however_stubborn_it_is()
    {
        // A writer that keeps proposing the same refused edge must cost one extra call, not a loop.
        var refused = Scripted.Plan(Scripted.Link("Alex", "has", "Niamh", fromKind: Kinds.Person, toKind: Kinds.Person));
        var model = new Scripted(refused, refused, refused);

        var (memory, graph) = Fresh(model);
        memory.Note("my sister is called Niamh");
        var done = await memory.ReconcileAsync();

        Assert.Equal(2, model.Calls);
        Assert.Empty(graph.Around("alex").Where(s => s.To.Id == "niamh"));
        Assert.NotEmpty(done.Questions);
    }

    [Fact]
    public void Owning_a_thing_is_still_a_fact()
    {
        // The guard has to stay off the honest uses of the same verb, or every ticket and account in the graph breaks.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person);
        graph.Upsert("Flight ticket", Kinds.Thing);

        graph.Link("alex", "has", "flight-ticket");
        Assert.Single(graph.Edges.Where(e => e.State == EdgeState.Active));
    }

    [Fact]
    public void A_different_label_back_the_other_way_is_a_real_second_fact()
    {
        // The guard must not start refusing genuine pairs: two people can stand in different relations to each other.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person);
        graph.Upsert("Tom", Kinds.Person);

        graph.Link("alex", "brother of", "tom");
        graph.Link("tom", "lives with", "alex");

        Assert.Equal(2, graph.Edges.Count(e => e.State == EdgeState.Active));
    }

    [Fact]
    public void An_erased_edge_leaves_nothing_behind_unlike_a_retired_one()
    {
        // Retiring keeps something that was true and stopped being true, which is the point of the history. This is for
        // an edge that was never a fact about this person at all.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Fonda", Kinds.Place);
        var edge = graph.State("fonda", "has /menu page", "returns a 404");

        Assert.True(graph.Erase(edge.Id));
        Assert.Empty(graph.Edges);
        Assert.False(graph.Erase(edge.Id));
    }
}
