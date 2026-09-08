using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Smarty.Plugins;

namespace Smarty.Plugin.Roborock;

/// <summary>
/// The link to the vacuums: one MQTT connection to Roborock's broker, shared by every device on the account,
/// carrying the encrypted frames <see cref="RoborockCodec"/> builds.
/// </summary>
/// <remarks>
/// The broker is a relay, not the vacuum — a command is published to the device's inbound topic and the answer
/// arrives on its outbound one, matched to the request by the id inside the encrypted body. So the connection
/// keeps one dictionary of calls still waiting, and every reply either completes one or is a status push the
/// vacuum sent unasked, which is ignored.
/// </remarks>
internal sealed class RoborockConnection : IAsyncDisposable
{
    private readonly RRiot _rriot;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _waiting = new();
    private readonly ConcurrentDictionary<string, string> _localKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connecting = new(1, 1);

    private IMqttClient? _client;

    public RoborockConnection(RRiot rriot) => _rriot = rriot;

    /// <summary>The broker's own credentials, hashed out of the account's — not the account password, and not
    /// the same for any two accounts.</summary>
    internal static (string User, string Password) Credentials(RRiot rriot) => (
        RoborockWebApi.Md5Hex(rriot.U + ":" + rriot.K).Substring(2, 8),
        RoborockWebApi.Md5Hex(rriot.S + ":" + rriot.K)[16..]);

    internal static string InboundTopic(RRiot rriot, string duid) =>
        $"rr/m/i/{rriot.U}/{Credentials(rriot).User}/{duid}";

    internal static string OutboundTopic(RRiot rriot, string duid) =>
        $"rr/m/o/{rriot.U}/{Credentials(rriot).User}/{duid}";

    /// <summary>
    /// Call a method on one device and wait for its answer. Connects on first use and subscribes to that
    /// device the first time it's addressed, so a two-vacuum account costs one connection, not two.
    /// </summary>
    public async Task<JsonElement> CallAsync(
        RoborockDevice device, string method, object? parameters, CancellationToken ct)
    {
        var client = await ConnectedAsync(device, ct).ConfigureAwait(false);

        int requestId = Random.Shared.Next(10_000, 32_767);
        uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var inner = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = parameters ?? Array.Empty<object>(),
        });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["dps"] = new Dictionary<string, string> { ["101"] = inner },
            ["t"] = timestamp,
        });

        var frame = new RoborockFrame(
            Sequence: (uint)Random.Shared.Next(100_000, 999_999),
            Random: (uint)Random.Shared.Next(10_000, 999_999),
            Timestamp: timestamp,
            Protocol: RoborockFrame.RpcRequest,
            Payload: Encoding.UTF8.GetBytes(payload));

        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[requestId] = answer;

        try
        {
            await client.PublishBinaryAsync(
                InboundTopic(_rriot, device.Duid),
                RoborockCodec.Encode(frame, device.LocalKey),
                MqttQualityOfServiceLevel.AtLeastOnce, retain: false, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using (timeout.Token.Register(() => answer.TrySetCanceled()))
                return await answer.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The broker took it and the vacuum said nothing. Almost always asleep or off its dock rather than
            // anything wrong with the request, so it is worth another go.
            throw new InvalidOperationException(
                $"{device.Name} didn't answer '{method}' within 20 seconds — it may be asleep or offline.");
        }
        finally
        {
            _waiting.TryRemove(requestId, out _);
        }
    }

    private async Task<IMqttClient> ConnectedAsync(RoborockDevice device, CancellationToken ct)
    {
        await _connecting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is null || !_client.IsConnected)
            {
                _client?.Dispose();
                _localKeys.Clear();

                var url = new Uri(_rriot.MqttUrl.Replace("ssl://", "mqtts://", StringComparison.OrdinalIgnoreCase));
                var (user, password) = Credentials(_rriot);

                var client = new MqttFactory().CreateMqttClient();
                client.ApplicationMessageReceivedAsync += OnMessage;

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(url.Host, url.Port > 0 ? url.Port : 8883)
                    .WithCredentials(user, password)
                    .WithProtocolVersion(MqttProtocolVersion.V500)
                    .WithCleanSession()
                    .WithTlsOptions(o => o.UseTls(url.Scheme is "mqtts" or "ssl"))
                    .Build();

                var result = await client.ConnectAsync(options, ct).ConfigureAwait(false);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                    throw new InvalidOperationException($"Roborock's message broker refused the connection: {result.ResultCode}.");

                _client = client;
            }

            if (_localKeys.TryAdd(device.Duid, device.LocalKey))
                await _client.SubscribeAsync(
                    OutboundTopic(_rriot, device.Duid), MqttQualityOfServiceLevel.AtLeastOnce, ct).ConfigureAwait(false);

            return _client;
        }
        finally
        {
            _connecting.Release();
        }
    }

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        // The topic ends with the device id, which is what says whose key decrypts this.
        var duid = e.ApplicationMessage.Topic.Split('/').LastOrDefault();
        if (duid is null || !_localKeys.TryGetValue(duid, out var localKey)) return Task.CompletedTask;

        foreach (var frame in RoborockCodec.Decode(e.ApplicationMessage.PayloadSegment, localKey))
        {
            if (frame.Protocol != RoborockFrame.RpcResponse || frame.Payload.Length == 0) continue;

            if (Answer(frame.Payload) is not { } answer) continue;
            if (!_waiting.TryRemove(answer.Id, out var waiting)) continue;

            // A refusal from the vacuum is the answer to this call, not a fault in the connection — so it
            // completes the call that asked, rather than escaping into the MQTT handler where the only
            // symptom would be that same call timing out twenty seconds later with nothing to say.
            if (answer.Error is { } refusal)
                waiting.TrySetException(new PluginDeadEndException($"{duid} refused it: {refusal}"));
            else
                waiting.TrySetResult(answer.Result);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The answer inside a reply frame: JSON, holding a data point keyed "102", holding more JSON. Returns
    /// null for anything else, which is how the status the vacuum pushes while it works is passed over.
    /// </summary>
    internal static (int Id, JsonElement Result, string? Error)? Answer(byte[] payload)
    {
        try
        {
            using var outer = JsonDocument.Parse(payload);
            if (!outer.RootElement.TryGetProperty("dps", out var dps) ||
                !dps.TryGetProperty("102", out var point) ||
                point.GetString() is not { } text) return null;

            using var inner = JsonDocument.Parse(text);
            if (!inner.RootElement.TryGetProperty("id", out var id)) return null;

            var error = inner.RootElement.TryGetProperty("error", out var e) ? e.GetRawText() : null;
            var result = inner.RootElement.TryGetProperty("result", out var r) ? r : default;
            // Cloned because the document is disposed the moment this returns.
            return (id.GetInt32(), result.ValueKind == JsonValueKind.Undefined ? default : result.Clone(), error);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        try { if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false); }
        catch { /* going away anyway */ }
        _client.Dispose();
        _client = null;
    }
}
