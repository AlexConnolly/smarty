using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The two destructive operations, and what they must leave behind.
///
/// <para>
/// Everything else here appends: a fact that stops being true is retired with a reason, and the history is the point.
/// These two are different, and the difference is who is asking. A node deleted by hand is one the owner has just said
/// should not exist, so keeping its edges as history would leave the graph still answering with it — and a wipe that
/// kept anything at all would read as empty and answer as though it were not.
/// </para>
/// </summary>
public class TakingThingsOut
{
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static Graph Seeded()
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "i" });
        graph.Upsert("Tom", Kinds.Person);
        graph.Upsert("Marlow Lido", Kinds.Place);

        graph.Link("alex", "brother of", "tom");
        graph.Link("alex", "swims at", "marlow-lido");
        graph.Link("tom", "swims at", "marlow-lido");
        graph.State("alex", "lives in", "London");
        return graph;
    }

    [Fact]
    public void Deleting_a_node_takes_every_edge_touching_it_and_nothing_else()
    {
        var graph = Seeded();

        Assert.True(graph.Forget("marlow-lido"));

        Assert.Null(graph.Get("marlow-lido"));
        Assert.Equal(2, graph.Nodes.Count);

        // The two swims-at edges went with it; the brother link and the property did not.
        Assert.DoesNotContain(graph.Edges, e => e.To == "marlow-lido");
        Assert.Contains(graph.Edges, e => e.Label == "brother of");
        Assert.Contains(graph.Edges, e => e.Label == "lives in");
    }

    [Fact]
    public void Deleting_a_node_erases_rather_than_retires_it()
    {
        // The difference from retiring one fact. A retired edge stays as something that was true; these are edges of a
        // thing the owner has said is not real, so leaving them as history would keep the graph answering with it.
        var graph = Seeded();
        graph.Forget("marlow-lido");

        Assert.DoesNotContain(graph.Edges, e => e.From == "marlow-lido" || e.To == "marlow-lido");
    }

    [Fact]
    public void A_name_merged_into_a_deleted_thing_comes_back_rather_than_going_with_it()
    {
        // What happened to Matt. He was merged into Matthew, which is a tombstone kept ON PURPOSE so that every later
        // "Matt" still lands on the right person. Then Matthew was deleted and nothing touched the tombstone, so it
        // pointed at a node that no longer existed: Get followed it and returned null, and the list of everything on
        // file skips merged nodes. The graph both held the name Matt and could not find it.
        var graph = Seeded();
        var matthew = graph.Upsert("Matthew", Kinds.Person);
        var matt = graph.Upsert("Matt", Kinds.Person);
        graph.Merge(matt.Id, matthew.Id);

        // While Matthew is here, that is the whole point of the tombstone.
        Assert.Equal(matthew.Id, graph.Get(matt.Id)?.Id);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == matt.Id);

        graph.Forget(matthew.Id);

        // And now Matt is findable again, as himself.
        Assert.Equal(matt.Id, graph.Get(matt.Id)?.Id);
        Assert.Contains(graph.Nodes, n => n.Id == matt.Id);
    }

    [Fact]
    public void The_next_mention_of_that_name_is_not_a_second_person()
    {
        // The symptom, which is what actually cost something: the writer is shown everything on file and asked to
        // reuse an id. Matt was not in that list, so a sentence about Matt correctly created a NEW Matt — stranded,
        // with a fact on it that belonged to the one already known.
        var graph = Seeded();
        var matthew = graph.Upsert("Matthew", Kinds.Person);
        var matt = graph.Upsert("Matt", Kinds.Person);
        graph.Merge(matt.Id, matthew.Id);
        graph.Forget(matthew.Id);

        Assert.Equal(matt.Id, graph.Upsert("Matt", Kinds.Person).Id);
    }

    [Fact]
    public void Deleting_something_that_is_not_there_says_so()
    {
        Assert.False(Seeded().Forget("nowhere"));
    }

    [Fact]
    public void A_wipe_empties_everything_and_reports_what_went()
    {
        var graph = Seeded();

        var (nodes, edges) = graph.Wipe();

        Assert.Equal(3, nodes);
        Assert.Equal(4, edges);
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void A_wipe_takes_the_owner_with_it()
    {
        // The honest consequence of a clean slate: "me" is a fact in the graph like any other, so afterwards nobody is
        // recorded as the owner and the host has to ask again.
        var graph = Seeded();
        graph.Wipe();

        Assert.Null(graph.Resolve("me").Node);
    }

    [Fact]
    public void A_wipe_leaves_nothing_that_can_write_itself_back()
    {
        // The failure worth guarding: a queue left behind is read against the empty graph a minute later and rebuilds a
        // handful of the things just deleted, which looks exactly like a wipe that did not work.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "swims at", "Marlow Lido", fromKind: Kinds.Person, toKind: Kinds.Place)));

        var graph = Seeded();
        var memory = new Memory(graph, model, "test-model") { Now = () => Tuesday };
        memory.Note("I swim at Marlow Lido");
        Assert.Single(memory.Waiting);

        graph.Wipe();
        memory.Clear();

        Assert.Empty(memory.Waiting);
        Assert.Null(memory.Self);
    }

    [Fact]
    public async Task Draining_after_a_wipe_writes_nothing_back()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "swims at", "Marlow Lido", fromKind: Kinds.Person, toKind: Kinds.Place)));

        var graph = Seeded();
        var memory = new Memory(graph, model, "test-model") { Now = () => Tuesday };
        memory.Note("I swim at Marlow Lido");

        graph.Wipe();
        memory.Clear();
        await memory.ReconcileAsync();

        Assert.Empty(graph.Nodes);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public void A_wipe_of_the_context_removes_the_copies_on_disk_too()
    {
        // The files are the part that matters: these are copies of documents somebody handed over, so clearing the index
        // and leaving the folder full would be the worst of both outcomes.
        var dir = Path.Combine(Path.GetTempPath(), $"ctx-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var source = Path.Combine(dir, "ticket.txt");
            File.WriteAllText(source, "starts at 9pm");

            var contexts = new Contexts(Path.Combine(dir, "held"));
            contexts.Keep("leadmill", source, "ticket.txt");
            Assert.Single(contexts.All);

            var went = contexts.Wipe();

            Assert.Equal(1, went);
            Assert.Empty(contexts.All);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "held")).Where(f => f.EndsWith(".txt")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_wiped_graph_can_be_used_again_straight_away()
    {
        var graph = Seeded();
        graph.Wipe();

        var fresh = graph.Upsert("Alex", Kinds.Person, new[] { "me" });
        graph.Upsert("Fonda", Kinds.Place);
        var edge = graph.Link(fresh.Id, "is going to", "fonda");

        Assert.Equal("e1", edge.Id);
        Assert.Equal(2, graph.Nodes.Count);
    }
}
