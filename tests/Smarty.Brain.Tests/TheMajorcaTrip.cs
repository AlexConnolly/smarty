using Smarty.Brain;
using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The questions this graph exists to answer, written as the questions rather than as unit tests of the parts.
///
/// <para>
/// Every scenario here came out of a real design conversation about a real trip, and each one is a question a flat list
/// of facts cannot answer: who is coming and how do I know them, why didn't we book that hotel, what has the trip got
/// to do with Palma. If these read like English it is because the point of the thing is to answer English.
/// </para>
/// </summary>
public class TheMajorcaTrip
{
    /// <summary>
    /// The world these tests ask about.
    ///
    /// <para>
    /// Deliberately imperfect, because a graph that only works on tidy data is no use: the two guests are recorded with
    /// DIFFERENT labels for the same idea ("guest" and "going on"), one person has no stated relationship to the user at
    /// all, and one hotel is in the wrong country.
    /// </para>
    /// </summary>
    private static Graph World()
    {
        var g = new Graph();

        var me = g.Upsert("Alex", Kinds.Person, new[] { "me", "self" });
        var emma = g.Upsert("Emma", Kinds.Person);
        var dave = g.Upsert("Dave", Kinds.Person);
        var priya = g.Upsert("Priya", Kinds.Person);
        var marcus = g.Upsert("Marcus", Kinds.Person);

        var trip = g.Upsert("Holiday with my wife", Kinds.Project, new[] { "the holiday", "the majorca trip" });
        var mallorca = g.Upsert("Mallorca", Kinds.Place, new[] { "majorca" });
        var palma = g.Upsert("Palma", Kinds.Place);
        var llaut = g.Upsert("Iberostar Selection Llaut Palma", Kinds.Place, new[] { "the llaut", "the hotel" });
        var stella = g.Upsert("Stella Island", Kinds.Place);
        var crete = g.Upsert("Crete", Kinds.Place);

        // How the user knows each person, in the words that were used. No canonical vocabulary: "wife" and "old friend
        // from uni" are what somebody said, and reading them is the reader's job.
        g.Link(me.Id, "wife", emma.Id);
        g.Link(me.Id, "old friend from uni", dave.Id, note: "met in halls");
        g.Link(me.Id, "friend", priya.Id);
        // Marcus deliberately has no tie to the user — he must still be findable, and the gap must be visible.

        // The same idea, two different labels. Filtering on labels would find one of these and miss the other.
        g.Link(trip.Id, "guest", dave.Id);
        g.Link(trip.Id, "going on", emma.Id);
        g.Link(trip.Id, "going on", priya.Id);
        g.Link(trip.Id, "organising flights", marcus.Id);

        g.Link(trip.Id, "destination", mallorca.Id);
        g.Link(trip.Id, "chosen hotel", llaut.Id, note: "5* adults-only all-inclusive");
        g.Link(llaut.Id, "in", palma.Id);
        g.Link(palma.Id, "in", mallorca.Id);

        g.Link(trip.Id, "considering", stella.Id, note: "the splurge option");
        g.Link(stella.Id, "in", crete.Id);

        g.State(trip.Id, "budget", "~£1,000–1,300pp including flights");
        g.State(llaut.Id, "nightly rate", "~$189");

        return g;
    }

    [Fact]
    public void Who_is_coming_on_the_trip_regardless_of_how_anyone_worded_it()
    {
        // The heart of it. Dave is a "guest", Emma and Priya are "going on", Marcus is "organising flights" — four
        // different labels. Filtering on the label finds one group and silently loses the rest; filtering on the KIND
        // of thing that comes back finds all four.
        var g = World();

        var people = g.Ask(new Ask { About = "the majorca trip", Wanting = Kinds.Person, Hops = 1 });

        Assert.Equal(new[] { "Dave", "Emma", "Marcus", "Priya" }, people.Found.Select(f => f.Node.Name).OrderBy(n => n));
    }

    [Fact]
    public void What_friends_are_coming_is_answered_by_annotation_not_by_a_filter()
    {
        // The question that started this design. "Friend" is not a kind and not a canonical type — it is however the
        // user happened to describe knowing someone. So the graph returns the people with their relationship to the
        // user in the original words, and the reader picks out the friends.
        var g = World();

        var people = g.Ask(new Ask
        {
            About = "the majorca trip",
            Wanting = Kinds.Person,
            Hops = 1,
            Anchors = { "me" },
            Exclude = { Without.Node("me") },
        });

        string TieOf(string name) => people.Found.Single(f => f.Node.Name == name).Anchored.FirstOrDefault().Relation ?? "";

        Assert.Equal("old friend from uni", TieOf("Dave"));
        Assert.Equal("friend", TieOf("Priya"));
        Assert.Equal("wife", TieOf("Emma"));

        // The gap is the important part: Marcus is returned, with nothing claimed about how he is known. A tie-typed
        // filter would have dropped him silently and nobody would have known to ask.
        Assert.Equal("", TieOf("Marcus"));
    }

