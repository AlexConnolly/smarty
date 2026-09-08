using Xunit;

namespace Smarty.Brain.Tests;

/// <summary>
/// Context: what nodes actually contain, kept out of the graph.
///
/// <para>
/// The graph says what things are to each other and nothing about what they hold, which is the right division and also a
/// gap — the connection between a person and their ticket is not the ticket. These tests cover the two questions that gap
/// leaves unanswerable: where is my document, and what does it say.
/// </para>
/// </summary>
public class WhatThingsHold : IDisposable
{
    private readonly List<string> _dirs = new();

    private string Somewhere()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smarty-context-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>A real file on disk, because the point of this is that documents are real.</summary>
    private string AFile(string name, string contents)
    {
        var path = Path.Combine(Somewhere(), name);
        File.WriteAllText(path, contents);
        return path;
    }

    private (Memory Memory, Graph Graph, Contexts Held) Fresh(Scripted model)
    {
        var graph = new Graph();
        var held = new Contexts(Somewhere());
        var memory = new Memory(graph, model, "test-model") { Contexts = held };
        memory.WhoAmI("Owner");
        return (memory, graph, held);
    }

    [Fact]
    public async Task A_document_handed_over_ends_up_on_the_thing_it_belongs_to()
    {
        var ticket = AFile("boarding-pass.txt", "Gate 14. Boarding 20:40.");

        var model = new Scripted(Scripted.WithFile(
            "flight ticket",
            Scripted.Link("Owner", "has", "flight ticket", fromKind: "person", toKind: "thing")));

        var (memory, graph, held) = Fresh(model);
        memory.Keep("here's my flight ticket", ticket, "boarding-pass.txt");
        await memory.ReconcileAsync();

        var node = graph.Resolve("flight ticket").Node;
        Assert.NotNull(node);

        var kept = Assert.Single(held.For(node!.Id));
        Assert.Equal(Sorts.File, kept.Sort);
        Assert.Equal("boarding-pass.txt", kept.Name);

        // Copied in, not referenced. What arrives in a conversation lives somewhere that gets cleaned.
        Assert.NotNull(held.PathOf(kept));
        File.Delete(ticket);
        Assert.NotNull(held.PathOf(kept));
    }

    [Fact]
    public async Task The_document_is_read_BEFORE_the_nodes_are_made_so_what_is_inside_it_becomes_the_memory()
    {
        // The point of taking a file at all. "Here's my flight ticket" on its own yields a thing called a flight ticket,
        // holding a document, connected to nothing — handed back on request and findable by no other question. The ticket
        // itself names the airports, the date and the flight, and every one of those is worth having.
        var ticket = AFile("boarding.txt",
            "BRITISH AIRWAYS BA2604\nLONDON GATWICK (LGW) -> MALAGA (AGP)\nSat 12 Sep 2026  06:55\nSeat 14A");

        var model = new Scripted();
        var (memory, graph, held) = Fresh(model);
        memory.Keep("here's my flight ticket", ticket, "boarding.txt");

        // Whatever it plans, it planned it having READ the ticket — which is what the brief must carry.
        model.Then(Scripted.WithFile(
            "flight BA2604",
            Scripted.Link("Owner", "flying on", "flight BA2604", fromKind: "person", toKind: "thing"),
            Scripted.Link("flight BA2604", "from", "London Gatwick", toKind: "place"),
            Scripted.Link("flight BA2604", "to", "Malaga", toKind: "place"),
            Scripted.Fact("flight BA2604", "departs", "Sat 12 Sep 2026 06:55")));

        await memory.ReconcileAsync();

        var brief = Assert.Single(model.Prompts);
        Assert.Contains("MALAGA", brief);
        Assert.Contains("BA2604", brief);
        Assert.Contains("boarding.txt", brief);

        // And the places from inside it are real things now, not text buried in a file.
        Assert.NotNull(graph.Resolve("Malaga").Node);
        Assert.NotNull(graph.Resolve("London Gatwick").Node);
        Assert.Contains("Sat 12 Sep 2026 06:55",
            graph.Properties(graph.Resolve("flight BA2604").Node!.Id).Select(e => e.Value));

        // The document itself hangs off the flight, so it can still be handed over.
        Assert.Single(held.For(graph.Resolve("flight BA2604").Node!.Id));
    }

    [Fact]
    public async Task A_document_nothing_can_be_read_out_of_says_so_in_the_brief()
    {
        // So it records what the sentence said rather than inventing detail from a file it could not open.
        var image = Path.Combine(Somewhere(), "scan.png");
        File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var model = new Scripted(Scripted.WithFile(
            "flight ticket",
            Scripted.Link("Owner", "has", "flight ticket", fromKind: "person", toKind: "thing")));

        var (memory, _, _) = Fresh(model);
        memory.Keep("here's my flight ticket", image, "scan.png");
        await memory.ReconcileAsync();

        Assert.Contains("Nothing readable", Assert.Single(model.Prompts));
    }

