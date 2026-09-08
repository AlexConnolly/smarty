using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Handing over a set of pictures.
/// </summary>
/// <remarks>
/// <para>
/// Written after a job that did everything right except the last step. Asked for photos of a restaurant's food, a worker
/// opened the listing, looked at every picture, checked each one against the menu — and then delivered a markdown
/// document describing them in words, with not one image in it. The reply was a numbered list of dish names.
/// </para>
/// <para>
/// Nothing was missing from the plumbing. Markdown images in a result are already mirrored locally and rendered, and
/// bare links already become cards with their preview image. Both were reachable and neither was reached for, because a
/// convention is not an affordance: a model asked to gather pictures looks at the tools it has, sees somewhere to write
/// a file, and writes a file. So pictures get a tool, and the convention stops being the only way in.
/// </para>
/// <para>
/// It renders through the same card row a link preview does — image, title, one line under it — which is why this adds
/// no rendering. The subtitle is the one field that had to be added, and it is the one the pictures needed: what the
/// dish IS matters more than which site it came from.
/// </para>
/// </remarks>
public static class GalleryTool
{
    /// <summary>More than this in one go is a contact sheet, not an answer.</summary>
    public const int Most = 12;

    /// <summary>One picture, as the caller describes it.</summary>
    public sealed record Picture(string Url, string? Title, string? Subtitle);

    /// <param name="show">
    /// Given the pictures, put them in front of the user. Returns how many actually made it — a URL that turns out not
    /// to be an image is dropped rather than shown broken, and the caller is told, so it can go and find another.
    /// </param>
    public static AgentTool Create(Func<IReadOnlyList<Picture>, CancellationToken, Task<GalleryResult>> show) => new(
        "show_pictures",
        "Put pictures in front of the user, each with a caption — the way to answer anything asking to SEE " +
        "something. They appear as a row of images they can look at and tap. For each one give whichever you have, " +
        "with a short title saying what it is: the name of an image FILE you have already saved, the url of the " +
        "image itself, or the path a browser grab handed back. A site that refuses to hand an image over to a " +
        "download still shows it to a browser, and this is shown in one — so pass the url anyway rather than giving " +
        "up. Never describe a picture in words instead of showing it, and never leave images sitting in a file: a " +
        "file has to be opened, and this does not.",
        new[]
        {
            ToolParameter.FromSchema(
                "pictures",
                """
                {"type":"array","description":"The pictures, in the order they should appear.",
                 "items":{"type":"object","properties":{
                   "url":{"type":"string","description":"The image: a saved file name, a direct image url, or a grab path."},
                   "title":{"type":"string","description":"Short caption: what this is a picture of."},
                   "subtitle":{"type":"string","description":"One more line if it needs one - a price, a place, a detail."}
                 },"required":["url"]}}
                """,
                required: true),
        },
        async (args, ct) =>
        {
            var pictures = Read(args).Take(Most).ToList();
            if (pictures.Count == 0)
                return ToolOutput.Error("No pictures in that call — each one needs at least a url.");

            var done = await show(pictures, ct).ConfigureAwait(false);

            // Only reachable now when nothing was even a usable address. A copy that fails is no longer a failure —
            // the picture is shown from the source instead — so this means the urls themselves were unusable.
            if (done.Shown == 0)
                return ToolOutput.Error(
                    "None of those could be shown — each needs to be an image file you have saved, a full http url " +
                    "for the image itself, or the path a browser grab handed back. " +
                    (done.Why is { Length: > 0 } why ? why : "Find the direct image url and try again."));

            return ToolOutput.Ok(Report(done));
        });

    /// <summary>
    /// What to tell the caller once they are up.
    /// </summary>
    /// <remarks>
    /// Shared because the orchestrator dispatches its own tool calls against a switch rather than running these
    /// executors, so the wording would otherwise exist twice and drift. The last line is the one that matters: the
    /// failure this whole tool exists for was a job answering in adjectives.
    /// </remarks>
    public static string Report(GalleryResult done) =>
        $"Showed {done.Shown} picture{(done.Shown == 1 ? "" : "s")} to the user." +
        (done.Dropped > 0
            ? $" {done.Dropped} couldn't be loaded and {(done.Dropped == 1 ? "was" : "were")} left out."
            : "") +
        " They can see them now, so don't describe them again.";

    /// <summary>
    /// Read the pictures out of the call.
    /// </summary>
    /// <remarks>
    /// Forgiving about shape and strict about the outcome: a bare array of url strings is accepted, because a model that
    /// has only urls to give passes only urls. Anything without a url is skipped rather than failing the whole call —
    /// eleven good pictures should not be lost to one malformed entry.
    /// </remarks>
    public static IEnumerable<Picture> Read(ToolCallArguments args)
    {
        if (!args.Raw.TryGetProperty("pictures", out var value)) yield break;

        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is { Length: > 0 } one) yield return new Picture(one, null, null);
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString() is { Length: > 0 } url) yield return new Picture(url, null, null);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;
            if (Text(item, "url") is not { Length: > 0 } address) continue;

            yield return new Picture(address, Text(item, "title"), Text(item, "subtitle"));
        }
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;
}

/// <summary>What came of showing them: how many the user can actually see.</summary>
public sealed record GalleryResult(int Shown, int Dropped, string? Why = null);