    [Fact]
    public void Every_answer_says_why_it_is_there()
    {
        // A result you cannot check is a result you have to trust. Each one carries the edge that produced it, in the
        // words that were written.
        var g = World();

        var people = g.Ask(new Ask { About = "the holiday", Wanting = Kinds.Person, Hops = 1 });
        var dave = people.Found.Single(f => f.Node.Name == "Dave");

        Assert.Contains("guest", dave.Because);
        Assert.Contains("Dave", dave.Because);
    }

    [Fact]
    public void The_user_can_be_left_out_without_pretending_they_are_not_connected()
    {
        var g = World();
        var trip = g.Resolve("the majorca trip").Node!;
        g.Link(trip.Id, "going on", g.Resolve("me").Node!.Id);

        var withMe = g.Ask(new Ask { About = "the holiday", Wanting = Kinds.Person, Hops = 1 });
        var withoutMe = g.Ask(new Ask
        {
            About = "the holiday", Wanting = Kinds.Person, Hops = 1,
            Exclude = { Without.Node("me") },
        });

        Assert.Contains("Alex", withMe.Found.Select(f => f.Node.Name));
        Assert.DoesNotContain("Alex", withoutMe.Found.Select(f => f.Node.Name));
    }

    [Fact]
    public void Who_have_I_not_asked_yet_is_answerable_because_absence_is_structural()
    {
        // A subtractive on the PRESENCE of an edge, which a flat store cannot express at all — "not asked" has no row
        // to match on.
        var g = World();
        var me = g.Resolve("me").Node!;
        g.Link(me.Id, "asked about dates", g.Resolve("Dave").Node!.Id);

        var unasked = g.Ask(new Ask
        {
            About = "the holiday",
            Wanting = Kinds.Person,
            Hops = 1,
            Exclude = { Without.Node("me"), Without.Edge("asked about dates") },
        });

        Assert.DoesNotContain("Dave", unasked.Found.Select(f => f.Node.Name));
        Assert.Contains("Priya", unasked.Found.Select(f => f.Node.Name));
        Assert.Contains("Marcus", unasked.Found.Select(f => f.Node.Name));
    }

    [Fact]
    public void What_has_the_trip_got_to_do_with_Palma()
    {
        // Two facts, neither mentioning both ends. The path is the explanation, and it reads as one.
        var g = World();
        var trip = g.Resolve("the holiday").Node!;
        var palma = g.Resolve("Palma").Node!;

        var path = g.Path(trip.Id, palma.Id);

        // Deliberately not asserting WHICH two facts. There are two equally short and equally true answers here — via
        // the chosen hotel, and via the destination — and a graph that preferred one would be applying a ranking nobody
        // asked for and nobody could check. What must hold is that the chain starts where the question did, ends where
        // it was aimed, and is made of facts actually written down.
        Assert.Equal(2, path.Count);
        Assert.Equal(trip.Id, path[0].From.Id);
        Assert.Equal(palma.Id, path[^1].To.Id);
        Assert.All(path, step => Assert.Contains(step.Edge, g.Edges));

        var explanation = string.Join("; ", path.Select(step => step.Sentence()));
        Assert.Contains("Palma", explanation);
        Assert.Contains("Holiday with my wife", explanation);
    }

    [Fact]
    public void A_candidate_in_the_wrong_country_shows_up_as_a_contradiction()
    {
        // Nobody has to notice this. The trip points at two countries, and a place with no path to the destination is
        // a place the trip cannot actually use.
        var g = World();
        var trip = g.Resolve("the holiday").Node!;
        var mallorca = g.Resolve("Mallorca").Node!;

        var places = g.Ask(new Ask { About = "the holiday", Wanting = Kinds.Place, Hops = 2 });
        var reachable = places.Found.Select(f => f.Node.Name).ToList();

        Assert.Contains("Mallorca", reachable);
        Assert.Contains("Crete", reachable);   // the contradiction, visible

        // Being connected is NOT the test, and assuming it was is the mistake worth recording: everything in a graph
        // that is actually used connects eventually, and Stella Island reaches Mallorca in two steps simply by going
        // back out through the trip that is considering it. Connectivity means "these are both in your life".
        Assert.NotEmpty(g.Path(g.Resolve("Stella Island").Node!.Id, mallorca.Id));

        // What "wrong country" means is CONTAINMENT, which is a chain of one particular relationship — and the word for
        // it is not decreed anywhere. It is read off the graph, then followed.
        var words = g.LabelsBetween(Kinds.Place, Kinds.Place);
        Assert.Contains("in", words);

        Assert.NotEmpty(g.Path(g.Resolve("the llaut").Node!.Id, mallorca.Id, along: words));
        Assert.NotEmpty(g.Path(g.Resolve("Stella Island").Node!.Id, g.Resolve("Crete").Node!.Id, along: words));
        Assert.Empty(g.Path(g.Resolve("Stella Island").Node!.Id, mallorca.Id, along: words));

        Assert.NotEmpty(g.Path(trip.Id, mallorca.Id));
    }

