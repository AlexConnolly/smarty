using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Standing work.
///
/// <para>
/// The scheduler, the store and the two tools have existed since Slack was built, and the web app — the surface
/// actually used — had them sat behind a gate that refused any session id not shaped like a Slack thread. So
/// "every morning tell me what's on" answered "I can only schedule things inside a Slack thread", and there was
/// no recurrence anywhere: every task was one-shot.
/// </para>
/// <para>
/// Every recurrence here resolves to ONE concrete UTC instant, computed when it's set and again when it fires.
/// Nothing is re-interpreted by the tick, which only ever asks whether FireAt is in the past — that's what lets a
/// daily job and a one-off reminder be the same row.
/// </para>
/// </summary>
public class ScheduleTests : IDisposable
{
    private readonly List<string> _paths = new();

    private ScheduleStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schedules-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new ScheduleStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    // A fixed Thursday, so "next monday" and "tomorrow" mean something checkable.
    private static readonly DateTimeOffset Thursday1030 =
        new(2026, 8, 20, 10, 30, 0, TimeSpan.Zero);   // 2026-08-20 is a Thursday

    private static ScheduledTask Add(ScheduleStore s, string repeat = "", DateTimeOffset? at = null) =>
        s.Add("chat-1", "", "", "check the overnight orders",
              at ?? DateTimeOffset.UtcNow.AddMinutes(5), null, null, repeat);

