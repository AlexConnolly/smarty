using System.Text.Json;
using Smarty.Slack;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The Slack live-status contract: a running job is ONE message that ticks along, not a pile of "still on it"
/// posts. Starting a job posts one status line; each progress heartbeat EDITS that same message; finishing flips
/// the spinner to ✅/❌; and a resume gets its own new message. The result itself is a separate assistant message,
/// so it isn't the sink's job here. A recording fake stands in for Slack so no HTTP is involved.
/// </summary>
public class SlackThreadSinkTests
{
    // Records every Slack op as a simple string so a test can assert the exact sequence. Post/blocks return an
    // incrementing ts; because every task completes synchronously, the sink's chained edits run to completion
    // before OnEvent returns — so Calls is fully settled without any waiting.
    private sealed class FakeSlackApi : ISlackThreadApi
    {
        public readonly List<string> Calls = new();
        private int _ts;

        public Task<string?> PostMessageAsync(string channel, string threadTs, string text, CancellationToken ct = default)
        {
            var ts = $"ts{++_ts}";
            Calls.Add($"post {ts}: {text}");
            return Task.FromResult<string?>(ts);
        }

        public Task<string?> PostMessageBlocksAsync(string channel, string threadTs, string text, string blocksJson, CancellationToken ct = default)
        {
            var ts = $"ts{++_ts}";
            Calls.Add($"postblocks {ts}: {text}");
            return Task.FromResult<string?>(ts);
        }

        public Task<bool> UpdateMessageAsync(string channel, string ts, string text, CancellationToken ct = default)
        {
            Calls.Add($"update {ts}: {text}");
            return Task.FromResult(true);
        }

        public Task<bool> UploadFileAsync(string channel, string threadTs, string filePath, string? title = null, string? comment = null, CancellationToken ct = default)
        {
            Calls.Add($"upload {filePath}");
            return Task.FromResult(true);
        }
    }

    // Task events carry the id AS A STRING in production (Session.NextTaskId() is an int rendered ToString()),
    // so the helpers mirror that exactly — emitting a numeric id here would test a shape that never occurs and
    // miss the string-id bug that stopped every job card from posting.
    private static string Working(int id, string task) => JsonSerializer.Serialize(new { id = id.ToString(), task });
    private static string Progress(int id, string task, string note) => JsonSerializer.Serialize(new { id = id.ToString(), task, note });
    private static string Done(int id, string status) => JsonSerializer.Serialize(new { id = id.ToString(), status });

    private static (SlackThreadSink sink, FakeSlackApi api) NewSink()
    {
        var api = new FakeSlackApi();
        return (new SlackThreadSink(api, "C123", "1680000000.000100"), api);
    }

    [Fact]
    public void Job_start_posts_one_status_message()
    {
        var (sink, api) = NewSink();
        sink.OnEvent("working", Working(1, "Check the weather for the weekend"));

        Assert.Single(api.Calls);
        Assert.StartsWith("post ts1:", api.Calls[0]);
        Assert.Contains("⏳", api.Calls[0]);
        Assert.Contains("Check the weather for the weekend", api.Calls[0]);
    }

