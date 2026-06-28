using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// An implementation of <see cref="IGateProvider"/> that pushes a permission request to the session stream
/// and blocks the worker task until resolved (approved/denied) by the user.
/// </summary>
public sealed class TaskGateProvider : IGateProvider
{
    private readonly Session _session;
    private readonly TaskInfo _task;
    private readonly JsonSerializerOptions _json;
    private readonly object _lock = new();
    private readonly HashSet<string> _approvedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<GateResolution>> _pendingByTool = new(StringComparer.Ordinal);

    public TaskGateProvider(Session session, TaskInfo task, JsonSerializerOptions json)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _json = json ?? throw new ArgumentNullException(nameof(json));
    }

    public async Task<bool> RequestAccessAsync(string action, string description, CancellationToken ct = default)
    {
        Task<GateResolution>? existingRequest;
        lock (_lock)
        {
            if (_approvedTools.Contains(action))
                return true;

            if (!_pendingByTool.TryGetValue(action, out existingRequest))
                existingRequest = null;
        }

        if (existingRequest is not null)
        {
            try
            {
                var sharedResolution = await existingRequest.WaitAsync(ct).ConfigureAwait(false);
                return sharedResolution.Approved;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        var gateReqId = Guid.NewGuid().ToString("N")[..8];
        var pending = new PendingGateRequest
        {
            GateRequestId = gateReqId,
            Action = action,
            Description = description
        };

        lock (_lock)
        {
            if (_approvedTools.Contains(action))
                return true;

            if (!_pendingByTool.TryGetValue(action, out existingRequest))
            {
                _pendingByTool[action] = pending.CompletionSource.Task;
            }
        }

        if (existingRequest is not null)
        {
            try
            {
                var sharedResolution = await existingRequest.WaitAsync(ct).ConfigureAwait(false);
                return sharedResolution.Approved;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        _task.PendingGate = pending;
        _task.PendingGates[gateReqId] = pending;
        var originalStatus = _task.Status;
        _task.Status = "waiting_gate";

        // Append 'gate_request' event to the session event stream so the UI / Slack displays the prompt
        _session.Append("gate_request", JsonSerializer.Serialize(new
        {
            id = _task.Id,
            gateRequestId = gateReqId,
            action = action,
            description = description
        }, _json));

        // When/if the cancellation token triggers, complete the TCS as false (access denied)
        using var registration = ct.Register(() => pending.CompletionSource.TrySetResult(new GateResolution(false, false)));

        var resolution = new GateResolution(false, false);
        try
        {
            resolution = await pending.CompletionSource.Task.ConfigureAwait(false);
            if (resolution.Approved && resolution.RememberForTask)
            {
                lock (_lock) _approvedTools.Add(action);
            }
        }
        finally
        {
            lock (_lock) _pendingByTool.Remove(action);
            _task.PendingGates.TryRemove(gateReqId, out _);
            _task.PendingGate = _task.PendingGates.Values.FirstOrDefault();
            _task.Status = _task.PendingGates.IsEmpty ? originalStatus : "waiting_gate";

            // Notify client that the gate is resolved
            _session.Append("gate_resolved", JsonSerializer.Serialize(new
            {
                id = _task.Id,
                gateRequestId = gateReqId,
                approved = resolution.Approved
            }, _json));
        }

        return resolution.Approved;
    }
}
