using System.Reflection;
using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// What a WORKER knows about here and now.
///
/// The date and the user's whereabouts were assembled into the orchestrator's per-turn context and nowhere else,
/// so the half of the system that only talks had them and the half that actually does things did not. The chat
/// voice would resolve "somewhere near me" perfectly well and then hand a worker a task with no date, no
/// coordinates and no idea what "near" meant — which reads, from the outside, exactly like location sharing
/// having silently stopped working.
/// </summary>
public class WorkerContextTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string Temp(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        _paths.Add(path);
        return path;
    }

    private Orchestrator Build(LocationStore? location)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return new Orchestrator(
            "test-model",
            "http://127.0.0.1:1",
            () => "WORKER PROMPT",
            json,
            new TrainingLog(Temp("training"), json),
            SilentBrain.Over(Temp("brain")),
            new ProjectStore(Temp("projects") + ".json", json),
            new ProjectRunStore(Temp("runs") + ".json", json),
            new OrchestratorOptions
            {
                TurnTimeout = TimeSpan.FromSeconds(5),
                LocationNote = location is null ? null : location.Note,
            });
    }

    /// <summary>The block appended to every worker's system prompt.</summary>
    private static string HereAndNow(Orchestrator orchestrator) =>
        (string)typeof(Orchestrator)
            .GetMethod("HereAndNow", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(orchestrator, Array.Empty<object>())!;

    private LocationStore At(double lat, double lon, string? place, double accuracy = 11)
    {
        var store = new LocationStore(Temp("location") + ".json", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        store.Set(lat, lon, accuracy, place);
        return store;
    }

    [Fact]
    public void A_worker_is_told_where_the_user_is()
    {
        // The exact fix: the doing half now gets the position the talking half already had.
        var block = HereAndNow(Build(At(51.5229, -0.1417, "Cleveland Street, Fitzrovia, Greater London")));

        Assert.Contains("Fitzrovia", block);
        Assert.Contains("51.5229", block);
    }

    [Fact]
    public void A_worker_is_told_what_day_it_is()
    {
        var block = HereAndNow(Build(At(51.5229, -0.1417, "Fitzrovia")));

        Assert.Contains("Today is", block);
        Assert.Contains(DateTime.Now.Year.ToString(), block);
    }

    [Fact]
    public void The_instruction_to_confirm_before_acting_travels_with_it()
    {
        // The worker is the one that would actually book the taxi, so this is the half that most needs the rule.
        var block = HereAndNow(Build(At(51.5229, -0.1417, "Fitzrovia")));

        Assert.Contains("confirm", block);
    }

    [Fact]
    public void With_no_fix_a_worker_is_told_nothing_about_where_it_is()
    {
        // Never a dangling "the user is at" with nothing after it, and never an invented position.
        var block = HereAndNow(Build(location: null));

        Assert.Contains("Today is", block);
        Assert.DoesNotContain("latitude", block);
        Assert.DoesNotContain("The user is", block);
    }

    public void Dispose()
    {
        foreach (var p in _paths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best effort */ }
        }
    }
}
