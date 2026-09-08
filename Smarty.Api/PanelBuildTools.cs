using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// One tool per state, and a state cannot finish without calling its own.
///
/// <para>
/// This is what makes a state a state rather than a paragraph. The old build reported in prose — "I've designed the
/// panel and identified a source" — and prose is not checkable: a design that was described but never shown and a
/// design that was shown read identically. Each of these writes a named artifact onto the panel's build record, the
/// machine looks at the record rather than at the sentence, and a state that produced nothing failed however it
/// describes itself.
/// </para>
/// <para>
/// They are deliberately small. Everything they refuse is something that cost a real build: a contract with no
/// fields, a source with no url, a proof of a call nobody made.
/// </para>
/// </summary>
public static class PanelBuildTools
{
    /// <summary>How much of a response is worth keeping to write a mapping against.</summary>
    /// <remarks>
    /// Enough to see the shape and find the fields, short enough that it doesn't dominate the binding state's
    /// prompt. A response that needs more than this to understand is one to narrow with a query, not to paste.
    /// </remarks>
    private const int ProofKept = 4000;

    /// <summary>State one: what this panel is going to show, before anything is drawn.</summary>
    public static AgentTool ShowsTool(WidgetStore widgets) => new(
        "panel_shows",
        "Write down WHAT the panel shows, as fields. This is the first thing that happens and the only thing this " +
        "step produces: the component will be written against these names, the source will be judged on whether it " +
        "can supply them, and the loader will be mapped onto them. Nothing here is about where the data comes " +
        "from — that question comes later and is somebody else's.",
        new[]
        {
            ToolParameter.String("id", "The panel's id, from your brief.", required: true),
            ToolParameter.FromSchema("fields",
                "{\"type\":\"array\",\"description\":\"The fields the component will read off `data`. Flat names a " +
                "component can write as data.name — no dots.\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\"},\"type\":{\"type\":\"string\",\"description\":\"string | number | " +
                "boolean | list\"},\"description\":{\"type\":\"string\"},\"example\":{\"type\":\"string\"}," +
                "\"required\":{\"type\":\"boolean\"}},\"required\":[\"name\",\"description\"]}}"),
            ToolParameter.String("when_empty",
                "What it shows when the subject has nothing to report — no game on, no flight today, nothing sold. " +
                "Every panel eventually has one of these days and a panel that goes blank on it looks broken.",
                required: true),
        },
        (args, _) =>
        {
            var id = args.GetStringOrNull("id")?.Trim();
            if (string.IsNullOrWhiteSpace(id) || widgets.Get(id!) is null)
                return Task.FromResult(ToolOutput.DeadEnd($"There's no panel \"{id}\". Use the id from your brief exactly."));

            var fields = ReadFields(args);
            if (fields.Count == 0)
                return Task.FromResult(ToolOutput.Error(
                    "No fields. Name what goes on the panel — a score panel has a home team, an away team, a score " +
                    "and a kick-off; \"data\" is not a field."));

            if (fields.FirstOrDefault(f => !Readable(f.Name)) is { } awkward)
                return Task.FromResult(ToolOutput.Error(
                    $"\"{awkward.Name}\" can't be read off `data` as a property. Use a flat name — letters, digits " +
                    "and underscores, not starting with a digit — and map to it in the loader later."));

            var empty = args.GetStringOrNull("when_empty")?.Trim();
            if (string.IsNullOrWhiteSpace(empty))
                return Task.FromResult(ToolOutput.Error(
                    "Say what it shows when there is nothing on. This is the state the panel spends most of its " +
                    "life in and the one nobody designs."));

            if (!widgets.Produced(id!, r => r.Contract = new PanelContract { Fields = fields, WhenEmpty = empty }))
                return Task.FromResult(ToolOutput.DeadEnd("That panel isn't being built."));

            return Task.FromResult(ToolOutput.Ok(
                $"Agreed: {string.Join(", ", fields.Select(f => f.Name))}. These are now fixed — the design, the " +
                "source and the loader are all measured against them. Draw it next."));
        });

