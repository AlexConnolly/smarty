using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// Who is allowed in.
///
/// <para>
/// There was nothing here at all, and what sits behind it is the whole reason that matters: a browser already signed
/// into everything its owner uses, a shell, their files, their memory, and an assistant that will act on instructions.
/// Anybody who reached the URL had all of it. On a laptop behind a firewall that is survivable; the moment it is
/// tunnelled, published or reached over the house Wi-Fi it is not, and nothing about the app says which of those is
/// currently true.
/// </para>
/// <para>
/// So: one password, held in the environment, and a SESSION once it has been given. The session is what makes it
/// usable — a password on every request means it lives in a query string or in local storage, and both of those leak
/// through logs, screenshots and history. A cookie the page cannot read leaks through none of them.
/// </para>
/// <para>
/// Sessions are kept on disk deliberately. This process restarts constantly during development, and a sign-in that
/// does not survive that is a sign-in nobody keeps switched on.
/// </para>
/// </summary>
public sealed class Signin
{
    private readonly string _path;
    private readonly string? _password;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Failed attempts, by where they came from — the only brake on guessing a short password.</summary>
    private readonly Dictionary<string, Attempts> _tried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name of the cookie holding a session. Read by nothing but this.</summary>
    public const string Cookie = "smarty_session";

    /// <summary>How long a session lasts without being used.</summary>
    public static readonly TimeSpan Lasts = TimeSpan.FromDays(30);

    /// <summary>How many wrong guesses from one place before it stops answering, and for how long.</summary>
    public const int Guesses = 5;

    public static readonly TimeSpan Cools = TimeSpan.FromMinutes(5);

