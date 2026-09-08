using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Knowing where the user is, and being careful with it.
///
/// The point is "book me an Uber to London Bridge" not needing a pickup point spelled out. The care is that a
/// position is a guess about a person: it goes stale, it must never be acted on without checking, and a record of
/// where someone has BEEN is a different thing from where they are — so only the current fix is kept.
/// </summary>
public class LocationTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 18, 0, 0, TimeSpan.Zero);

    private LocationStore NewStore(DateTimeOffset? now = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"location-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new LocationStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        {
            Now = () => now ?? Now,
        };
    }

    [Fact]
    public void With_no_fix_it_says_nothing_at_all()
    {
        // Not "location unknown" — an absent line costs nothing, whereas telling a model it doesn't know where
        // someone is invites it to go on about it.
        Assert.Equal("", NewStore().Note());
    }

    [Fact]
    public void A_fresh_fix_gives_the_place_the_coordinates_and_an_instruction_to_confirm()
    {
        var store = NewStore();
        store.Set(51.5203, -0.0865, 25, "Borough High Street, Southwark, London");

        var note = store.Note();
        Assert.Contains("Borough High Street", note);
        Assert.Contains("51.5203", note);
        Assert.Contains("-0.0865", note);
        Assert.Contains("accurate to about 25m", note);
        Assert.Contains("currently", note);
        // The part that matters most: it may infer from this, but not act on it unasked.
        Assert.Contains("ALWAYS confirm", note);
    }

    [Fact]
    public void An_old_fix_is_flagged_as_last_known_with_its_age()
    {
        // Stated as current, a day-old position is how a taxi gets ordered to yesterday's city.
        var store = NewStore();
        store.Set(51.5203, -0.0865, 20, "Southwark, London");

        var later = new LocationStore(Path.Combine(Path.GetTempPath(), $"loc-{Guid.NewGuid():N}.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) { Now = () => Now.AddHours(30) };
        later.Set(51.5203, -0.0865, 20, "Southwark, London");
        _paths.Add(Path.Combine(Path.GetTempPath(), "unused"));

        // Age is measured from when the fix was taken, so re-read the first store's note with a moved clock.
        store.Now = () => Now.AddHours(30);
        var note = store.Note();
        Assert.Contains("last known", note);
        Assert.Contains("30 hours ago", note);
        Assert.DoesNotContain("currently", note);
    }

    [Fact]
    public void Nonsense_coordinates_are_refused_rather_than_stated_confidently()
    {
        var store = NewStore();

        Assert.False(store.Set(91, 0, null, "the North Pole and then some"));
        Assert.False(store.Set(0, 181, null, "off the map"));
        Assert.False(store.Set(double.NaN, 0, null, null));
        Assert.Null(store.Current);
        Assert.Equal("", store.Note());
    }

    [Fact]
    public void A_new_fix_replaces_the_old_one_and_no_trail_is_kept()
    {
        // Where someone has been answers questions nobody asked it to answer.
        var store = NewStore();
        store.Set(51.5203, -0.0865, 20, "Southwark");
        store.Set(53.4808, -2.2426, 30, "Manchester");

        Assert.Equal("Manchester", store.Current!.Place);
        Assert.Contains("Manchester", store.Note());
        Assert.DoesNotContain("Southwark", store.Note());
    }

    [Fact]
    public void A_jittery_fix_at_the_same_spot_keeps_the_name_it_already_had()
    {
        // GPS wobbles by metres while standing still, and a failed lookup shouldn't blank the address for it.
        var store = NewStore();
        store.Set(51.5203, -0.0865, 20, "Borough High Street, Southwark");
        store.Set(51.52035, -0.08655, 22, null);

        Assert.Equal("Borough High Street, Southwark", store.Current!.Place);
    }

    [Fact]
    public void A_real_move_with_no_name_does_not_inherit_the_old_one()
    {
        var store = NewStore();
        store.Set(51.5203, -0.0865, 20, "Southwark");
        store.Set(53.4808, -2.2426, 30, null);

        Assert.Null(store.Current!.Place);
        Assert.DoesNotContain("Southwark", store.Note());
    }

    [Fact]
    public void Coordinates_alone_still_produce_a_usable_note()
    {
        var store = NewStore();
        store.Set(51.5203, -0.0865, null, null);

        var note = store.Note();
        Assert.Contains("51.5203", note);
        Assert.DoesNotContain("accurate to", note); // nothing claimed about a precision we weren't given
    }

    [Fact]
    public void Clearing_removes_it_and_the_file_behind_it()
    {
        var store = NewStore();
        store.Set(51.5203, -0.0865, 20, "Southwark");

        Assert.True(store.Clear());
        Assert.Null(store.Current);
        Assert.Equal("", store.Note());
        Assert.False(store.Clear()); // already gone
    }

    [Fact]
    public void It_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"location-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        new LocationStore(path, json) { Now = () => Now }.Set(51.5203, -0.0865, 20, "Southwark");

        var reopened = new LocationStore(path, json) { Now = () => Now };
        Assert.Equal("Southwark", reopened.Current!.Place);
    }

    public void Dispose()
    {
        foreach (var path in _paths) try { File.Delete(path); } catch { }
    }
}
