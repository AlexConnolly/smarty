using Smarty.Api;
using Smarty.Brain;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A tool the model can see and cannot call.
///
/// <para>
/// The orchestrator ADVERTISES its tools from one list and EXECUTES them from a switch on the name, and the two are
/// written in different places. A name in the first and missing from the second is the worst kind of gap: the model does
/// exactly the right thing, is told the tool does not exist, and has nowhere to go — so it tries again. It happened the
/// first time same_thing shipped, four times in one turn, and nothing in the app reported a fault.
/// </para>
/// <para>
/// This is the check that would have caught it before the model did. It reads the memory tools — the set most likely to
/// grow, because a new one is a new verb rather than a new capability — and asserts every name it offers is a name the
/// dispatch answers to.
/// </para>
/// </summary>
public class EveryToolItOffersCanBeCalledTests
{
    /// <summary>
    /// The names the orchestrator's switch has a case for.
    /// </summary>
    /// <remarks>
    /// Read out of the source rather than mocked, because the thing being tested IS that the source has a case for each
    /// name. A hand-kept copy of this list would be a third place to forget.
    /// </remarks>
    private static string Dispatch()
    {
        var here = Path.GetDirectoryName(typeof(EveryToolItOffersCanBeCalledTests).Assembly.Location)!;
        var root = new DirectoryInfo(here);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Smarty.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var source = Path.Combine(root!.FullName, "Smarty.Api", "Orchestrator.cs");
        Assert.True(File.Exists(source), $"couldn't find the orchestrator at {source}");
        return File.ReadAllText(source);
    }

    [Fact]
    public void Every_memory_tool_the_orchestrator_offers_has_a_case_that_runs_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var memory = SilentBrain.Over(dir);

            var offered = MemoryTools.All(memory, memory.Graph, () => null, () => null).Select(t => t.Name).ToList();
            var dispatch = Dispatch();

            Assert.NotEmpty(offered);
            foreach (var name in offered)
                Assert.True(dispatch.Contains($"case \"{name}\":", StringComparison.Ordinal),
                    $"{name} is offered to the model but the orchestrator has no case for it — it would be called and " +
                    "told it does not exist.");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a temp dir */ }
        }
    }
}
