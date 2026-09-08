using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The list tools. One to make or read, one to change, one to remove.
/// <para>
/// Update takes add and remove together on purpose. A change arrives as a sentence — "not fussy about a pool, but a
/// spa would be nice" — and splitting that into two calls invites doing half of it. Removal is matched on the words
/// rather than a position, because the user says "the pool one", never "item three".
/// </para>
/// </summary>
public static class ProjectListTools
{
    /// <summary>
    /// The list tools.
    /// </summary>
    /// <param name="knows">
    /// What the brain holds about an item that names something it knows, given the node id. Optional, and the reason a
    /// list is worth more than its words: a shortlist of restaurants answers "where shall we eat" only if the thing
    /// reading it can also see which one has been sitting there since March and which one you have already been to.
    /// Without it the items read exactly as they used to.
    /// </param>
    public static IEnumerable<AgentTool> All(ProjectListStore lists, Func<string?> currentProject,
        Func<string, string?>? knows = null) => new[]
    {
        new AgentTool(
            "list_write",
            "Make a list for the current project, or add to one that already exists — preferred amenities, " +
            "must-sees, people to ask, anything the user is accumulating rather than deciding once. Asking twice " +
            "for the same title adds to that list rather than starting a second one. Use a list when the answer is " +
            "several things of the same kind; use set_memory when it is one settled fact.",
            new[]
            {
                ToolParameter.String("title", "What the list is of, e.g. \"preferred amenities\".", required: true),
                StringList("items", "The items to put in it."),
                ToolParameter.String("project", "Project slug. Omit to use the one in focus.", required: false),
                new ToolParameter("checklist", "boolean",
                    "True when these are things to WORK THROUGH and tick off one by one — a shopping list, " +
                    "packing, jobs before a trip. Leave it off for things simply being held, like preferences " +
                    "or criteria, where a tick would mean nothing.", required: false),
            },
            (args, _) =>
            {
                var project = args.GetStringOrNull("project") ?? currentProject();
                if (string.IsNullOrWhiteSpace(project))
                    return Task.FromResult(ToolOutput.DeadEnd(
                        "A list belongs to a project, and there isn't one in focus. Resolve it with find_project."));

                var title = args.GetStringOrNull("title")?.Trim();
                if (string.IsNullOrWhiteSpace(title))
                    return Task.FromResult(ToolOutput.Error("A list needs a title."));

                var list = lists.Create(project!, title!, Items(args, "items"), args.GetBoolOrNull("checklist"));
                return Task.FromResult(ToolOutput.Ok(Describe(lists.Get(list.Id) ?? list, knows)));
            })
        {
            // The same title twice is a return visit, not a repeat: it means add these as well.
            Repeatable = true,
        },

        new AgentTool(
            "list_update",
            "Change a list: add items, remove items, or rename it. Pass both add and remove when a single change " +
            "does both — \"not fussy about a pool, but a spa would be nice\" is one call. Items are removed by " +
            "naming them, near enough; you don't need the exact wording.",
            new[]
            {
                ToolParameter.String("id", "The list's id, as shown with the project's lists.", required: true),
                StringList("add", "Items to add."),
                StringList("remove", "Items to take out."),
                ToolParameter.String("title", "A new title, if it's being renamed.", required: false),
            },
            (args, _) =>
            {
                var id = args.GetStringOrNull("id")?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                    return Task.FromResult(ToolOutput.Error("Which list? Pass its id."));

                var list = lists.Update(id!, Items(args, "add"), Items(args, "remove"),
                    args.GetStringOrNull("title"));

                return Task.FromResult(list is null
                    ? ToolOutput.DeadEnd($"There's no list \"{id}\". The project's lists are shown with its context.")
                    : ToolOutput.Ok(Describe(lists.Get(list.Id) ?? list, knows)));
            })
        {
            Repeatable = true,
        },

        new AgentTool(
            "list_check",
            "Tick items off a checklist as you finish them — or untick one you got wrong. Do this AS YOU GO, " +
            "not at the end: a run that stops halfway then leaves a list showing exactly what landed and what " +
            "didn't, so the next attempt starts from the remainder instead of doing it all again. Name items " +
            "the way the list does, near enough; exact wording isn't needed. Ticking anything makes the list a " +
            "checklist if it wasn't already.",
            new[]
            {
                ToolParameter.String("id", "The list's id, as shown with the project's lists.", required: true),
                StringList("done", "Items now finished."),
                StringList("undone", "Items to un-tick."),
            },
            (args, _) =>
            {
                var id = args.GetStringOrNull("id")?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                    return Task.FromResult(ToolOutput.Error("Which list? Pass its id."));

                var result = lists.Check(id!, Items(args, "done"), Items(args, "undone"));
                if (result is not { } outcome)
                    return Task.FromResult(ToolOutput.DeadEnd(
                        $"There's no list \"{id}\". The project's lists are shown with its context."));

                // Nothing moved usually means the words didn't match anything on the list, which is worth
                // saying — silently reporting success would have a worker believe it had recorded progress.
                return Task.FromResult(outcome.Changed == 0
                    ? ToolOutput.Error(
                        $"Nothing on that list matched. {Describe(outcome.List, knows)}")
                    : ToolOutput.Ok(Describe(outcome.List, knows)));
            })
        {
            Repeatable = true,
        },

        new AgentTool(
            "list_schedule",
            "Put items on days — which dinner is Tuesday, which job is before Friday. Dated items appear on the " +
            "user's home page under the day they belong to, which is the only way anything on a list gets in " +
            "front of them without them going looking for it. Do this whenever a list is inherently by-day (a " +
            "meal plan, a week's jobs, a rota): writing the seven dinners and leaving them undated means nobody " +
            "can be told what tonight's is. Name items the way the list does, near enough. A date is a DAY, not " +
            "a time — for something that should go off at a moment, schedule a task instead.",
            new[]
            {
                ToolParameter.String("id", "The list's id, as shown with the project's lists.", required: true),
                ToolParameter.FromSchema("on",
                    "{\"type\":\"array\",\"description\":\"Item and the day it's for.\",\"items\":{\"type\":\"object\"," +
                    "\"properties\":{\"item\":{\"type\":\"string\"},\"date\":{\"type\":\"string\"," +
                    "\"description\":\"The day as YYYY-MM-DD, or empty to clear it.\"}}," +
                    "\"required\":[\"item\",\"date\"]}}"),
            },
            (args, _) =>
            {
                var id = args.GetStringOrNull("id")?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                    return Task.FromResult(ToolOutput.Error("Which list? Pass its id."));

                var wanted = new Dictionary<string, DateOnly?>(StringComparer.OrdinalIgnoreCase);
                var unreadable = new List<string>();
                if (args.Raw.TryGetProperty("on", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
                    foreach (var pair in pairs.EnumerateArray())
                    {
                        if (pair.ValueKind != JsonValueKind.Object) continue;
                        var item = pair.TryGetProperty("item", out var i) ? i.GetString()?.Trim() ?? "" : "";
                        var date = pair.TryGetProperty("date", out var d) ? d.GetString()?.Trim() ?? "" : "";
                        if (item.Length == 0) continue;

                        if (date.Length == 0) wanted[item] = null;
                        else if (DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out var on))
                            wanted[item] = on;
                        else unreadable.Add($"{item} → \"{date}\"");
                    }

                // Said out loud rather than skipped. A half-applied week is worse than a refused one: it looks
                // done, and the days that silently didn't take are the ones nobody thinks to check.
                if (unreadable.Count > 0)
                    return Task.FromResult(ToolOutput.Error(
                        $"Couldn't read the date for: {string.Join("; ", unreadable)}. Give each as YYYY-MM-DD."));

                if (wanted.Count == 0)
                    return Task.FromResult(ToolOutput.Error("Pass `on` as item/date pairs."));

                var result = lists.Schedule(id!, wanted);
                if (result is not { } outcome)
                    return Task.FromResult(ToolOutput.DeadEnd(
                        $"There's no list \"{id}\". The project's lists are shown with its context."));

                return Task.FromResult(outcome.Changed == 0
                    ? ToolOutput.Error($"Nothing on that list matched those items. {Describe(outcome.List, knows)}")
                    : ToolOutput.Ok(Describe(outcome.List, knows)));
            })
        {
            Repeatable = true,
        },

        new AgentTool(
            "list_delete",
            "Remove a list entirely. For taking items out of one, use list_update — deleting and rebuilding loses " +
            "everything else on it.",
            new[] { ToolParameter.String("id", "The list's id.", required: true) },
            (args, _) =>
            {
                var id = args.GetStringOrNull("id")?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                    return Task.FromResult(ToolOutput.Error("Which list? Pass its id."));

                return Task.FromResult(lists.Delete(id!)
                    ? ToolOutput.Ok($"Deleted list {id}.")
                    : ToolOutput.DeadEnd($"There's no list \"{id}\"."));
            }),
    };

    /// <summary>An array-of-strings parameter. There is no helper for one, so the schema is given directly.</summary>
    private static ToolParameter StringList(string name, string description) =>
        ToolParameter.FromSchema(
            name,
            $"{{\"type\":\"array\",\"items\":{{\"type\":\"string\"}},\"description\":\"{description}\"}}");

    /// <summary>Read one of those back. A single string is accepted too — models pass one when they mean one.</summary>
    private static IEnumerable<string> Items(ToolCallArguments args, string name)
    {
        if (!args.Raw.TryGetProperty(name, out var value)) return Array.Empty<string>();

        if (value.ValueKind == JsonValueKind.String)
            return new[] { value.GetString() ?? "" };

        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? "")
            .ToList();
    }