    /// <summary>State three: who could answer, and what this panel's subject is called to them.</summary>
    public static AgentTool SourceTool(WidgetStore widgets) => new(
        "panel_source",
        "Write down where the data will come from, before calling anything. The important half is the IDENTIFIER: " +
        "what this subject is called to that source, and the word in the response that proves the answer is about " +
        "the right thing. A guessed id is not refused by an API — it is answered, correctly, about something else.",
        new[]
        {
            ToolParameter.String("id", "The panel's id, from your brief.", required: true),
            ToolParameter.String("subject",
                "The thing itself, in words: \"Leicester City FC, the English football club\".", required: true),
            ToolParameter.String("identifier",
                "What the source calls it — a team id, a ticker, an ICAO code. Say how you established it; a number " +
                "you assumed is the single most expensive mistake available here.", required: false),
            ToolParameter.String("expect",
                "A string that will appear in the response ONLY if it is about this subject — usually the subject's " +
                "own name. Checked against what comes back, so an id you passed in yourself proves nothing.",
                required: true),
            ToolParameter.String("url",
                "The url to call, with {parameterName} where this panel's particular values go. Omit only for a " +
                "client-mode or internal panel, which have nothing to call.", required: false),
            ToolParameter.String("mode", "http | browser | client | internal. http unless it has to be otherwise.",
                required: false),
            ToolParameter.FromSchema("alternatives",
                "{\"type\":\"array\",\"description\":\"The next sources to try if this one doesn't answer, best " +
                "first. Naming them now is what stops the build grinding on one dead url.\",\"items\":{\"type\":\"string\"}}"),
        },
        (args, _) =>
        {
            var id = args.GetStringOrNull("id")?.Trim();
            if (string.IsNullOrWhiteSpace(id) || widgets.Get(id!) is null)
                return Task.FromResult(ToolOutput.DeadEnd($"There's no panel \"{id}\". Use the id from your brief exactly."));

            var subject = args.GetStringOrNull("subject")?.Trim();
            if (string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(ToolOutput.Error("Name the subject — it is what the answer gets checked against."));

            var mode = (args.GetStringOrNull("mode")?.Trim() ?? LoaderModes.Http).ToLowerInvariant();
            var url = args.GetStringOrNull("url")?.Trim() ?? "";
            bool needsUrl = mode is not (LoaderModes.Client or LoaderModes.Internal);
            if (needsUrl && url.Length == 0)
                return Task.FromResult(ToolOutput.Error(
                    "An http or browser source is a url. Give the one you intend to call, with {parameters} where " +
                    "this panel's values go."));

            var expect = args.GetStringOrNull("expect")?.Trim();
            if (needsUrl && string.IsNullOrWhiteSpace(expect))
                return Task.FromResult(ToolOutput.Error(
                    "Say what proves the answer is about this subject. Without it there is no way to tell a right " +
                    "answer from a confident answer about the wrong thing, which is exactly how a Leicester panel " +
                    "was built showing Reading's fixtures."));

            var source = new PanelSource
            {
                Subject = subject!,
                Identifier = args.GetStringOrNull("identifier")?.Trim(),
                Expect = expect,
                Url = url,
                Mode = mode,
                Alternatives = ReadList(args, "alternatives"),
            };

            if (widgets.BuildOf(id!) is { Struck.Count: > 0 } record
                && PanelMachine.Named(source) is { Length: > 0 } name
                && record.Struck.Contains(name, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(ToolOutput.Error(
                    $"{name} has already been tried in this build and did not answer. Offering it again cannot " +
                    $"work. Already struck off: {string.Join(", ", record.Struck)}."));

            if (!widgets.Produced(id!, r => r.Source = source))
                return Task.FromResult(ToolOutput.DeadEnd("That panel isn't being built."));

            return Task.FromResult(ToolOutput.Ok(
                $"Noted: {source.Subject} from {PanelMachine.Named(source)}" +
                (source.Identifier is { Length: > 0 } ident ? $" as {ident}" : "") +
                $". Now call it for real and record what comes back — it is checked for \"{expect}\"."));
        });

    /// <summary>State four: what the source actually said when it was called.</summary>
    public static AgentTool ProofTool(WidgetStore widgets) => new(
        "panel_proof",
        "Record what the source returned when you called it with the real values. Paste what came back — the head " +
        "of it is enough. Whether it is about the right subject is worked out from the response itself, not from " +
        "your reading of it.",
        new[]
        {
            ToolParameter.String("id", "The panel's id, from your brief.", required: true),
            ToolParameter.String("url", "The exact url you called, with the real values in it.", required: true),
            new ToolParameter("status", "integer", "The HTTP status it answered with.", required: false),
            ToolParameter.String("body",
                "What came back, verbatim, from the start. Not your summary of it — the loader gets written against " +
                "this, so a paraphrase would be a mapping onto fields nobody has seen.", required: true),
        },
        (args, _) =>
        {
            var id = args.GetStringOrNull("id")?.Trim();
            if (string.IsNullOrWhiteSpace(id) || widgets.Get(id!) is null)
                return Task.FromResult(ToolOutput.DeadEnd($"There's no panel \"{id}\". Use the id from your brief exactly."));

            var body = args.GetStringOrNull("body") ?? "";
            if (body.Trim().Length == 0)
                return Task.FromResult(ToolOutput.Error(
                    "Nothing came back, or nothing was pasted. Either way there is no proof here — if the call " +
                    "genuinely returned nothing, that source has failed and the next one is the answer."));

            var record = widgets.BuildOf(id!);
            var expect = record?.Source?.Expect;

            // The check the model does not get to make. It reported "the feed works" about a response for another
            // club once already, and every mechanism downstream believed it.
            bool names = string.IsNullOrWhiteSpace(expect)
                         || body.Contains(expect!, StringComparison.OrdinalIgnoreCase);

            var proof = new PanelProof
            {
                Url = args.GetStringOrNull("url")?.Trim() ?? record?.Source?.Url ?? "",
                Status = args.GetIntOrNull("status"),
                Body = body.Length > ProofKept ? body[..ProofKept] : body,
                Subject = names,
            };

            if (!widgets.Produced(id!, r => r.Proof = proof))
                return Task.FromResult(ToolOutput.DeadEnd("That panel isn't being built."));

            if (!names)
                return Task.FromResult(ToolOutput.Error(
                    $"That response never mentions \"{expect}\". It answered, and it answered about something " +
                    "else — a wrong identifier is not an error, it is a different subject. Either establish the " +
                    "right identifier for this source, or say plainly that this source doesn't carry the subject."));

            return Task.FromResult(ToolOutput.Ok(
                $"Proved: {proof.Url} answers{(proof.Status is { } s ? $" {s}" : "")} and the response names " +
                $"\"{expect}\". Map it onto the agreed fields and publish."));
        });

    private static List<WidgetField> ReadFields(ToolCallArguments args)
    {
        var fields = new List<WidgetField>();
        if (!args.Raw.TryGetProperty("fields", out var items) || items.ValueKind != JsonValueKind.Array) return fields;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = Text(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            fields.Add(new WidgetField
            {
                Name = name!.Trim(),
                Type = Text(item, "type")?.Trim() is { Length: > 0 } t ? t : "string",
                Description = Text(item, "description")?.Trim() ?? "",
                Example = Text(item, "example")?.Trim(),
                Required = !item.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.False,
            });
        }
        return fields;
    }

    private static List<string> ReadList(ToolCallArguments args, string name)
    {
        var list = new List<string>();
        if (!args.Raw.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim() is { Length: > 0 } value)
                list.Add(value);
        return list;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The same rule publishing applies: a name a component can write as <c>data.something</c>.</summary>
    private static bool Readable(string? name) =>
        name is { Length: > 0 } && !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>The tools that belong to one state, and to no other.</summary>
    /// <remarks>
    /// A state holding another state's tool is a state that can do another state's job badly: give the research
    /// step a publish tool and it will eventually publish something it never proved, which is the shape of every
    /// bad panel this system has produced.
    /// </remarks>
    public static IEnumerable<AgentTool> For(PanelStep step, WidgetStore widgets, IEnumerable<AgentTool> panelTools)
    {
        switch (step)
        {
            case PanelStep.Agree:
                yield return ShowsTool(widgets);
                break;

            case PanelStep.Design:
                foreach (var tool in panelTools.Where(t => t.Name == "widget_design")) yield return tool;
                break;

            case PanelStep.Research:
                yield return SourceTool(widgets);
                break;

            case PanelStep.Prove:
                yield return ProofTool(widgets);
                break;

            case PanelStep.Bind:
                foreach (var tool in panelTools.Where(t => t.Name == "widget_publish")) yield return tool;
                break;
        }
    }
}
