using System.Runtime.CompilerServices;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The multi-discipline router. A delegated task is triaged for how many DISCIPLINES it spans: one (or none)
/// runs as a single worker; several get a step plan, run in order with each step's output feeding the next.
/// These cover the decision/parsing logic in <see cref="TaskPlanner"/> — triage, step planning, per-step
/// verification, and routing a refinement back onto a finished plan — with a scripted provider so the verdicts
/// are deterministic and never touch a real model.
/// </summary>
public class WorkPlanRoutingTests
{
    private static TaskPlanner PlannerReturning(string json, out ScriptedProvider provider)
    {
        provider = new ScriptedProvider(json);
        var registry = new ModelProviderRegistry();
        var p = provider;
        registry.Register("scripted", _ => p);
        return new TaskPlanner(new ModelSpec("scripted", "stub"), registry);
    }

    [Fact]
    public async Task Triage_single_discipline_routes_to_one_persona()
    {
        var planner = PlannerReturning("{\"personas\":[\"data_scientist\"]}", out _);
        var disciplines = await planner.TriageDisciplinesAsync("analyse this csv", "- data_scientist: data", default);
        Assert.Equal(new[] { "data_scientist" }, disciplines);
    }

    [Fact]
    public async Task Triage_multi_discipline_returns_ordered_set()
    {
        var planner = PlannerReturning(
            "{\"personas\":[\"data_scientist\",\"product_manager\",\"software_engineer\"]}", out _);
        var disciplines = await planner.TriageDisciplinesAsync("segment, plan, build", "roster", default);
        Assert.Equal(new[] { "data_scientist", "product_manager", "software_engineer" }, disciplines);
    }

    [Fact]
    public async Task Triage_malformed_output_fails_open_to_empty()
    {
        // A bad/non-JSON verdict must NOT throw — routing falls back to the ordinary single-worker path.
        var planner = PlannerReturning("not json at all", out _);
        var disciplines = await planner.TriageDisciplinesAsync("anything", "roster", default);
        Assert.Empty(disciplines);
    }

