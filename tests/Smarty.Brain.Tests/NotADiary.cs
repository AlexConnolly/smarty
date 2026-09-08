using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The brain is not a diary, and it used to try to be.
///
/// <para>
/// It is kept out STRUCTURALLY rather than by instruction: the schema a write is answered with has no field for a
/// moment, so a scheduled thing cannot be expressed at all. Two whole files of tests used to assert the opposite —
/// that an appointment took its own time, that two people going shared one moment, that a stated date landed on the
/// fact — and they were replaced by this one, because the machinery they tested is gone rather than discouraged.
/// </para>
/// <para>
/// Which is why the brief no longer needs a rule against dates, and why it was harmful when it had one: with no way
/// to express a schedule, "anything time-specific is out" could only bite the things that are NOT schedules — a
/// birthday, who is coming to a wedding — and it did. See WhatOnlyTheyCanTellYou.
/// </para>
/// <para>
/// The diary already exists elsewhere: the schedule store holds standing work, list items carry dates, and the agenda
/// feed answers "what is on today". A second copy in here was duplication that went wrong by the following Monday, and
/// nothing in the app ever read it.
/// </para>
/// </summary>
public class NotADiary
{
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static (Memory Memory, Graph Graph) Fresh(Scripted model)
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        return (new Memory(graph, model, "test-model") { Now = () => Tuesday }, graph);
    }

    [Fact]
    public void There_is_no_kind_for_something_that_happens()
    {
        // An event was one of the system kinds and the only one that required a field. Both are gone: a kind that exists
        // is a kind the writer will file things under.
        Assert.DoesNotContain("event", Kinds.System, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_write_has_nowhere_to_put_a_date()
    {
        // Structural, not advisory. The schema the writer answers with has no field for a moment, on a write or on a
        // thing — so a dated fact cannot be expressed, let alone stored.
        var schema = Reconcile.Schema.ToJsonString();

        Assert.DoesNotContain("\"when\"", schema);
        Assert.DoesNotContain("\"start\"", schema);
        Assert.DoesNotContain("\"finish\"", schema);
    }

    [Fact]
    public void The_brief_says_which_half_of_a_sentence_survives()
    {
        var brief = Reconcile.Brief("anything", retract: false, known: "", self: "alex", kinds: "", wording: "",
            today: Tuesday);

        Assert.Contains("WHAT CAN BE LOOKED UP IS OUT", brief);
        // And the distinction that decides the common case: the booking goes, the preference stays.
        Assert.Contains("favourite restaurant", brief);
    }

    [Fact]
    public async Task A_preference_survives_a_sentence_that_also_states_a_plan()
    {
        // The everyday shape of it: somebody says where they are going AND what they think of the place. One half is a
        // diary entry and one half is a memory, and the sentence does not separate them — the reading does.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "favourite restaurant", "Fonda", fromKind: Kinds.Person, toKind: Kinds.Place,
                lasts: Lifetimes.WhileItHolds)));

        var (memory, graph) = Fresh(model);
        memory.Note("We are at Fonda on Friday at 6 — it is my favourite restaurant.");
        var done = await memory.ReconcileAsync();

        Assert.Single(done.Wrote);
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "fonda");
        // And nothing anywhere is holding Friday.
        Assert.All(graph.Edges, e => Assert.DoesNotContain("Friday", e.Value ?? ""));
    }

    [Fact]
    public async Task A_time_stated_on_a_fact_is_simply_not_read()
    {
        // A writer that keeps offering a moment — from habit, or from an older schema — gets it dropped rather than
        // stored. Nothing in what comes back is consulted for a date.
        var model = new Scripted(
            "{\"writes\":[{\"from\":\"Alex\",\"fromKind\":\"person\",\"label\":\"swims at\"," +
            "\"to\":\"London Fields Lido\",\"toKind\":\"place\",\"when\":\"2026-08-22T18:00\"," +
            "\"lasts\":\"until-it-changes\"}]}");

        var (memory, graph) = Fresh(model);
        memory.Note("I swim at London Fields Lido.");
        await memory.ReconcileAsync();

        var step = Assert.Single(graph.Around("alex"));
        Assert.Equal("london-fields-lido", step.To.Id);
        Assert.Null(step.Edge.When);
    }
}
