using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// How a panel that could never change came to be published, and be the panel you were looking at.
///
/// <para>
/// The vacuum panel showed 82% while the vacuum was on 100%, and pressing Refresh did nothing at all. Three
/// separate things had to be true for that: the feed it wanted could not be NAMED, the fallback that produced
/// was publishable, and the page had no way to say that a refresh had been refused.
/// </para>
/// </summary>
public class PanelFeedTests
{
    [Fact]
    public void A_plugins_command_is_a_feed_you_can_name()
    {
        // The route that SERVES these already understood them. The list that publish asks did not, so a panel
        // binding to a vacuum was told to use one of agenda, lists, schedule, tasks or projects — and the build
        // took the nearest thing on the menu, or gave up and shipped its example values as parameters.
        Assert.True(WidgetInternals.Known("plugin:roborock/status"));
        Assert.True(WidgetInternals.Known("source:photos"));

        // The five fixed ones still are, and still don't care about case.
        Assert.True(WidgetInternals.Known("agenda"));
        Assert.True(WidgetInternals.Known("Tasks"));
    }

    [Fact]
    public void Half_of_a_plugin_feed_addresses_nothing()
    {
        // A plugin with no command names a plugin, not a feed, and would fail at load time with nothing to say.
        Assert.False(WidgetInternals.Known("plugin:roborock"));
        Assert.False(WidgetInternals.Known("plugin:roborock/"));
        Assert.False(WidgetInternals.Known("plugin:/status"));
        Assert.False(WidgetInternals.Known("source:"));
        Assert.False(WidgetInternals.Known("whatever"));
        Assert.False(WidgetInternals.Known(""));
        Assert.False(WidgetInternals.Known(null));
    }

    [Fact]
    public void A_name_somebody_else_chose_is_stored_as_they_wrote_it()
    {
        // The fixed names are ours and fold to lower case. A command a plugin declared and a folder a person named
        // are not ours to fold: the lookup that follows is by the exact string.
        Assert.Equal("tasks", WidgetInternals.Store("Tasks"));
        Assert.Equal("source:Holiday Photos", WidgetInternals.Store("source:Holiday Photos"));
        Assert.Equal("plugin:roborock/status", WidgetInternals.Store("  plugin:roborock/status  "));
        Assert.Null(WidgetInternals.Store("plugin:roborock"));
    }

    [Fact]
    public void What_it_says_when_the_name_is_wrong_includes_the_two_that_carry_an_argument()
    {
        // The refusal is the only place a builder finds out what a feed may be called. Listing five fixed names
        // and silently omitting the two shapes is how a build concludes its feed is impossible.
        var options = WidgetInternals.Options;

        Assert.Contains("agenda", options);
        Assert.Contains("plugin:<plugin>/<command>", options);
        Assert.Contains("source:<name>", options);
    }

    [Fact]
    public void A_client_mode_panel_that_never_fetches_has_no_business_declaring_a_model()
    {
        // The published shape of the frozen vacuum panel: client mode, no map, and a component whose first line
        // read `const { battery, state, suction } = params`. Every value came from stored parameters, and the
        // stored parameters were the example values the design invented — 82, cleaning, Turbo. It renders, it
        // photographs well, and it is a picture of a vacuum.
        var frozen = "const { battery } = params || {}; return <Panel title=\"Vacuum\">{battery}%</Panel>";
        Assert.False(WidgetLibraryTools.CanFill(frozen, clientMode: true));

        // A client-mode panel that really does fetch in the page is the case client mode exists for.
        var live = "const [d, set] = useState(null); useEffect(() => { fetch('/x').then(r => r.json()).then(set) }, []);"
                   + " return <Panel title=\"Vacuum\">{d?.battery}%</Panel>";
        Assert.True(WidgetLibraryTools.CanFill(live, clientMode: true));

        // Server modes are not asked: the loader fills the model, and the map check already covers that.
        Assert.True(WidgetLibraryTools.CanFill(frozen, clientMode: false));
    }
}