    /// <summary>
    /// The list as the caller gets it back. A checklist reads as its progress and marks the ticked items, so a
    /// worker resuming one can see what is left without asking a second question about it.
    /// </summary>
    private static string Describe(ProjectList list, Func<string, string?>? knows = null)
    {
        if (list.Items.Count == 0) return $"{list.Title} ({list.Id}): empty";

        // What the graph holds about an item, when the item turned out to name something. Kept to one clause each, so a
        // list of ten places stays readable — the point is to make one of them stand out, not to print the graph.
        string Written(string item)
        {
            var text = ProjectListStore.Label(list, item);
            if (knows is null || list.NodeOf(item) is not { Length: > 0 } id) return text;
            return knows(id) is { Length: > 0 } held ? $"{text} ({held})" : text;
        }

        if (!list.Checklist)
            return $"{list.Title} ({list.Id}): " + string.Join(", ", list.Items.Select(Written));

        var shown = list.Items.Select(i => list.IsDone(i) ? $"[x] {Written(i)}" : $"[ ] {Written(i)}");
        var left = list.Items.Count - list.DoneCount;
        return $"{list.Title} ({list.Id}) — {list.DoneCount}/{list.Items.Count} done" +
               (left > 0 ? $", {left} left" : ", all done") +
               $": {string.Join(", ", shown)}";
    }
}
