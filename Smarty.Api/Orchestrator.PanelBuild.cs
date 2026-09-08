using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Running a panel build as states: the coordinator, and the worker that runs one state.
///
/// <para>
/// The coordinator runs no model. It walks <see cref="PanelMachine"/>, and each state it reaches becomes a child
/// task with its own brief, its own tools and its own transcript. That last part is the expensive-sounding one and
/// is the point: a state that inherits the previous state's transcript inherits its dead ends, pays for them on
/// every turn, and picks up its habits — the build that spent four minutes on one API had spent the first minute
/// deciding that API was the answer, and every later turn re-read that decision as context.
/// </para>
/// </summary>
public sealed partial class Orchestrator
{
    /// <summary>
    /// How long to wait for a freshly published panel to actually load something before looking at it.
    /// </summary>
    /// <remarks>
    /// Published is not loaded. A panel whose source is the user's own browser can take a while, and photographing
    /// it in between catches its loading branch — which reads as a broken panel and had a perfectly good one
    /// rebuilt. Ninety seconds is longer than any server load and short enough not to hold a build open.
    /// </remarks>
    private static readonly TimeSpan WaitForFirstLoad = TimeSpan.FromSeconds(90);

    private async Task DrivePanelBuildAsync(Session session, TaskInfo parent, string panelId)
    {
        if (_widgets is null) return;

        var panel = _widgets.Get(panelId);
        if (panel is null)
        {
            parent.Result = "That panel is gone — nothing to build.";
            return;
        }

        var record = _widgets.Begin(panelId, parent.PanelShows ?? parent.Description);
        var board = new WidgetBoard(_widgets);
        var runner = new PanelStateWorker(this, session, parent, panelId);

        // A build picked back up after a restart re-enters at the state it died in — but a restart takes the design
        // down with it, and a panel being watchable is an invariant rather than an ordering detail. So if there is
        // nothing on the page, draw it again before going any further. Not past publishing: by then the design has
        // legitimately come down, because the real data replaced it.
        if (record.Step is PanelStep.Research or PanelStep.Prove or PanelStep.Bind && !board.ShowsDesign(panelId))
        {
            Trace($"[panel {panelId}] resuming at {record.Step} with no design on the page — drawing it again");
            record.Step = PanelStep.Design;
            record.Attempts[PanelStep.Design] = 0;
        }

        Trace($"[panel {panelId}] building as states, from {record.Step}");
        await PanelRun.DriveAsync(record, runner, board, parent.Cts.Token).ConfigureAwait(false);

        // The record IS the panel's, so it is already saved; this only forces the terminal state out to disk.
        _widgets.Produced(panelId, _ => { });
        parent.Result = Says(record);
        Trace($"[panel {panelId}] {record.Step} after {record.Log.Count} states: {Snip(parent.Result, 120)}");

        // The coordinator finishes itself, exactly as a plan's does. It returns early out of the worker loop, so
        // nothing else will set its status or put its pill down — and a pill nobody can put down spins for ever and
        // survives a refresh, because the replay finds a start and no end.
        parent.Status = parent.Cts.IsCancellationRequested ? "cancelled" : "done";
        session.Append("working_done", Json(new
        {
            id = parent.Id,
            status = parent.Status,
            msgId = parent.OriginMessageId,
            at = DateTimeOffset.UtcNow,
        }));
    }

    /// <summary>What the build tells the user, from the record rather than from a worker's closing paragraph.</summary>
    /// <remarks>
    /// The old version handed back whatever the worker last said, which is why a build that failed reported four
    /// paragraphs of self-assessment ("I have to stop here and be honest…") with the actual reason in the middle of
    /// the third. The states know what happened without being asked.
    /// </remarks>
    private static string Says(PanelBuildRecord record) => record.Step switch
    {
        PanelStep.Done when record.Kind is { Length: > 0 } kind =>
            $"The panel is live, built on the {kind} kind.",
        PanelStep.Done => "The panel is live.",
        _ => record.Blocked ?? "The build stopped short.",
    };

    /// <summary>
    /// One state, run as a worker. Everything state-specific is decided here and nowhere else.
    /// </summary>
    private sealed class PanelStateWorker : IPanelStepRunner
    {
        private readonly Orchestrator _o;
        private readonly Session _session;
        private readonly TaskInfo _parent;
        private readonly string _panelId;

        internal PanelStateWorker(Orchestrator o, Session session, TaskInfo parent, string panelId)
        {
            _o = o;
            _session = session;
            _parent = parent;
            _panelId = panelId;
        }