    [Fact]
    public async Task PlanSteps_parses_ordered_steps_with_personas_and_outputs()
    {
        const string json = """
        {"steps":[
          {"persona":"data_scientist","instruction":"profile the csv","produces":"customer profile"},
          {"persona":"product_manager","instruction":"draft tickets","produces":"jira tickets"}
        ]}
        """;
        var planner = PlannerReturning(json, out _);
        var plan = await planner.PlanStepsAsync("segment then ticket", "roster", default);

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Steps.Count);
        Assert.Equal("data_scientist", plan.Steps[0].Persona);
        Assert.Equal("customer profile", plan.Steps[0].Produces);
        Assert.Equal("product_manager", plan.Steps[1].Persona);
        Assert.Equal(0, plan.CurrentStep);
    }

    [Fact]
    public async Task PlanSteps_empty_returns_null()
    {
        var planner = PlannerReturning("{\"steps\":[]}", out _);
        Assert.Null(await planner.PlanStepsAsync("x", "roster", default));
    }

    [Fact]
    public async Task VerifyStep_empty_result_fails_without_calling_the_model()
    {
        var planner = PlannerReturning("{\"ok\":true}", out var provider);
        var (ok, _) = await planner.VerifyStepAsync("do x", "an artifact", "   ", default);
        Assert.False(ok);
        Assert.Equal(0, provider.Calls); // the empty-result short-circuit must not spend a model call
    }

    [Fact]
    public async Task VerifyStep_accepts_a_satisfactory_result()
    {
        var planner = PlannerReturning("{\"ok\":true}", out _);
        var (ok, _) = await planner.VerifyStepAsync("do x", "an artifact", "here is the artifact", default);
        Assert.True(ok);
    }

    [Fact]
    public async Task VerifyStep_rejects_with_reason()
    {
        var planner = PlannerReturning("{\"ok\":false,\"reason\":\"no file produced\"}", out _);
        var (ok, reason) = await planner.VerifyStepAsync("do x", "a file", "I couldn't.", default);
        Assert.False(ok);
        Assert.Equal("no file produced", reason);
    }

    [Fact]
    public async Task VerifyStep_verifier_failure_does_not_block_the_plan()
    {
        // A flaky/empty verifier response must not stall a plan — treat as pass.
        var planner = PlannerReturning("", out _);
        var (ok, _) = await planner.VerifyStepAsync("do x", "a file", "some real output", default);
        Assert.True(ok);
    }

    [Fact]
    public async Task RouteRefine_existing_step_returns_zero_based_index()
    {
        var plan = TwoStepPlan();
        var planner = PlannerReturning("{\"step\":2}", out _);
        var (idx, newPersona) = await planner.RouteRefineAsync(plan, "roster", "tweak the tickets", default);
        Assert.Equal(1, idx);
        Assert.Null(newPersona);
    }

    [Fact]
    public async Task RouteRefine_new_discipline_appends_step()
    {
        var plan = TwoStepPlan();
        var planner = PlannerReturning("{\"step\":0,\"persona\":\"software_engineer\"}", out _);
        var (idx, newPersona) = await planner.RouteRefineAsync(plan, "roster", "now email it", default);
        Assert.Equal(-1, idx);
        Assert.Equal("software_engineer", newPersona);
    }

    [Fact]
    public async Task RouteRefine_out_of_range_defaults_to_last_step()
    {
        var plan = TwoStepPlan();
        var planner = PlannerReturning("{\"step\":99}", out _);
        var (idx, _) = await planner.RouteRefineAsync(plan, "roster", "?", default);
        Assert.Equal(1, idx); // clamps to the last step rather than crashing
    }

    [Fact]
    public async Task ResolveBrand_no_clients_returns_house_without_a_model_call()
    {
        var planner = PlannerReturning("{\"brand\":\"adidas\"}", out var provider);
        var brand = await planner.ResolveBrandAsync("make our playbook", new[] { "house" }, default);
        Assert.Equal("house", brand);
        Assert.Equal(0, provider.Calls); // nothing to disambiguate → no spend
    }

    [Fact]
    public async Task ResolveBrand_picks_a_known_client()
    {
        var planner = PlannerReturning("{\"brand\":\"adidas\"}", out _);
        var brand = await planner.ResolveBrandAsync("one-pager for the Adidas launch",
            new[] { "house", "adidas", "nike" }, default);
        Assert.Equal("adidas", brand);
    }

    [Fact]
    public async Task ResolveBrand_unknown_slug_falls_back_to_house()
    {
        // The model must not be able to mount a brand that doesn't exist.
        var planner = PlannerReturning("{\"brand\":\"reebok\"}", out _);
        var brand = await planner.ResolveBrandAsync("something", new[] { "house", "adidas" }, default);
        Assert.Equal("house", brand);
    }

    [Fact]
    public async Task ResolveBrand_malformed_defaults_to_house()
    {
        var planner = PlannerReturning("garbage", out _);
        var brand = await planner.ResolveBrandAsync("x", new[] { "house", "adidas" }, default);
        Assert.Equal("house", brand);
    }

    private static WorkPlan TwoStepPlan() => new()
    {
        Goal = "g",
        Steps = new List<PlanStep>
        {
            new("data_scientist", "profile", "profile"),
            new("product_manager", "tickets", "tickets"),
        },
    };

    /// <summary>A provider that replays one canned response for every call (the planner methods each issue a
    /// single completion), and counts calls so a test can assert the model was/wasn't reached.</summary>
    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly string _content;
        public int Calls { get; private set; }
        public ScriptedProvider(string content) => _content = content;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Calls++;
            yield return new ModelStreamEvent.Completed(new ModelResponse
            {
                Content = _content,
                Finish = FinishReason.Stop,
            });
        }
    }
}
