using Smarty.Agents;
using Smarty.Brain;

namespace Smarty.Agents.Tests;

/// <summary>
/// A brain for tests that are not about the brain.
/// </summary>
/// <remarks>
/// The orchestrator needs one to be constructed, and most tests of it care about attachments, plans or worker context
/// rather than memory. So this one holds a real graph — reads and structural writes work exactly as they do in
/// production — behind a model that never answers, which means nothing queued ever reconciles. Filing something is
/// still recorded as waiting, so a test that wants to assert a sentence was filed can.
/// </remarks>
public static class SilentBrain
{
    public static Memory Over(string dir) => new(new Graph(dir), new Mute(), "test-model", dir);

    private sealed class Mute : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ModelStreamEvent.Completed(new ModelResponse());
        }
    }
}