        public async Task<PanelStepOutcome> RunAsync(
            PanelBuildRecord record, PanelStep step, string note, CancellationToken ct)
        {
            // The check is not a worker at all: it is a screenshot and a judgement, and giving it a model with tools
            // would invite it to start fixing what it found — which is the machine's decision, not its.
            if (step == PanelStep.Verify) return await CheckAsync(record, ct).ConfigureAwait(false);

            var panel = _o._widgets?.Get(_panelId);
            if (panel is null) return PanelStepOutcome.Failed("The panel is gone.");

            var child = new TaskInfo
            {
                Id = _session.NextTaskId(),
                Description = PanelBuildBriefs.For(step, panel, record, note),
                ParentTaskId = _parent.Id,
                Persona = "software_engineer",
                Project = _parent.Project,
                UserScope = _parent.UserScope,
                Room = _parent.Room,
                UserName = _parent.UserName,
                BuildsWidget = _panelId,
                PanelStep = step,
            };
            child.WorkspaceDir = _o.CreateWorkspace(_session, child, null);
            _session.Tasks[child.Id] = child;

            await _o.DriveWorker(_session, child, child.Description).ConfigureAwait(false);

            if (child.Status == "cancelled" || ct.IsCancellationRequested)
                return PanelStepOutcome.Stop("The build was stopped.");

            // A state that stopped to ask the user something ends the build. Running it again would ask the same
            // question in a louder voice, and the machine has no way to answer it — so the question becomes the
            // reason, and it sits on the panel over the design with everything else the build worked out.
            if (child.Status == "waiting")
                return PanelStepOutcome.Stop(child.Pending?.Question is { Length: > 0 } asked
                    ? $"It needs an answer before it can go any further: {asked}"
                    : "It stopped to ask something before it could go any further.");

            // What the state SAID is carried as the reason either way. When the artifact is there it is ignored;
            // when it is not, it is the only account of why — and it is what the user ends up reading.
            var said = Snip(child.Result ?? "", 400);
            return child.Status == "failed"
                ? PanelStepOutcome.Failed(said)
                : new PanelStepOutcome(true, said);
        }

        /// <summary>
        /// Look at the finished panel, and say which part is wrong if any is.
        /// </summary>
        private async Task<PanelStepOutcome> CheckAsync(PanelBuildRecord record, CancellationToken ct)
        {
            if (_o._widgets is null) return PanelStepOutcome.Done();

            var panel = await LoadedAsync(ct).ConfigureAwait(false);
            if (panel is null) return PanelStepOutcome.Done(); // gone, or stopped — not a fault of the panel

            // A panel that publishes and never loads is the loader's failure, and the loader knows why. No
            // photograph improves on the reason it already recorded.
            //
            // Server-fed panels only. A client-mode panel fetches in the page and the server never sees its data —
            // holding it to the same test would fail every camera, map and stream ever built, and those are the
            // panels a photograph is most worth taking of.
            if (panel.ServerLoads && panel.Data is not { Length: > 0 })
                return PanelStepOutcome.Wrong(PanelFault.Data,
                    panel.LastLoadError is { Length: > 0 } why
                        ? $"It published, and the first load failed: {why}"
                        : "It published and then loaded nothing at all.");

            if (_o._checkWidget is null) return PanelStepOutcome.Done();

            var verdict = await _o._checkWidget(panel, record.Shows, ct).ConfigureAwait(false);
            // Undecidable is not the panel's fault. It is up, it loaded, and nothing has said otherwise.
            if (verdict is null) return PanelStepOutcome.Done();

            _o._widgets.Looked(panel.Id, verdict.Working, verdict.Working
                ? verdict.Seen
                : $"{verdict.Wrong ?? "it doesn't show what it should"} (seen: {verdict.Seen})");

            if (verdict.Working) return PanelStepOutcome.Done();

            return PanelStepOutcome.Wrong(Fault(verdict.Fault),
                verdict.Wrong ?? "It doesn't show what it should.");
        }

        /// <summary>Wait for the first load, because publishing is not loading.</summary>
        private async Task<Widget?> LoadedAsync(CancellationToken ct)
        {
            var until = DateTimeOffset.UtcNow + WaitForFirstLoad;
            while (true)
            {
                var panel = _o._widgets?.Get(_panelId);
                if (panel is null) return null;
                if (panel.Data is { Length: > 0 } || panel.LastLoadError is { Length: > 0 }) return panel;
                // Client-mode panels load in the page and the server never sees it; there is nothing to wait for.
                if (!panel.ServerLoads) return panel;
                if (DateTimeOffset.UtcNow >= until || ct.IsCancellationRequested) return panel;

                try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return panel; }
            }
        }

        private static PanelFault Fault(string? named) => named switch
        {
            "looks" => PanelFault.Looks,
            "data" => PanelFault.Data,
            "source" => PanelFault.Source,
            _ => PanelFault.None,
        };

        private static string Snip(string text, int max)
        {
            var t = text.Trim();
            return t.Length <= max ? t : t[..max].TrimEnd() + "…";
        }
    }
}
