using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    // channel id -> name (e.g. "food"), resolved once and cached.
    private readonly Dictionary<string, string> _channels = new();

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
    /// <summary>A channel's name (e.g. "food"), cached. Null if it can't be resolved — needs the
    /// channels:read / groups:read scope; without it the call returns missing_scope and we just carry on.</summary>
    public async Task<string?> GetChannelNameAsync(string channelId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(channelId)) return null;
        lock (_channels) if (_channels.TryGetValue(channelId, out var cached)) return cached;
        try
        {
            using var doc = await PostAsync("conversations.info", _botToken, new() { ["channel"] = channelId }, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.GetProperty("ok").GetBoolean() && root.TryGetProperty("channel", out var ch)
                && ch.GetPropertyOrNull("name") is { Length: > 0 } name)
            {
                lock (_channels) _channels[channelId] = name;
                return name;
            }
            Console.Error.WriteLine($"[slack] conversations.info: {root.GetPropertyOrNull("error") ?? "no name"}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[slack] conversations.info error: {ex.Message}"); }
        return null;
    }

    /// <summary>Download a Slack-hosted file to local disk. Slack's url_private(_download) requires the bot
    /// token as a Bearer header (and the files:read scope). Returns true on success — best-effort.</summary>
    public async Task<bool> DownloadFileAsync(string url, string destPath, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[slack] file download failed: HTTP {(int)resp.StatusCode}");
                return false;
            }
            // A missing files:read scope returns Slack's HTML sign-in page (200 text/html), not the file —
            // treat that as a failure so we don't write a login page to disk as if it were the document.
            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("[slack] file download returned HTML (missing files:read scope?) — skipped.");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using var fs = File.Create(destPath);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[slack] file download error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Upload a local file and share it into a thread. Uses Slack's external-upload flow
    /// (files.getUploadURLExternal → POST the bytes → files.completeUploadExternal), which replaced the
    /// deprecated files.upload. Needs the <c>files:write</c> scope. Best-effort: true on success.</summary>
    public async Task<bool> UploadFileAsync(
        string channel, string threadTs, string filePath, string? title = null, string? comment = null, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"[slack] upload: no file at {filePath}");
                return false;
            }
            var info = new FileInfo(filePath);
            string filename = Path.GetFileName(filePath);

            // 1. Reserve a one-time upload URL + file id for this file.
            using var urlDoc = await PostAsync("files.getUploadURLExternal", _botToken, new()
            {
                ["filename"] = filename,
                ["length"] = info.Length.ToString(),
            }, ct).ConfigureAwait(false);
            var urlRoot = urlDoc.RootElement;
            if (!urlRoot.GetProperty("ok").GetBoolean())
            {
                Console.Error.WriteLine($"[slack] getUploadURLExternal failed: {urlRoot.GetPropertyOrNull("error")}");
                return false;
            }
            string uploadUrl = urlRoot.GetProperty("upload_url").GetString()!;
            string fileId = urlRoot.GetProperty("file_id").GetString()!;

            // 2. POST the raw bytes to that URL (multipart form field "file").
            using (var content = new MultipartFormDataContent())
            {
                var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
                content.Add(new ByteArrayContent(bytes), "file", filename);
                using var up = await _http.PostAsync(uploadUrl, content, ct).ConfigureAwait(false);
                if (!up.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"[slack] upload POST failed: HTTP {(int)up.StatusCode}");
                    return false;
                }
            }

            // 3. Complete the upload and share it into the thread.
            var files = new JsonArray { new JsonObject { ["id"] = fileId, ["title"] = title ?? filename } };
            var form = new Dictionary<string, string>
            {
                ["files"] = files.ToJsonString(),
                ["channel_id"] = channel,
                ["thread_ts"] = threadTs,
            };
            if (!string.IsNullOrWhiteSpace(comment)) form["initial_comment"] = comment;
            using var doneDoc = await PostAsync("files.completeUploadExternal", _botToken, form, ct).ConfigureAwait(false);
            var doneRoot = doneDoc.RootElement;
            if (!doneRoot.GetProperty("ok").GetBoolean())
            {
                Console.Error.WriteLine($"[slack] completeUploadExternal failed: {doneRoot.GetPropertyOrNull("error")}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[slack] file upload error: {ex.Message}");
            return false;
        }
    }

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
