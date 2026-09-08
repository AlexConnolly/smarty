using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// Whether the data a loader produced actually honours the model the component was written against.
///
/// <para>
/// The model is a contract, and until this existed only half of it was ever checked. A required field that came back
/// null was caught; everything INSIDE a list was not, because a list was checked for being there rather than for
/// carrying anything. So a scrape whose title selector matched nothing returned four listings with
/// <c>title: ""</c>, the shape was perfect, every mechanism reported the panel healthy — and the panel on the home
/// page was four prices and four thumbnails with not one word on it. The user found that, not the system.
/// </para>
/// <para>
/// A list is where this failure lives, and it is not bad luck. A list comes from a repeated selector, one path
/// applied to every row, so a path that is wrong is wrong for every row at once — which is exactly what makes it
/// detectable without knowing anything about the subject. One row with an empty field is a row whose value is
/// genuinely missing. Every row empty is a path that matches nothing.
/// </para>
/// </summary>
public static class WidgetContract
{
    /// <summary>
    /// A single row proves nothing. A caption that is honestly blank, a price not yet set — with one item there is no
    /// way to tell that from a selector that missed, and reporting it would make a panel with one row unbuildable.
    /// Two rows agreeing is the smallest thing that can only be the path.
    /// </summary>
    private const int EnoughRowsToBeSure = 2;

    /// <summary>
    /// Read the loaded data against the declared model.
    /// </summary>
    /// <param name="code">
    /// The component, when there is one. A required field the component never reads cannot spoil what is on screen, so
    /// it does not fail the load — and the difference is not academic. A panel adjusted to stop showing an avatar kept
    /// the avatar in its model, the profile page didn't render one on the next read, and a whole good load of the
    /// pictures the panel is FOR was thrown away over a field nothing rendered. "Required" has to mean "the component
    /// needs it", or the contract protects the model instead of the panel.
    /// </param>
    /// <returns>What is wrong with it, as the loader's author needs to hear it, or null when it holds.</returns>
    public static string? Broken(JsonElement data, IReadOnlyList<WidgetField> model, string? code = null)
    {
        if (model.Count == 0) return null;

        if (data.ValueKind != JsonValueKind.Object)
            return $"The loader returned {Describe(data.ValueKind)} rather than an object with the model's fields " +
                   $"({string.Join(", ", model.Select(f => f.Name))}) on it.";

        if (model.Where(f => f.Required && Read(code, f.Name) && !Carries(data, f.Name))
                .Select(f => f.Name).ToList() is { Count: > 0 } gone)
            return $"The source didn't carry {string.Join(", ", gone)} — the loader needs a mapping for " +
                   $"{(gone.Count == 1 ? "that" : "those")}, or the expression should return " +
                   $"{(gone.Count == 1 ? "it" : "them")}.";

        foreach (var field in model)
        {
            if (!Find(data, field.Name, out var value)) continue;

            var isList = string.Equals(field.Type, "list", StringComparison.OrdinalIgnoreCase);
            if (isList && value.ValueKind != JsonValueKind.Array && value.ValueKind != JsonValueKind.Null)
                // The component will call .map on it. Said plainly, because the browser's version of this is
                // "undefined is not a function" inside a compiled panel, which names nothing useful.
                return $"{field.Name} is declared as a list but the loader returned " +
                       $"{Describe(value.ValueKind)}. The component reads it as one, so it will throw.";

            if (value.ValueKind != JsonValueKind.Array) continue;
            if (Rowless(value, field.Name, code) is { } rowless) return rowless;
            if (Hollow(value, field.Name) is { } hollow) return hollow;
        }

        return null;
    }

    /// <summary>
    /// The component reads each row of a list as an object, and the list came back as plain values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap between the two ways a list is honestly built here, which nothing checked. A map produces PARALLEL
    /// ARRAYS — one path per field, applied down a list — and that is the established idiom for an http loader:
    /// <c>city-weather-hourly</c> declares times/temps/codes, <c>football-match-tracker</c> declares
    /// lastTeams/lastScore/lastLogos, and their components pair them up by index. A browser expression or an internal
    /// feed, by contrast, returns real row objects. Both work. Mixing them does not.
    /// </para>
    /// <para>
    /// A weather panel mapped days, day, high, low and cond to five sibling arrays and then rendered
    /// <c>days.map(d =&gt; d.high)</c>. Every check passed: the array was there, every field was present and
    /// non-blank, and <see cref="Hollow"/> looks only at object rows so it saw nothing to say. What reached the page
    /// was seven rows of correct spacing with every value undefined — which photographs as a working panel, and is
    /// the same emptiness that once arrived as "NaN° / NaN°".
    /// </para>
    /// <para>
    /// Detected from the component, because the component is the only thing that says which idiom was intended. A
    /// row parameter that is never dereferenced is the parallel-array idiom and correct; one that is dereferenced
    /// wanted objects. Being wrong in that direction costs a refused publish naming both idioms, which is a far
    /// better day than a panel of blanks nobody can see is blank.
    /// </para>
    /// </remarks>
    private static string? Rowless(JsonElement list, string fieldName, string? code)
    {
        if (code is not { Length: > 0 }) return null;

        var rows = list.EnumerateArray().ToList();
        if (rows.Count == 0) return null;
        // One object anywhere means the loader is producing rows; this is not that fault.
        if (rows.Any(r => r.ValueKind == JsonValueKind.Object)) return null;

        if (RowParameter(code, fieldName) is not { Length: > 0 } row) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(code,
                $@"(?<![\w.]){System.Text.RegularExpressions.Regex.Escape(row)}\s*\.\s*\w"))
            return null;