    [Fact]
    public void Progress_edits_the_same_message_rather_than_posting_new_ones()
    {
        var (sink, api) = NewSink();
        sink.OnEvent("working", Working(1, "Research pubs in Fitzrovia"));
        sink.OnEvent("progress", Progress(1, "Research pubs in Fitzrovia", "searching the web"));
        sink.OnEvent("progress", Progress(1, "Research pubs in Fitzrovia", "reading reviews"));

        // Exactly one post; every heartbeat is an in-place edit of that one message's ts.
        Assert.Single(api.Calls.Where(c => c.StartsWith("post ")));
        var edits = api.Calls.Where(c => c.StartsWith("update ")).ToList();
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.StartsWith("update ts1:", e));
        Assert.Contains("reading reviews", edits[^1]);
    }

    [Fact]
    public void Completion_flips_the_spinner_to_a_tick_on_the_same_message()
    {
        var (sink, api) = NewSink();
        sink.OnEvent("working", Working(1, "Book a table"));
        sink.OnEvent("progress", Progress(1, "Book a table", "calling around"));
        sink.OnEvent("working_done", Done(1, "done"));

        var last = api.Calls[^1];
        Assert.StartsWith("update ts1:", last);
        Assert.Contains("✅", last);
        Assert.DoesNotContain("⏳", last);
    }

    [Fact]
    public void Failure_flips_the_spinner_to_a_cross()
    {
        var (sink, api) = NewSink();
        sink.OnEvent("working", Working(1, "Generate the report"));
        sink.OnEvent("working_done", Done(1, "failed"));

        Assert.Contains("❌", api.Calls[^1]);
    }

    [Fact]
    public void Resume_after_finishing_posts_a_brand_new_status_message()
    {
        var (sink, api) = NewSink();
        sink.OnEvent("working", Working(1, "Draft the email"));
        sink.OnEvent("working_done", Done(1, "done"));      // first run finishes on ts1
        sink.OnEvent("working", Working(1, "Draft the email")); // user refines → resume

        var posts = api.Calls.Where(c => c.StartsWith("post ")).ToList();
        Assert.Equal(2, posts.Count);              // a new message, not a revival of ts1
        Assert.StartsWith("post ts2:", posts[1]);
        Assert.Contains("⏳", posts[1]);
    }

    [Fact]
    public void A_job_that_pauses_to_ask_shows_paused_then_a_new_message_on_reply()
    {
        var (sink, api) = NewSink();
        sink.OnEvent("working", Working(1, "Plan the trip"));
        sink.OnEvent("working_done", Done(1, "waiting")); // paused on a question

        Assert.Contains("⏸", api.Calls[^1]);
        Assert.StartsWith("update ts1:", api.Calls[^1]);

        sink.OnEvent("working", Working(1, "Plan the trip")); // the answer resumes it
        Assert.StartsWith("post ts2:", api.Calls[^1]);
    }

    [Fact]
    public void Concurrent_jobs_each_track_their_own_message()
    {
        var (sink, api) = NewSink();
        sink.OnEvent("working", Working(1, "Task one"));
        sink.OnEvent("working", Working(2, "Task two"));
        sink.OnEvent("progress", Progress(2, "Task two", "halfway"));
        sink.OnEvent("progress", Progress(1, "Task one", "starting out"));
        sink.OnEvent("working_done", Done(1, "done"));
        sink.OnEvent("working_done", Done(2, "failed"));

        // Job 1's edits/finish target ts1; job 2's target ts2 — no crossed wires.
        Assert.Contains("update ts1:", api.Calls.First(c => c.Contains("starting out")));
        Assert.Contains("update ts2:", api.Calls.First(c => c.Contains("halfway")));
        Assert.Contains("✅", api.Calls.First(c => c.StartsWith("update ts1:") && c.Contains("✅")));
        Assert.Contains("❌", api.Calls.First(c => c.StartsWith("update ts2:") && c.Contains("❌")));
    }

    [Fact]
    public void Head_truncates_a_long_task_description_to_one_line()
    {
        var longTask = new string('x', 100);
        var head = SlackThreadSink.Head(longTask);
        Assert.True(head.Length <= 61);
        Assert.EndsWith("…", head);

        Assert.Equal("one line", SlackThreadSink.Head("one\nline"));
    }

    [Theory]
    [InlineData("done", "✅")]
    [InlineData("failed", "❌")]
    [InlineData("cancelled", "🚫")]
    [InlineData("waiting", "⏸️")]
    [InlineData("something-unexpected", "✅")]
    public void FinalLine_maps_each_status_to_its_icon(string status, string icon)
    {
        Assert.Contains(icon, SlackThreadSink.FinalLine(status, "job"));
    }

    [Fact]
    public void A_string_task_id_posts_and_updates_without_throwing()
    {
        // Regression: task ids arrive as JSON strings. Reading them with GetInt32() threw ("requires Number,
        // has String"), which was swallowed by the event sink and silently dropped every job status card.
        var (sink, api) = NewSink();
        string sid = "\"7\""; // a bare string id, exactly the production shape
        sink.OnEvent("working", $"{{\"id\":{sid},\"task\":\"Research the venue\"}}");
        sink.OnEvent("progress", $"{{\"id\":{sid},\"task\":\"Research the venue\",\"note\":\"reading reviews\"}}");
        sink.OnEvent("working_done", $"{{\"id\":{sid},\"status\":\"done\"}}");

        Assert.Single(api.Calls, c => c.StartsWith("post "));           // the card posted…
        Assert.Contains(api.Calls, c => c.Contains("reading reviews")); // …updated in place…
        Assert.Contains("✅", api.Calls[^1]);                           // …and finished on the same message
    }

    [Fact]
    public void A_numeric_message_id_still_works()
    {
        // Message ids remain JSON numbers — make sure the string-tolerant id read didn't break that shape.
        var (sink, api) = NewSink();
        sink.OnEvent("msg_start", "{\"id\":5,\"role\":\"assistant\"}");
        sink.OnEvent("msg_end", "{\"id\":5,\"text\":\"Here's your answer.\"}");

        Assert.Single(api.Calls, c => c.StartsWith("post "));
        Assert.Contains("Here's your answer.", api.Calls[0]);
    }
}
