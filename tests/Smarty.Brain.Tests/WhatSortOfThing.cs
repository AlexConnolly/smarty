using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The kind vocabulary: open, but shown to everybody who touches it.
///
/// <para>
/// A closed list of kinds has a junk drawer. Anything that fits none of the built-in five lands in the catch-all, and the
/// catch-all fills with precisely the things hardest to categorise — which makes the one filter a question is allowed to
/// rely on useless for exactly the awkward cases. So the list is open. The argument against opening it is drift, and drift
/// is held off the same way it is for labels: whoever writes and whoever reads are both shown what this graph already
/// uses. These tests are about that symmetry, because showing it to only one side is worse than showing it to neither.
/// </para>
/// </summary>
public class WhatSortOfThing
{
    private static Memory Fresh(Scripted model, out Graph graph)
    {
        graph = new Graph();
        var memory = new Memory(graph, model, "test-model");
        memory.WhoAmI("Owner");
        return memory;
    }

    [Fact]
    public async Task A_kind_nothing_built_in_covers_can_be_coined()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "member of", "Fenwick Harriers", fromKind: "person", toKind: "club")));

        var memory = Fresh(model, out var graph);
        memory.Note("joined a running club");
        await memory.ReconcileAsync();

        var club = graph.Resolve("Fenwick Harriers").Node;
        Assert.NotNull(club);
        Assert.Equal("club", club!.Kind);

        // And it takes its place in the vocabulary, alongside the ones that always exist.
        var vocabulary = graph.KindsInUse();
        Assert.Contains(("club", 1), vocabulary);
        foreach (var system in Kinds.System)
            Assert.Contains(system, vocabulary.Select(k => k.Kind));
    }

    [Fact]
    public async Task A_coined_kind_can_then_be_filtered_on_like_any_other()
    {
        // The half that makes coining worth anything. A kind that can be written but not asked for is a kind that has
        // silently lost the thing it was created to make findable.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "member of", "Fenwick Harriers", fromKind: "person", toKind: "club"),
            Scripted.Link("Owner", "works at", "Ashgrove Mill", fromKind: "person", toKind: "place")));

        var memory = Fresh(model, out _);
        memory.Note("joined a club and started a job");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Owner", wanting: "club", hops: 1));
        var answer = await memory.RecallAsync("what clubs am I in?");

        Assert.Contains("Fenwick Harriers", answer);
        Assert.DoesNotContain("Ashgrove Mill", answer);
    }

    [Fact]
    public async Task Both_sides_are_shown_the_same_vocabulary()
    {
        // Writer and reader must see the same list. Show it to the writer only and questions filter on kinds that were
        // never used; show it to the reader only and every awkward thing keeps landing in the catch-all.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "member of", "Fenwick Harriers", fromKind: "person", toKind: "club")));

        var memory = Fresh(model, out _);
        memory.Note("joined a running club");
        await memory.ReconcileAsync();

        var writing = Assert.Single(model.Prompts);
        Assert.Contains("person", writing);
        Assert.Contains("topic", writing);

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        await memory.RecallAsync("what am I part of?");

        var reading = model.Prompts[^1];
        Assert.Contains("club (1)", reading);
        Assert.Contains("person", reading);
    }

    [Fact]
    public async Task Asking_for_a_kind_this_graph_has_never_used_is_dropped_not_honoured()
    {
        // Filtering on a kind nobody wrote can only return nothing, and nothing reads as "there are no facts" rather than
        // "you asked for a sort of thing that isn't one here". Dropping the filter gives an answer that can be judged.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "member of", "Fenwick Harriers", fromKind: "person", toKind: "club")));

        var memory = Fresh(model, out _);
        memory.Note("joined a running club");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Owner", wanting: "institution", hops: 1));
        Assert.Contains("Fenwick Harriers", await memory.RecallAsync("what am I a member of?"));
    }

    [Fact]
    public void Kinds_are_normalised_on_the_way_in_so_capitals_cannot_fork_one()
    {
        // The whole defence against drift, and it has to be unskippable: two spellings of one kind split the only filter
        // that works, and nothing about the graph would look wrong afterwards.
        var graph = new Graph();
        graph.Upsert("Fenwick Harriers", "Club");
        graph.Upsert("Ashcombe Wheelers", " club ");
        graph.Upsert("Bell Ringers", "Sports Club");

        Assert.Equal(new[] { "club", "sports-club" },
            graph.Nodes.Select(n => n.Kind).Where(k => k.Contains("club")).Distinct().OrderBy(k => k));
        Assert.Equal(2, graph.KindsInUse().Single(k => k.Kind == "club").Count);
    }

    [Fact]
    public void Anything_unusable_as_a_kind_falls_back_rather_than_being_stored_blank()
    {
        var graph = new Graph();
        Assert.Equal(Kinds.Topic, graph.Upsert("Something", "").Kind);
        Assert.Equal(Kinds.Topic, graph.Upsert("Another", "   ").Kind);
        Assert.Equal(Kinds.Topic, graph.Upsert("A third", "!!!").Kind);
    }

    [Fact]
    public void The_vocabulary_reports_counts_so_a_near_duplicate_is_visible()
    {
        // Nothing can stop a model coining a second word for an idea it already had one for. What CAN be done is make it
        // obvious afterwards: a kind holding one node next to a kind holding several is either brand new or a duplicate,
        // and both are worth a human's glance.
        var graph = new Graph();
        for (var n = 0; n < 4; n++) graph.Upsert($"Firm {n}", "company");
        graph.Upsert("An outlier", "business");

        var vocabulary = graph.KindsInUse();
        Assert.Equal("company", vocabulary[0].Kind);
        Assert.Equal(4, vocabulary[0].Count);
        Assert.Equal(1, vocabulary.Single(k => k.Kind == "business").Count);
    }

    [Fact]
    public async Task A_new_name_written_as_a_slug_is_stored_the_way_it_would_be_read()
    {
        // Told to reference existing things by id, the model writes id-shaped names for new ones too. The graph does not
        // mind — ids are slugged regardless — but every answer is prose read back to a person, and a slug in a sentence
        // is a database dump.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "member of", "fenwick-harriers", fromKind: "person", toKind: "club"),
            Scripted.Link("Owner", "knows", "tom", fromKind: "person", toKind: "person"),
            Scripted.Link("Owner", "swims at", "marlow lido", fromKind: "person", toKind: "place")));

        var memory = Fresh(model, out var graph);
        memory.Note("a few things at once");
        await memory.ReconcileAsync();

        Assert.Equal("Fenwick Harriers", graph.Resolve("fenwick-harriers").Node!.Name);
        Assert.Equal("Tom", graph.Resolve("tom").Node!.Name);
        // Spaced and lowercase is the same problem as hyphenated and lowercase — the separator was never the point.
        Assert.Equal("Marlow Lido", graph.Resolve("marlow-lido").Node!.Name);
        Assert.Equal("Marlow Lido", graph.Resolve("marlow-lido").Node!.Name);

        // And it is still the same node under either spelling, because identity was never the display name.
        Assert.Equal(graph.Resolve("Fenwick Harriers").Node!.Id, graph.Resolve("fenwick-harriers").Node!.Id);
    }

    [Fact]
    public void A_description_stored_as_a_thing_is_reported_rather_than_left_to_pile_up()
    {
        // The recurring slip: a time or an amount written as a node instead of a value. It cannot be judged reliably in
        // code — deleting on a guess loses real facts — so it is surfaced while the pile is small, because the names it
        // adds go into the list shown on every write and every question.
        var graph = new Graph();
        var me = graph.Upsert("Owner", Kinds.Person);
        var pool = graph.Upsert("Marlow Lido", Kinds.Place);

        graph.Link(me.Id, "swims on", graph.Upsert("wednesday mornings", Kinds.Topic).Id);
        graph.Link(me.Id, "swims at", pool.Id);

        // A topic that several things point at, or that has facts of its own, is a real topic and stays out of it.
        var running = graph.Upsert("trail running", Kinds.Topic);
        graph.Link(me.Id, "hobby", running.Id);
        graph.Link(graph.Upsert("Rosa", Kinds.Person).Id, "hobby", running.Id);

        var loose = graph.Loose();

        Assert.Equal("wednesday mornings", Assert.Single(loose).Name);
        Assert.DoesNotContain("trail running", loose.Select(n => n.Name));
        Assert.DoesNotContain("Marlow Lido", loose.Select(n => n.Name));
    }

    [Fact]
    public async Task A_new_topic_nothing_else_mentions_is_written_as_a_value_not_a_node()
    {
        // The write that kept coming back wrong, taken out of the model's hands. Nothing in the graph could ever reach a
        // brand-new topic except the single edge about to be made, which is what a value IS — so it is stored as one.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "swims on", "wednesday mornings", fromKind: "person", toKind: "topic"),
            Scripted.Link("Owner", "swims at", "Marlow Lido", fromKind: "person", toKind: "place")));

        var memory = Fresh(model, out var graph);
        memory.Note("swimming on wednesday mornings at the lido");
        await memory.ReconcileAsync();

        Assert.Null(graph.Resolve("wednesday mornings").Node);
        Assert.Contains("wednesday mornings", graph.Properties(graph.Resolve("Owner").Node!.Id).Select(e => e.Value));

        // A real place is untouched by the rule — it was never called a topic.
        Assert.NotNull(graph.Resolve("Marlow Lido").Node);
    }

    [Fact]
    public async Task A_topic_the_same_statement_mentions_twice_is_a_thing()
    {
        // Mentioned once it can only ever be described by one edge; mentioned twice it is something being related to, and
        // a value cannot carry a second connection.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "hobby", "trail running", fromKind: "person", toKind: "topic"),
            Scripted.Link("Rosa", "hobby", "trail running", fromKind: "person", toKind: "topic")));

        var memory = Fresh(model, out var graph);
        memory.Note("we both run trails");
        await memory.ReconcileAsync();

        var running = graph.Resolve("trail running").Node;
        Assert.NotNull(running);
        Assert.Equal(2, graph.Around(running!.Id).Count);
    }

    [Fact]
    public async Task A_value_becomes_a_thing_the_moment_it_earns_it()
    {
        // Why storing it as a value costs nothing. The graph already relinks text to a node created later, so a
        // description that turns out to matter is promoted without anybody having to have guessed right first time.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "hobby", "trail running", fromKind: "person", toKind: "topic")));

        var memory = Fresh(model, out var graph);
        memory.Note("took up trail running");
        await memory.ReconcileAsync();

        Assert.Null(graph.Resolve("trail running").Node);

        // Somebody else takes it up, so now it is a thing two people share.
        model.Then(Scripted.Plan(
            Scripted.Link("Rosa", "hobby", "trail running", fromKind: "person", toKind: "topic"),
            Scripted.Link("Rosa", "neighbour of", "Owner", fromKind: "person", toKind: "person")));
        memory.Note("Rosa runs trails too, she is my neighbour");
        await memory.ReconcileAsync();

        var running = graph.Resolve("trail running").Node;
        Assert.NotNull(running);
        Assert.Equal(2, graph.Around(running!.Id).Count);
    }

    [Fact]
    public void Names_still_may_not_repeat_for_a_coined_kind()
    {
        // The one rule that does not become negotiable just because the vocabulary did: two people sharing a name is
        // ordinary, two of anything else sharing one is the beginning of the rot.
        var graph = new Graph();
        var first = graph.Upsert("Fenwick Harriers", "club");
        Assert.Equal(first.Id, graph.Upsert("Fenwick Harriers", "club").Id);
        Assert.Equal(first.Id, graph.Add("Fenwick Harriers", "club").Id);
        Assert.Single(graph.Nodes);
    }
}
