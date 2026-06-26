using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Smarty.Slack;

/// <summary>
/// Drives a Slack Socket Mode connection: opens the WebSocket (a fresh one-time URL each time), reads
/// envelopes, ACKs each one immediately (Slack requires an ack within 3s or it redelivers), and hands the
/// inner Events API payload to a callback. Reconnects automatically on disconnect/drop — Socket Mode
/// rotates the connection periodically, so a clean reconnect loop is expected, not exceptional.
/// </summary>
public sealed class SlackSocketMode
{
    private readonly SlackApiClient _api;

    public SlackSocketMode(SlackApiClient api) => _api = api;

    /// <summary>Run forever: connect, dispatch payloads, reconnect on drop. Returns only on cancellation.</summary>
    public async Task RunAsync(Func<JsonElement, Task> onPayload, CancellationToken ct)
    {
        int backoffSeconds = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string url = await _api.OpenSocketUrlAsync(ct).ConfigureAwait(false);
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                Console.WriteLine("[slack] socket connected — listening for @mentions");
                backoffSeconds = 1; // a healthy connection resets the backoff

                await ReceiveLoopAsync(ws, onPayload, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[slack] socket error: {ex.Message} — reconnecting in {backoffSeconds}s");
                try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoffSeconds = Math.Min(backoffSeconds * 2, 30); // cap the backoff
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, Func<JsonElement, Task> onPayload, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
                    return;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (message.Length == 0) continue;
            await HandleFrameAsync(ws, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), onPayload, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, string frame, Func<JsonElement, Task> onPayload, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(frame); }
        catch { return; } // ignore anything that isn't JSON

        using (doc)
        {
            var root = doc.RootElement;
            string? type = root.GetPropertyOrNull("type");

            // A rotating connection: Slack tells us it's about to drop this socket — let the receive loop
            // end so RunAsync opens a fresh one.
            if (type == "disconnect")
            {
                Console.WriteLine($"[slack] disconnect ({root.GetPropertyOrNull("reason")}) — will reconnect");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", ct).ConfigureAwait(false);
                return;
            }
            if (type == "hello") return; // connection acknowledged by Slack

            // Every actionable envelope carries an envelope_id that MUST be acked within 3s.
            string? envelopeId = root.GetPropertyOrNull("envelope_id");
            if (envelopeId is not null)
            {
                await SendAsync(ws, JsonSerializer.Serialize(new { envelope_id = envelopeId }), ct).ConfigureAwait(false);

                // Only Events API payloads carry messages/mentions we care about. Dispatch on a background
                // task so a slow handler can't delay the next ack (we've already acked above).
                if (type == "events_api" && root.TryGetProperty("payload", out var payload))
                {
                    var clone = payload.Clone();
                    _ = Task.Run(async () =>
                    {
                        try { await onPayload(clone).ConfigureAwait(false); }
                        catch (Exception ex) { Console.Error.WriteLine($"[slack] payload handler: {ex}"); }
                    }, ct);
                }
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }
}
