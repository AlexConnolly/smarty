using System.Net.Http.Json;

namespace Smarty.Api;

/// <summary>
/// Mirrors a Smarty.Api session's event stream into the in-process <see cref="ControlHub"/>. Attached as a
/// session's <see cref="IEventSink"/> at creation, so every event the orchestrator appends is also seen by
/// the control centre — without the orchestrator knowing. Read-only: it never writes back to the session.
/// </summary>
public sealed class ControlSink : IEventSink
{
    private readonly ControlHub _hub;
    private readonly Session _session;
    private readonly string _surface;

    public ControlSink(ControlHub hub, Session session, string surface = "chat")
    {
        _hub = hub;
        _session = session;
        _surface = surface;
    }

    public void OnEvent(string @event, string data)
    {
        var meta = new ConversationMeta(
            Project: _session.PinnedProject ?? _session.CurrentProject,
            UserName: _session.CurrentUserName);
        _hub.Ingest(_session.Id, _surface, @event, data, meta);
    }
}

/// <summary>Fans a session's events out to several sinks at once. Lets a host keep its own rendering sink
/// (e.g. Slack's thread sink) AND mirror the same events somewhere else (the control hub) from one seam.</summary>
public sealed class CompositeEventSink : IEventSink
{
    private readonly IReadOnlyList<IEventSink> _sinks;

    public CompositeEventSink(params IEventSink[] sinks) => _sinks = sinks;

    public void OnEvent(string @event, string data)
    {
        foreach (var s in _sinks)
        {
            try { s.OnEvent(@event, data); }
            catch (Exception ex) { Console.Error.WriteLine($"[composite-sink] {ex.Message}"); }
        }
    }
}

/// <summary>
/// Forwards a session's events to a remote Smarty.Api control hub over HTTP (POST /api/control/ingest). This
/// is the cross-process seam: Smarty.Slack runs in its own process, so its threads can't share the hub's
/// memory — instead each Slack thread attaches one of these and pushes its events to the API. Fire-and-forget
/// and fully best-effort: a control hub being down must never disturb a live Slack conversation.
/// </summary>
public sealed class HubForwardingSink : IEventSink
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _ingestUrl;
    private readonly string? _token;
    private readonly string _conversationId;
    private readonly string _surface;
    private readonly Func<ConversationMeta> _meta;

    public HubForwardingSink(string ingestUrl, string? token, string conversationId, string surface, Func<ConversationMeta> meta)
    {
        _ingestUrl = ingestUrl;
        _token = token;
        _conversationId = conversationId;
        _surface = surface;
        _meta = meta;
    }

    public void OnEvent(string @event, string data)
    {
        var meta = _meta();
        var payload = new IngestPayload
        {
            ConversationId = _conversationId,
            Surface = _surface,
            Event = @event,
            Data = data,
            Title = meta.Title,
            Subtitle = meta.Subtitle,
            Project = meta.Project,
            Persona = meta.Persona,
            UserName = meta.UserName,
        };
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _ingestUrl) { Content = JsonContent.Create(payload) };
                if (!string.IsNullOrEmpty(_token)) req.Headers.Add("X-Control-Token", _token);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            }
            catch { /* the hub may be offline; never let that affect the conversation */ }
        });
    }
}

/// <summary>The wire shape for a forwarded event (POST /api/control/ingest). Mirrors <see cref="ConversationMeta"/>
/// fields plus the event itself, so the receiving hub can register + enrich the conversation in one call.</summary>
public sealed class IngestPayload
{
    public string ConversationId { get; set; } = "";
    public string Surface { get; set; } = "";
    public string Event { get; set; } = "";
    public string Data { get; set; } = "";
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Project { get; set; }
    public string? Persona { get; set; }
    public string? UserName { get; set; }
}
