using Smarty.Brain;
using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// The invariants that decide whether this graph is still trustworthy in a year.
///
/// <para>
/// Where the behavioural tests ask questions, these hold the structure to account: that a relationship cannot silently
/// eat another one, that identity does not fork, that a merge leaves nothing broken behind it, and that nothing is ever
/// destroyed. Each of these has a specific failure it was written against, and each failure is quiet — which is why
/// they are worth testing rather than trusting.
/// </para>
/// </summary>
public class GraphIntegrity
{
    // ---- identity, once somebody has answered ----

    [Fact]
    public void Saying_two_things_are_different_takes_away_the_name_they_were_confused_over()
    {
        // The state this was written against: Alex, and Alex's wife who also answered to "alex". Every question that
        // resolved "me" found two candidates and refused to answer — and it kept refusing AFTER being told they are
        // different people, because nothing removed the shared name. The graph knew they were not the same and still
        // could not tell them apart, which is worse than never having asked.
        var g = new Graph();
        var alex = g.Upsert("Alex", Kinds.Person, new[] { "me" });
        var wife = g.Upsert("Alex's wife", Kinds.Person, new[] { "alex" });

        Assert.True(g.NotTheSame(alex.Id, wife.Id));

        Assert.DoesNotContain(wife.Aliases, a => Graph.Normalise(a) == "alex");
        Assert.Equal(alex.Id, g.Resolve("alex").Node?.Id);

        // Her own name is untouched, and so is his.
        Assert.Equal("Alex's wife", wife.Name);
        Assert.Equal("Alex", alex.Name);
    }

    [Fact]
    public void Two_people_who_really_are_both_called_Matt_keep_their_names()
    {
        // The limit of it. A borrowed name goes; a real one cannot, because the ambiguity is real and a question
        // asking which Matt is the right behaviour rather than a fault.
        var g = new Graph();
        var one = g.Upsert("Matt", Kinds.Person);
        var two = g.Upsert("Matt Brennan", Kinds.Person, new[] { "matt" });

        g.NotTheSame(one.Id, two.Id);

        Assert.Equal("Matt", one.Name);
        Assert.DoesNotContain(two.Aliases, a => Graph.Normalise(a) == "matt");
    }

    // ---- the slot rule, which is the difference between a graph and a graph that eats itself ----