    [Fact]
    public void Why_did_we_not_book_that_hotel()
    {
        // The one the whole design turned on: a connection that ended, keeping its reason, and still answerable months
        // later.
        var g = World();
        var trip = g.Resolve("the holiday").Node!;
        var stella = g.Resolve("Stella Island").Node!;

        Assert.True(g.End(trip.Id, "considering", stella.Id, because: "it's in Crete, not Mallorca"));

        // Gone from the live picture...
        var considering = g.Around(trip.Id).Where(s => s.Edge.Label == "considering");
        Assert.Empty(considering);

        // ...but the reason survives, and reads.
        var history = g.History(trip.Id);
        var ended = history.Single(e => e.Label == "considering");
        Assert.Equal(EdgeState.Ended, ended.State);
        Assert.Equal("it's in Crete, not Mallorca", ended.Because);
        Assert.NotNull(ended.Ended);
    }

    [Fact]
    public void Ending_something_without_saying_why_is_refused()
    {
        // The one place a field is mandatory, because it is the field the whole idea rests on. A tidy graph with no
        // story is the outcome worth preventing.
        var g = World();
        var trip = g.Resolve("the holiday").Node!;
        var stella = g.Resolve("Stella Island").Node!;

        Assert.Throws<ArgumentException>(() => g.End(trip.Id, "considering", stella.Id, because: "   "));
    }

    [Fact]
    public void What_should_I_sort_out_before_the_trip_reaches_past_the_trip_itself()
    {
        // Second-order relevance: the useful answer is usually one step beyond the thing asked about. Nobody attached
        // Crete to the trip; it is two hops out, via a hotel somebody is considering.
        var g = World();

        var near = g.Ask(new Ask { About = "the holiday", Hops = 1 }).Found.Select(f => f.Node.Name).ToHashSet();
        var wider = g.Ask(new Ask { About = "the holiday", Hops = 2 }).Found.Select(f => f.Node.Name).ToHashSet();

        Assert.DoesNotContain("Crete", near);
        Assert.Contains("Crete", wider);
        Assert.Contains("Palma", wider);
    }

    [Fact]
    public void Properties_come_only_when_asked_for()
    {
        // A ten-person answer that drags a hundred facts along is unreadable, so the RESULTS' properties are opt-in.
        var g = World();

        var plain = g.Ask(new Ask { About = "the holiday", Hops = 1 });
        var detailed = g.Ask(new Ask { About = "the holiday", Hops = 1, WithProperties = true });

        Assert.All(plain.Found, f => Assert.Empty(f.Properties));
        Assert.Contains(detailed.Found, f => f.Properties.Any(p => p.Label == "nightly rate"));

        // The SUBJECT's own are not opt-in and never were worth making so. One node's facts are bounded however many
        // neighbours come back, and gating them on the neighbour count meant the better connected a thing was, the less
        // it said about itself.
        Assert.Contains(plain.AboutProperties, p => p.Label == "budget");
    }

    [Fact]
    public void The_thing_asked_about_is_described_as_well_as_walked_from()
    {
        // "Tell me about the trip" should not need a second call to learn its budget.
        var g = World();

        var answer = g.Ask(new Ask { About = "the majorca trip", Hops = 1, WithProperties = true });

        Assert.Equal("Holiday with my wife", answer.About!.Name);
        Assert.Contains(answer.AboutProperties, p => p.Label == "budget");
    }

    [Fact]
    public void An_unknown_subject_is_a_clear_miss_rather_than_an_empty_answer()
    {
        // An empty result and "I have never heard of this" are different answers, and conflating them is how a system
        // ends up confidently knowing nothing.
        var g = World();

        var answer = g.Ask(new Ask { About = "the Reykjavik trip", Wanting = Kinds.Person });

        Assert.Null(answer.About);
        Assert.False(answer.Resolved);
        Assert.Empty(answer.Found);
    }

    [Fact]
    public void Two_people_of_the_same_name_produce_a_question_not_a_guess()
    {
        // Picking one is how a graph starts producing confident nonsense. The cost of asking is one question.
        var g = World();
        g.Add("Emma", Kinds.Person, new[] { "emma@work.example" });

        var answer = g.Ask(new Ask { About = "Emma" });

        Assert.False(answer.Resolved);
        Assert.True(answer.Ambiguous.Count >= 2);
    }

    [Fact]
    public void The_words_in_use_are_reported_rather_than_decreed()
    {
        // No invented vocabulary of relationship types. What a query may rely on is what is actually written down.
        var g = World();

        var between = g.LabelsBetween(Kinds.Person, Kinds.Person);

        Assert.Contains("wife", between);
        Assert.Contains("old friend from uni", between);
        Assert.Contains("friend", between);
        Assert.DoesNotContain("destination", between);
    }
}
