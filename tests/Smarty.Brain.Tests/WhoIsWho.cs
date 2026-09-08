using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// Two names, one person — or two people.
///
/// <para>
/// "My brother is Matthew", and a week later "Matt says he can drive". Both options open to the graph were wrong and
/// neither left a trace: invent a second person and every question about him finds half the answer, or decide they are
/// one and start recording facts against somebody who may not exist. The name is not evidence either way — plenty of
/// people know two Matts.
/// </para>
/// <para>
/// So the fact lands, the node carries the doubt, and the doubt is answerable exactly once. Yes merges them and the
/// shortened name becomes another name for the survivor, so it resolves for ever afterwards. No is recorded too, which
/// is the half that makes asking bearable: a question that comes back after being answered is one nobody answers twice.
/// </para>
/// </summary>
public class WhoIsWho
{
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static (Memory Memory, Graph Graph) Fresh(Scripted model)
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "self", "i" });
        return (new Memory(graph, model, "test-model") { Now = () => Tuesday }, graph);
    }

    // ── known as ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_name_it_answers_to_finds_it()
    {
        // The mechanism the rest of this rests on, and it was already here — what was missing was any way to SAY it.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Matthew", Kinds.Person, new[] { "matt" });

        Assert.Equal("matthew", graph.Resolve("Matt").Node?.Id);
        Assert.Equal("matthew", graph.Resolve("MATT").Node?.Id);
    }

    [Fact]
    public async Task A_nickname_in_the_sentence_is_recorded_as_another_name()
    {
        // "My brother Matthew — I call him Matt" used to record Matthew and then invent a stranger the first time
        // anybody said Matt, because a second name had nowhere to go.
        var model = new Scripted(Scripted.Happening(
            new[] { Scripted.Known("Matthew", Kinds.Person, "Matt", "Matty") },
            Scripted.Link("Alex", "brother of", "Matthew", fromKind: Kinds.Person, toKind: Kinds.Person,
                lasts: Lifetimes.Forever)));

        var (memory, graph) = Fresh(model);
        memory.Note("my brother Matthew, I call him Matt");
        await memory.ReconcileAsync();

        Assert.Equal("matthew", graph.Resolve("Matt").Node?.Id);
        Assert.Equal("matthew", graph.Resolve("Matty").Node?.Id);
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "matthew");
    }

    [Fact]
    public async Task A_name_already_known_never_becomes_a_second_person()
    {
        // Once the alias is on file the question does not arise at all — which is the whole point of recording it.
        var model = new Scripted(
            Scripted.Happening(
                new[] { Scripted.Known("Matthew", Kinds.Person, "Matt") },
                Scripted.Link("Alex", "brother of", "Matthew", fromKind: Kinds.Person, toKind: Kinds.Person)),
            Scripted.Plan(
                Scripted.Fact("Matt", "drives", "a van", fromKind: Kinds.Person)));

        var (memory, graph) = Fresh(model);
        memory.Note("my brother Matthew, known as Matt");
        await memory.ReconcileAsync();

        memory.Note("Matt drives a van");
        var done = await memory.ReconcileAsync();

        Assert.Single(graph.Nodes.Where(n => n.Kind == Kinds.Person && n.Id != "alex"));
        Assert.Empty(done.Questions);
        Assert.Contains(graph.Properties("matthew"), e => e.Value == "a van");
    }

    [Fact]
    public void A_relationship_is_not_another_name()
    {
        // Found within a day of `also` existing: the writer put "brother" and "sister" in it, from two sentences that
        // had stated the relationship perfectly well. The damage is quiet and total — "brother" then resolves to
        // Matthew, so the next person's brother is recorded as him.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person);
        graph.Upsert("Matthew", Kinds.Person);
        graph.Link("alex", "brother", "matthew");

        var added = graph.AddAliases("matthew", new[] { "Matt", "brother" });

        Assert.Equal(new[] { "matt" }, added);
        Assert.Null(graph.Resolve("brother").Node);
        Assert.Equal("matthew", graph.Resolve("Matt").Node?.Id);
    }

    [Fact]
    public void Only_names_that_are_new_are_reported()
    {
        // Reporting the whole list meant every reading claimed to have recorded the nine names Alex already answers to,
        // which buries whatever the sentence actually said.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "i" });

        Assert.Empty(graph.AddAliases("alex", new[] { "me", "I", "Alex" }));
        Assert.Equal(new[] { "big-al" }, graph.AddAliases("alex", new[] { "me", "Big Al" }));
    }

    // ── the maybe ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_shortened_name_is_noticed_as_a_maybe()
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Matthew", Kinds.Person);

        Assert.Equal("matthew", graph.MightBe("Matt", Kinds.Person)?.Id);
        // And the other way round: the full name arriving after the short one.
        var other = new Graph { Now = () => Tuesday };
        other.Upsert("Matt", Kinds.Person);
        Assert.Equal("matt", other.MightBe("Matthew", Kinds.Person)?.Id);
    }

    [Fact]
    public void What_is_deliberately_not_noticed()
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Robert", Kinds.Person);
        graph.Upsert("Katherine", Kinds.Person);

        // Only whole-prefix names are caught. A nickname table or a similarity score would catch these too — and would
        // eventually produce a CONFIDENT wrong pairing, which is far worse than a question nobody was asked.
        Assert.Null(graph.MightBe("Bob", Kinds.Person));
        Assert.Null(graph.MightBe("Kate", Kinds.Person));
        // Two letters is not a name, it is a coincidence waiting to happen.
        Assert.Null(graph.MightBe("Ro", Kinds.Person));
        // And a different sort of thing entirely is never a maybe, whatever it is called.
        Assert.Null(graph.MightBe("Robertson Road", Kinds.Place));
    }

    [Fact]
    public void Two_candidates_is_a_coin_toss_not_a_maybe()
    {
        // A question offering a choice between two people who might both be wrong is worse than no question.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Matthew", Kinds.Person);
        graph.Add("Mattias", Kinds.Person);

        Assert.Null(graph.MightBe("Matt", Kinds.Person));
    }

    [Fact]
    public async Task A_fact_about_a_maybe_still_lands_and_the_doubt_is_recorded()
    {
        // The fact is not the thing in doubt. Losing "Matt can drive" because nobody knew which Matt is the outcome to
        // avoid — it lands, and the identity question is asked beside it.
        var model = new Scripted(
            Scripted.Plan(Scripted.Link("Alex", "brother of", "Matthew", fromKind: Kinds.Person, toKind: Kinds.Person)),
            Scripted.Plan(Scripted.Fact("Matt", "can drive", "a van", fromKind: Kinds.Person)));

        var (memory, graph) = Fresh(model);
        memory.Note("my brother is Matthew");
        await memory.ReconcileAsync();

        memory.Note("Matt can drive a van");
        var done = await memory.ReconcileAsync();

        Assert.Contains(graph.Properties("matt"), e => e.Value == "a van");
        Assert.Contains(done.Questions, q => q.Contains("Matt") && q.Contains("Matthew"));

        var (node, maybe) = Assert.Single(graph.Maybes());
        Assert.Equal("matt", node.Id);
        Assert.Equal("matthew", maybe.Id);
        // And it is marked as unconfirmed, so nothing treats it as an established person meanwhile.
        Assert.True(node.Provisional);
    }

    // ── the other half of identity: not confusing one thing for another ─────────────────────────────────

    [Fact]
    public async Task Somewhere_the_sentence_never_mentions_is_refused()
    {
        // The real one, from a live run. "My brother is Matthew, he lives in Leeds" was recorded as Matthew living in
        // BRISTOL — because Bristol was on file, so the name resolved perfectly and nothing failed. The list of what
        // already exists is what makes reuse possible and is simultaneously a menu of confident wrong answers.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "brother of", "Matthew", fromKind: Kinds.Person, toKind: Kinds.Person),
            Scripted.Link("Matthew", "lives in", "Bristol", fromKind: Kinds.Person, toKind: Kinds.Place)));

        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "i" });
        graph.Upsert("Bristol", Kinds.Place);
        var memory = new Memory(graph, model, "test-model") { Now = () => Tuesday };

        memory.Note("My brother is Matthew, and he lives in Leeds.");
        var done = await memory.ReconcileAsync();

        // The half it got right lands.
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "matthew");
        // The invented one does not, and it is asked about rather than swallowed.
        Assert.DoesNotContain(graph.Around("matthew"), step => step.To.Id == "bristol");
        Assert.Contains(done.Questions, q => q.Contains("Bristol"));
    }

    [Fact]
    public async Task Somewhere_the_sentence_does_mention_is_fine()
    {
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Matthew", "lives in", "Bristol", fromKind: Kinds.Person, toKind: Kinds.Place)));

        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "i" });
        graph.Upsert("Bristol", Kinds.Place);
        var memory = new Memory(graph, model, "test-model") { Now = () => Tuesday };

        memory.Note("Matthew lives in Bristol now.");
        var done = await memory.ReconcileAsync();

        Assert.Contains(graph.Around("matthew"), step => step.To.Id == "bristol");
        Assert.Empty(done.Questions);
    }

    [Fact]
    public async Task An_id_and_the_owner_are_both_exempt()
    {
        // Ids are slugs and nobody says them out loud, and the person whose memory this is arrives as "me". Refusing
        // either would refuse the writer doing exactly what it was told.
        var model = new Scripted(Scripted.Plan(
            Scripted.Link("Alex", "brother of", "matthew", fromKind: Kinds.Person, toKind: Kinds.Person)));

        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person, new[] { "me", "i" });
        graph.Upsert("Matthew", Kinds.Person);
        var memory = new Memory(graph, model, "test-model") { Now = () => Tuesday };

        memory.Note("He's my brother.");
        var done = await memory.ReconcileAsync();

        Assert.Contains(graph.Around("alex"), step => step.To.Id == "matthew");
        Assert.Empty(done.Questions);
    }

    [Fact]
    public async Task A_short_name_the_plan_resolved_for_itself_still_becomes_a_question()
    {
        // The live run that showed the gap. "Matt has just bought a camper van" was written straight against the Matthew
        // already on file — a guess that may well be right, and is nobody's to make silently. The fact belongs to what
        // was SAID, and the identity question belongs beside it.
        var model = new Scripted(
            Scripted.Plan(Scripted.Link("Alex", "brother of", "Matthew", fromKind: Kinds.Person, toKind: Kinds.Person)),
            Scripted.Plan(Scripted.Link("Matthew", "bought", "camper van", fromKind: Kinds.Person, toKind: Kinds.Thing)));

        var (memory, graph) = Fresh(model);
        memory.Note("my brother is Matthew");
        await memory.ReconcileAsync();

        memory.Note("Matt has just bought a camper van.");
        var done = await memory.ReconcileAsync();

        // Recorded against Matt, not Matthew.
        Assert.Contains(graph.Around("matt"), step => step.To.Id == "camper-van");
        Assert.DoesNotContain(graph.Around("matthew"), step => step.To.Id == "camper-van");

        var (node, maybe) = Assert.Single(graph.Maybes());
        Assert.Equal("matt", node.Id);
        Assert.Equal("matthew", maybe.Id);
        Assert.Contains(done.Questions, q => q.Contains("Matt") && q.Contains("Matthew"));
    }

    // ── settling it ─────────────────────────────────────────────────────────────────────────────────────    // ── settling it ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Yes_merges_them_and_the_short_name_keeps_working()
    {
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person);
        graph.Upsert("Matthew", Kinds.Person);
        graph.Link("alex", "brother of", "matthew");

        var matt = graph.Add("Matt", Kinds.Person, provisional: true);
        graph.State(matt.Id, "can drive", "a van");
        graph.Might(matt.Id, "matthew");

        Assert.True(graph.TheSame(matt.Id, "matthew"));

        // Everything that was Matt's is Matthew's.
        Assert.Contains(graph.Properties("matthew"), e => e.Value == "a van");
        Assert.Contains(graph.Around("alex"), step => step.To.Id == "matthew");
        // The name in doubt is now just another name for him — which is what stops this ever being asked again.
        Assert.Equal("matthew", graph.Resolve("Matt").Node?.Id);
        Assert.Empty(graph.Maybes());
        Assert.False(graph.Get("matthew")!.Provisional);
        // One person, not two.
        Assert.Single(graph.Nodes.Where(n => n.Kind == Kinds.Person && n.Id != "alex"));
    }

    [Fact]
    public void The_node_everything_points_at_is_the_one_that_survives()
    {
        // Not the older one — the ESTABLISHED one. A merge that keeps the doubtful node quietly renames somebody, and
        // "Matthew" becoming "Matt" everywhere is a worse outcome than the question being left open.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person);
        graph.Upsert("Matthew", Kinds.Person);
        graph.Link("alex", "brother of", "matthew");

        var matt = graph.Add("Matt", Kinds.Person, provisional: true);
        graph.TheSame(matt.Id, "matthew");

        Assert.Equal("Matthew", graph.Get("matt")!.Name);
    }

    [Fact]
    public void It_does_not_matter_which_way_round_the_question_is_answered()
    {
        // Two nodes with equal standing and one instant between them: the answer still has to be the same both ways, or
        // settling the same question twice renames a person.
        static Graph Pair()
        {
            var g = new Graph { Now = () => Tuesday };
            g.Add("Matthew", Kinds.Person);
            g.Add("Matt", Kinds.Person);
            return g;
        }

        var oneWay = Pair();
        oneWay.TheSame("matt", "matthew");

        var other = Pair();
        other.TheSame("matthew", "matt");

        Assert.Equal(oneWay.Get("matt")!.Id, other.Get("matt")!.Id);
        Assert.Equal(oneWay.Get("matthew")!.Id, other.Get("matthew")!.Id);
        // And either way, both names find the one person.
        Assert.NotNull(oneWay.Resolve("Matt").Node);
        Assert.Equal(oneWay.Resolve("Matt").Node!.Id, oneWay.Resolve("Matthew").Node!.Id);
    }

    [Fact]
    public void The_same_fact_twice_becomes_the_same_fact_once()
    {
        // Seen the first time a real merge ran: the camper van appeared on Matthew twice, because it had been recorded
        // once against Matt and once against Matthew and repointing made the two identical.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Matthew", Kinds.Person);
        var matt = graph.Add("Matt", Kinds.Person, provisional: true);
        graph.Upsert("Camper van", Kinds.Thing);

        graph.Link(matt.Id, "has bought", "camper-van");
        graph.Link("matthew", "has bought", "camper-van");

        graph.TheSame(matt.Id, "matthew");

        Assert.Single(graph.Around("matthew").Where(step => step.To.Id == "camper-van"));
    }

    [Fact]
    public void No_is_recorded_so_it_is_never_asked_again()
    {
        // The half that makes asking bearable. Without it, every later mention raises the same question.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Matthew", Kinds.Person);
        var matt = graph.Add("Matt", Kinds.Person, provisional: true);
        graph.Might(matt.Id, "matthew");

        Assert.True(graph.NotTheSame(matt.Id, "matthew"));

        Assert.Empty(graph.Maybes());
        // Two people, both real.
        Assert.Equal(2, graph.Nodes.Count(n => n.Kind == Kinds.Person));
        Assert.False(graph.Get("matt")!.Provisional);

        // And it cannot be raised a second time, however often the name comes up.
        Assert.False(graph.Might(matt.Id, "matthew"));
        Assert.Empty(graph.Maybes());
    }

    [Fact]
    public async Task A_second_mention_after_a_no_asks_nothing()
    {
        // End to end: the answer has to hold through the reconciler, not just in the graph, or the question comes back
        // the next time anybody says the name.
        var model = new Scripted(
            Scripted.Plan(Scripted.Link("Alex", "brother of", "Matthew", fromKind: Kinds.Person, toKind: Kinds.Person)),
            Scripted.Plan(Scripted.Fact("Matt", "can drive", "a van", fromKind: Kinds.Person)),
            Scripted.Plan(Scripted.Fact("Matt", "lives in", "Bristol", fromKind: Kinds.Person)));

        var (memory, graph) = Fresh(model);
        memory.Note("my brother is Matthew");
        await memory.ReconcileAsync();

        memory.Note("Matt can drive a van");
        await memory.ReconcileAsync();
        graph.NotTheSame("matt", "matthew");

        memory.Note("Matt lives in Bristol");
        var done = await memory.ReconcileAsync();

        Assert.Empty(done.Questions);
        Assert.Empty(graph.Maybes());
    }

    [Fact]
    public void A_merged_away_name_still_resolves_from_anywhere_holding_its_id()
    {
        // The tombstone. A panel, a task or an old conversation may still hold the loser's id, and none of them should
        // break because two nodes turned out to be one.
        var graph = new Graph { Now = () => Tuesday };
        graph.Upsert("Alex", Kinds.Person);
        graph.Upsert("Matthew", Kinds.Person);
        graph.Link("alex", "brother of", "matthew");

        // Provisional, which is what a node created from a name nobody recognised actually looks like.
        var matt = graph.Add("Matt", Kinds.Person, provisional: true);
        graph.TheSame(matt.Id, "matthew");

        Assert.Equal("matthew", graph.Get("matt")?.Id);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "matt");
    }
}