    public Signin(string path, string? password)
    {
        _path = path;
        _password = string.IsNullOrWhiteSpace(password) ? null : password.Trim();

        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<List<Session>>(File.ReadAllText(path)) is { } kept)
                foreach (var session in kept.Where(s => s.LastSeen > DateTimeOffset.UtcNow - Lasts))
                    _sessions[session.Token] = session;
        }
        catch { /* a corrupt file means everybody signs in again, which is the safe direction */ }
    }

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Is a password configured at all?
    /// </summary>
    /// <remarks>
    /// When it is not, this refuses everything rather than allowing everything. An unprotected instance that looks like
    /// a working one is how the whole problem happened; a locked one with a plain reason cannot be mistaken for either.
    /// </remarks>
    public bool Ready => _password is not null;

    /// <summary>How many sessions are currently valid — for the page, so "signed in elsewhere" is visible.</summary>
    public int Open
    {
        get { lock (_lock) return _sessions.Count; }
    }

    /// <summary>
    /// Sign in. Returns the session token, or null when the password is wrong or the caller is cooling off.
    /// </summary>
    public string? In(string? password, string? from)
    {
        if (_password is null) return null;

        lock (_lock)
        {
            var where = from is { Length: > 0 } ? from : "somewhere";
            if (Cooling(where)) return null;

            // Fixed-time comparison. The password is short and local, but a length-leaking compare is free to avoid.
            var given = Encoding.UTF8.GetBytes((password ?? "").Trim());
            var wanted = Encoding.UTF8.GetBytes(_password);
            if (given.Length != wanted.Length || !CryptographicOperations.FixedTimeEquals(given, wanted))
            {
                Missed(where);
                return null;
            }

            _tried.Remove(where);

            var session = new Session
            {
                Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('='),
                From = where,
                Started = Now(),
                LastSeen = Now(),
            };
            _sessions[session.Token] = session;
            Save();
            return session.Token;
        }
    }

    /// <summary>
    /// Is this a session that may act? Touches it, so a session in daily use never expires.
    /// </summary>
    public bool Holds(string? token)
    {
        if (_password is null || string.IsNullOrWhiteSpace(token)) return false;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(token!, out var session)) return false;

            if (session.LastSeen < Now() - Lasts)
            {
                _sessions.Remove(token!);
                Save();
                return false;
            }

            // Written back at most hourly: every request is a disk write otherwise, and the point of the timestamp is
            // "was this used this month", not "was this used this second".
            if (session.LastSeen < Now() - TimeSpan.FromHours(1))
            {
                session.LastSeen = Now();
                Save();
            }
            return true;
        }
    }

    /// <summary>Sign out this one session. The others stay — signing out a phone should not sign out a desk.</summary>
    public bool Out(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (_lock)
        {
            if (!_sessions.Remove(token!)) return false;
            Save();
            return true;
        }
    }

    /// <summary>Sign out everywhere. What you want the moment you think a session has gone somewhere it shouldn't.</summary>
    public int Everywhere()
    {
        lock (_lock)
        {
            var gone = _sessions.Count;
            _sessions.Clear();
            Save();
            return gone;
        }
    }

    /// <summary>Is this caller being made to wait?</summary>
    public bool Waiting(string? from)
    {
        lock (_lock) return Cooling(from is { Length: > 0 } ? from : "somewhere");
    }

    private bool Cooling(string where) =>
        _tried.TryGetValue(where, out var attempts)
        && attempts.Count >= Guesses
        && attempts.Last > Now() - Cools;

    private void Missed(string where)
    {
        if (!_tried.TryGetValue(where, out var attempts) || attempts.Last < Now() - Cools)
            attempts = _tried[where] = new Attempts();

        attempts.Count++;
        attempts.Last = Now();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_sessions.Values.ToList()));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[auth] couldn't save sessions: {ex.Message}");
        }
    }

    public sealed class Session
    {
        public string Token { get; set; } = "";

        /// <summary>Where it was signed in from, so a list of sessions means something to a person reading it.</summary>
        public string From { get; set; } = "";

        public DateTimeOffset Started { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private sealed class Attempts
    {
        public int Count { get; set; }
        public DateTimeOffset Last { get; set; }
    }
}

/// <summary>
/// The pass this app uses to call ITSELF.
///
/// <para>
/// Putting a password in front of the API locked out the one caller nobody thought of: the app. The screenshot check
/// posts to its own <c>/look</c> endpoint, and the page it photographs reads the panel back through the same API —
/// both from this process, on this machine, with no session and no way to get one. So every one of those requests
/// answered 401. The POST failed silently, because nothing was reading the status code, and the check that exists to
/// notice a panel looking wrong stopped running at all. Which is a particularly bad thing to lose quietly: it is the
/// only mechanism in the system with eyes.
/// </para>
/// <para>
/// A secret held in memory and minted per process, never written down and never the user's password. Presented as a
/// header where the caller is code, and as a query where the caller is a browser being pointed at a page — that one
/// plants a cookie lasting minutes, so the page's own requests carry it without the url having to.
/// </para>
/// </summary>
public sealed class OurPass
{
    public const string Cookie = "smarty_pass";
    public const string Header = "X-Smarty-Pass";

    /// <summary>The name of the query that carries it into a page. Short, because it goes on a command line.</summary>
    public const string Query = "pass";

    /// <summary>
    /// How long the planted cookie lasts. Minutes, because the only thing it has to outlive is one screenshot — a
    /// pass that lingers is a pass that gets used by something nobody meant to authorise.
    /// </summary>
    public static readonly TimeSpan Lasts = TimeSpan.FromMinutes(2);

    public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Is this request one of ours?</summary>
    public bool Holds(HttpRequest request) =>
        Same(request.Headers[Header].FirstOrDefault())
        || Same(request.Cookies[Cookie])
        || Same(request.Query[Query].FirstOrDefault());

    /// <summary>Handed to a browser as a query, on the one page this app opens for itself.</summary>
    public string On(string url) => $"{url}{(url.Contains('?') ? '&' : '?')}{Query}={Token}";

    private bool Same(string? given) =>
        given is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Token));
}

/// <summary>
/// Which requests are allowed through without a session.
/// </summary>
/// <remarks>
/// <para>
/// Kept as a named function rather than written inline in the pipeline, because the interesting thing about an
/// allow-list in front of an API is being able to READ it: every exemption here is a door, and a door nobody can list
/// is a door nobody checks.
/// </para>
/// <para>
/// The static files are not on it and do not need to be: the page itself is a shell that shows a password box, and
/// everything it would display comes from behind the gate.
/// </para>
/// </remarks>
public static class Doors
{
    /// <summary>Signing in, signing out, and asking whether either is needed.</summary>
    public static bool Open(PathString path) =>
        path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase);

    /// <summary>Anything under the API needs a session; anything else is the shell around it.</summary>
    public static bool Guarded(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) && !Open(path);
}
