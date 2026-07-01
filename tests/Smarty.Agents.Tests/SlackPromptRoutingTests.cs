using Smarty.Slack;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The orchestrator's act-vs-confirm contract. Whether Smarty actually clarifies a vague request is the
/// model's judgement, so it can't be pinned with a scripted provider — but the GUIDANCE that produces that
/// judgement is a pure string we can guard against silent removal. These lock in the three-way routing:
/// act on a clear ask, confirm on a tentative one, and never lose the bias to just-go on the obvious stuff.
/// </summary>
public class SlackPromptRoutingTests
{
    private static string Prompt() => SlackPrompts.OrchestratorSystem("Acme", null);

    [Fact]
    public void Tentative_requests_must_be_confirmed_not_actioned()
    {
        var p = Prompt();
        Assert.Contains("vague/half-formed", p);
        Assert.Contains("ask one sharp question", p);
        Assert.Contains("then wait", p);
    }

    [Fact]
    public void Clear_asks_still_keep_the_bias_to_act()
    {
        // The confirm lane must not make it timid on obvious, directive requests.
        var p = Prompt();
        Assert.Contains("clear ask", p);
        Assert.Contains("don't stall", p);
    }

    [Fact]
    public void Now_or_soon_work_is_gated_on_clarity_not_fired_blindly()
    {
        // Vague asks get confirmed rather than fired blindly; no unconditional "just do it now" accelerator.
        var p = Prompt();
        Assert.Contains("confirm the specific thing", p);
        Assert.DoesNotContain("do anything now-or-soon straight away", p);
    }
}
