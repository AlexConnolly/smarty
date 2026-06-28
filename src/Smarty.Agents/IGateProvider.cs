using System.Threading;
using System.Threading.Tasks;

namespace Smarty.Agents;

/// <summary>
/// A provider that requests permission from the user before executing sensitive operations (e.g. running shell commands).
/// </summary>
public interface IGateProvider
{
    /// <summary>
    /// Request access for a specific action. Awaits/hangs until the user approves or denies it.
    /// </summary>
    /// <param name="action">The name of the action (e.g., "run_shell_command").</param>
    /// <param name="description">Additional details about the action (e.g., the command line).</param>
    /// <param name="ct">The token to monitor for cancellation.</param>
    /// <returns>True if the user approved the request; otherwise, false.</returns>
    Task<bool> RequestAccessAsync(string action, string description, CancellationToken ct = default);
}
