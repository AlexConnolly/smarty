using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The model is a contract, and a list is where it was never enforced.
///
/// <para>
/// What happened, exactly. An eBay panel's scrape read each row's title from the link wrapping the row — which on
/// that page wraps the photograph, so its text is empty. Four listings came back with <c>title: ""</c>. The shape
/// matched the declared model perfectly: the list was there, it had four items, every field was present. So the load
/// succeeded, the panel went live, the refresher was happy, the screenshot check called it working — and the panel on
/// the home page was four prices and four thumbnails with not one word on it. The person who found it was the user.
/// </para>
/// <para>
/// A list is where this failure lives and that is not luck: a list comes from one path applied to every row, so a
/// path that is wrong is wrong for every row at once. That is what makes it detectable without knowing anything
/// about eBay, titles or listings.
/// </para>
/// </summary>
public class WidgetContractTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static List<WidgetField> Model(params (string Name, string Type)[] fields) =>
        fields.Select(f => new WidgetField { Name = f.Name, Type = f.Type, Description = f.Name }).ToList();

    [Fact]
    public void A_field_empty_in_every_row_is_the_loader_failing()
    {
        var data = Json("""
            {"count":3,"rows":[
              {"name":"","price":"150.00","views":27},
              {"name":"","price":"28.16","views":7},
              {"name":"","price":"76.25","views":3}]}
            """);

        var wrong = WidgetContract.Broken(data, Model(("count", "number"), ("rows", "list")));

        Assert.NotNull(wrong);
        Assert.Contains("name", wrong);
        // The complaint has to name the loader, because the loader is what gets rewritten. Blamed on the source, the
        // repair goes looking for a different site.
        Assert.Contains("loader", wrong);
    }

    [Fact]
    public void One_row_with_a_value_missing_is_left_alone()
    {
        // A row whose value genuinely isn't there is normal, and refusing the load would take a working panel off the
        // page over one blank cell. The signal is agreement across every row, not a blank anywhere.
        var data = Json("""
            {"rows":[{"name":"","price":"150.00"},{"name":"Second","price":"28.16"}]}
            """);

        Assert.Null(WidgetContract.Broken(data, Model(("rows", "list"))));
    }

    [Fact]
    public void A_single_row_proves_nothing_either_way()
    {
        // With one row there is no way to tell an honest blank from a path that missed, and calling it a fault would
        // make a panel about one thing unbuildable.
        var data = Json("""{"rows":[{"name":"","price":"150.00"}]}""");

        Assert.Null(WidgetContract.Broken(data, Model(("rows", "list"))));
    }

    [Fact]
    public void A_zero_in_every_row_is_a_real_answer()
    {
        // Nothing being watched is an answer. A number is never blank, however uninteresting it is — the same reason
        // an empty list is a real answer rather than a missing one.
        var data = Json("""
            {"rows":[{"name":"One","watchers":0},{"name":"Two","watchers":0},{"name":"Three","watchers":0}]}
            """);

        Assert.Null(WidgetContract.Broken(data, Model(("rows", "list"))));
    }

    [Fact]
    public void An_uneven_source_is_not_a_fault()
    {
        // A key some rows carry and others don't is a source being uneven, which is most sources. Only a key EVERY
        // row has and no row fills is a path.
        var data = Json("""
            {"rows":[{"name":"One","note":""},{"name":"Two"},{"name":"Three","note":""}]}
            """);

        Assert.Null(WidgetContract.Broken(data, Model(("rows", "list"))));
    }

    [Fact]
    public void An_empty_list_is_a_real_answer()
    {
        // "Nothing on today" is the panel answering, not the panel broken. Every generated panel has a line for it.
        Assert.Null(WidgetContract.Broken(Json("""{"rows":[]}"""), Model(("rows", "list"))));
    }

    [Fact]
    public void A_required_field_that_came_back_blank_is_a_fault()
    {
        var wrong = WidgetContract.Broken(Json("""{"followers":"","posts":[]}"""),
            Model(("followers", "string"), ("posts", "list")));

        Assert.NotNull(wrong);
        Assert.Contains("followers", wrong);
    }

    [Fact]
    public void An_optional_field_may_be_blank()
    {
        var model = Model(("total", "number"));
        model.Add(new WidgetField { Name = "note", Type = "string", Description = "note", Required = false });

        Assert.Null(WidgetContract.Broken(Json("""{"total":4,"note":null}"""), model));
    }

    [Fact]
    public void A_list_that_arrived_as_something_else_is_reported_before_the_component_throws()
    {
        // The browser's version of this is "undefined is not a function" inside a compiled panel, which names
        // nothing. The component calls .map on it, so this is a certain crash caught on the server side of it.
        var wrong = WidgetContract.Broken(Json("""{"rows":"none"}"""), Model(("rows", "list")));

        Assert.NotNull(wrong);
        Assert.Contains("rows", wrong);
        Assert.Contains("list", wrong);
    }

    [Fact]
    public void A_loader_that_returned_a_list_where_the_model_is_an_object_is_reported()
    {
        var wrong = WidgetContract.Broken(Json("""[{"name":"One"}]"""), Model(("rows", "list")));

        Assert.NotNull(wrong);
        Assert.Contains("rows", wrong);
    }

    [Fact]
    public void A_panel_with_no_declared_model_is_not_checked()
    {
        // Client mode: the component fetches for itself and the server produces nothing to check.
        Assert.Null(WidgetContract.Broken(Json("""{"anything":""}"""), new List<WidgetField>()));
    }

    [Fact]
    public void A_required_field_the_component_never_reads_does_not_fail_the_load()
    {
        // "Required" has to mean "the component needs it". A panel adjusted to stop showing an avatar kept the avatar in
        // its model; the profile page didn't render one on the next read, and a whole good load of the PICTURES the
        // panel exists for was thrown away over a field nothing rendered.
        var model = Model(("avatar", "string"), ("posts", "list"));
        var data = Json("""{"avatar":"","posts":[{"src":"a.jpg"},{"src":"b.jpg"}]}""");
        var code = "return <Pictures items={data.posts} />";

        Assert.Null(WidgetContract.Broken(data, model, code));
        // And with no component to consult, it is still a fault — the check does not get quieter for knowing less.
        Assert.NotNull(WidgetContract.Broken(data, model));
    }

    [Fact]
    public void A_required_field_the_component_does_read_still_fails_the_load()
    {
        var model = Model(("headline", "string"));
        var data = Json("""{"headline":""}""");

        var wrong = WidgetContract.Broken(data, model, "return <Meta>{data.headline}</Meta>");

        Assert.NotNull(wrong);
        Assert.Contains("headline", wrong);
    }

    // ── Through the loader, which is where it has to bite ─────────────────────────────────────────────

    private static WidgetKind ListKind(Dictionary<string, string>? map = null) => new()
    {
        Name = "rows",
        Model = Model(("count", "number"), ("rows", "list")),
        Loader = new WidgetLoader { Mode = LoaderModes.Browser, Url = "https://example.com", Map = map ?? new() },
    };

    [Fact]
    public void A_browser_expression_whose_row_selector_missed_fails_the_load()
    {
        // Browser mode returns the shaped object outright, so nothing had ever looked inside it. This is the exact
        // payload the eBay panel was storing and serving without complaint.
        var kind = ListKind();
        var raw = """
            {"count":2,"rows":[{"name":"","price":"150.00"},{"name":"","price":"28.16"}]}
            """;

        var result = WidgetDataLoader.Shape(kind, kind.Loader, raw);

        Assert.False(result.Worked);
        Assert.Contains("name", result.Error);
    }

    [Fact]
    public void A_browser_expression_that_read_the_page_properly_loads()
    {
        var kind = ListKind();
        var raw = """
            {"count":2,"rows":[{"name":"One","price":"150.00"},{"name":"Two","price":"28.16"}]}
            """;

        Assert.True(WidgetDataLoader.Shape(kind, kind.Loader, raw).Worked);
    }

    [Fact]
    public void A_wildcard_mapping_that_resolved_to_nothing_fails_the_load()
    {
        // The mapped half of the same fault. A wildcard path returns a value per row whether or not the field it
        // names exists, so "the mapping resolved" was never the same as "the mapping found anything".
        var kind = ListKind(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = "total",
            ["rows"] = "items.*.detail",
        });
        var raw = """
            {"total":2,"items":[{"detail":{"name":"","price":"9.99"}},{"detail":{"name":"","price":"4.99"}}]}
            """;

        var result = WidgetDataLoader.Shape(kind, kind.Loader, raw);

        Assert.False(result.Worked);
        Assert.Contains("name", result.Error);
    }

    [Fact]
    public void A_failed_contract_keeps_the_panel_and_sends_the_loader_back_to_be_fixed()
    {
        // The point of failing the load rather than storing it: the panel keeps its last good data and says it is
        // stale, and the second identical failure is what hands the loader to a worker. Stored quietly, it stays
        // wrong for ever — which is what happened.
        var path = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        try
        {
            var store = new WidgetStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var kind = ListKind();
            var panel = store.Reserve("Listings", WidgetSizes.Wide, 70, null, null, proposed: false);
            store.Attach(panel.Id, kind, new Dictionary<string, string>());
            store.Loaded(panel.Id, """{"count":1,"rows":[{"name":"One"}]}""", null);

            var wrong = WidgetDataLoader.Shape(kind, kind.Loader,
                """{"count":2,"rows":[{"name":""},{"name":""}]}""");
            Assert.False(wrong.Worked);

            store.Loaded(panel.Id, null, wrong.Error);
            Assert.Contains("One", store.Get(panel.Id)!.Data);
            Assert.False(store.LoaderStuck(panel.Id));

            store.Loaded(panel.Id, null, wrong.Error);
            Assert.True(store.LoaderStuck(panel.Id));
        }
        finally
        {
            try { File.Delete(path); } catch { /* a temp file */ }
        }
    }
}
