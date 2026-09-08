using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The turn that reports a finished task can talk, look things up, and nothing else.
///
/// Given the full toolset it did more than talk: a worker handed back a partial result ("I've hit my tool
/// budget, here's where things stand"), and the re-voice read that as unfinished work and called message_task to
/// carry on — seven seconds after the task had been marked done. The user watched a job finish and immediately
/// restart, and the resumed worker picked up from its own opening premise (log into Ocado) which had been ruled
/// out two answers earlier.
/// </summary>
public class RevoiceToolsTests
{
    /// <summary>The same filter the re-voice applies to the orchestrator's toolset.</summary>
    private static string[] RevoiceNames(IEnumerable<string> all) =>
        all.Where(n => n is not ("delegate" or "message_task")).ToArray();

    private static readonly string[] OrchestratorToolNames =
    {
        "delegate", "message_task", "task_status", "cancel_task", "list_tasks", "run_result",
        "search_memory", "set_memory", "memory_overview", "forget_memory",
        "find_project", "create_project", "list_projects", "project_summary", "promote_file",
    };

    [Fact]
    public void It_cannot_start_new_work()
    {
        Assert.DoesNotContain("delegate", RevoiceNames(OrchestratorToolNames));
    }

    [Fact]
    public void It_cannot_re_open_the_task_it_is_reporting_on()
    {
        // The exact call that caused the restart.
        Assert.DoesNotContain("message_task", RevoiceNames(OrchestratorToolNames));
    }

    [Fact]
    public void It_can_still_look_things_up_to_report_accurately()
    {
        var names = RevoiceNames(OrchestratorToolNames);

        Assert.Contains("search_memory", names);
        Assert.Contains("project_summary", names);
        Assert.Contains("run_result", names);
    }

    [Fact]
    public void It_can_still_record_what_was_learned_and_file_what_was_produced()
    {
        // Recording a fact and shelving a deliverable are part of finishing, not of starting something new.
        var names = RevoiceNames(OrchestratorToolNames);

        Assert.Contains("set_memory", names);
        Assert.Contains("promote_file", names);
    }

    [Fact]
    public void Only_the_two_are_withheld()
    {
        // A narrow cut: everything else the orchestrator can do, the re-voice can still do.
        var removed = OrchestratorToolNames.Except(RevoiceNames(OrchestratorToolNames)).ToList();

        Assert.Equal(2, removed.Count);
    }
}
