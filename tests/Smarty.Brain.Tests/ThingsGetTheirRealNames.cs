using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// A thing recorded before anybody said its name, once somebody does.
///
/// <para>
/// Much of this graph is created from a description: "my wife" lands as "Alex's wife", "the woman who runs the deli"
/// lands as itself. Then the name arrives — and the only thing the writer could express was another ALIAS, because
/// <c>things</c> had <c>name</c>, <c>kind</c> and <c>also</c> and nothing else. So the node kept the placeholder as
/// its title and answered to a real name nobody could see: told his wife is Eleonore, the graph stored
/// <c>aliases: ["eleonore"]</c> and went on calling her Alex's wife in every answer, panel and listing.
/// </para>
/// <para>
/// <see cref="Graph.Rename"/> existed the whole time. It was wired to exactly one caller — <c>WhoAmI</c>, for the
/// owner — so the only person in the memory who could ever be renamed was the person whose memory it is.
/// </para>
/// </summary>
public class ThingsGetTheirRealNames
{
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static (Memory Memory, Graph Graph) Fresh(Scripted model)
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        return (new Memory(graph, model, "test-model") { Now = () => Tuesday }, graph);
    }

    [Fact]
    public async Task A_placeholder_becomes_the_real_name()
    {
        var model = new Scripted(Scripted.Happening(
            new[] { Scripted.Called("Alex's wife", Kinds.Person, "Eleonore") }));

        var (memory, graph) = Fresh(model);
        graph.Upsert("Alex's wife", Kinds.Person);
        memory.Note("my wife is called Eleonore");
        var done = await memory.ReconcileAsync();

        var her = graph.Get("alex-s-wife")!;
        Assert.Equal("Eleonore", her.Name);
        Assert.Contains(done.Wrote, w => w.Contains("Eleonore"));
    }

    [Fact]
    public async Task The_description_still_finds_her_afterwards()
    {
        // Why renaming is safe to let a model do. Nothing is lost: the old name becomes an alias, so every phrase
        // that used to reach her still does — including the one the rest of the graph was built with.
        var model = new Scripted(Scripted.Happening(
            new[] { Scripted.Called("Alex's wife", Kinds.Person, "Eleonore") }));

        var (memory, graph) = Fresh(model);
        graph.Upsert("Alex's wife", Kinds.Person);
        graph.Link("alex", "has wife", "alex-s-wife");
        memory.Note("my wife is called Eleonore");
        await memory.ReconcileAsync();

        Assert.Equal("alex-s-wife", graph.Resolve("Alex's wife").Node?.Id);
        Assert.Equal("alex-s-wife", graph.Resolve("Eleonore").Node?.Id);

        // And the relationship is untouched — it was never keyed on the name.
        Assert.Contains(graph.Around("alex"), step => step.To.Name == "Eleonore");
    }

    [Fact]
    public async Task Being_told_the_name_it_already_has_changes_nothing()
    {
        var model = new Scripted(Scripted.Happening(
            new[] { Scripted.Called("Eleonore", Kinds.Person, "Eleonore") }));

        var (memory, graph) = Fresh(model);
        var her = graph.Upsert("Eleonore", Kinds.Person);
        memory.Note("my wife is called Eleonore");
        var done = await memory.ReconcileAsync();

        Assert.Equal("Eleonore", graph.Get(her.Id)!.Name);
        Assert.Empty(graph.Get(her.Id)!.Aliases);
        Assert.DoesNotContain(done.Wrote, w => w.Contains("is called"));
    }

    [Fact]
    public void The_writer_is_told_it_can_do_this()
    {
        var brief = Reconcile.Brief("anything", retract: false, known: "", self: "alex", kinds: "", wording: "",
            today: Tuesday);

        Assert.Contains("A THING ON FILE UNDER A DESCRIPTION", brief);
        Assert.Contains("`called`", brief);

        // And the schema can carry it, which is the half that was actually missing.
        Assert.Contains("\"called\"", Reconcile.Schema.ToJsonString());
    }
}
