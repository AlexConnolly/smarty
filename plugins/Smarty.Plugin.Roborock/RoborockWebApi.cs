using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Smarty.Plugins;

namespace Smarty.Plugin.Roborock;

/// <summary>The credentials the account hands back for talking to the devices: identity, secrets, and the two
/// addresses (REST and MQTT broker) for the region the account lives in.</summary>
internal sealed record RRiot(string U, string S, string H, string K, string ApiBase, string MqttUrl);

internal sealed record RoborockSession(string Token, RRiot Rriot);

internal sealed record RoborockRoom(long Id, string Name);

internal sealed record RoborockDevice(string Duid, string Name, string LocalKey, string Model, bool Online);

internal sealed record RoborockHome(IReadOnlyList<RoborockDevice> Devices, IReadOnlyList<RoborockRoom> Rooms);

/// <summary>The stored session is no longer accepted. Its own type because the response is otherwise
/// indistinguishable from any other failure, and the right answer to it — throw the token away and ask for a
/// new code — is not the right answer to anything else.</summary>
internal sealed class RoborockSignedOutException : Exception
{
    public RoborockSignedOutException()
        : base("Roborock no longer accepts the stored sign-in.") { }
}

/// <summary>
/// The Roborock account, over their app's own HTTPS API — there is no published one, so this is the flow the
/// phone app performs: find the region for the email, sign in, find the home, then read the home's devices and
/// rooms with a Hawk-signed request against the regional API.
/// </summary>
/// <remarks>
/// Signing in is rate-limited hard at their end (roughly twenty a day), which is why nothing here is called
/// per command: <see cref="RoborockAccount"/> signs in once and holds on to the result.
/// </remarks>
internal sealed class RoborockWebApi
{
    /// <summary>The four regional front doors. An account lives in exactly one of them and the others don't
    /// know the email, so finding the right one is the first request of the day.</summary>
    private static readonly string[] Regions =
    {
        "https://usiot.roborock.com",
        "https://euiot.roborock.com",
        "https://cniot.roborock.com",
        "https://ruiot.roborock.com",
    };

    private readonly HttpClient _http;
    private readonly string _email;
    private readonly string _clientId;

