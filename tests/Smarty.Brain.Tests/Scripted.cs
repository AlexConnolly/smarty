using System.Text.Json;
using System.Text.Json.Nodes;
using Smarty.Agents;

namespace Smarty.Brain.Tests;

/// <summary>
/// A model provider that answers from a script.
/// </summary>
/// <remarks>
/// <para>
/// The reconciler's whole job is deciding what a sentence means and then doing it. Those are two different things and
/// only the second one is testable — so this fixes the first. Every test here says "given the model read it THIS way,
/// does the graph end up right", which is the half that has to be correct every time.
/// </para>
/// <para>
/// It also records what it was asked, so a test can assert the model was handed the candidate nodes it needed. A
/// reconciler that never shows the model what already exists cannot possibly reuse it, and that failure looks exactly
/// like a model mistake from the outside.
/// </para>
/// </remarks>
public sealed class Scripted : IModelProvider
{
    private readonly Queue<string> _answers = new();

    public Scripted(params string[] answers)
    {
        foreach (var answer in answers) _answers.Enqueue(answer);
    }

    /// <summary>Every prompt this was sent, in order.</summary>
    public List<string> Prompts { get; } = new();

    public int Calls => Prompts.Count;

    /// <summary>Add another scripted answer, for tests that go a second round.</summary>
    public Scripted Then(string answer)
    {
        _answers.Enqueue(answer);
        return this;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Prompts.Add(string.Join("\n", request.Messages.Select(m => m.Content)));

        var content = _answers.Count > 0 ? _answers.Dequeue() : "{}";
        await Task.Yield();
        yield return new ModelStreamEvent.Completed(new ModelResponse { Content = content });
    }

    /// <summary>A plan, written the way the model is asked to write one.</summary>
    public static string Plan(params object[] writes) => new JsonObject
    {
        ["writes"] = new JsonArray(writes.Select(w => JsonNode.Parse(JsonSerializer.Serialize(w))!).ToArray()),
    }.ToJsonString();

    public static object Link(string from, string label, string to, string? fromKind = null, string? toKind = null,
        string? note = null, string? when = null, string? lasts = null)
        => new { from, label, to, fromKind, toKind, note, when, lasts };

    public static object Fact(string from, string label, string value, string? fromKind = null, string? when = null,
        string? lasts = null)
        => new { from, label, value, fromKind, when, lasts };

    /// <summary>A thing whose kind carries a shape, declared with it.</summary>
    public static object Thing(string name, string kind, string? start = null, string? finish = null)
        => new { name, kind, start, finish };

    /// <summary>A thing on file under a description, declared with what it is actually called.</summary>
    public static object Called(string name, string kind, string called)
        => new { name, kind, called };

    /// <summary>A thing declared with the other names it answers to.</summary>
    public static object Known(string name, string kind, params string[] also)
        => new { name, kind, also };

    /// <summary>A plan that raises shaped things as well as stating facts.</summary>
    public static string Happening(object[] things, params object[] writes) => new JsonObject
    {
        ["things"] = new JsonArray(things.Select(t => JsonNode.Parse(JsonSerializer.Serialize(t))!).ToArray()),
        ["writes"] = new JsonArray(writes.Select(w => JsonNode.Parse(JsonSerializer.Serialize(w))!).ToArray()),
    }.ToJsonString();

    public static string Retraction(string from, string label, string to, string because) => new JsonObject
    {
        ["retract"] = new JsonArray(JsonNode.Parse(JsonSerializer.Serialize(new { from, label, to, because }))!),
    }.ToJsonString();

    public static string Query(string about, string? wanting = null, int hops = 2, string[]? anchors = null,
        string[]? exclude = null, bool properties = false, string? with = null)
        => JsonSerializer.Serialize(new { about, wanting, hops, anchors, exclude, properties, with });

    /// <summary>A plan that also says which thing a document handed over belongs to.</summary>
    public static string WithFile(string attachTo, params object[] writes) => new JsonObject
    {
        ["attachTo"] = attachTo,
        ["writes"] = new JsonArray(writes.Select(w => JsonNode.Parse(JsonSerializer.Serialize(w))!).ToArray()),
    }.ToJsonString();

    public static string Nothing => "{}";
}
