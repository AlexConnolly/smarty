using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The question the brief asks of every sentence, and why it is a question rather than a list.
///
/// <para>
/// It used to be two lists of categories: anything TIME-SPECIFIC was out, and anything OUTSIDE FORCES CAN CHANGE was
/// out. The second was right. The first said "a date" — and so refused a birthday, which never stops being true and
/// which nothing but this person can tell you, and refused who is coming to a wedding, which is a social connection
/// wearing an occasion. It had already eaten a real fact once ("my favourite day to relax is Sunday", recorded as
/// nothing, twice) and been patched with a special case rather than fixed.
/// </para>
/// <para>
/// Both lists were reaching for the same thing: <b>could anybody but this person have told you?</b> An opening time,
/// a price, whether they are free on Thursday — a search answers the first two and their own calendar answers the
/// third, and a copy here goes quietly wrong while still reading as current. A birthday, a brother's taste, who is
/// coming to the wedding: nothing else holds those. That is the whole of what this is for.
/// </para>
/// </summary>
public class WhatOnlyTheyCanTellYou
{
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static (Memory Memory, Graph Graph) Fresh(Scripted model)
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        return (new Memory(graph, model, "test-model") { Now = () => Tuesday }, graph);
    }

    [Fact]
    public async Task A_birthday_is_a_date_and_belongs_here()
    {
        // The plainest case the old rule refused. It is a date, it never stops being true, and there is nowhere else
        // to look it up — you cannot search for when somebody's brother was born.
        var model = new Scripted(Scripted.Plan(
            Scripted.Fact("Matt", "birthday", "14 March", fromKind: Kinds.Person, lasts: Lifetimes.Forever)));

        var (memory, graph) = Fresh(model);
        memory.Note("Matt's birthday is on 14 March");
        var done = await memory.ReconcileAsync();

        Assert.Single(done.Wrote);
        Assert.Contains(graph.Properties("matt"), e => e.Value == "14 March");
    }

    [Fact]
    public async Task Who_is_coming_to_something_is_a_social_connection_not_a_diary_entry()
    {
        // The old rule read this as "an appointment" and dropped it. What it actually states is who stands where in
        // this person's world — and after the day passes it is still true that Matt went.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Matt", "coming to", "Sarah's wedding", fromKind: Kinds.Person, toKind: "occasion",
                lasts: Lifetimes.WhileItHolds)));

        var (memory, graph) = Fresh(model);
        memory.Note("Matt is coming to Sarah's wedding");
        await memory.ReconcileAsync();

        Assert.Contains(graph.Around("matt"), step => step.To.Name == "Sarah's wedding");
    }

    [Fact]
    public void The_brief_asks_the_question_instead_of_listing_the_categories()
    {
        var brief = Reconcile.Brief("anything", retract: false, known: "", self: "alex", kinds: "", wording: "",
            today: Tuesday);

        Assert.Contains("COULD ANYBODY BUT THIS PERSON HAVE TOLD YOU", brief);
        Assert.Contains("WHAT CAN BE LOOKED UP IS OUT", brief);
        Assert.Contains("THIS IS NOT A MAP OF THE WORLD", brief);

        // A birthday and who is coming to something are named as belonging, because both were refused by the rule
        // this replaced and a brief that only states a principle leaves the old reading available.
        Assert.Contains("birthday", brief);
        Assert.Contains("coming to the wedding", brief);

        // And the categories are gone. "A date" as a reason to refuse is what cost the birthday.
        Assert.DoesNotContain("ANYTHING TIME-SPECIFIC", brief);
        Assert.DoesNotContain("not even with the time attached", brief);
    }

    [Fact]
    public void What_is_looked_up_still_leaves_the_way_back_to_it()
    {
        // The valuable half of the rule that WAS right, and it has to survive the rewrite: refusing to copy the
        // opening hours is only useful if the café itself is recorded, so the thing that can look them up knows
        // which café to ask about.
        var brief = Reconcile.Brief("anything", retract: false, known: "", self: "alex", kinds: "", wording: "",
            today: Tuesday);

        Assert.Contains("Record the way BACK", brief);
        Assert.Contains("Opening hours", brief);
        Assert.Contains("free on Thursday", brief);
    }
}
