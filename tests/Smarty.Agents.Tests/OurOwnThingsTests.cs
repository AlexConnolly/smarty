using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The assistant is not in the memory, and neither is anything it made.
///
/// <para>
/// The map holds the user's world. A panel on their home page is not part of it, and "Alex has Home Page" was on file
/// as a permanent fact — honestly permanent, which is why the lifetime test alone does not catch it. What the assistant
/// owns is knowable, so the host is asked rather than the model.
/// </para>
/// </summary>
public class OurOwnThingsTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private T Store<T>(string name, Func<string, T> make)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return make(path);
    }

    private Func<string, bool> Ours(out WidgetStore panels, out WidgetLibrary kinds)
    {
        panels = Store("widgets", p => new WidgetStore(p, Json));
        kinds = Store("kinds", p => new WidgetLibrary(p, Json));
        return OurOwnThings.Test(panels, kinds);
    }

    [Fact]
    public void A_panel_on_their_page_is_ours_not_theirs()
    {
        var ours = Ours(out var panels, out _);
        panels.Reserve("Latest eBay sale", WidgetSizes.Kpi, 70, null, null, proposed: false);

        Assert.True(ours("Latest eBay sale"));
        // Same name, different punctuation and case — one panel, however it gets written down.
        Assert.True(ours("latest ebay sale"));
        Assert.True(ours("Latest-eBay-Sale"));
    }

    [Fact]
    public void A_shape_in_its_own_library_is_ours_too()
    {
        var ours = Ours(out _, out var kinds);
        kinds.Upsert(new WidgetKind { Name = "ebay-listing", Title = "eBay listing", Description = "a listing" });

        Assert.True(ours("ebay-listing"));
        Assert.True(ours("eBay listing"));
    }

    [Fact]
    public void The_app_s_own_furniture_is_ours()
    {
        var ours = Ours(out _, out _);

        Assert.True(ours("Home Page"));
        Assert.True(ours("the home page"));
        Assert.True(ours("Smarty"));
    }

    [Fact]
    public void Somewhere_real_is_theirs()
    {
        // The narrowness is the point. This is a list of what the assistant owns, not a list of words nobody may
        // record — anything looser and a place, a person or a band gets quietly refused for resembling a panel.
        var ours = Ours(out var panels, out _);
        panels.Reserve("Flights", WidgetSizes.Kpi, 70, null, null, proposed: false);

        Assert.False(ours("Flight to Palma"));
        Assert.False(ours("London Fields Lido"));
        Assert.False(ours("Niamh"));
        Assert.False(ours(""));
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            if (File.Exists(p)) File.Delete(p);
    }
}
