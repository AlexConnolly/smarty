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
        int tooManyStreak = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string url = await _api.OpenSocketUrlAsync(ct).ConfigureAwait(false);
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                Console.WriteLine("[slack] socket connected — listening for @mentions");

                var reason = await ReceiveLoopAsync(ws, onPayload, ct).ConfigureAwait(false);

                // Slack rotates connections routinely (a "refresh"/normal disconnect) — reconnect promptly. But
                // "too_many_websockets" means another live connection already holds this app's slot: one we were
                // force-killed out of that Slack hasn't reaped yet, or a second instance running elsewhere.
                // Reconnecting instantly just keeps the slot busy and piles on, so back off (growing, capped) and
                // wait quietly for it to free — don't hammer.
                if (string.Equals(reason, "too_many_websockets", StringComparison.OrdinalIgnoreCase))
                {
                    backoffSeconds = Math.Min(Math.Max(backoffSeconds * 2, 5), 60);
                    if (tooManyStreak++ == 0)
                        Console.Error.WriteLine("[slack] another Socket Mode connection holds this app's slot — backing " +
                            "off until it frees. If it persists, stop the other instance or rotate the app-level token.");
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                else
                {
                    tooManyStreak = 0;
                    backoffSeconds = 1; // healthy rotation — reconnect promptly
                }
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

    // Returns the disconnect REASON when Slack drops the socket with a disconnect frame (so RunAsync can pace
    // the reconnect), or null when the socket simply closed / was cancelled.
    private async Task<string?> ReceiveLoopAsync(ClientWebSocket ws, Func<JsonElement, Task> onPayload, CancellationToken ct)
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
                    return null;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (message.Length == 0) continue;
            var reason = await HandleFrameAsync(ws, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), onPayload, ct)
                .ConfigureAwait(false);
            if (reason is not null) return reason; // a disconnect frame — bubble its reason up to RunAsync
        }
        return null;
    }

    private async Task<string?> HandleFrameAsync(ClientWebSocket ws, string frame, Func<JsonElement, Task> onPayload, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(frame); }
        catch { return null; } // ignore anything that isn't JSON

        using (doc)
        {
            var root = doc.RootElement;
            string? type = root.GetPropertyOrNull("type");

            // A rotating connection: Slack tells us it's about to drop this socket — end the receive loop and
            // return the REASON so RunAsync can pace the reconnect (rotation = prompt; too_many = back off).
            if (type == "disconnect")
            {
                string reason = root.GetPropertyOrNull("reason") ?? "disconnect";
                Console.WriteLine($"[slack] disconnect ({reason}) — will reconnect");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", ct).ConfigureAwait(false);
                return reason;
            }
            if (type == "hello") return null; // connection acknowledged by Slack

            // Every actionable envelope carries an envelope_id that MUST be acked within 3s.
            string? envelopeId = root.GetPropertyOrNull("envelope_id");
            if (envelopeId is not null)
            {
                await SendAsync(ws, JsonSerializer.Serialize(new { envelope_id = envelopeId }), ct).ConfigureAwait(false);

                // Only Events API payloads carry messages/mentions we care about. Dispatch on a background
                // task so a slow handler can't delay the next ack (we've already acked above).
                if ((type == "events_api" || type == "interactive") && root.TryGetProperty("payload", out var payload))
                {
                    JsonElement parsedPayload;
                    if (payload.ValueKind == JsonValueKind.String)
                    {
                        using var docParsed = JsonDocument.Parse(payload.GetString()!);
                        parsedPayload = docParsed.RootElement.Clone();
                    }
                    else
                    {
                        parsedPayload = payload.Clone();
                    }

                    var clone = parsedPayload;
                    _ = Task.Run(async () =>
                    {
                        try { await onPayload(clone).ConfigureAwait(false); }
                        catch (Exception ex) { Console.Error.WriteLine($"[slack] payload handler: {ex}"); }
                    }, ct);
                }
            }
        }
        return null;
    }

    private static async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }
}