    [Fact]
    public async Task Where_is_my_document_names_it_and_hands_it_over()
    {
        var ticket = AFile("boarding-pass.txt", "Gate 14.");

        var model = new Scripted(Scripted.WithFile(
            "flight ticket",
            Scripted.Link("Owner", "has", "flight ticket", fromKind: "person", toKind: "thing")));

        var (memory, graph, held) = Fresh(model);
        memory.Keep("here's my flight ticket", ticket, "boarding-pass.txt");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        var answer = await memory.RecallAsync("where's my flight ticket?");

        Assert.Contains("boarding-pass.txt", answer);
        // Its id travels, because naming a file nobody can fetch is not handing it over.
        Assert.Contains(held.For(graph.Resolve("flight ticket").Node!.Id)[0].Id, answer);
    }

    [Fact]
    public async Task What_time_does_it_start_is_answered_from_inside_the_document()
    {
        // The other half, and the reason extraction happens at all. Nothing in the graph knows the time — it is a line
        // inside a file somebody handed over, and either the answer reaches it or the memory is a filing cabinet.
        var ticket = AFile("gig-ticket.txt", "THE LEADMILL\nDoors 19:30\nOn stage 21:00\nStanding");

        var model = new Scripted(Scripted.WithFile(
            "gig ticket",
            Scripted.Link("Owner", "has", "gig ticket", fromKind: "person", toKind: "thing")));

        var (memory, graph, held) = Fresh(model);
        memory.Keep("here's the ticket for the gig", ticket, "gig-ticket.txt");
        await memory.ReconcileAsync();

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        var answer = await memory.RecallAsync("what time does the gig start?");

        Assert.Contains("On stage 21:00", answer);
    }

    [Fact]
    public async Task A_file_with_nothing_readable_says_so_rather_than_promising_an_answer()
    {
        // A photograph of a ticket is a file with no words in it. Saying that plainly is the difference between handing it
        // over and inventing a time that was never extracted.
        var image = Path.Combine(Somewhere(), "ticket.png");
        File.WriteAllBytes(image, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var model = new Scripted(Scripted.WithFile(
            "gig ticket",
            Scripted.Link("Owner", "has", "gig ticket", fromKind: "person", toKind: "thing")));

        var (memory, graph, held) = Fresh(model);
        memory.Keep("here's the ticket", image, "ticket.png");
        await memory.ReconcileAsync();

        var kept = Assert.Single(held.For(graph.Resolve("gig ticket").Node!.Id));
        Assert.False(kept.Readable);

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        var answer = await memory.RecallAsync("what time is the gig?");

        Assert.Contains("ticket.png", answer);
        Assert.Contains("nothing readable", answer);
    }

    [Fact]
    public async Task Context_is_loaded_only_for_what_the_walk_settled_on()
    {
        // The whole reason it is a separate store. A hundred documents must cost nothing to a question about none of them,
        // so nothing is read until the graph has decided which nodes matter.
        var model = new Scripted(Scripted.WithFile(
            "flight ticket",
            Scripted.Link("Owner", "has", "flight ticket", fromKind: "person", toKind: "thing")));

        var (memory, graph, held) = Fresh(model);
        memory.Keep("here's my flight ticket", AFile("pass.txt", "Gate 14"), "pass.txt");
        await memory.ReconcileAsync();

        // A node the question will not reach, holding something noisy.
        var elsewhere = graph.Upsert("Someone else's paperwork", Kinds.Thing);
        held.Note(elsewhere.Id, "NOT PART OF THE ANSWER");

        model.Then(Scripted.Query(about: "Owner", hops: 1));
        var answer = await memory.RecallAsync("where's my flight ticket?");

        Assert.Contains("pass.txt", answer);
        Assert.DoesNotContain("NOT PART OF THE ANSWER", answer);
    }

    [Fact]
    public void A_filename_cannot_climb_out_of_its_own_folder()
    {
        var held = new Contexts(Somewhere());
        var graph = new Graph();
        var node = graph.Upsert("Thing", Kinds.Thing);

        var kept = held.Keep(node.Id, AFile("innocent.txt", "fine"), "../../escaped.txt");

        Assert.NotNull(kept);
        Assert.DoesNotContain("..", kept!.File);
        Assert.NotNull(held.PathOf(kept));
    }

    [Fact]
    public void Context_survives_a_restart_and_forgetting_takes_the_file_with_it()
    {
        var dir = Somewhere();
        var graph = new Graph();
        var node = graph.Upsert("Thing", Kinds.Thing);

        var first = new Contexts(dir);
        var kept = first.Keep(node.Id, AFile("keep.txt", "words"), "keep.txt")!;
        var landed = first.PathOf(kept)!;

        var again = new Contexts(dir);
        Assert.Single(again.For(node.Id));
        Assert.Equal("keep.txt", again.For(node.Id)[0].Name);

        Assert.True(again.Forget(kept.Id));
        Assert.Empty(again.For(node.Id));
        Assert.False(File.Exists(landed));
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