    [Fact]
    public void A_property_taking_a_new_value_replaces_the_old_one()
    {
        // What keeps the store deterministic: one active answer to "what is the price".
        var g = new Graph();
        var lp = g.Upsert("Kingfishr signed LP", Kinds.Thing);

        g.State(lp.Id, "price", "£150 Buy It Now");
        g.State(lp.Id, "price", "£140 Buy It Now");

        var active = g.Properties(lp.Id).Where(p => p.Label == "price").ToList();
        Assert.Single(active);
        Assert.Equal("£140 Buy It Now", active[0].Value);

        // And the old value is retired rather than destroyed.
        var history = g.History(lp.Id).Where(e => e.Label == "price").ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, e => e.State == EdgeState.Superseded && e.Value.Contains("150"));
    }

    [Fact]
    public void Two_relationships_with_the_same_label_do_not_supersede_each_other()
    {
        // THE bug this design exists to avoid. Under a property's slot rule, "the trip involves Emma" followed by "the
        // trip involves Dave" would leave only Dave — and "who is coming" would answer wrongly, for ever, silently.
        var g = new Graph();
        var trip = g.Upsert("Trip", Kinds.Project);
        var emma = g.Upsert("Emma", Kinds.Person);
        var dave = g.Upsert("Dave", Kinds.Person);

        g.Link(trip.Id, "involves", emma.Id);
        g.Link(trip.Id, "involves", dave.Id);

        var coming = g.Around(trip.Id).Select(s => s.To.Name).OrderBy(n => n);
        Assert.Equal(new[] { "Dave", "Emma" }, coming);
    }

    [Fact]
    public void The_same_relationship_asserted_twice_is_one_edge_refreshed()
    {
        var g = new Graph();
        var trip = g.Upsert("Trip", Kinds.Project);
        var emma = g.Upsert("Emma", Kinds.Person);

        var first = g.Link(trip.Id, "involves", emma.Id);
        var again = g.Link(trip.Id, "involves", emma.Id);

        Assert.Equal(first.Id, again.Id);
        Assert.Single(g.Around(trip.Id));
    }

    [Fact]
    public void Re_pointing_the_same_label_at_a_different_node_keeps_both_as_distinct_slots()
    {
        // "chosen hotel" is arguably a property-shaped relationship, and a caller may well want the old one gone — but
        // that is an End, not a side effect. Nothing is retired without being asked.
        var g = new Graph();
        var trip = g.Upsert("Trip", Kinds.Project);
        var a = g.Upsert("Hotel A", Kinds.Place);
        var b = g.Upsert("Hotel B", Kinds.Place);

        g.Link(trip.Id, "chosen hotel", a.Id);
        g.Link(trip.Id, "chosen hotel", b.Id);

        Assert.Equal(2, g.Around(trip.Id).Count);

        g.End(trip.Id, "chosen hotel", a.Id, because: "changed our minds after reading reviews");
        Assert.Single(g.Around(trip.Id));
    }

    // ---- identity ----

    [Fact]
    public void A_name_and_its_aliases_resolve_to_one_node_whatever_the_punctuation()
    {
        // "holiday with my wife" and "holiday-with-my-wife" forked into two nodes in the store this replaces. Matching
        // on letters and digits alone is what stops that.
        var g = new Graph();
        var trip = g.Upsert("Holiday with my wife", Kinds.Project, new[] { "the holiday" });

        Assert.Equal(trip.Id, g.Resolve("holiday with my wife").Node!.Id);
        Assert.Equal(trip.Id, g.Resolve("holiday-with-my-wife").Node!.Id);
        Assert.Equal(trip.Id, g.Resolve("  Holiday With My Wife  ").Node!.Id);
        Assert.Equal(trip.Id, g.Resolve("the holiday").Node!.Id);
    }

    [Fact]
    public void Resolution_never_guesses_between_two_candidates()
    {
        var g = new Graph();
        g.Upsert("Emma", Kinds.Person, new[] { "emma@home.example" });
        g.Add("Emma", Kinds.Person, new[] { "emma@work.example" });

        var resolved = g.Resolve("Emma");

        Assert.False(resolved.Found);
        Assert.True(resolved.IsAmbiguous);
        Assert.Equal(2, resolved.Ambiguous.Count);

        // But an alias belonging to only one of them is unambiguous.
        Assert.True(g.Resolve("emma@work.example").Found);
    }

    [Fact]
    public void Names_may_repeat_for_people_and_may_not_for_anything_else()
    {
        // Two friends called Emma is ordinary. Two places called Palma is always a mistake. And the refusal belongs to
        // the graph, so asking for a second Palma outright gets the first one back rather than a duplicate.
        var g = new Graph();

        var emmaOne = g.Upsert("Emma", Kinds.Person, new[] { "emma@home.example" });
        var emmaTwo = g.Add("Emma", Kinds.Person, new[] { "emma@work.example" });
        Assert.NotEqual(emmaOne.Id, emmaTwo.Id);

        var palmaOne = g.Upsert("Palma", Kinds.Place);
        Assert.Equal(palmaOne.Id, g.Upsert("Palma", Kinds.Place).Id);
        Assert.Equal(palmaOne.Id, g.Add("Palma", Kinds.Place).Id);
    }

    [Fact]
    public void A_bare_repeat_of_a_name_means_the_one_already_known()
    {
        // The half that keeps the graph from forking. Mentioning Emma again means the Emma on file, and anything else
        // given along with her is another way to find her — because the graph genuinely cannot tell "em" (a nickname for
        // the Emma it knows) from "emma@work.example" (a different Emma), so it must not try. A second Emma is an
        // explicit act, and only the caller can perform it.
        var g = new Graph();

        var first = g.Upsert("Emma", Kinds.Person);
        Assert.Equal(first.Id, g.Upsert("Emma", Kinds.Person).Id);
        Assert.Equal(first.Id, g.Upsert("Emma", Kinds.Person, new[] { "em" }).Id);
        Assert.Equal(first.Id, g.Resolve("em").Node!.Id);
        Assert.Equal(first.Id, g.Upsert("em", Kinds.Person).Id);
        Assert.Single(g.Nodes);
    }

    [Fact]
    public void Writing_an_already_ambiguous_name_asks_rather_than_adding_a_third()
    {
        // Two Emmas is ordinary. A third invented because a write could not tell which was meant is exactly the
        // confident nonsense this design refuses — so the write fails, carrying the candidates so the caller can ask.
        var g = new Graph();
        var home = g.Upsert("Emma", Kinds.Person, new[] { "emma@home.example" });
        var work = g.Add("Emma", Kinds.Person, new[] { "emma@work.example" });

        var asked = Assert.Throws<AmbiguousName>(() => g.Upsert("Emma", Kinds.Person));

        Assert.Equal(2, asked.Candidates.Count);
        Assert.Contains(home.Id, asked.Message);
        Assert.Contains(work.Id, asked.Message);
        Assert.Equal(2, g.Nodes.Count);

        // And a detail that names one of them settles it without a question.
        Assert.Equal(work.Id, g.Upsert("Emma", Kinds.Person, new[] { "emma@work.example" }).Id);
    }

    [Fact]
    public void The_same_name_under_different_kinds_is_two_things()
    {
        // Palma the city and Palma the hotel-name are not the same thing, and a globally unique name would have forced
        // one of them to be contorted.
        var g = new Graph();

        var place = g.Upsert("Kingfishr", Kinds.Topic);
        var thing = g.Upsert("Kingfishr", Kinds.Thing);

        Assert.NotEqual(place.Id, thing.Id);
        Assert.Equal(place.Id, g.Resolve("Kingfishr", Kinds.Topic).Node!.Id);
        Assert.Equal(thing.Id, g.Resolve("Kingfishr", Kinds.Thing).Node!.Id);
    }

    [Fact]
    public void An_ids_kind_can_change_without_breaking_a_single_edge()
    {
        // The reason the kind is not in the id. The store this replaces held "topic:holiday with my wife" AND
        // "project:holiday-with-my-wife" — one thing understood two ways — and re-classifying would have orphaned every
        // edge pointing at it.
        var g = new Graph();
        var trip = g.Upsert("The holiday", Kinds.Topic);
        var mallorca = g.Upsert("Mallorca", Kinds.Place);
        g.Link(trip.Id, "destination", mallorca.Id);

        var id = trip.Id;
        trip.Kind = Kinds.Project;

        Assert.Equal(id, trip.Id);
        Assert.Single(g.Around(trip.Id));
        Assert.Equal("Mallorca", g.Around(trip.Id)[0].To.Name);
    }

    // ---- merging ----

    [Fact]
    public void Merging_repoints_every_edge_in_both_directions()
    {
        var g = new Graph();
        var loser = g.Upsert("Holiday with my wife", Kinds.Topic);
        var winner = g.Upsert("Holiday with my wife", Kinds.Project, new[] { "the holiday" });
        var mallorca = g.Upsert("Mallorca", Kinds.Place);
        var emma = g.Upsert("Emma", Kinds.Person);

        g.Link(loser.Id, "destination", mallorca.Id);   // outbound from the loser
        g.Link(emma.Id, "going on", loser.Id);          // inbound to the loser

        Assert.True(g.Merge(loser.Id, winner.Id));

        var ring = g.Around(winner.Id).Select(s => s.To.Name).OrderBy(n => n);
        Assert.Equal(new[] { "Emma", "Mallorca" }, ring);
        Assert.Empty(g.Around(loser.Id).Where(s => s.From.Id == loser.Id && s.To.Id != winner.Id));
    }

    [Fact]
    public void A_merged_away_id_still_resolves_so_nothing_holding_it_breaks()
    {
        // A panel, a task or an old conversation may still hold the dead id. Deleting the loser is how one mess becomes
        // two.
        var g = new Graph();
        var loser = g.Upsert("Holiday", Kinds.Topic);
        var winner = g.Upsert("Holiday with my wife", Kinds.Project);
        var deadId = loser.Id;

        g.Merge(loser.Id, winner.Id);

        Assert.Equal(winner.Id, g.Get(deadId)!.Id);
        Assert.DoesNotContain(winner.Id, g.Nodes.Where(n => n.Id == deadId).Select(n => n.Id));
    }

    [Fact]
    public void A_merge_takes_the_losers_names_with_it()
    {
        // Otherwise every phrase that used to find it stops working the moment it is tidied up.
        var g = new Graph();
        var loser = g.Upsert("The Majorca thing", Kinds.Topic, new[] { "majorca thing" });
        var winner = g.Upsert("Holiday with my wife", Kinds.Project);

        g.Merge(loser.Id, winner.Id);

        Assert.Equal(winner.Id, g.Resolve("The Majorca thing").Node!.Id);
        Assert.Equal(winner.Id, g.Resolve("majorca thing").Node!.Id);
    }

    // ---- what catches the rot ----

    [Fact]
    public void Duplicates_are_found_by_structure_not_only_by_name()
    {
        // The check that would have caught the real duplicate: "holiday with my wife" and "holiday-with-my-wife" are
        // different strings, so no naming rule could refuse them. Pointing at the same neighbours gives them away.
        var g = new Graph();
        var a = g.Upsert("Holiday with my wife", Kinds.Project);
        var b = g.Upsert("Our Mallorca break", Kinds.Topic);
        var mallorca = g.Upsert("Mallorca", Kinds.Place);
        var hotel = g.Upsert("Iberostar Llaut", Kinds.Place);

        foreach (var node in new[] { a, b })
        {
            g.Link(node.Id, "destination", mallorca.Id);
            g.Link(node.Id, "hotel", hotel.Id);
        }

        var found = g.Duplicates();
        Assert.Contains(found, d =>
            (d.A.Id == a.Id && d.B.Id == b.Id) || (d.A.Id == b.Id && d.B.Id == a.Id));
    }

    [Fact]
    public void Two_nodes_sharing_a_single_neighbour_are_not_called_duplicates()
    {
        // Everyone in a household shares an address. One shared neighbour is not evidence of anything.
        var g = new Graph();
        var a = g.Upsert("Dave", Kinds.Person);
        var b = g.Upsert("Priya", Kinds.Person);
        var london = g.Upsert("London", Kinds.Place);

        g.Link(a.Id, "lives in", london.Id);
        g.Link(b.Id, "lives in", london.Id);

        Assert.Empty(g.Duplicates());
    }

    [Fact]
    public void An_edge_pointing_at_nothing_is_reported()
    {
        // The one cost of storing edges separately from nodes: a reference can dangle. Cheap to detect, and it has to
        // be detected or a traversal quietly returns less than the truth.
        var g = new Graph();
        var trip = g.Upsert("Trip", Kinds.Project);
        var hotel = g.Upsert("Hotel", Kinds.Place);
        g.Link(trip.Id, "hotel", hotel.Id);

        Assert.Empty(g.Dangling());

        g.Edges.Single(e => e.IsRelationship).To = "a-node-that-never-existed";
        Assert.Single(g.Dangling());
    }

    // ---- the graph densifying itself ----

    [Fact]
    public void Creating_a_node_wires_up_facts_already_written_in_words()
    {
        // The mechanism that makes this get denser through ordinary use rather than needing a gardening session: a text
        // fact naming something becomes a real connection the moment that something exists.
        var g = new Graph();
        var trip = g.Upsert("Trip", Kinds.Project);
        g.State(trip.Id, "chosen hotel", "Iberostar Selection Llaut Palma");

        Assert.Empty(g.Around(trip.Id));   // nothing to point at yet

        var hotel = g.Upsert("Iberostar Selection Llaut Palma", Kinds.Place);

        var ring = g.Around(trip.Id);
        Assert.Single(ring);
        Assert.Equal(hotel.Id, ring[0].To.Id);
    }

    [Fact]
    public void Retro_linking_matches_exactly_and_never_approximately()
    {
        // A fuzzy match here would wire "cycling in France" to the cycling topic, and a confidently wrong edge is worse
        // than a missing one — you never see the edge, only the conclusion it produces.
        var g = new Graph();
        var me = g.Upsert("Alex", Kinds.Person);
        g.State(me.Id, "did in July", "cycling in France");

        g.Upsert("cycling", Kinds.Topic);

        Assert.Empty(g.Around(me.Id));
    }

    // ---- walking ----

    [Fact]
    public void A_walk_reads_correctly_in_the_direction_it_was_walked()
    {
        // Crossed either way, it reports the fact as RECORDED — which is the only reading that cannot lie. An earlier
        // version built English instead and inverted every verb-like label: "Me — going on → Majorca trip", walked from
        // the trip, read as "Majorca trip is the going on of Me", stating the opposite of the truth.
        var g = new Graph();
        var alex = g.Upsert("Alex", Kinds.Person);
        var cycling = g.Upsert("cycling", Kinds.Topic);
        g.Link(alex.Id, "hobby", cycling.Id);

        Assert.Equal("Alex — hobby → cycling", g.Around(alex.Id)[0].Sentence());
        Assert.Equal("Alex — hobby → cycling", g.Around(cycling.Id)[0].Sentence());

        // And the walk direction is still known, for anyone who needs it.
        Assert.False(g.Around(alex.Id)[0].Backwards);
        Assert.True(g.Around(cycling.Id)[0].Backwards);
    }

    [Fact]
    public void A_topic_can_say_who_cares_about_it()
    {
        // The inbound direction, which is the entire reason edges are their own records. Three cyclists recorded and
        // cycling still could not name them under the old shape.
        var g = new Graph();
        var cycling = g.Upsert("cycling", Kinds.Topic);
        foreach (var name in new[] { "Alex", "Emma", "Dave" })
            g.Link(g.Upsert(name, Kinds.Person).Id, "hobby", cycling.Id);

        var cyclists = g.Ask(new Ask { About = "cycling", Wanting = Kinds.Person, Hops = 1 });

        Assert.Equal(3, cyclists.Found.Count);
    }

    [Fact]
    public void A_walk_visits_nothing_twice_and_stops_where_it_is_told()
    {
        var g = new Graph();
        var a = g.Upsert("A", Kinds.Topic);
        var b = g.Upsert("B", Kinds.Topic);
        var c = g.Upsert("C", Kinds.Topic);

        // A ring, so an unguarded walk would go round for ever.
        g.Link(a.Id, "next", b.Id);
        g.Link(b.Id, "next", c.Id);
        g.Link(c.Id, "next", a.Id);

        var one = g.Neighbourhood(a.Id, hops: 1);
        var two = g.Neighbourhood(a.Id, hops: 2);

        Assert.Equal(2, one.Count);                       // B and C, both one step from A
        Assert.Equal(2, two.Count);                       // and there is nothing else to find
        Assert.Equal(2, two.Select(r => r.Node.Id).Distinct().Count());
    }

    [Fact]
    public void A_crowd_of_the_wrong_kind_cannot_squeeze_out_the_answer()
    {
        // The cap bounds the ANSWER, not the search. Eighty topics are attached before the four people, so a filter
        // applied after the walk would find the cap already full of topics and report that nobody is coming.
        var g = new Graph();
        var trip = g.Upsert("Trip", Kinds.Project);

        for (var n = 0; n < 80; n++)
            g.Link(trip.Id, "note", g.Upsert($"Thought {n}", Kinds.Topic).Id);

        foreach (var name in new[] { "Alex", "Emma", "Dave", "Priya" })
            g.Link(trip.Id, "going on", g.Upsert(name, Kinds.Person).Id);

        var people = g.Ask(new Ask { About = "Trip", Wanting = Kinds.Person, Hops = 1 });

        Assert.Equal(4, people.Found.Count);
        Assert.False(people.Truncated);

        // And a genuine overflow says so rather than looking complete.
        var topics = g.Ask(new Ask { About = "Trip", Wanting = Kinds.Topic, Hops = 1, Most = 10 });
        Assert.Equal(10, topics.Found.Count);
        Assert.True(topics.Truncated);
    }

    [Fact]
    public void A_walk_is_capped_however_big_the_graph_is()
    {
        var g = new Graph();
        var hub = g.Upsert("Hub", Kinds.Topic);
        for (var i = 0; i < 50; i++)
            g.Link(hub.Id, "spoke", g.Upsert($"Spoke {i}", Kinds.Topic).Id);

        Assert.Equal(10, g.Neighbourhood(hub.Id, hops: 1, max: 10).Count);
    }

    [Fact]
    public void A_path_is_the_shortest_one_and_is_made_only_of_real_edges()
    {
        var g = new Graph();
        var a = g.Upsert("A", Kinds.Topic);
        var b = g.Upsert("B", Kinds.Topic);
        var c = g.Upsert("C", Kinds.Topic);
        var d = g.Upsert("D", Kinds.Topic);

        g.Link(a.Id, "long way", b.Id);
        g.Link(b.Id, "long way", c.Id);
        g.Link(c.Id, "long way", d.Id);
        g.Link(a.Id, "short cut", d.Id);

        var path = g.Path(a.Id, d.Id);

        Assert.Single(path);
        Assert.Equal("short cut", path[0].Edge.Label);
    }

    [Fact]
    public void Unconnected_things_are_reported_as_unconnected_rather_than_guessed_at()
    {
        // Never infer a path from similarity. A synthesised chain is a plausible fiction, and the value of this whole
        // design is that the reasoning can be read and checked.
        var g = new Graph();
        var a = g.Upsert("Trip", Kinds.Project);
        var b = g.Upsert("Roomba", Kinds.Thing);

        Assert.Empty(g.Path(a.Id, b.Id));
    }

    [Fact]
    public void An_ended_edge_is_left_out_of_walks_but_kept_for_the_asking()
    {
        var g = new Graph();
        var trip = g.Upsert("Trip", Kinds.Project);
        var emma = g.Upsert("Emma", Kinds.Person);
        g.Link(trip.Id, "going on", emma.Id);
        g.End(trip.Id, "going on", emma.Id, because: "she can't get the time off");

        Assert.Empty(g.Around(trip.Id));
        Assert.Single(g.Around(trip.Id, includeEnded: true));
        Assert.Empty(g.Ask(new Ask { About = "Trip", Wanting = Kinds.Person }).Found);
    }

    // ---- persistence ----

    [Fact]
    public void Everything_survives_a_reload_including_the_history()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brain-{Guid.NewGuid():N}");
        try
        {
            var first = new Graph(dir);
            var trip = first.Upsert("Trip", Kinds.Project, new[] { "the trip" });
            var emma = first.Upsert("Emma", Kinds.Person);
            first.Link(trip.Id, "going on", emma.Id);
            first.State(trip.Id, "budget", "£2,000");
            first.End(trip.Id, "going on", emma.Id, because: "no time off");

            var reopened = new Graph(dir);

            Assert.Equal(trip.Id, reopened.Resolve("the trip").Node!.Id);
            Assert.Contains(reopened.Properties(trip.Id), p => p.Label == "budget");
            Assert.Contains(reopened.History(trip.Id), e => e.Because == "no time off");

            // And the next edge id continues rather than colliding with a reloaded one.
            var dave = reopened.Upsert("Dave", Kinds.Person);
            var fresh = reopened.Link(trip.Id, "going on", dave.Id);
            Assert.DoesNotContain(fresh.Id, reopened.Edges.Where(e => e != fresh).Select(e => e.Id));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_graph_with_no_directory_still_works_entirely_in_memory()
    {
        // Tests, and anything that wants a scratch graph, should not need a filesystem.
        var g = new Graph();
        var a = g.Upsert("A", Kinds.Topic);
        var b = g.Upsert("B", Kinds.Topic);
        g.Link(a.Id, "next", b.Id);

        Assert.Single(g.Around(a.Id));
    }

    [Fact]
    public void A_fact_needs_a_label_and_a_real_node_at_each_end()
    {
        var g = new Graph();
        var a = g.Upsert("A", Kinds.Topic);

        Assert.Throws<ArgumentException>(() => g.State(a.Id, "", "something"));
        Assert.Throws<ArgumentException>(() => g.State("nobody", "label", "something"));
        Assert.Throws<ArgumentException>(() => g.Link(a.Id, "next", "nobody"));
    }
}