    /// <summary>
    /// <paramref name="device"/> identifies this installation to Roborock, the way the app is identified by the
    /// handset it runs on. It MUST be the same one across a whole sign-in: Roborock ties an emailed code to the
    /// client that asked for it, so a code requested by one and redeemed by another comes back "invalid code" —
    /// which reads exactly like mistyping it, and is the reason this is passed in rather than made up here.
    /// </summary>
    public RoborockWebApi(HttpClient http, string email, string device)
    {
        _http = http;
        _email = email;
        _clientId = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(_email + device)));
    }

    /// <summary>A fresh installation identifier, to be kept for as long as the sign-in is.</summary>
    public static string NewDevice() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).TrimEnd('=');

    /// <summary>
    /// Ask Roborock to email a sign-in code. Their newer endpoint is tried first and the older one stands in
    /// for it, because which one an account answers to has been moving — password sign-in is being retired and
    /// accounts now come back with "need two step validate" instead.
    /// </summary>
    /// <returns>Which endpoint family actually sent it, to be handed back to <see cref="SignInWithCodeAsync"/>.
    /// A code sent by one and redeemed against the other is refused as invalid, so this is not a detail the
    /// caller can be left to guess at.</returns>
    public async Task<string> RequestCodeAsync(CancellationToken ct)
    {
        var regional = await RegionAsync(ct);

        if (_country is not null && _countryCode is not null)
        {
            try
            {
                Sent(await SendAsync(HttpMethod.Post, $"{regional}/api/v4/email/code/send",
                    headers: new() { ["header_clientid"] = _clientId, ["header_clientlang"] = "en" },
                    form: new() { ["email"] = _email, ["type"] = "login", ["platform"] = "" }, ct: ct));
                return "v4";
            }
            catch (PluginDeadEndException) { throw; }
            catch (Exception) { /* their older endpoint still answers for plenty of accounts */ }
        }

        Sent(await SendAsync(HttpMethod.Post,
            Url($"{regional}/api/v1/sendEmailCode", new() { ["username"] = _email, ["type"] = "auth" }),
            headers: new() { ["header_clientid"] = _clientId }, ct: ct));
        return "v1";

        static void Sent(JsonDocument response)
        {
            var code = response.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            if (code == 200) return;

            var message = response.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : null;
            throw code switch
            {
                2008 => new PluginDeadEndException("Roborock has no account for that email address."),
                9002 => new PluginDeadEndException(
                    "Roborock won't send any more codes for now — too many have been asked for. Try again later."),
                _ => new InvalidOperationException(
                    $"Roborock wouldn't send a code ({code}): {message ?? "no reason given"}"),
            };
        }
    }

    /// <summary>
    /// Trade an emailed code for a session, through the SAME endpoint family that sent it — pass back what
    /// <see cref="RequestCodeAsync"/> returned. Crossing the two is refused as an invalid code, which is
    /// indistinguishable from mistyping it and sends everyone hunting for the wrong problem.
    /// </summary>
    public async Task<RoborockSession> SignInWithCodeAsync(string code, string? sentBy, CancellationToken ct)
    {
        var regional = await RegionAsync(ct);

        if (sentBy != "v1" && _country is not null && _countryCode is not null)
        {
            try { return Session(await LoginV4Async(regional, code, ct)); }
            // A refusal is Roborock's answer, not a reason to ask the other endpoint the same question.
            catch (PluginDeadEndException) { throw; }
            catch (Exception) when (sentBy is null) { /* unknown provenance: the older endpoint is worth a try */ }
        }

        return Session(await SendAsync(HttpMethod.Post,
            Url($"{regional}/api/v1/loginWithCode", new()
            {
                ["username"] = _email,
                ["verifycode"] = code,
                ["verifycodetype"] = "AUTH_EMAIL_CODE",
            }),
            headers: new() { ["header_clientid"] = _clientId }, ct: ct));
    }

    /// <summary>The newer sign-in. Their app signs a random string and sends both halves; the endpoint refuses
    /// the login without them, and it wants the account's country, which only the region lookup knows.</summary>
    private async Task<JsonDocument> LoginV4Async(string regional, string code, CancellationToken ct)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var ks = new string(Enumerable.Range(0, 16)
            .Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());

        var signed = await SendAsync(HttpMethod.Post, Url($"{regional}/api/v3/key/sign", new() { ["s"] = ks }),
            headers: new() { ["header_clientid"] = _clientId }, ct: ct);
        var k = signed.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("k", out var kv)
            ? kv.GetString()
            : null;
        if (k is null) throw new InvalidOperationException("Roborock wouldn't sign the sign-in request.");

        return await SendAsync(HttpMethod.Post, $"{regional}/api/v4/auth/email/login/code",
            headers: new()
            {
                ["header_clientid"] = _clientId,
                ["x-mercy-ks"] = ks,
                ["x-mercy-k"] = k,
                ["header_clientlang"] = "en",
                ["header_appversion"] = "4.54.02",
                ["header_phonesystem"] = "iOS",
                ["header_phonemodel"] = "iPhone16,1",
            },
            form: new()
            {
                ["country"] = _country!,
                ["countryCode"] = _countryCode!,
                ["email"] = _email,
                ["code"] = code,
                // The user-agreement version their app currently sends.
                ["majorVersion"] = "14",
                ["minorVersion"] = "0",
            }, ct: ct);
    }

    /// <summary>The session out of a login response, or the clearest available reason there isn't one. Every
    /// code named here is settled by a person doing something, so none of them is worth retrying.</summary>
    internal static RoborockSession Session(JsonDocument login)
    {
        var code = login.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
        if (code != 200)
        {
            var message = login.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : null;
            throw code switch
            {
                2018 => new PluginDeadEndException("That code isn't right, or it has expired. Ask for another one."),
                2031 => new PluginDeadEndException(
                    "Roborock wants a code emailed to you rather than a password. Ask it to send one, then sign in with that."),
                3009 or 3006 => new PluginDeadEndException(
                    "Roborock wants its user agreement accepted again — open the Roborock app, accept it, then sign in here."),
                2008 or 3039 => new PluginDeadEndException("Roborock has no account for that email address."),
                _ => new InvalidOperationException(
                    $"Roborock sign-in failed ({code}): {message ?? "no reason given"}"),
            };
        }

        var data = login.RootElement.GetProperty("data");
        var token = data.GetProperty("token").GetString()
                    ?? throw new InvalidOperationException("Roborock signed in but returned no token.");

        if (!data.TryGetProperty("rriot", out var rriot))
            throw new InvalidOperationException("Roborock signed in but returned no device credentials (rriot).");

        var r = rriot.GetProperty("r");
        return new RoborockSession(token, new RRiot(
            U: rriot.GetProperty("u").GetString()!,
            S: rriot.GetProperty("s").GetString()!,
            H: rriot.GetProperty("h").GetString()!,
            K: rriot.GetProperty("k").GetString()!,
            ApiBase: r.GetProperty("a").GetString()!,
            MqttUrl: r.GetProperty("m").GetString()!));
    }

    /// <summary>The devices on the account and the rooms of the home they're in. The room list is what turns
    /// "clean the kitchen" into a segment id the vacuum understands.</summary>
    public async Task<RoborockHome> HomeAsync(RoborockSession session, CancellationToken ct)
    {
        var regional = await RegionAsync(ct);
        var detail = await SendAsync(HttpMethod.Get, $"{regional}/api/v1/getHomeDetail",
            headers: new() { ["header_clientid"] = _clientId, ["Authorization"] = session.Token }, ct: ct);

        // A token that has been revoked or has aged out. Told apart from every other failure by its own type,
        // so the caller can throw the stored session away rather than keep presenting one Roborock won't take.
        if (detail.RootElement.TryGetProperty("code", out var status) && status.GetInt32() == 2010)
            throw new RoborockSignedOutException();

        if (!detail.RootElement.TryGetProperty("data", out var detailData) ||
            !detailData.TryGetProperty("rrHomeId", out var homeIdElement))
            throw new PluginDeadEndException(
                "Roborock has no home on this account — set the vacuum up in the Roborock app first.");

        var homeId = homeIdElement.GetInt64();

        // v3 is what the current app asks for; the older shape is still served and still has what we need, so
        // an account their newer endpoint doesn't cover isn't a dead end.
        foreach (var path in new[] { $"/v3/user/homes/{homeId}", $"/user/homes/{homeId}" })
        {
            JsonDocument response;
            try
            {
                response = await SendAsync(HttpMethod.Get, session.Rriot.ApiBase.TrimEnd('/') + path,
                    headers: new() { ["Authorization"] = Hawk(session.Rriot, path) }, ct: ct);
            }
            catch (Exception) when (path.StartsWith("/v3", StringComparison.Ordinal)) { continue; }

            if (!response.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object)
                continue;

            return ReadHome(result);
        }

        throw new InvalidOperationException("Roborock returned a home with nothing readable in it.");
    }

    /// <summary>The devices, their keys and the home's rooms, out of the shape their API returns.</summary>
    internal static RoborockHome ReadHome(JsonElement result)
    {
        // A product carries the human model name; a device only references it by id.
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
            foreach (var product in products.EnumerateArray())
                if (product.TryGetProperty("id", out var id) && id.GetString() is { } key)
                    models[key] = product.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "";

        var devices = new List<RoborockDevice>();
        foreach (var name in new[] { "devices", "receivedDevices" })
            if (result.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var device in list.EnumerateArray())
                {
                    var duid = device.TryGetProperty("duid", out var d) ? d.GetString() : null;
                    var localKey = device.TryGetProperty("localKey", out var k) ? k.GetString() : null;
                    if (duid is null || localKey is null) continue;

                    var productId = device.TryGetProperty("productId", out var p) ? p.GetString() ?? "" : "";
                    devices.Add(new RoborockDevice(
                        duid,
                        device.TryGetProperty("name", out var n) ? n.GetString() ?? duid : duid,
                        localKey,
                        models.TryGetValue(productId, out var model) ? model : "",
                        device.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True));
                }

        var rooms = new List<RoborockRoom>();
        if (result.TryGetProperty("rooms", out var roomList) && roomList.ValueKind == JsonValueKind.Array)
            foreach (var room in roomList.EnumerateArray())
                if (room.TryGetProperty("id", out var id) && room.TryGetProperty("name", out var name))
                    rooms.Add(new RoborockRoom(id.GetInt64(), name.GetString() ?? ""));

        return new RoborockHome(devices, rooms);
    }

    private string? _region;
    private string? _country;
    private string? _countryCode;

    private async Task<string> RegionAsync(CancellationToken ct)
    {
        if (_region is not null) return _region;

        foreach (var region in Regions)
        {
            JsonDocument response;
            try
            {
                response = await SendAsync(HttpMethod.Post,
                    Url($"{region}/api/v1/getUrlByEmail", new() { ["email"] = _email, ["needtwostepauth"] = "false" }),
                    headers: new() { ["header_clientid"] = _clientId }, ct: ct);
            }
            catch (Exception) { continue; }

            if (!response.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 200) continue;
            if (response.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } found)
            {
                // The v4 sign-in has to say which country the account is in, and this is the only call that
                // knows. Kept rather than discarded, so the newer flow doesn't need a second lookup.
                _country = data.TryGetProperty("country", out var c) ? c.GetString() : null;
                _countryCode = data.TryGetProperty("countrycode", out var cc)
                    ? (cc.ValueKind == JsonValueKind.Number ? cc.GetInt32().ToString() : cc.GetString())
                    : null;
                return _region = found;
            }
        }

        throw new PluginDeadEndException(
            $"No Roborock account was found for {_email} in any region. Check the address you sign in to the app with.");
    }

    /// <summary>
    /// Roborock's Hawk signature over a request: identity, a nonce, the second it was made, and hashes of the
    /// path, the query and the body, HMAC'd with the account's key. The empty fields for query and body are
    /// still part of the signed string, so they cannot be dropped.
    /// </summary>
    internal static string Hawk(RRiot rriot, string path, long? at = null, string? nonce = null)
    {
        var timestamp = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        nonce ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(6))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var prestr = string.Join(":", rriot.U, rriot.S, nonce, timestamp.ToString(), Md5Hex(path), "", "");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(rriot.H));
        var mac = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(prestr)));

        return $"Hawk id=\"{rriot.U}\", s=\"{rriot.S}\", ts=\"{timestamp}\", nonce=\"{nonce}\", mac=\"{mac}\"";
    }

    internal static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Url(string baseUrl, Dictionary<string, string> query) =>
        baseUrl + "?" + string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string url, Dictionary<string, string>? headers = null,
        Dictionary<string, string>? form = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers ?? new()) request.Headers.TryAddWithoutValidation(name, value);
        if (form is not null) request.Content = new FormUrlEncodedContent(form);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Roborock returned {(int)response.StatusCode} for {method} {new Uri(url).AbsolutePath}: {Short(body)}");

        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException($"Roborock returned something that isn't JSON: {Short(body)}"); }
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
