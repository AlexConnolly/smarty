using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The privacy boundary.
///
/// <para>
/// Its own file because it is the one part of this where a bug is not an inconvenience. Everything else here fails by
/// forgetting something; this fails by telling the wrong room, and nobody finds out. The rule is the host's — the graph
/// only carries an opaque key and asks — so what is tested here is that the key is stamped on every write and consulted
/// on every read, including the reads nobody thinks of as reads.
/// </para>
/// </summary>
public class WhoMaySeeIt
{
    /// <summary>The rule under test: a fact belongs to the room it was said in, and to nowhere else.</summary>
    private static Memory Fresh(Scripted model, out Graph graph)
    {
        graph = new Graph();
        var memory = new Memory(graph, model, "test-model")
        {
            Sees = (fact, reader) => fact is null || string.Equals(fact, reader, StringComparison.Ordinal),
        };
        memory.WhoAmI("Owner");
        return memory;
    }

    [Fact]
    public async Task A_fact_said_in_one_room_is_not_readable_in_another()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "colleague", "Wren", fromKind: "person", toKind: "person")));

        var memory = Fresh(model, out var graph);
        memory.Note("Wren is a colleague", audience: "room-a");
        await memory.ReconcileAsync();

        Assert.NotNull(graph.Resolve("Wren").Node);

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        var inside = await memory.RecallAsync("who do I work with?", "room-a");
        Assert.Contains("Wren", inside);

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        var outside = await memory.RecallAsync("who do I work with?", "room-b");
        Assert.DoesNotContain("Wren", outside);
    }

    [Fact]
    public async Task The_index_does_not_leak_the_name_either()
    {
        // The subtle half. Scoping the facts and then listing every node name gives away the most sensitive part of a
        // private remark — that the thing was mentioned at all.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "seeing", "Ash", fromKind: "person", toKind: "person")));

        var memory = Fresh(model, out _);
        memory.Note("seeing Ash", audience: "room-a");
        await memory.ReconcileAsync();

        Assert.Contains("Ash", memory.Index(audience: "room-a"));
        Assert.DoesNotContain("Ash", memory.Index(audience: "room-b"));
    }

    [Fact]
    public async Task A_hidden_fact_is_not_walked_THROUGH_either()
    {
        // Filtering the answer is not enough. If a private edge can still be traversed, whatever lies beyond it appears
        // in a room that was never party to the step that reached it — and it arrives with an explanation quoting the
        // very fact being kept back.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "sees", "Ash", fromKind: "person", toKind: "person")));

        var memory = Fresh(model, out var graph);
        memory.Note("Owner sees Ash", audience: "room-a");
        await memory.ReconcileAsync();

        model.Then(Scripted.Plan(
            Scripted.Link("Ash", "works at", "Thornbury Mill", fromKind: "person", toKind: "place")));
        memory.Note("Ash works at Thornbury Mill", audience: "room-b");
        await memory.ReconcileAsync();

        // Two hops from the owner: the mill is reachable ONLY by crossing the private edge.
        model.Then(Scripted.Query(about: "Owner", hops: 2));
        var elsewhere = await memory.RecallAsync("what places am I connected to?", "room-b");

        Assert.DoesNotContain("Thornbury Mill", elsewhere);
        Assert.DoesNotContain("Ash", elsewhere);
    }

    [Fact]
    public async Task Nothing_said_about_the_audience_stays_visible_to_everyone()
    {
        // The single-user case, and the one that must not regress: a host with no notion of rooms stamps nothing, and
        // everything it records has to remain readable.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "friend", "Bly", fromKind: "person", toKind: "person")));

        var memory = Fresh(model, out _);
        memory.Note("Bly is a friend");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        Assert.Contains("Bly", await memory.RecallAsync("who are my friends?", "any room at all"));
    }

    [Fact]
    public async Task With_no_rule_at_all_the_boundary_is_off_rather_than_half_on()
    {
        // A host that never says what its rule is gets no filtering — deliberately, and stated in one place. A partly
        // applied boundary is worse than none, because it reads as protection.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Owner", "friend", "Bly", fromKind: "person", toKind: "person")));

        var graph = new Graph();
        var memory = new Memory(graph, model, "test-model");
        memory.WhoAmI("Owner");
        memory.Note("Bly is a friend", audience: "room-a");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        Assert.Contains("Bly", await memory.RecallAsync("who are my friends?", "room-b"));
    }

    [Fact]
    public async Task The_room_it_was_said_in_is_the_room_it_belongs_to_however_late_it_is_worked_through()
    {
        // Filing is instant and reconciliation happens later, so the audience has to travel on the queue. Read at
        // reconciliation time instead, a sentence would be stamped with whoever happened to be talking when the drain
        // ran — which is how a private fact ends up owned by the wrong room.
        var model = new Scripted();
        var memory = Fresh(model, out var graph);

        var filed = memory.Note("something private", audience: "room-a");
        Assert.Equal("room-a", filed.Audience);

        model.Then(Scripted.Plan(
            Scripted.Link("Owner", "confided in", "Wren", fromKind: "person", toKind: "person")));
        await memory.ReconcileAsync();

        var edge = Assert.Single(graph.Edges.Where(e => e.Label == "confided in"));
        Assert.Equal("room-a", edge.Audience);
    }
}
