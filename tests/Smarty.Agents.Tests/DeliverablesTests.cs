using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The end-of-leg "deliver" verdict parsing. When a worker finishes, a schema-constrained call classifies which
/// files to hand the user (show) and which to persist as reusable assets (keep). These cover
/// <see cref="Orchestrator.ParseDeliverables"/> — the guard that a model can only ever deliver/save files that
/// actually exist, plus de-duping and the file+scope requirement — with hand-written JSON so no model is touched.
/// </summary>
public class DeliverablesTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    private static HashSet<string> Present(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Show_keeps_only_existing_files_and_dedupes()
    {
        var (show, keep) = Orchestrator.ParseDeliverables(
            Json("""{"status":"done","show":["playbook.pdf","ghost.pdf","playbook.pdf"]}"""),
            Present("playbook.pdf", "build.py"));

        Assert.Equal(new[] { "playbook.pdf" }, show); // ghost.pdf dropped (not on disk), duplicate collapsed
        Assert.Empty(keep);
    }

    [Fact]
    public void Show_flattens_a_path_to_the_bare_name_before_matching()
    {
        // A model that returns a directory-y path must still resolve to the flat conversation file.
        var (show, _) = Orchestrator.ParseDeliverables(
            Json("""{"status":"done","show":["some/folder/playbook.pdf"]}"""),
            Present("playbook.pdf"));

        Assert.Equal(new[] { "playbook.pdf" }, show);
    }

    [Fact]
    public void Keep_requires_a_present_file_and_a_scope()
    {
        var (_, keep) = Orchestrator.ParseDeliverables(
            Json("""
            {"status":"done","keep":[
                {"file":"tokens.json","scope":"brand:house"},
                {"file":"tokens.json","scope":""},
                {"file":"missing.json","scope":"global"}
            ]}
            """),
            Present("tokens.json"));

        var item = Assert.Single(keep);                 // empty-scope and missing-file entries both dropped
        Assert.Equal("tokens.json", item.File);
        Assert.Equal("brand:house", item.Scope);
    }

    [Fact]
    public void Missing_or_empty_arrays_yield_nothing()
    {
        var (show, keep) = Orchestrator.ParseDeliverables(
            Json("""{"status":"done"}"""), Present("a.pdf"));

        Assert.Empty(show);
        Assert.Empty(keep);
    }
}
