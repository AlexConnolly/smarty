using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Smarty.Slack;

/// <summary>One message in a Slack thread, normalised from the Events API / conversations.replies shapes.</summary>
public sealed record SlackMessage(string? User, string? BotId, string Text, string Ts, string? ThreadTs);

/// <summary>
/// A thin hand-rolled Slack Web API client (and the Socket Mode handshake). Honours the repo's "no external
/// deps" ethos — just HttpClient + System.Text.Json. Only the handful of methods Smarty needs: identify
/// itself, open a Socket Mode connection, post into a thread, read a thread, and resolve user names.
/// </summary>
public sealed class SlackApiClient
{
    private const string Base = "https://slack.com/api/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _botToken;
    private readonly string _appToken;

    // user id -> display name, resolved once (a thread's authors rarely change).
    private readonly Dictionary<string, string> _names = new();
    private readonly SemaphoreSlim _namesLock = new(1, 1);

    public SlackApiClient(string botToken, string appToken)
    {
        _botToken = botToken;
        _appToken = appToken;
    }

    /// <summary>Confirm the token and return the bot's own user id (so we can ignore our own messages).</summary>
    public async Task<string> AuthTestAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync("auth.test", _botToken, new(), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"auth.test failed: {root.GetPropertyOrNull("error")}");
        return root.GetProperty("user_id").GetString()!;
    }

    /// <summary>Open a Socket Mode connection and return the one-time WebSocket URL to connect to.</summary>
    public async Task<string> OpenSocketUrlAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync("apps.connections.open", _appToken, new(), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"apps.connections.open failed: {root.GetPropertyOrNull("error")}");
        return root.GetProperty("url").GetString()!;
    }

    /// <summary>Post a message into a thread. Returns the new message ts (or null on failure — best-effort).</summary>
    public async Task<string?> PostMessageAsync(string channel, string threadTs, string text, CancellationToken ct = default)
    {
        try
        {
            using var doc = await PostAsync("chat.postMessage", _botToken,
                new() { ["channel"] = channel, ["thread_ts"] = threadTs, ["text"] = text }, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                Console.Error.WriteLine($"[slack] chat.postMessage failed: {root.GetPropertyOrNull("error")}");
                return null;
            }
            return root.GetPropertyOrNull("ts");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[slack] chat.postMessage error: {ex.Message}");
            return null;
        }
    }

    /// <summary>All messages in a thread, oldest first — used to backfill what humans said before tagging us.</summary>
    public async Task<IReadOnlyList<SlackMessage>> GetThreadRepliesAsync(string channel, string threadTs, CancellationToken ct = default)
    {
        try
        {
            using var doc = await PostAsync("conversations.replies", _botToken,
                new() { ["channel"] = channel, ["ts"] = threadTs, ["limit"] = "50" }, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean() || !root.TryGetProperty("messages", out var msgs))
                return Array.Empty<SlackMessage>();

            var list = new List<SlackMessage>();
            foreach (var m in msgs.EnumerateArray())
                list.Add(new SlackMessage(
                    m.GetPropertyOrNull("user"),
                    m.GetPropertyOrNull("bot_id"),
                    m.GetPropertyOrNull("text") ?? "",
                    m.GetPropertyOrNull("ts") ?? "",
                    m.GetPropertyOrNull("thread_ts")));
            return list;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[slack] conversations.replies error: {ex.Message}");
            return Array.Empty<SlackMessage>();
        }
    }

    /// <summary>A user's display name (cached). Falls back to the raw id if it can't be resolved.</summary>
    public async Task<string> GetUserNameAsync(string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return "someone";
        lock (_names) if (_names.TryGetValue(userId, out var cached)) return cached;

        await _namesLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_names) if (_names.TryGetValue(userId, out var cached)) return cached;
            string name = userId;
            try
            {
                using var doc = await PostAsync("users.info", _botToken, new() { ["user"] = userId }, ct).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.GetProperty("ok").GetBoolean() && root.TryGetProperty("user", out var u))
                {
                    var profile = u.TryGetProperty("profile", out var p) ? p : default;
                    name = profile.GetPropertyOrNull("display_name") is { Length: > 0 } dn ? dn
                        : profile.GetPropertyOrNull("real_name") is { Length: > 0 } rn ? rn
                        : u.GetPropertyOrNull("name") ?? userId;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[slack] users.info error: {ex.Message}"); }
            lock (_names) _names[userId] = name;
            return name;
        }
        finally { _namesLock.Release(); }
    }

    // Slack's Web API methods take FORM-ENCODED params. (A JSON body is honoured for a few write methods like
    // chat.postMessage, but the read methods — users.info, conversations.replies — silently ignore it and fail
    // with user_not_found / missing args. Form-encoding works for every method, so we use it throughout.)
    private async Task<JsonDocument> PostAsync(string method, string token, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + method);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new FormUrlEncodedContent(form);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }
}

internal static class JsonElementExtensions
{
    /// <summary>A string property's value, or null if absent/not a string — keeps the call sites terse.</summary>
    public static string? GetPropertyOrNull(this System.Text.Json.JsonElement el, string name) =>
        el.ValueKind == System.Text.Json.JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;
}