        return $"The component maps over {fieldName} and reads properties off each row (\"{row}.…\"), but " +
               $"{fieldName} came back as a list of plain values, so every one of those reads is undefined and the " +
               "panel renders rows with nothing in them.\n" +
               "Pick one of the two ways this system builds a list and follow it through:\n" +
               $"  - PARALLEL ARRAYS, which is what a map produces: keep the sibling fields and read them by index " +
               $"— {fieldName}.map((v, i) => … otherField[i] …). This is what city-weather-hourly and " +
               "football-match-tracker do.\n" +
               $"  - ROW OBJECTS, which a browser expression or an internal feed can return: make {fieldName} a list " +
               "of objects carrying those fields, and drop the sibling fields from the model.";
    }

    /// <summary>The name the component gives each row of a list, from its <c>.map(</c>, or null if it maps nothing.</summary>
    private static string? RowParameter(string code, string fieldName)
    {
        // The first parameter of the callback: `days.map(d => …)`, `days.map((d, i) => …)`, `days.map(function (d)`.
        var match = System.Text.RegularExpressions.Regex.Match(code,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\s*\.\s*map\s*\(\s*(?:function\s*)?\(?\s*(\w+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// A key every row has and no row has anything in. The signature of a repeated path that matches nothing.
    /// </summary>
    private static string? Hollow(JsonElement list, string fieldName)
    {
        var rows = list.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToList();
        if (rows.Count < EnoughRowsToBeSure) return null;

        // Only keys the FIRST row has, then narrowed by every other row: a key some rows carry and others don't is a
        // source being uneven, which is normal and none of this check's business.
        var suspects = rows[0].EnumerateObject().Select(p => p.Name).ToList();
        foreach (var row in rows)
            suspects = suspects.Where(name => Find(row, name, out var v) && Blank(v)).ToList();

        if (suspects.Count == 0) return null;

        return $"Every one of the {rows.Count} {fieldName} came back with " +
               $"{(suspects.Count == 1 ? "an empty " + suspects[0] : "empty " + string.Join(" and ", suspects))}. " +
               "A path that matches nothing is empty for every row, so that is a fault in the loader, not in the " +
               $"source — and the panel renders {(suspects.Count == 1 ? "a column with nothing in it" : "columns with nothing in them")} " +
               "rather than looking broken.";
    }

    /// <summary>
    /// Does the component actually read this field?
    /// </summary>
    /// <remarks>
    /// A whole-word match on the name, and generous on purpose: with no code to look at, or a name that appears
    /// anywhere in it, the answer is yes. Being wrong in that direction costs a failed load that says exactly what is
    /// missing; being wrong the other way would let a field the panel really does render go quietly empty.
    /// </remarks>
    private static bool Read(string? code, string field) =>
        code is not { Length: > 0 }
        || System.Text.RegularExpressions.Regex.IsMatch(code, $@"\b{System.Text.RegularExpressions.Regex.Escape(field)}\b");

    /// <summary>
    /// Is the field there AND does it have something in it?
    ///
    /// <para>
    /// Presence alone isn't enough. A browser expression whose selectors all missed returns every field as null and
    /// the shape looks perfect — which produced a panel reading "— / false / —" with no error anywhere.
    /// </para>
    /// </summary>
    public static bool Carries(JsonElement obj, string field) => Find(obj, field, out var value) && !Blank(value);

    /// <summary>
    /// Nothing in it. A zero, a false and an EMPTY LIST are all real answers and none of them are blank — treating
    /// an empty array as an absence is how "your list is empty" once got reported as a broken loader, over a list
    /// the person had simply finished.
    /// </summary>
    private static bool Blank(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        _ => false,
    };

    /// <summary>
    /// Case-insensitive, because a source that renames Price to price should not silently empty a panel.
    /// </summary>
    private static bool Find(JsonElement obj, string field, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        return false;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a true/false",
        JsonValueKind.Null or JsonValueKind.Undefined => "nothing",
        _ => "something else",
    };
}