    // ── Reading a recurrence ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hourly", 11, 30)]
    [InlineData("every hour", 11, 30)]
    [InlineData("every 2 hours", 12, 30)]
    [InlineData("every 15 minutes", 10, 45)]
    public void A_plain_interval_comes_round_one_period_from_now(string repeat, int hour, int minute)
    {
        Assert.True(ScheduleStore.TryParseRepeat(repeat, Thursday1030, out var next));
        Assert.Equal(new DateTimeOffset(2026, 8, 20, hour, minute, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("daily at 08:00", 21, 8, 0)]     // 08:00 has gone today → tomorrow
    [InlineData("daily at 14:00", 20, 14, 0)]    // 14:00 hasn't → today
    [InlineData("daily at 7am", 21, 7, 0)]
    [InlineData("daily at 7pm", 20, 19, 0)]
    [InlineData("daily at 12am", 21, 0, 0)]      // midnight, not noon
    [InlineData("daily at 12pm", 20, 12, 0)]     // noon, not midnight
    public void A_time_of_day_means_the_next_time_that_clock_comes_round(string repeat, int day, int hour, int minute)
    {
        Assert.True(ScheduleStore.TryParseRepeat(repeat, Thursday1030, out var next));
        Assert.Equal(new DateTimeOffset(2026, 8, day, hour, minute, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Weekly_on_a_named_day_finds_that_day()
    {
        // Set on a Thursday, wanted on a Monday → the 24th.
        Assert.True(ScheduleStore.TryParseRepeat("weekly on monday at 09:00", Thursday1030, out var next));
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero), next);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
    }

    [Fact]
    public void Weekly_with_no_day_named_keeps_the_day_it_was_set_on()
    {
        // A week later, same weekday — the only reading of "weekly at 09:00" that doesn't invent a day.
        Assert.True(ScheduleStore.TryParseRepeat("weekly at 09:00", Thursday1030, out var next));
        Assert.Equal(DayOfWeek.Thursday, next.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Every_weekday_skips_the_weekend()
    {
        // Friday 10:30, wanting 07:30 → Saturday is next by the clock, Monday is next by the rule.
        var friday = new DateTimeOffset(2026, 8, 21, 10, 30, 0, TimeSpan.Zero);
        Assert.True(ScheduleStore.TryParseRepeat("every weekday at 07:30", friday, out var next));
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 7, 30, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("")]
    [InlineData("every so often")]
    [InlineData("second tuesday of the month")]
    [InlineData("0 8 * * *")]                 // cron isn't the vocabulary here
    [InlineData("every 0 hours")]
    [InlineData("daily at 25:00")]
    [InlineData("hourly at 09:00")]           // a daily job wearing the wrong word
    [InlineData("weekly on someday at 09:00")]
    public void A_phrase_it_cannot_read_is_refused_rather_than_stored(string repeat)
    {
        // Refused at the point someone can still fix it. Accepting anything and working it out at fire time is
        // how you get a task that sits in the list looking scheduled and never goes off.
        Assert.False(ScheduleStore.TryParseRepeat(repeat, Thursday1030, out _));
    }

    // ── One-off times still work ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("in 25m")]
    [InlineData("in 2 hours")]
    [InlineData("in 3 days")]
    [InlineData("2026-06-26T14:27")]
    public void The_one_off_times_are_untouched(string when)
    {
        Assert.True(ScheduleStore.TryParseWhen(when, Thursday1030, out var at));
        Assert.NotEqual(default, at);
    }

    // ── Coming back ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_one_off_is_done_when_it_has_been()
    {
        var store = NewStore();
        var t = Add(store);

        store.Complete(t.Id, result: "Nothing overnight.");

        var after = store.Get(t.Id)!;
        Assert.Equal("done", after.Status);
        Assert.Equal(1, after.Runs);
        Assert.Equal("Nothing overnight.", after.LastResult);
        Assert.NotNull(after.LastFiredAt);
    }

    [Fact]
    public void A_recurring_task_books_its_next_one_instead_of_retiring()
    {
        var store = NewStore();
        var t = Add(store, "daily at 08:00");
        var firstDue = t.FireAt;

        store.Complete(t.Id, result: "Three came in.");

        var after = store.Get(t.Id)!;
        Assert.Equal("pending", after.Status);
        Assert.True(after.FireAt > firstDue);
        Assert.Equal(1, after.Runs);
    }

    [Fact]
    public void One_bad_morning_does_not_end_a_daily_job()
    {
        // A site being down at 07:00 is not a reason never to look again. Retiring on the first failure is how a
        // schedule quietly becomes a schedule of one.
        var store = NewStore();
        var t = Add(store, "daily at 08:00");

        store.Complete(t.Id, failed: true, result: "Failed: the site timed out");

        var after = store.Get(t.Id)!;
        Assert.Equal("pending", after.Status);
        Assert.Contains("timed out", after.LastResult);
    }

    [Fact]
    public void A_long_result_is_kept_short_enough_to_read()
    {
        var store = NewStore();
        var t = Add(store);

        store.Complete(t.Id, result: new string('x', 5000));

        Assert.True(store.Get(t.Id)!.LastResult!.Length <= 601);
    }

    // ── The tick ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_what_is_actually_due_is_due()
    {
        var store = NewStore();
        var soon = Add(store, at: DateTimeOffset.UtcNow.AddMinutes(-1));
        Add(store, at: DateTimeOffset.UtcNow.AddHours(1));

        var due = store.Due(DateTimeOffset.UtcNow);

        Assert.Single(due);
        Assert.Equal(soon.Id, due[0].Id);
    }

    [Fact]
    public void A_paused_task_keeps_its_place_and_stays_put()
    {
        // Pausing rather than cancelling, because a daily job you want to stop for a week is the common case and
        // cancelling loses the wording you'd have to type again.
        var store = NewStore();
        var t = Add(store, "daily at 08:00", DateTimeOffset.UtcNow.AddMinutes(-1));

        store.Edit(t.Id, paused: true);

        Assert.Empty(store.Due(DateTimeOffset.UtcNow));
        Assert.Single(store.All());                       // still listed
        Assert.Equal("pending", store.Get(t.Id)!.Status);  // and still scheduled

        store.Edit(t.Id, paused: false);
        Assert.Single(store.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Claiming_happens_once_so_one_tick_fires_one_task_once()
    {
        var store = NewStore();
        var t = Add(store, at: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.True(store.TryClaim(t.Id));
        Assert.False(store.TryClaim(t.Id));
    }

    [Fact]
    public void A_crash_mid_fire_leaves_it_to_fire_again()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schedules-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = new ScheduleStore(path, json);
        var t = first.Add("chat-1", "", "", "check the orders", DateTimeOffset.UtcNow.AddMinutes(-1), null, null);
        first.TryClaim(t.Id);   // now "firing", and the process dies here

        Assert.Equal("pending", new ScheduleStore(path, json).Get(t.Id)!.Status);
    }

    // ── Editing ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_recurrence_that_cannot_be_read_is_refused_on_edit_too()
    {
        var store = NewStore();
        var t = Add(store, "daily at 08:00");

        Assert.False(store.Edit(t.Id, repeat: "whenever you fancy"));
        Assert.Equal("daily at 08:00", store.Get(t.Id)!.Repeat);   // left exactly as it was
    }

    [Fact]
    public void A_new_rhythm_takes_effect_now_not_after_one_more_of_the_old_one()
    {
        // Moving something off "every 2 minutes" and having it fire in ninety seconds anyway is the change
        // appearing not to have worked: the list says daily and the thing goes off immediately.
        var store = NewStore();
        var t = Add(store, "every 2 minutes", DateTimeOffset.UtcNow.AddMinutes(2));

        store.Edit(t.Id, repeat: "daily at 07:30");

        var after = store.Get(t.Id)!;

        // The NEXT 07:30, computed rather than approximated. This used to assert "more than thirty minutes away",
        // which is only true for most of the day: run it at 07:13 and the correct answer is seventeen minutes, so the
        // test failed every morning between seven and half past for reasons that had nothing to do with the code.
        var now = DateTimeOffset.Now;
        var todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, 7, 30, 0, now.Offset);
        var expected = todayAt > now ? todayAt : todayAt.AddDays(1);

        Assert.Equal(expected, after.FireAt.ToLocalTime());
        // And emphatically not the rhythm it just left: the old one would have fired inside two minutes.
        Assert.True(after.FireAt > now.AddMinutes(2), "the old two-minute cadence must not survive the edit");
    }

    [Fact]
    public void An_explicit_time_wins_over_the_recurrence_it_was_given_with()
    {
        var store = NewStore();
        var t = Add(store, "daily at 07:30");
        var wanted = DateTimeOffset.UtcNow.AddHours(3);

        store.Edit(t.Id, fireAtUtc: wanted, repeat: "weekly on monday at 09:00");

        Assert.Equal(wanted, store.Get(t.Id)!.FireAt);
    }

    [Fact]
    public void Re_saving_the_same_recurrence_leaves_the_next_run_where_it_was()
    {
        // Editing the wording of the task shouldn't quietly move tomorrow's run.
        var store = NewStore();
        var t = Add(store, "daily at 07:30");
        var due = store.Get(t.Id)!.FireAt;

        store.Edit(t.Id, repeat: "daily at 07:30", taskText: "check the orders and the returns");

        Assert.Equal(due, store.Get(t.Id)!.FireAt);
    }

    [Fact]
    public void Clearing_the_recurrence_makes_it_a_one_off()
    {
        var store = NewStore();
        var t = Add(store, "daily at 08:00");

        Assert.True(store.Edit(t.Id, repeat: ""));

        Assert.False(store.Get(t.Id)!.Recurring);
        store.Complete(t.Id);
        Assert.Equal("done", store.Get(t.Id)!.Status);
    }

    [Fact]
    public void Giving_a_finished_task_a_new_time_puts_it_back_in_the_queue()
    {
        // The only thing anyone means by editing a done one-off. The alternative is a row you can edit that then
        // sits there doing nothing.
        var store = NewStore();
        var t = Add(store);
        store.Complete(t.Id);

        store.Edit(t.Id, fireAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal("pending", store.Get(t.Id)!.Status);
    }

    [Fact]
    public void Editing_something_that_is_not_there_fails_rather_than_inventing_it()
    {
        Assert.False(NewStore().Edit("99", paused: true));
    }

    // ── Which conversation it belongs to ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_slack_thread_splits_into_its_parts()
    {
        Assert.True(ScheduleStore.TryParseSlackSessionId("slack:C123:1699999999.001", out var c, out var ts));
        Assert.Equal("C123", c);
        Assert.Equal("1699999999.001", ts);
    }

    [Theory]
    [InlineData("d290f1ee-6c54-4b01-90e6-d701748f0851")]
    [InlineData("task-abc123")]
    [InlineData("slack:")]
    [InlineData("slack:C123")]
    public void Anything_else_is_its_own_whole_address(string sessionId)
    {
        // False here is not a failure — it's how a web conversation says it needs no channel and no thread. The
        // gate that treated it as one is why the web app never had scheduling.
        Assert.False(ScheduleStore.TryParseSlackSessionId(sessionId, out _, out _));
    }

    [Fact]
    public void A_task_set_in_a_web_conversation_belongs_to_it_and_is_listed_under_it()
    {
        var store = NewStore();
        store.Add("d290f1ee-6c54-4b01-90e6-d701748f0851", "", "", "check the orders",
                  DateTimeOffset.UtcNow.AddHours(1), null, null, "daily at 08:00");

        var mine = store.PendingFor("d290f1ee-6c54-4b01-90e6-d701748f0851");

        Assert.Single(mine);
        Assert.True(mine[0].Recurring);
        Assert.Empty(store.PendingFor("some-other-chat"));
    }

    public void Dispose()
    {
        foreach (var p in _paths) { try { File.Delete(p); } catch { } }
    }
}
