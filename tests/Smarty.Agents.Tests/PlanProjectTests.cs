using System.Reflection;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// What a PLAN leaves behind on the project it was run for.
///
/// A profile PDF was asked for inside "Friends photo profiles", built correctly, and handed to the user in the
/// chat — and the project's file list stayed empty. The work was routed through a two-step plan, and a plan's
/// coordinator does its delivering in a different place from a single worker: the shelving call lived inside a
/// block gated on being top-level, which a coordinator is not. So a project whose work happened to be PLANNED
/// rather than done in one go collected no files at all, and nothing anywhere said so.
///
/// Its steps were worse off still — they inherited no project, so a step could not write a fact to the project
/// it was working on. That is the same shape as the Dinners failure: a worker with no means to record anything,
/// writing prose claiming it had.
/// </summary>
public class PlanProjectTests
{
    /// <summary>The gate that decides whether a delivered file is copied to its project's shelf.</summary>
    private static readonly MethodInfo KeepForProject =
        typeof(Orchestrator).GetMethod("KeepForProject", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void The_coordinator_shelves_what_it_delivered()
    {
        // Asserted on the source rather than by running a plan: driving one needs a live model, and the defect
        // was purely that the call was absent from this path.
        var source = File.ReadAllText(SourceFile("Smarty.Api", "Orchestrator.cs"));
        var plan = Between(source, "DELIVER the plan's output the SAME structured way", "plan.CurrentStep = plan.Steps.Count");

        Assert.Contains("KeepForProject", plan);
    }

    [Fact]
    public void It_shelves_both_what_finalize_named_and_what_the_steps_handed_over()
    {
        // Either can be the file the user goes looking for later: a step often delivers mid-run, and the
        // finalize only names what it picked at the end.
        var source = File.ReadAllText(SourceFile("Smarty.Api", "Orchestrator.cs"));
        var plan = Between(source, "DELIVER the plan's output the SAME structured way", "plan.CurrentStep = plan.Steps.Count");

        Assert.Contains("planOutcome.Show", plan);
        Assert.Contains("parent.DeliveredFiles", plan);
    }

    [Fact]
    public void A_plan_step_inherits_the_project_it_is_working_on()
    {
        var source = File.ReadAllText(SourceFile("Smarty.Api", "Orchestrator.cs"));
        var creation = Between(source, "child = new TaskInfo", "child.WorkspaceDir = CreateWorkspace");

        Assert.Contains("Project = parent.Project", creation);
    }

    [Fact]
    public void Shelving_still_refuses_a_task_with_no_project()
    {
        // The guard that stops every conversation file being copied somewhere: no project, no shelf.
        var orchestrator = Build(out var root);
        var task = new TaskInfo { Id = "1", Description = "d" };   // no Project

        KeepForProject.Invoke(orchestrator, new object?[] { new Session("s"), task, new[] { "a.pdf" } });

        Assert.False(Directory.Exists(Path.Combine(root, "_projects")));
    }

    [Fact]
    public void A_projects_shelf_is_where_the_api_looks_for_it()
    {
        // The writer and the reader have to agree on the folder, or files land somewhere nothing lists.
        Assert.Equal(
            Path.Combine("root", "_projects", "friends-photo-profiles"),
            Orchestrator.ProjectFilesDirFor("root", "friends-photo-profiles"));
    }

    // --- helpers ---

    private static string SourceFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Smarty.sln"))
                               && !Directory.Exists(Path.Combine(dir.FullName, "Smarty.Api")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static string Between(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"couldn't find \"{from}\" in the source");
        int b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, $"couldn't find \"{to}\" after it");
        return text[a..b];
    }

    private static Orchestrator Build(out string workspaceRoot)
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        workspaceRoot = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        return new Orchestrator(
            "test-model", "http://127.0.0.1:1", () => "worker", json,
            new TrainingLog(Path.Combine(workspaceRoot, "training"), json),
            SilentBrain.Over(workspaceRoot),
            new ProjectStore(Path.Combine(workspaceRoot, "projects.json"), json),
            new ProjectRunStore(Path.Combine(workspaceRoot, "runs.json"), json),
            new OrchestratorOptions { TurnTimeout = TimeSpan.FromSeconds(5) });
    }
}
