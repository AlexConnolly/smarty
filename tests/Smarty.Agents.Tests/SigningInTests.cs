using Microsoft.AspNetCore.Http;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The door, and what it is protecting.
///
/// <para>
/// There was nothing at all in front of this API. Behind it: a browser already signed into its owner's accounts, their
/// files, their memory, and an assistant that carries out what it is told. Anybody who reached the URL had all of it,
/// and nothing in the app said whether that URL was reachable from the sofa or from the internet.
/// </para>
/// <para>
/// One password from the environment, exchanged ONCE for a session in a cookie the page cannot read — because a
/// password sent on every request ends up in a query string or in local storage, and both of those leak through logs,
/// screenshots and shared screens.
/// </para>
/// </summary>
public class SigningInTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private Signin New(string? password, DateTimeOffset? now = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new Signin(path, password) { Now = () => now ?? Tuesday };
    }

    [Fact]
    public void No_password_means_nothing_is_allowed_in()
    {
        // The safe direction, and the opposite of what an unconfigured instance did before. An open API that behaves like
        // a working one is exactly how this went unnoticed for months.
        var door = New(null);

        Assert.False(door.Ready);
        Assert.Null(door.In("chickens", "127.0.0.1"));
        Assert.False(door.Holds("anything at all"));
    }

    [Fact]
    public void The_right_password_buys_a_session()
    {
        var door = New("chickens");

        var token = door.In("chickens", "127.0.0.1");

        Assert.NotNull(token);
        Assert.True(door.Holds(token));
        // Long and unguessable: it is the thing that stands in for the password from here on.
        Assert.True(token!.Length >= 32, $"token was {token.Length} characters");
    }

    [Fact]
    public void The_wrong_password_buys_nothing()
    {
        var door = New("chickens");

        Assert.Null(door.In("chicken", "127.0.0.1"));
        Assert.Null(door.In("", "127.0.0.1"));
        Assert.Null(door.In(null, "127.0.0.1"));
        Assert.Equal(0, door.Open);
    }

    [Fact]
    public void A_session_nobody_issued_is_not_a_session()
    {
        var door = New("chickens");
        door.In("chickens", "127.0.0.1");

        Assert.False(door.Holds("made-up"));
        Assert.False(door.Holds(""));
        Assert.False(door.Holds(null));
    }

    [Fact]
    public void Guessing_stops_being_answered()
    {
        // A short password with no brake is a password anybody with a script has. Five tries from one place, then it
        // stops answering for a few minutes — which is the difference between a weekend of guesses and a few thousand.
        var door = New("chickens");

        for (var i = 0; i < Signin.Guesses; i++) Assert.Null(door.In("nope", "10.0.0.9"));

        Assert.True(door.Waiting("10.0.0.9"));
        // Even the RIGHT password waits, or the cool-off is only an inconvenience to somebody who has already got in.
        Assert.Null(door.In("chickens", "10.0.0.9"));

        // Somewhere else is unaffected: one machine guessing must not lock its owner out from their phone.
        Assert.False(door.Waiting("192.168.1.4"));
        Assert.NotNull(door.In("chickens", "192.168.1.4"));
    }

    [Fact]
    public void The_cool_off_ends()
    {
        // It is a brake, not a ban: somebody who mistypes five times has to be able to get in a few minutes later.
        var path = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}.json");
        _paths.Add(path);

        var clock = Tuesday;
        var door = new Signin(path, "chickens") { Now = () => clock };
        for (var i = 0; i < Signin.Guesses; i++) door.In("nope", "10.0.0.9");
        Assert.True(door.Waiting("10.0.0.9"));

        clock = Tuesday + Signin.Cools + TimeSpan.FromMinutes(1);

        Assert.False(door.Waiting("10.0.0.9"));
        Assert.NotNull(door.In("chickens", "10.0.0.9"));
    }

    [Fact]
    public void A_session_survives_a_restart()
    {
        // This process restarts constantly, and a sign-in that does not survive that is a sign-in nobody keeps on.
        var path = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}.json");
        _paths.Add(path);

        var token = new Signin(path, "chickens") { Now = () => Tuesday }.In("chickens", "127.0.0.1");
        var after = new Signin(path, "chickens") { Now = () => Tuesday.AddDays(3) };

        Assert.True(after.Holds(token));
    }

    [Fact]
    public void A_session_nobody_has_used_for_a_month_is_gone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}.json");
        _paths.Add(path);

        var token = new Signin(path, "chickens") { Now = () => Tuesday }.In("chickens", "127.0.0.1");
        var muchLater = new Signin(path, "chickens") { Now = () => Tuesday + Signin.Lasts + TimeSpan.FromDays(1) };

        Assert.False(muchLater.Holds(token));
    }

    [Fact]
    public void Signing_out_one_place_leaves_the_others()
    {
        var door = New("chickens");
        var desk = door.In("chickens", "127.0.0.1");
        var phone = door.In("chickens", "192.168.1.4");

        Assert.True(door.Out(desk));

        Assert.False(door.Holds(desk));
        Assert.True(door.Holds(phone));
    }

    [Fact]
    public void Signing_out_everywhere_means_everywhere()
    {
        // What you want the moment you think a session has gone somewhere it should not have.
        var door = New("chickens");
        var desk = door.In("chickens", "127.0.0.1");
        var phone = door.In("chickens", "192.168.1.4");

        Assert.Equal(2, door.Everywhere());

        Assert.False(door.Holds(desk));
        Assert.False(door.Holds(phone));
    }

    // ── what is behind the door and what is in front of it ──────────────────────────────────────────────

    [Theory]
    [InlineData("/api/widgets")]
    [InlineData("/api/session/abc/message")]
    [InlineData("/api/control/brain")]
    [InlineData("/api/feeds")]
    [InlineData("/api/chats")]
    public void Everything_under_the_api_needs_a_session(string path)
    {
        Assert.True(Doors.Guarded(path));
    }

    [Theory]
    [InlineData("/api/auth/state")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/logout")]
    public void Only_the_door_itself_is_open(string path)
    {
        Assert.True(Doors.Open(path));
        Assert.False(Doors.Guarded(path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/assets/index-abc123.js")]
    [InlineData("/control/")]
    public void The_shell_around_it_is_not_guarded(string path)
    {
        // Deliberately: the page is a password box until the API answers, and serving the bundle to somebody who cannot
        // sign in gives them nothing. Guarding it instead would mean a blank white screen with no way to log in.
        Assert.False(Doors.Guarded(path));
    }

    // ── The caller nobody thought of: the app itself ──────────────────────────────────────────────────

    private static HttpRequest Request(Action<DefaultHttpContext> set)
    {
        var ctx = new DefaultHttpContext();
        set(ctx);
        return ctx.Request;
    }

    [Fact]
    public void The_app_can_call_itself()
    {
        // What locking the API actually broke. The screenshot check posts to this app's own /look endpoint and the page
        // it photographs reads the panel back — both from this process, with no session and no way to get one. Both
        // answered 401, the POST's status code was never read, and the one check in the system with EYES stopped
        // running without a word.
        var pass = new OurPass();

        Assert.True(pass.Holds(Request(c => c.Request.Headers[OurPass.Header] = pass.Token)));
        Assert.True(pass.Holds(Request(c => c.Request.QueryString = new QueryString($"?{OurPass.Query}={pass.Token}"))));
        Assert.True(pass.Holds(Request(c => c.Request.Headers["Cookie"] = $"{OurPass.Cookie}={pass.Token}")));
    }

    [Fact]
    public void Nobody_else_can()
    {
        var pass = new OurPass();

        Assert.False(pass.Holds(Request(_ => { })));
        Assert.False(pass.Holds(Request(c => c.Request.Headers[OurPass.Header] = "chickens")));
        // Another process's pass is not this one's. It is minted per process and never written down, so a token that
        // leaked out of a log is dead as soon as the app restarts.
        Assert.False(pass.Holds(Request(c => c.Request.Headers[OurPass.Header] = new OurPass().Token)));
    }

    [Fact]
    public void The_pass_goes_on_a_url_that_already_has_a_query()
    {
        var pass = new OurPass();

        Assert.Contains($"?{OurPass.Query}={pass.Token}", pass.On("http://localhost:5179/widget/abc123"));
        Assert.Contains($"&{OurPass.Query}={pass.Token}", pass.On("http://localhost:5179/widget/abc123?full=1"));
    }

    [Fact]
    public void The_pass_is_not_the_password()
    {
        // Two different things doing two different jobs, and conflating them would put the user's password on a
        // command line. The pass is random, per process, and buys nothing a person would want.
        var pass = new OurPass();

        Assert.True(pass.Token.Length >= 32);
        Assert.False(New("chickens").Holds(pass.Token));
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            if (File.Exists(p)) File.Delete(p);
    }
}
