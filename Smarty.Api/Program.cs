using System.Net.WebSockets;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Smarty.Agents;
using Smarty.Api;
using Smarty.Api.Plugins;

var builder = WebApplication.CreateBuilder(args);

// Open by design — this is a local tester API. Allow any frontend to talk to it.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<AgentRunStore>();

// Local Whisper speech-to-text. Model is downloaded once to disk and cached.
string whisperModelPath = builder.Configuration["Whisper:ModelPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "models", "ggml-base.bin");
string whisperModelUrl = builder.Configuration["Whisper:ModelUrl"]
    ?? "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
builder.Services.AddSingleton(new WhisperTranscriber(whisperModelPath, whisperModelUrl));

// Bind all interfaces so a phone on the same Wi-Fi can reach it directly (http://<pc-lan-ip>:5179)
// — that path streams properly, unlike a free Cloudflare quick tunnel which buffers SSE.
string url = builder.Configuration["Urls"] ?? "http://0.0.0.0:5179";
builder.WebHost.UseUrls(url);

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

// A shared secret in front of everything, when one is configured.
//
// Off by default, deliberately: this is the machine's own tester, and locking someone out of their own assistant
// is worse than the risk on a laptop behind a firewall. That stops being true the moment it is published — a
// tunnel URL is public, and behind this one sits a shell and a browser already signed into everything. Set
// SMARTY_TOKEN and pass it as ?k=, an X-Smarty-Token header, or the cookie the first query plants.
string? gateToken = builder.Configuration["Auth:Token"] ?? Environment.GetEnvironmentVariable("SMARTY_TOKEN");
if (!string.IsNullOrWhiteSpace(gateToken))
{
    Console.WriteLine("[auth] SMARTY_TOKEN set — requests need ?k=<token>, an X-Smarty-Token header, or its cookie.");
    app.Use(async (ctx, next) =>
    {
        var supplied = ctx.Request.Query["k"].FirstOrDefault()
            ?? ctx.Request.Headers["X-Smarty-Token"].FirstOrDefault()
            ?? ctx.Request.Cookies["smarty_token"];

        if (!string.Equals(supplied, gateToken, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Unauthorized. Append ?k=<token>.");
            return;
        }

        // Plant it so everything the page fetches afterwards — the event stream, an image, a deck — carries it
        // without the URL having to. HttpOnly: no page script needs to read it.
        if (ctx.Request.Query.ContainsKey("k"))
            ctx.Response.Cookies.Append("smarty_token", gateToken, new CookieOptions
            {
                HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(30),
            });

        await next();
    });
}

// The pass this app calls ITSELF with, and the one page it opens for itself.
//
// In FRONT of the static files on purpose. The page the screenshot check photographs is /widget/<id>, which is served
// by the fallback below — so a middleware registered after it never runs for that request, and the cookie this plants
// would never be planted. That is exactly the mistake made once already: the planting sat with the API's own guard,
// forty lines further down, and the photograph came back reading "No panel <id>" with everything apparently in order.
var ourPass = new OurPass();
app.Use(async (ctx, next) =>
{
    // The pass arrives once, as a query on the page, and leaves a cookie behind it — because it is that page's own
    // calls to /api that have to get through, and they carry no query.
    if (ctx.Request.Query.ContainsKey(OurPass.Query) && ourPass.Holds(ctx.Request))
        ctx.Response.Cookies.Append(OurPass.Cookie, ourPass.Token, new CookieOptions
        {
            // Rooted, which is the whole point: planted on /widget/<id>, sent with that page's calls to /api. A cookie
            // with no path is scoped to the directory it came from, so it would go back to /widget and nowhere else.
            Path = "/",
            HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, MaxAge = OurPass.Lasts,
        });

    await next();
});

// Serve the built React frontend from the same origin, so a single tunnel exposes the whole
// app (UI + API) and the UI's relative "/api" calls just work. Build it with: cd Smarty.Chat && npm run build
string webRoot = builder.Configuration["WebRoot"]
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Smarty.Chat", "dist"));
if (Directory.Exists(webRoot))
{
    var files = new PhysicalFileProvider(webRoot);
    var staticOptions = new StaticFileOptions { FileProvider = files, OnPrepareResponse = CacheHeaders };
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(staticOptions);
    app.MapFallbackToFile("index.html", staticOptions);
}

// Serve the Smarty.Control command centre at /control from the same origin (built with base "/control/").
// Its API calls hit /api/control/* at the origin root. Build with: cd Smarty.Control && npm run build
string controlRoot = builder.Configuration["ControlRoot"]
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Smarty.Control", "dist"));
if (Directory.Exists(controlRoot))
{
    var controlFiles = new PhysicalFileProvider(controlRoot);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = controlFiles, RequestPath = "/control", OnPrepareResponse = CacheHeaders,
    });
    // SPA fallback for client-side routes under /control.
    app.MapFallbackToFile("/control/{*path:nonfile}", "index.html",
        new StaticFileOptions { FileProvider = controlFiles, OnPrepareResponse = CacheHeaders });
}

string ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"] ?? OllamaModelProvider.DefaultBaseUrl;
string defaultModel = builder.Configuration["Ollama:Model"] ?? ModelSpec.DefaultModelName;

// Cache searches + fetched pages for an hour (persisted under data/, survives restarts) so repeated
// lookups reuse the earlier result instead of re-hitting — and re-tripping the bot-blocks of — the network.
string researchCachePath = builder.Configuration["Research:CachePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "research-cache.json");
WebResearch.Cache = new FileResearchCache(researchCachePath);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

// Conversational orchestrator + async workers. Orchestrator and worker share one model (two roles).
// Passive capture of interactions + feedback → a fine-tune dataset that builds from real usage.
string trainingDir = builder.Configuration["Training:Dir"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "training-data");
var trainingLog = new TrainingLog(trainingDir, json);

// Long-term memory of the user — structured facts, persisted to disk (see MEMORY_SPEC.md).
// Embedder: turns text into a vector via a local Ollama embed model, so memory relevance is semantic.
string embedModel = builder.Configuration["Memory:EmbedModel"] ?? "nomic-embed-text";
Func<string, CancellationToken, Task<float[]?>> embed = async (text, ct) =>
{
    try
    {
        var payload = JsonSerializer.Serialize(new { model = embedModel, prompt = text }, json);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ollamaBaseUrl}/api/embeddings") { Content = content };
        var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY") ?? Environment.GetEnvironmentVariable("SMARTY_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("embedding", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray()
            : null;
    }
    catch { return null; } // embedding is best-effort; the store falls back to keyword matching
};
// ---- the brain ----
// A graph of things joined to other things, and three free-text verbs over it: ask it, tell it, take something
// back (see BRAIN_SPEC.md). What replaced the flat store is not a bigger store — it is moving the resolving from
// the model to the reconciler, which is the only thing that can see both the sentence and what already exists.
var people = new PeopleStore(Path.Combine(builder.Environment.ContentRootPath, "data", "people.json"), json);
// The local app's single user. Point Me:Email at your work address and this machine's brain and the same
// human's Slack identity become one person; leave it unset and it stays a private local identity.
people.SetSelf(builder.Configuration["Me:Email"] ?? Environment.GetEnvironmentVariable("SMARTY_ME"),
    builder.Configuration["Me:Name"]);

string brainDir = builder.Configuration["Brain:Dir"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var graph = new Smarty.Brain.Graph(brainDir, json) { Trace = line => Console.WriteLine(line) };

// The model that reads sentences into graph writes. Deliberately the SAME model as the conversation rather than
// something smaller: the whole job is judgement about meaning, and it is the one place where being wrong writes
// something durable.
// What the nodes actually contain — files and notes, keyed by node id and kept out of the graph so the structure stays
// cheap to read whole. Its own folder, because it holds copies of real documents.
var contexts = new Smarty.Brain.Contexts(Path.Combine(brainDir, "context"), json)
{
    Trace = line => Console.WriteLine(line),
};

var brain = new Smarty.Brain.Memory(graph, ModelRouting.Provider(defaultModel, ollamaBaseUrl), defaultModel, brainDir, json)
{
    Contexts = contexts,
    Trace = line => Console.WriteLine(line),

    // Who may recall what. The graph carries an opaque audience key and asks; the rule of what a room is lives with the
    // rooms. One host serves a single person and a whole workspace on the same predicate.
    Sees = BrainContext.Sees,
};

// The audience of the local app: the one person using it.
var here = BrainContext.Solo(people.Self).Room.Key;

// Whose memory this is. Without it the graph has no "I", so "going with my wife" records a wife joined to nothing —
// and a model told to use the id of somebody not on file correctly records nothing at all instead.
// Who the brain belongs to, IF anybody has said. Nothing is invented here: a placeholder owner is worse than none,
// because the graph then looks configured and every fact about "my brother" hangs off a fiction. Unknown is a question
// for the user — see /api/identity — and the reconciler already handles it honestly, recording what it can and leaving
// the rest rather than guessing.
//
// NameOf falls back to the local part of an address, so an identifier can arrive looking like a name. Rejected here.
static string? RealName(string? candidate) =>
    candidate is { Length: > 1 }
    && !candidate.Contains(':')
    && !candidate.Contains('@')
    && !candidate.Equals("self", StringComparison.OrdinalIgnoreCase)
    && !candidate.Equals("someone", StringComparison.OrdinalIgnoreCase)
        ? candidate
        : null;

if (new[] { builder.Configuration["Me:Name"], people.NameOf(people.Self) }.Select(RealName)
        .FirstOrDefault(n => n is not null) is { } knownName)
    brain.WhoAmI(knownName, people.Self.IsEmpty ? null : new[] { people.Self.Value });

// What the assistant is called. Chosen at setup, used in its own prompt and everywhere it is named on screen.
var naming = new Naming(Path.Combine(brainDir, "naming.json"), json);

// Which project a remark belongs to. Not memory — a classifier that reads the graph.
var projectRouting = new ProjectRouting(graph, embed);

// What is recorded against one project. Read straight off the graph — the subject is the project, so there is
// nothing to work out and no model needed.
// A slug written the way a person reads it: hyphens back to spaces, first letter up.
//
// Only ever for a slug standing in for a name it could not find. Real titles are left exactly as typed — a title may
// legitimately contain a hyphen ("sub-25 5k") and rewriting it would be changing what somebody wrote, not tidying it.
// Sentence case rather than title case, because that is how every other name in this system reads.
static string Readable(string? slug)
{
    var text = (slug ?? "").Replace('-', ' ').Replace('_', ' ').Trim();
    if (text.Length == 0) return "";

    return char.ToUpperInvariant(text[0]) + text[1..];
}

static List<Smarty.Brain.Edge> ProjectKnowledge(Smarty.Brain.Graph g, string title)
{
    if (g.Resolve(title).Node is not { } node) return new List<Smarty.Brain.Edge>();

    return g.Edges
        .Where(e => e.State == Smarty.Brain.EdgeState.Active)
        .Where(e => e.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => e.Asserted)
        .ToList();
}

// Draining the queue. Telling it something is instant precisely because the graph work happens here instead, and
// a question drains what is waiting before it answers — so this loop is a backstop for everything nobody asks
// about, not the only path. Sixty seconds because nothing observes the delay.
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            var done = await brain.ReconcileAsync().ConfigureAwait(false);

            foreach (var dropped in done.Dropped)
                Console.WriteLine($"[brain] gave up on \"{dropped.Text}\" — {dropped.Error}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[brain] reconciliation loop: {ex.Message}");
        }
    }
});

// Projects — long-running endeavours the chat routes context/jobs by (see PROJECTS_SPEC.md).
string projectsPath = builder.Configuration["Projects:Path"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "projects.json");
var projects = new ProjectStore(projectsPath, json);

// What each project's background workers actually did — thinking + tool calls + result — kept per project
// so the project overview can show it back (read-only).
string runsPath = builder.Configuration["Projects:RunsPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "runs.json");
var projectRuns = new ProjectRunStore(runsPath, json);
// Lists a project accumulates — preferred amenities, must-sees. Their own store: a list is edited an item at a
// time by both sides, which a key/value memory cannot represent and the brain should not be asked to.
var projectLists = new ProjectListStore(
    Path.Combine(builder.Environment.ContentRootPath, "data", "project-lists.json"), json)
{
    // What turns a list of words into a shortlist you can ask questions of. Resolution only — nothing is ever
    // CREATED from a list item, because a list is where half-formed ideas live and "somewhere new" is not a place.
    Resolve = item => graph.Resolve(item).Node?.Id,
};

// Specialist personas (software engineer, PM…) and the capabilities (integrations) they draw on.
string dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");



// Where the user is. The browser reports the fix (its own permission prompt is the consent gate) and the
// coordinates are resolved to a place name, because "51.52, -0.09" tells a model nothing useful. Current position
// only — never a trail.
var location = new LocationStore(Path.Combine(dataDir, "location.json"), json);
var geocoder = new ReverseGeocoder();
var integrations = IntegrationConfig.Load(Path.Combine(dataDir, "integrations.json"));

/*
 * The password, and the session it buys.
 *
 * Everything behind this is somebody's life: a browser signed into their accounts, their files, their memory, and an
 * assistant that acts on what it is told. Until now the only protection was that nobody had guessed the URL.
 *
 * Two rules, and the second is the one that matters. A password is required — with none set, the API answers nothing
 * at all, because an unprotected instance that behaves like a working one is exactly how this went unnoticed. And the
 * password is exchanged ONCE for a session in a cookie the page cannot read: a password on every request ends up in a
 * query string or in local storage, and both of those leak through logs, screenshots and shared screens.
 */
// The token the Slack process forwards its events with. Read here because the gate below has to let it through: it is
// a machine, so it has no session, and refusing it would take the Slack surface down in the name of securing this one.
string? controlToken = Environment.GetEnvironmentVariable("SMARTY_CONTROL_TOKEN") ?? integrations.Get("control", "token");

var signin = new Signin(Path.Combine(dataDir, "sessions.json"),
    builder.Configuration["Auth:Password"] ?? Environment.GetEnvironmentVariable("SMARTY_PASSWORD"));

Console.WriteLine(signin.Ready
    ? $"[auth] a password is set — the API needs a session ({signin.Open} open)"
    : "[auth] NO PASSWORD SET (SMARTY_PASSWORD) — the API will refuse everything until there is one");

app.Use(async (ctx, next) =>
{
    if (!Doors.Guarded(ctx.Request.Path))
    {
        await next();
        return;
    }

    // A machine caller. THREE of them now, none of them a person, so none of them has a session — and refusing them
    // here would take a working surface down in the name of securing it. This app itself (the screenshot check and the
    // page it photographs), the Slack process forwarding its events in, and a probe checking the app is up.
    var machine =
        ourPass.Holds(ctx.Request)
        || (!string.IsNullOrWhiteSpace(gateToken)
         && (ctx.Request.Headers["X-Smarty-Token"].FirstOrDefault() == gateToken
             || ctx.Request.Cookies["smarty_token"] == gateToken
             || ctx.Request.Query["k"].FirstOrDefault() == gateToken))
        || (!string.IsNullOrWhiteSpace(controlToken)
            && ctx.Request.Headers["X-Control-Token"].FirstOrDefault() == controlToken);

    if (machine || signin.Holds(ctx.Request.Cookies[Signin.Cookie]))
    {
        await next();
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsJsonAsync(new
    {
        error = signin.Ready ? "sign in" : "no password is set on this instance",
        signIn = signin.Ready,
    });
});

// Whether a password is wanted, and whether this browser already has a session. The one thing readable without one,
// because the page has to know which of three states it is in: signed in, needs a password, or not set up at all.
app.MapGet("/api/auth/state", (HttpContext ctx) => Results.Json(new
{
    wanted = signin.Ready,
    signedIn = signin.Holds(ctx.Request.Cookies[Signin.Cookie]),
    waiting = signin.Waiting(ctx.Connection.RemoteIpAddress?.ToString()),
}, json));

app.MapPost("/api/auth/login", (SignInAttempt body, HttpContext ctx) =>
{
    if (!signin.Ready)
        return Results.Json(new { error = "No password is set on this instance, so nobody can sign in." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var from = ctx.Connection.RemoteIpAddress?.ToString();
    if (signin.In(body?.Password, from) is not { } token)
        return Results.Json(new
        {
            error = signin.Waiting(from)
                ? $"Too many attempts. Try again in {Signin.Cools.TotalMinutes:0} minutes."
                : "That's not the password.",
        }, statusCode: StatusCodes.Status401Unauthorized);

    // HttpOnly: no script needs to read this, and a script that cannot read it cannot leak it. Lax rather than Strict
    // so that following a link into the app (a shared panel url, a chat link) does not land on a locked page.
    ctx.Response.Cookies.Append(Signin.Cookie, token, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = ctx.Request.IsHttps,
        MaxAge = Signin.Lasts,
    });

    Console.WriteLine($"[auth] signed in from {from}");
    return Results.Json(new { ok = true }, json);
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    signin.Out(ctx.Request.Cookies[Signin.Cookie]);
    ctx.Response.Cookies.Delete(Signin.Cookie);
    return Results.Json(new { ok = true }, json);
});

// Everywhere, for the moment you think a session has gone somewhere it should not have.
app.MapPost("/api/auth/logout-everywhere", (HttpContext ctx) =>
{
    var gone = signin.Everywhere();
    ctx.Response.Cookies.Delete(Signin.Cookie);
    return Results.Json(new { ok = true, ended = gone }, json);
});





// MCP servers: tools that live in another process (a browser driver, a database client, anything someone has
// already written) joining the same capability registry as the built-in integrations. No config, or nothing
// enabled in it, means MCP simply contributes nothing.
// data/mcp.json is the home for it — that folder is gitignored, so it can hold real paths and a server's own
// credentials (see mcp.example.json). A checked-in mcp.json beside appsettings.json is honoured as a fallback.
string mcpConfigPath = builder.Configuration["Mcp:ConfigPath"]
    ?? new[] { Path.Combine(dataDir, "mcp.json"), Path.Combine(builder.Environment.ContentRootPath, "mcp.json") }
        .FirstOrDefault(File.Exists)
    ?? Path.Combine(dataDir, "mcp.json");
var mcpServers = McpCatalog.RefreshHints(
    McpServerConfig.Load(mcpConfigPath, problem => Console.Error.WriteLine($"[mcp] {problem}")),
    note => Console.WriteLine($"[mcp] {note}"));
// Where a tool's images land. A screenshot is frequently the answer to "what does that look like", but base64 in
// a transcript is just context burned on something the model can't read — so the bytes go to disk and only a URL
// travels. Relative URL on purpose: the chat is served from this origin, so it works over a tunnel too.
string mediaDir = Path.Combine(builder.Environment.ContentRootPath, "data", "media");
McpTools.ImageSink saveImage = (bytes, mime) =>
{
    try
    {
        Directory.CreateDirectory(mediaDir);
        // Derived from the subtype rather than matched against a list: a grabbed image arrives as whatever the
        // site serves — avif and svg among them — and an unlisted type used to become ".bin", which is then served
        // as octet-stream and renders nowhere. The extension is only ever a hint to the route below, so the honest
        // default for an image is an image extension.
        var subtype = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? mime["image/".Length..].Split(';')[0].Trim().ToLowerInvariant()
            : "";
        var ext = subtype switch
        {
            "jpeg" or "jpg" => ".jpg",
            "svg+xml" => ".svg",
            "" => ".bin",
            _ => "." + string.Concat(subtype.Where(char.IsLetterOrDigit)),
        };
        var name = $"{Guid.NewGuid():N}{ext}";
        File.WriteAllBytes(Path.Combine(mediaDir, name), bytes);
        return $"/api/media/{name}";
    }
    catch
    {
        return null; // couldn't store it — the caller falls back to describing the image
    }
};

var mcp = new McpHub(mcpServers, Console.WriteLine, images: saveImage);

// Link cards. Shares the image sink with the tools, so a mirrored og:image is served from the same place and
// over the same origin as a screenshot — which is what makes it survive a tunnel and a hotlink-hostile CDN.
var linkPreviews = new LinkPreviews(http, (bytes, mime) => saveImage(bytes, mime));
// Connected before the registry is built, because what a server offers is only known once it's answered — and
// each connection is bounded by its own startup timeout, so an absent server delays boot but can't stop it.
await mcp.StartAsync();

// A server declares the persona functions it answers ("browser"), so personas keep referencing capabilities
// rather than a specific server: swap the browser server and the persona is unchanged.
var mcpFunctions = mcp.Connections
    .SelectMany(c => c.Config.Functions.Select(f => (Function: f, Id: McpCapability.IdFor(c.Config))))
    .GroupBy(x => x.Function, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        StringComparer.OrdinalIgnoreCase);

var capabilities = new CapabilityRegistry(new ICapability[]
{
    new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability(),
    new DataScienceCapability(), new FigmaCapability(), new DocumentCapability(),
}.Concat(mcp.Connections.Select(c => new McpCapability(c))), mcpFunctions);
// Personas are now persisted (and editable from Smarty.Control) — built-ins seeded, prompts owned by code.
var personas = new PersonaStore(Path.Combine(dataDir, "personas.json"), json);

// Plugins: a DLL somebody wrote and uploaded from the control centre, landing in the same registry as Kibana
// and the MCP servers. Each one contributes a capability (its commands, as tools) and a persona to delegate to,
// so "add the vacuum" is an upload and a form rather than a build of this repo. Their files and the answers to
// their configuration questions live under data/, which is gitignored — a plugin's API key is not ours to commit.
var pluginRoot = Path.Combine(dataDir, "plugins");
var pluginStore = new PluginStore(Path.Combine(dataDir, "plugins.json"), json);
var plugins = new PluginHost(pluginRoot, pluginStore, capabilities, personas, Console.WriteLine);
await plugins.StartAllAsync();

// Run startup validation on all registered capabilities (e.g. CLI tools / Python packages).
Console.WriteLine("[startup] Validating capability prerequisites...");
capabilities.ValidateAll();

// Ask the provider what its models cost, rather than trusting a table written months ago. Fire-and-forget:
// the seeded figures are what an unreachable catalogue falls back to, and nothing should wait on this.
_ = Task.Run(async () =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    int priced = await ModelPriceCatalogue.RefreshAsync(http, ModelRouting.ApiKey());
    if (priced > 0) Console.WriteLine($"[startup] Prices refreshed from the provider for {priced} model(s).");
});

// The browser IS the web now — there are no fetch-and-extract tools behind it. So a host with no browser
// server has workers that cannot reach the web at all, which is worth saying loudly rather than letting it be
// discovered as "the assistant got worse at research".
if (!mcp.ServersFor("browser").Any(c => c.Connected))
    Console.Error.WriteLine(
        "[startup] WARNING: no MCP server is providing the \"browser\" function, so workers have NO web access. " +
        $"Add one to {mcpConfigPath} with \"functions\": [\"browser\"] — see Smarty.Api/mcp.example.json.");

// Each delegated task gets a working dir under data/tasks/ — task.md plus any files the user attached.
string workspaceRoot = builder.Configuration["Tasks:WorkspaceRoot"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "tasks");

// Smarty.Control command centre: a hub that tracks every conversation (this chat AND Slack, which forwards
// its events here cross-process) and fans them out live to control dashboards. Each new web session gets a
// ControlSink so it's tracked from its first event. A shared token guards the cross-process ingest endpoint.
var controlHub = new ControlHub(Path.Combine(dataDir, "control.json"), json);
var taskHints = new TaskHintStore(Path.Combine(dataDir, "task-hints.json"), json);

// Scheduled work. The store, the tools and the tick have existed since Slack was built and were never wired up
// here, so this app — the one actually used — had schedule_task returning "not available in this context" and no
// way at all to say "every morning". Its own file, not Slack's: the two run as separate processes and would
// otherwise fire each other's tasks into threads the other one owns.
var schedules = new ScheduleStore(Path.Combine(dataDir, "schedules.json"), json);

// The home page, as data. Panels the system decides on rather than a stack of sections someone coded in — which
// is what made a finished meal plan invisible on the one screen that gets looked at.
var widgets = new WidgetStore(Path.Combine(dataDir, "widgets.json"), json);
// Folders on this machine the system has been given access to. A grant, not a config setting — see Sources.cs.
var sources = new SourceStore(Path.Combine(dataDir, "sources.json"), json);

// The library of panel KINDS, separate from the panels themselves. A kind is expensive to build and free to
// instantiate, which is the whole reason the two are not the same file.
var widgetKinds = new WidgetLibrary(Path.Combine(dataDir, "widget-kinds.json"), json);
WidgetSeed.Ensure(widgets, widgetKinds);

// What the memory may not record, because it is ours rather than theirs. Set here rather than where the brain is
// built, because this is where the things it names come into existence.
brain.Ours = OurOwnThings.Test(widgets, widgetKinds);

// A build that STOPPED TO ASK was not interrupted, and telling the user to ask again for it throws the work away.
//
// The store now leaves those alone on load — a panel holding a question keeps its task and stays building, because
// waiting on somebody is not the same as being killed by a restart. This is the same repair for panels written by an
// earlier version, whose files already say failed with a question still on them.
foreach (var w in widgets.All())
{
    if (w.Status != WidgetStatus.Failed || w.TaskId is not { Length: > 0 } || w.Session is not { Length: > 0 })
        continue;
    if (string.IsNullOrWhiteSpace(w.Asks)) continue;

    // The store's "ask again to rebuild it" is now wrong twice over: it is not broken, and asking again is exactly
    // what the user should not do. Cleared before the status change, which is what saves it.
    w.Error = null;
    widgets.Building(w.Id, w.TaskId);
    Console.WriteLine($"[widget] {w.Id} \"{w.Title}\" is not broken, it is waiting on an answer (task #{w.TaskId})");
}

var controlCatalog = new ControlCatalog(personas, capabilities, integrations, ollamaBaseUrl, defaultModel, brain);
var controlBuckets = new ControlBuckets(workspaceRoot, personas);

// Conversations, written down. The list of chats already survived a restart; the messages did not, so reopening
// one showed an empty conversation that the sidebar insisted existed.
var chatHistory = new ChatStore(Path.Combine(builder.Environment.ContentRootPath, "data", "chats"));

var sessions = new SessionStore(s =>
{
    s.Recorder = chatHistory;
    s.Sink = new ControlSink(controlHub, s, "chat");
    // This app has exactly one user, so the room is them — an audience of one. That's the same mechanism a
    // private Slack channel uses, which is why there's no "personal mode" to configure.
    s.Room = BrainContext.Solo(people.Self);
});
// The step that runs BEFORE any work: check for a material gap worth asking about, then size and route the task
// (which disciplines it needs, whether it's worth planning, which persona should hold it).
//
// This was never wired up here. Slack has had one since it was written, so the web app — the surface actually
// used — silently had no clarify gate, no discipline routing and no persona selection: it just delegated whatever
// it was given and let the worker guess. "Prepare my meals for next week" went straight to a worker with no idea
// how many people were eating.
//
// Recon tools are read-only on purpose: this step is deciding what it already knows, and a planning pass has no
// business writing files or driving a browser.
var planner = new TaskPlanner(defaultModel, ollamaBaseUrl, () => new[]
{
    FileTools.ReadFileTool(),
    FileTools.SummaryTool(ModelRouting.Provider(defaultModel, ollamaBaseUrl), defaultModel),
});

// Things that arrive, and what is being watched for. Declared here rather than with their tick further down,
// because the orchestrator hands out the tools that create them — while the READER of a feed needs the
// orchestrator's browser, so that half is filled in afterwards, the same way the panel looker is.
var feeds = new FeedStore(Path.Combine(dataDir, "feeds.json"), json);
// Proact's record. Up here with the feeds because the orchestrator is handed it — its three recording tools are the
// only things in the process allowed to write to it. The loop that drives it is built further down, once everything
// it reads from exists.
var proact = new ProactStore(Path.Combine(dataDir, "proact.json"), json);
var watchers = new WatcherStore(Path.Combine(dataDir, "watchers.json"), json);
FeedReader? feedReader = null;
// Declared here and built below, for the same reason the feed reader is: the orchestrator is handed a way to look at
// a panel, and the looker needs a url this app is still building.
WidgetLook? widgetLook = null;
// And the panel loader, for the same reason again — it needs the orchestrator's browser, so it cannot exist until
// after the orchestrator does. It is what PROVES a panel before it is published.
WidgetDataLoader? widgetLoader = null;

var orchestrator = new Orchestrator(defaultModel, ollamaBaseUrl, WorkerSystemPrompt, json, trainingLog, brain, projects, projectRuns,
    new OrchestratorOptions
    {
        Planner = planner,
        Routing = projectRouting,
        Naming = () => naming.Assistant,
        Hub = controlHub,
        Hints = taskHints,
        Schedules = schedules,
        Widgets = widgets,
        WidgetKinds = widgetKinds,
        Feeds = feeds,
        Watchers = watchers,
        // Reading a feed is what PROVES one before it is published, and the refusal for a feed that reads nothing is
        // the only thing between "watching" and "silently never firing". Deferred through the variable above.
        ReadFeed = (feed, ct) => feedReader is { } reader
            ? reader.ReadAsync(feed, ct)
            : Task.FromResult(FeedRead.Failed("Feeds can't be read on this instance yet.")),
        // The same bargain for a PANEL. Publishing one used to check its declaration only — the model against the
        // map, the holes against the parameters — all of which pass for a loader pointed at a page that serves
        // nothing, which is how a browser-mode weather panel went live and failed every refresh for nine hours.
        // Deferred through the variable above, exactly as the feed reader is.
        ProvePanel = (kind, values, ct) => widgetLoader is { } loader
            ? loader.LoadAsync(kind, values, ct)
            : Task.FromResult(new LoadResult(null, "Panels can't be proved on this instance yet.")),
        // And whether an internal feed name means anything. The naming rules live with the prefixes; only these two
        // registries can say whether the thing named is actually here.
        Proact = proact,
        // Their own devices and services, so it can notice the vacuum has not run today. Built from whatever is
        // installed and ready, which is why it cannot be a fixed list: plugin tool names are decided at install time.
        PluginTools = task =>
        {
            try
            {
                var ready = plugins.Status.Where(p => p.Ready).Select(p => p.CapabilityId).ToArray();
                return ready.Length == 0
                    ? Array.Empty<AgentTool>()
                    : capabilities.BuildFor(ready, integrations, task);
            }
            catch { return Array.Empty<AgentTool>(); }
        },
        // Does this picture actually load? Asked before an image is ever stored, because it renders inline and a
        // url that 404s is a broken-image icon on the page rather than a missing nicety. HEAD first with GET as the
        // fallback: plenty of hosts refuse HEAD and serve the image perfectly well.
        ImageLoads = async (url, ct) =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
                {
                    using var probe = new HttpRequestMessage(method, url);
                    BrowserLike.Wear(probe);
                    using var answered = await http.SendAsync(probe,
                        HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (!answered.IsSuccessStatusCode) continue;

                    // And it has to BE a picture. An html error page returning 200 is the commonest way a
                    // "working" image url turns out to be a page saying the image is gone.
                    var kind = answered.Content.Headers.ContentType?.MediaType ?? "";
                    if (kind.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return false; }
        },
        InternalFeedFault = name => WidgetInternals.Fault(name,
            sourceExists: wanted => sources.Get(wanted) is not null,
            pluginHas: (plugin, command) => plugins.Has(plugin, command)),
        // Filled in below the orchestrator's construction, because the looker needs a url this app is still building.
        LookAtWidget = panel => LookAtWidget(panel),
        // The same eyes, with the verdict handed back — a panel build's last state acts on what was seen.
        CheckWidget = (panel, shows, ct) => widgetLook is { } look
            ? look.AtAsync(panel, shows, ct)
            : Task.FromResult<LookVerdict?>(null),
        LocationNote = location.Note,
        CurrentLocation = () => location.Current,
        WorkspaceRoot = workspaceRoot,
        Personas = personas,
        Capabilities = capabilities,
        IntegrationConfig = integrations,
        People = people,
        LinkPreviews = linkPreviews,
        Lists = projectLists,
        Images = (bytes, mime) => saveImage(bytes, mime),
        MediaDir = mediaDir,
    });

// The tick. A due task fires into the conversation it was set in — OpenConversation, so one set weeks ago in a
// chat this process has never loaded is replayed first and arrives with its history rather than into an empty
// room. What it says is returned so the store can record it: the answer belongs in the thread (that thread is
// what gives the NEXT run its context), but "has this been working" is a question about the task, not the chat.
var scheduler = new Scheduler(schedules, async (ScheduledTask t) =>
{
    Console.WriteLine($"[schedule] #{t.Id} due → {t.SessionId}: {t.TaskText[..Math.Min(80, t.TaskText.Length)]}");
    var session = OpenConversation(t.SessionId);
    return await orchestrator.RunScheduledAsync(session, t.TaskText, t.UserScope, t.UserName);
});
_ = scheduler.RunAsync(app.Lifetime.ApplicationStopping);

// And the panel feeds. Once a panel has been built, keeping it current is an HTTP GET on a timer — no model, no
// browser, no cost. That is what makes a page full of live panels affordable at all.
// The loader: the one place a panel's data is produced, in the shape the component was written against. Browser
// mode goes through the user's own Chrome — both the way past a site that refuses a server request, and the only way
// to reach anything that is only visible when signed in as them.
// Its own client, not the process-wide one: it asks for compressed responses because a browser does, which means
// it has to be able to decompress them. See BrowserLike — the same headers WITHOUT Accept-Encoding go back to being
// refused, and this is the only place that can honestly send it.
var browserLikeHttp = BrowserLike.Client(TimeSpan.FromSeconds(30));
widgetLoader = new WidgetDataLoader(browserLikeHttp, InternalFeed,
    browser: (url, expression, ct) => orchestrator.ReadPageAsync(url, expression, ct),
    trace: Console.Error.WriteLine);
// Looking at a finished panel to see whether it actually works.
//
// The check that closes the last hole. A panel is watched by the error boundary (a component that throws) and by the
// loader check (data that won't load) — and a client-mode panel showing a broken image trips neither: an <img> whose
// source fails does not throw, and there is no server load to fail. The camera panel sat there showing a broken-image
// icon with every mechanism reporting it healthy. Eyes are the only thing that can tell those apart.
widgetLook = new WidgetLook(http, ModelRouting.ApiKey,
    judge: ModelRouting.Provider(defaultModel, ollamaBaseUrl), judgeModel: defaultModel,
    baseUrl: $"http://localhost:{Environment.GetEnvironmentVariable("PORT") ?? "5179"}",
    shotsDir: Path.Combine(dataDir, "_widget-shots"),
    pass: ourPass,
    trace: Console.Error.WriteLine,
    // So a panel can be judged on its DATA before a screenshot is taken. A component handed seven rows of nulls
    // photographs as a working panel; the model says so in one line, for nothing.
    kinds: widgetKinds);

// Make good the builds this process's predecessor was in the middle of.
//
// Here rather than in the store, because rebuilding needs the orchestrator and the store must not know about it. Fired
// and not awaited: a panel finishing its build is not something startup should wait on, and a failure to rebuild leaves
// exactly the state it was already in.
_ = Task.Run(async () =>
{
    foreach (var owed in widgets.ClaimInterrupted())
    {
        try
        {
            var session = OpenConversation(WidgetWork.SessionFor(owed.Id));
            // The ask this build was already working to, when it has one. Handing it a different form of words
            // ("why it's on the page") would read as a new ask and start the states again from the top, throwing
            // away a source that had already been established and proved.
            var shows = widgets.BuildOf(owed.Id)?.Shows is { Length: > 0 } asked
                ? asked
                : owed.Why ?? owed.Title;
            var task = await orchestrator.BuildWidgetAsync(session, owed, shows).ConfigureAwait(false);
            Console.WriteLine($"[widget] {owed.Id} \"{owed.Title}\" was interrupted by a restart — " +
                              $"rebuilding as task {task} (attempt {owed.RestartRetries})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[widget] couldn't pick {owed.Id} back up: {ex.Message}");
        }
    }
});

orchestrator.Sources = sources;

// No repair hook. A loader that keeps failing marks the panel and waits to be asked — see /api/widgets/{id}/fix.
var widgetRefresher = new WidgetRefresher(widgets, widgetKinds, widgetLoader, trace: Console.Error.WriteLine);
_ = widgetRefresher.RunAsync(app.Lifetime.ApplicationStopping);

/*
 * Things that arrive, and what to do about them.
 *
 * The counterpart to everything above. A panel PULLS: it asks its source on a timer and puts the answer on a screen,
 * and nothing happens unless somebody is looking. A feed brings items in and a watcher acts on them — which is the
 * first thing here that can start a conversation the user did not ask for.
 *
 * Same economics as the panels, deliberately: reading a feed is a GET on a timer with no model in it, so it costs
 * nothing to watch something for a month. The model is only reached when an item has already passed a watcher's free
 * filters, and only if that watcher asked for judgement at all.
 */
feedReader = new FeedReader(browserLikeHttp,
    browser: (url, expression, ct) => orchestrator.ReadPageAsync(url, expression, ct),
    trace: Console.Error.WriteLine);

var feedTick = new FeedTick(feeds, watchers, feedReader,
    fire: async (watcher, feed, item) =>
    {
        // Its own conversation, named after the fire rather than after the watcher: a watcher that goes off weekly
        // should not pile eight unrelated things into one thread, and each one is a separate piece of work.
        var chat = OpenConversation($"woke-{watcher.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        watchers.Fired(watcher.Id, chat.Id, feed.Id, item);
        await orchestrator.RunWokenAsync(chat, watcher, feed, item);
    },
    // The expensive half of a match, for a watcher that could not be written as a test. One call, on one item, with
    // the item in front of it — and a plain yes or no, because anything else is a second decision to make.
    judge: (watcher, item, ct) => orchestrator.AgreesAsync(
        "Something has arrived and somebody is watching for a particular sort of thing. Is this one of them?\n\n" +
        $"What they are watching for: {watcher.About}\n\n" +
        $"--- what arrived ---\ntopic: {item.Topic}\ntitle: {item.Title}\n\n{item.Body}\n\n" +
        "Say no if you are not sure: a wrong yes starts a conversation about nothing, and there will be another " +
        "item along shortly.", ct),
    trace: Console.Error.WriteLine);

_ = feedTick.RunAsync(app.Lifetime.ApplicationStopping);

/*
 * Noticing that a panel would be useful, and offering it.
 *
 * The proposal card, the accept-with-edits sheet and the decline have all existed for a while; nothing had ever set a
 * panel to proposed, so the page could only contain what someone had explicitly asked for. What it knows that a
 * website doesn't is exactly what makes a suggestion worth having: where the user is, what is on their lists this
 * week, and which panels they already keep — so that is what it is handed.
 */
var suggestionLog = new SuggestionLog(Path.Combine(dataDir, "widget-suggestions.json"), json);
var widgetSuggester = new WidgetSuggester(widgets, widgetKinds,
    ModelRouting.Provider(defaultModel, ollamaBaseUrl), defaultModel,
    context: () =>
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var parts = new List<string> { $"Today is {DateTime.Now:dddd d MMMM yyyy, HH:mm}." };

        // The place, not the whole location note: that note is a paragraph of instructions to a worker about
        // confirming journeys, and pasting it here buried the one fact that matters in guidance meant for someone else.
        if (location.Current is { Place: { Length: > 0 } place })
            parts.Add($"They are in {place} right now.");

        var panels = widgets.All().Where(w => w.Status != WidgetStatus.Proposed).ToList();
        parts.Add(panels.Count == 0
            ? "Their home page is empty."
            : "Panels they already have: " + string.Join("; ", panels.Select(w => $"{w.Title} ({w.Size})")) + ".");

        if (widgetKinds.All() is { Count: > 0 } kinds)
            parts.Add("Kinds already in the library, which cost nothing to instantiate: " +
                      string.Join(", ", kinds.Select(k => $"{k.Name} ({string.Join("/", k.Params.Select(x => x.Name))})")) + ".");

        // The next few days of whatever they are actually planning — the strongest signal there is, and the one a
        // stranger could never guess.
        if (projectLists.Agenda(today, 5) is { Count: > 0 } agenda)
            parts.Add("On their lists over the next few days: " +
                      string.Join("; ", agenda.Take(12).Select(e => $"{e.Item} ({e.ListTitle})")) + ".");

        // Their projects, as the store already words them for the model — no second formatting to keep in step.
        if (projects.List() is { Length: > 0 } open) parts.Add(open.Trim());

        return string.Join("\n", parts);
    },
    log: suggestionLog,
    every: TimeSpan.FromHours(4),
    trace: Console.Error.WriteLine);

_ = widgetSuggester.RunAsync(app.Lifetime.ApplicationStopping);

/*
 * Proact: the first thing here that acts without being asked.
 *
 * Everything else in this process is pulled — a timer fires, an item arrives, a person types. This wakes up on the
 * user's own cadence and decides for itself whether anything would help, which is a different kind of thing to have
 * running and is why it is off until somebody turns it on.
 *
 * The economics are the same bargain the feeds make and they matter more here, because the dial goes down to five
 * minutes: the fingerprint below is assembled from things already in memory and fetches nothing, so a wakeup with
 * nothing to think about costs nothing at all rather than costing a little. See PROACT_SPEC.md.
 */
var proactLoop = new ProactLoop(proact,
    ModelRouting.Provider(defaultModel, ollamaBaseUrl), defaultModel,
    context: () =>
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var parts = new List<string> { $"Today is {DateTime.Now:dddd d MMMM yyyy, HH:mm}." };

        if (location.Current is { Place: { Length: > 0 } place })
            parts.Add($"They are in {place} right now.");

        // What they have to do, and when — the closest thing to a calendar until there is one. Two days rather than
        // one: "tomorrow" is where the useful noticing lives, since today is already visible to them.
        if (projectLists.Agenda(today, 2) is { Count: > 0 } agenda)
            parts.Add("On their lists now: " + string.Join("; ", agenda.Take(14).Select(e =>
                $"{e.Item}{(e.ListTitle is { Length: > 0 } ? $" ({e.ListTitle})" : "")}{(e.Done ? " — done" : "")}")));
        else
            parts.Add("Nothing dated on their lists for today or tomorrow.");

        if (projects.ActiveProjects() is { Count: > 0 } active)
            parts.Add("Projects they have on: " + string.Join("; ", active.Take(8).Select(p =>
                $"{p.Title}{(p.Description is { Length: > 0 } d ? $" — {d}" : "")}")));

        // WITH WHAT EACH ONE ACTUALLY SHOWS, because a bare title is a guess waiting to happen.
        //
        // A list of names read as a list of interests, and one of the names is "Running work" — which is this
        // system's tasks-in-progress panel. It was read as jogging, and a whole deep look went into the London
        // Marathon ballot on the strength of it. The kind's own description is written to say what a panel shows,
        // so using it costs nothing and removes the ambiguity at source.
        if (widgets.All().Where(w => w.Status != WidgetStatus.Proposed).ToList() is { Count: > 0 } panels)
            parts.Add("Panels on their home page, and what each one shows. These are things they GLANCE at — a " +
                      "panel is not proof of a hobby, and some of them are just this system's own dashboards: " +
                      string.Join("; ", panels.Select(w =>
                          widgetKinds.Get(w.Kind)?.Description is { Length: > 0 } shows
                              ? $"{w.Title} — {shows}"
                              : w.Title)));

        if (feeds.All().Where(f => !f.Paused).ToList() is { Count: > 0 } live)
            parts.Add("Feeds arriving: " + string.Join("; ", live.Select(f => f.Name)));

        // WHAT IT CAN DO, not just what it knows.
        //
        // The gap this closes was invisible from the code and obvious the moment somebody asked why it never
        // suggested running the vacuum: the worker had all eight Roborock commands and the DECISION had never heard
        // of them. It cannot think "I could check whether that has run today" about a machine it does not know
        // exists, so the tools were reachable and unreachable at the same time — available to the half that acts,
        // hidden from the half that chooses.
        // WITH WHAT EACH COMMAND DOES, because a bare identifier is not a fact about their life.
        //
        // This printed `Roborock (roborock_status, roborock_rooms, roborock_clean, ...)` and nothing else, so the
        // only way to know that one of those reports when the machine last ran was to infer it from the word
        // "status". Asked afterwards why it had not looked, the decision said "nothing of yours needs attention" —
        // a claim about a state it cannot see, from a call that has no tools, made because the alternative on offer
        // was an identifier rather than a capability. The plugin already writes a description for every command and
        // it was being dropped on the floor here.
        if (plugins.Status.Where(p => p.Ready).ToList() is { Count: > 0 } theirs)
            parts.Add("Things of theirs you can actually look at and operate, and what each command does: " +
                      string.Join("; ", theirs.Select(p =>
                          $"{p.Name} — " + string.Join(", ", p.Commands.Take(8).Select(c =>
                              c.Description is { Length: > 0 } does
                                  ? $"{c.Tool} ({does.TrimEnd('.')})"
                                  : c.Tool)))));

        // WHAT THEY HAVE ACTUALLY BEEN TALKING ABOUT.
        //
        // The thing that was missing, and the reason it kept going back to restaurants. Asked "what would help this
        // person", the whole picture it had was: a location, empty lists, and one project — which happened to be
        // about places to eat. So it went there, four times, correctly, because that was the only substantial thing
        // it knew. Not a wayward model; a starved one.
        //
        // The opening line of each recent conversation is the richest signal there is about what somebody is
        // actually dealing with, and it costs a file read each. Their own words, not a summary of them.
        var talked = chatHistory.List()
            .Where(c => !c.Id.StartsWith("proact-", StringComparison.OrdinalIgnoreCase)
                        && !c.Id.StartsWith("woke-", StringComparison.OrdinalIgnoreCase)
                        && !c.Id.StartsWith("widget-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.LastWrite)
            .Take(12)
            .Select(c => (c.LastWrite, Opened: chatHistory.FirstUserMessage(c.Id)))
            .Where(c => c.Opened is { Length: > 0 })
            .ToList();

        if (talked.Count > 0)
            parts.Add("What they have been talking to you about lately, in their own words — this is the best guide " +
                      "you have to what is actually going on in their life:\n" +
                      string.Join("\n", talked.Select(c =>
                          $"  ({c.LastWrite.ToLocalTime():ddd d MMM}) {(c.Opened!.Length <= 200 ? c.Opened : c.Opened![..200] + "…")}")));

        // What it knows about them as standing fact is deliberately NOT pasted here. The brain is a graph you
        // query, this prompt runs on every single tick, and paying for a recall 288 times a day to inform a
        // decision that is usually "do nothing" is the wrong trade. The worker that actually goes and does
        // something holds the recall tool and can ask then — cheaper, and aimed at the thing it is doing.
        return string.Join("\n", parts);
    },
    // The free gate. Counts and latest ids only — anything that fetched here would be fetched 288 times a day.
    fingerprint: () => string.Join("|",
        projectLists.All().Sum(l => l.Items.Count),
        projectLists.All().Sum(l => l.DoneCount),
        projects.ActiveProjects().Count,
        widgets.All().Count,
        feeds.All().Sum(f => f.Seen),
        watchers.Fires().Count,
        chatHistory.List().Count,
        chatHistory.List().OrderByDescending(c => c.LastWrite).FirstOrDefault().LastWrite.ToUnixTimeSeconds(),
        DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd")),
    dispatch: async (mode, brief) =>
    {
        // Its own conversation per run, named for when it happened: a loop that piled a week of unrelated errands
        // into one thread would make every one of them harder to read than it needs to be.
        var chat = OpenConversation(Proact.Conversation(mode, DateTimeOffset.UtcNow));

        // The conversation comes back with the task. It is the only handle on a run in progress, and the page needs
        // it to show the work rather than a sentence saying work is happening somewhere.
        return (await orchestrator.RunProactAsync(chat, mode, brief), chat.Id);
    },
    trace: Console.Error.WriteLine);

_ = proactLoop.RunAsync(app.Lifetime.ApplicationStopping);

/*
 * Projects that come round again.
 *
 * A period project ended on its last day and everything built on it went stale — which is most of what makes a panel
 * over your own data worth having. "Week of dinners" is not finished on Sunday; it recurs, and the version that
 * mattered last week is still the record of last week. So the window advances and the new one gets filled in from the
 * project's own standing instruction.
 *
 * Checked hourly rather than at midnight: a machine asleep at midnight would miss the day entirely, and RenewedOn is
 * what keeps a second check from planning the same period twice.
 */
async Task RenewProjectsAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var project in projects.DueForRenewal(today))
            {
                if (ProjectRenewal.Next(project, today) is not { } next)
                {
                    Console.Error.WriteLine($"[project] {project.Slug} repeats but its next period can't be worked out");
                    continue;
                }

                if (!projects.Renew(project.Slug, next.Starts, next.Ends, today)) continue;
                Console.WriteLine($"[project] {project.Slug} rolled over to {next.Starts:d MMM} - {next.Ends:d MMM}");

                // And filled in, when there is a standing instruction to fill it from. Driven through the same path a
                // scheduled task uses, so the assistant reads it, uses its own list tools and reports into the
                // project's thread. Fire and forget: planning a week must not hold the hourly loop.
                if (ProjectRenewal.Brief(project, next.Starts, next.Ends) is not { } brief) continue;

                // A worker, given the project so it has that project's list tools. Not the scheduled-message path:
                // that one prepends "report the outcome to the thread in one short line", which is framing for a
                // heads-up rather than a job — and the renewal that rode it read around the project, called nothing
                // that writes, and reported a week of dinners it had not recorded.
                var chat = OpenConversation($"project-{project.Slug}");
                var work = await orchestrator.StartWorkAsync(chat, brief, project.Slug);
                Console.WriteLine($"[project] {project.Slug} new period being filled in by task {work}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[project] renewal: {ex.Message}"); }

        try { await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { break; }
    }
}

_ = RenewProjectsAsync(app.Lifetime.ApplicationStopping);

// Roll a recurring project forward now rather than waiting for its window to close. How this is testable at all, and
// the answer when somebody wants next week planned early.
app.MapPost("/api/projects/{slug}/renew", async (string slug) =>
{
    if (projects.Get(slug) is not { } project) return Results.NotFound(new { error = "no such project" });
    if (!project.Recurring) return Results.BadRequest(new { error = $"\"{project.Title}\" doesn't repeat" });

    var today = DateOnly.FromDateTime(DateTime.Now);
    // A project still inside its window can be rolled on deliberately: pretend today is the day after it ends, which
    // is exactly the situation renewal is written for.
    var asIf = project.EndsOn is { } ends && ends >= today ? ends.AddDays(1) : today;

    if (ProjectRenewal.Next(project, asIf) is not { } next)
        return Results.BadRequest(new { error = "couldn't work out its next period" });

    projects.Renew(slug, next.Starts, next.Ends, today);
    var brief = ProjectRenewal.Brief(project, next.Starts, next.Ends);

    string? work = null;
    if (brief is not null)
    {
        var chat = OpenConversation($"project-{slug}");
        work = await orchestrator.StartWorkAsync(chat, brief, slug);
    }

    return Results.Ok(new
    {
        renewed = slug,
        starts = next.Starts.ToString("yyyy-MM-dd"),
        ends = next.Ends.ToString("yyyy-MM-dd"),
        filling = work,
    });
});

// Fill in the period a project is CURRENTLY in, rather than rolling it forward.
//
// Renewal handles the next window; this handles the one you are in — which is what a project needs the moment
// recurrence is added to it half way through a week, and the only way to answer "what is for dinner tonight" when
// tonight falls inside a window nothing has planned yet.
app.MapPost("/api/projects/{slug}/fill", async (string slug) =>
{
    if (projects.Get(slug) is not { } project) return Results.NotFound(new { error = "no such project" });
    if (project.StartsOn is not { } starts || project.EndsOn is not { } ends)
        return Results.BadRequest(new { error = $"\"{project.Title}\" has no dates to fill" });
    if (ProjectRenewal.Brief(project, starts, ends) is not { } brief)
        return Results.BadRequest(new { error = "it has no standing instruction to fill from" });

    var chat = OpenConversation($"project-{slug}");
    var work = await orchestrator.StartWorkAsync(chat, brief, slug);
    return Results.Ok(new { filling = work, starts = starts.ToString("yyyy-MM-dd"), ends = ends.ToString("yyyy-MM-dd") });
});

// Make a project's period repeat, or stop it repeating.
app.MapPost("/api/projects/{slug}/repeat", (string slug, ProjectRepeat body) =>
    Results.Ok(new { message = projects.SetRepeat(slug, body?.Repeat, body?.RenewPrompt) }));


// Ask it to consider one now, rather than waiting for the interval. What a "suggest something" button calls, and how
// the whole thing is testable at all.
app.MapPost("/api/widgets/suggest", async () =>
{
    var proposal = await widgetSuggester.LookAsync(CancellationToken.None, force: true);
    return proposal is null
        ? Results.Ok(new { suggested = false, reason = "nothing worth offering right now" })
        : Results.Json(widgets.Get(proposal.Id), json);
});


// A file a task produced, by session and name. Name-only lookup so a path can't climb out of the thread's own
// folder, and inline rather than as a download so a presentation opens where the user is standing.
app.MapGet("/api/session/{id}/files/{name}", (string id, string name) =>
{
    var dir = Orchestrator.FilesDirFor(workspaceRoot, id);
    var safe = Path.GetFileName(name);
    var path = Path.Combine(dir, safe);
    if (!File.Exists(path)) return Results.NotFound();

    var type = Path.GetExtension(safe).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".md" or ".txt" => "text/plain; charset=utf-8",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
    return Results.File(path, type);
});

// ---- the remote browser ----
// For the user who is not at their machine. A picture of the tab, and their taps and keystrokes forwarded to it.
// Nothing here is logged: a password passes through this endpoint on its way to a login form and must leave no
// trace in a transcript, a trace file or a model prompt.
var remoteBrowser = new RemoteBrowser(mcp);

app.MapGet("/api/browser/view", async (int? tabId, CancellationToken ct) =>
    await remoteBrowser.ViewAsync(tabId, ct) is { } image
        ? Results.Json(new { image })
        : Results.Json(new { error = remoteBrowser.LastError ?? "no browser connected" }, statusCode: 503));

// Frames pushed down a socket, instead of a picture written to disk and then fetched back over HTTP.
//
// The panel used to cost four hops a frame: an HTTP call to ask for a screenshot, the capture itself, a PNG
// written into data/media, and a second HTTP request from the browser to collect it. Two of those are pure
// overhead — the bytes already exist in this process the moment the capture returns. Pushing them straight down
// an open socket removes the round-trip and the image-load, which is what made it feel like a slideshow.
app.Map("/api/browser/stream", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var ct = ctx.RequestAborted;
    // Ordered, because "delete the oldest" needs an oldest. A set has no order, so the previous version deleted
    // arbitrary frames — sometimes the one still being served, which is locked — and dropped them from tracking
    // even when the delete failed, leaking most of them.
    var sent = new Queue<string>();

    try
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var url = await remoteBrowser.ViewAsync(null, ct);
            if (url is null)
            {
                var problem = System.Text.Json.JsonSerializer.Serialize(
                    new { error = remoteBrowser.LastError ?? "no browser connected" });
                await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(problem),
                    WebSocketMessageType.Text, true, ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            var file = Path.Combine(mediaDir, Path.GetFileName(url));
            if (File.Exists(file))
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                var mime = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg",
                };
                var frame = System.Text.Json.JsonSerializer.Serialize(
                    new { frame = Convert.ToBase64String(bytes), mime });
                await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(frame),
                    WebSocketMessageType.Text, true, ct);

                // Each capture leaves a file behind and nobody is coming back for it: twelve frames a second is
                // seven hundred PNGs a minute. The oldest goes once a couple of newer ones exist — and if the
                // delete fails because something still holds it, it goes to the back of the queue for another
                // attempt rather than being forgotten.
                sent.Enqueue(file);
                while (sent.Count > 3)
                {
                    var oldest = sent.Dequeue();
                    try
                    {
                        File.Delete(oldest);
                    }
                    catch (IOException)
                    {
                        if (sent.Count < 30) sent.Enqueue(oldest); // try again shortly; never grow without bound
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (sent.Count < 30) sent.Enqueue(oldest);
                    }
                }
            }
        }
    }
    catch (OperationCanceledException) { /* the panel was closed */ }
    catch (WebSocketException) { /* likewise, less politely */ }
});

// The CSS viewport, so a tap on the screenshot maps to where the page thinks it is.
app.MapGet("/api/browser/viewport", async (CancellationToken ct) =>
    await remoteBrowser.ViewportAsync(ct) is { } v
        ? Results.Json(new { width = v.Width, height = v.Height })
        : Results.Json(new { width = 0, height = 0 }));

app.MapGet("/api/browser/tabs", async (CancellationToken ct) =>
    Results.Json(new { tabs = await remoteBrowser.TabsAsync(ct) }));

app.MapPost("/api/browser/act", async (HttpContext ctx, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
    var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
    var result = await remoteBrowser.ActAsync(kind, doc.RootElement, ct).ConfigureAwait(false);
    // ok reflects what the action actually returned, and the error travels so the panel can say something. The
    // previous version inferred success from a string prefix and reported a failed screenshot as ok:true.
    var image = await remoteBrowser.ViewAsync(null, ct).ConfigureAwait(false);
    bool ok = !result.StartsWith("no ", StringComparison.Ordinal)
              && !result.StartsWith("unknown", StringComparison.Ordinal)
              && !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
    return Results.Json(new { ok, image, error = image is null ? remoteBrowser.LastError : null, result = ok ? null : result });
});

// One link's card, for checking what a page actually advertises about itself.
app.MapGet("/api/link-preview", async (string url, CancellationToken ct) =>
    await linkPreviews.GetAsync(url, ct) is { } preview ? Results.Json(preview) : Results.NotFound());

// Serve stored tool images. Name-only lookup so a path can't escape the folder.
app.MapGet("/api/media/{name}", (string name) =>
{
    var safe = Path.GetFileName(name);
    var path = Path.Combine(mediaDir, safe);
    if (!File.Exists(path)) return Results.NotFound();
    // The mirror of the naming above: an image extension is served as that image type, so a format nobody
    // enumerated still reaches the browser as a picture.
    var ext = Path.GetExtension(safe).ToLowerInvariant().TrimStart('.');
    var type = ext switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "svg" => "image/svg+xml",
        "" or "bin" => "application/octet-stream",
        _ => $"image/{ext}",
    };
    return Results.File(path, type);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", model = defaultModel }));

// The projects on the go — for the slide-out bar. Only active projects, with how much is recorded against
// each. Projects are the only thing surfaced here: work with no project isn't shown anywhere.
// Newest ACTIVITY first, not newest created. A project you set up in January and worked on this morning is the
// one you want at the top; ordering by creation buried it under things you have not touched since.
// A project with no runs yet falls back to when it was made, which is the only interaction it has had.
app.MapGet("/api/projects", () => Results.Ok(
    projects.ActiveProjects()
        .Select(p => new
        {
            slug = p.Slug,
            title = p.Title,
            description = p.Description,
            // Which of the two it is, and what finishing would mean. The bar shows them apart because they are apart:
            // one is being driven at something, the other is only being kept.
            sort = projects.Get(p.Slug)?.Sort ?? "topic",
            goal = projects.Get(p.Slug)?.Goal ?? "",
            window = projects.Get(p.Slug)?.WindowNote(DateOnly.FromDateTime(DateTime.Now.Date)) ?? "",
            runs = projectRuns.CountFor(p.Slug),
            facts = ProjectKnowledge(graph, projects.Get(p.Slug)?.Title ?? p.Slug).Count,
            lastActivity = projectRuns.LastActivityFor(p.Slug) ?? projects.Get(p.Slug)?.Created,
        })
        .OrderByDescending(p => p.lastActivity ?? DateTimeOffset.MinValue)));

// One project's overview: everything remembered about it, and everything its background workers did
// (thinking + tool calls + results), newest first. Read-only.
// Serve a file filed against a project. Name-only, so a path can't climb out of the project's own folder, and
// inline rather than as a download so a deck opens where the user is standing.
app.MapGet("/api/projects/{slug}/files/{name}", (string slug, string name) =>
{
    var safe = Path.GetFileName(name);
    var path = Path.Combine(Orchestrator.ProjectFilesDirFor(workspaceRoot, slug), safe);
    if (!File.Exists(path)) return Results.NotFound(new { error = "no such file on that project" });

    var provider = new FileExtensionContentTypeProvider();
    var type = provider.TryGetContentType(safe, out var t) ? t : "application/octet-stream";
    return Results.File(path, type);
});

// A picture of a shelved document, so a project's files are something you can look at rather than a wall of
// identical grey tiles reading "PDF". Rendered on first request and cached; 404 when the type can't be drawn or
// the render failed, which the tile shows as the typed placeholder it always used to.
app.MapGet("/api/projects/{slug}/files/{name}/thumb", async (string slug, string name, CancellationToken ct) =>
{
    var safe = Path.GetFileName(name);
    var path = Path.Combine(Orchestrator.ProjectFilesDirFor(workspaceRoot, slug), safe);

    var thumb = await Thumbnails.GetOrCreateAsync(workspaceRoot, $"project-{slug}", path, safe, ct);
    return thumb is null
        ? Results.NotFound(new { error = "no preview for that file" })
        : Results.File(thumb, "image/png");
});

// The same for a file that lives on a conversation rather than a project — the chat's own file cards use it.
app.MapGet("/api/session/{id}/files/{name}/thumb", async (string id, string name, CancellationToken ct) =>
{
    var safe = Path.GetFileName(name);
    var dir = Orchestrator.FilesDirFor(workspaceRoot, id);
    var thumb = await Thumbnails.GetOrCreateAsync(workspaceRoot, $"chat-{id}", Path.Combine(dir, safe), safe, ct);
    return thumb is null
        ? Results.NotFound(new { error = "no preview for that file" })
        : Results.File(thumb, "image/png");
});

// A generated page as a PDF, printed headlessly so the export is the same for everyone. The browser's own
// print dialog was losing pages: the page's CSS is right, but an iframe print inherits whatever scale and
// margins that browser is set to, and background graphics are off by default.
app.MapGet("/api/projects/{slug}/files/{name}/pdf", async (string slug, string name, CancellationToken ct) =>
{
    var safe = Path.GetFileName(name);
    var path = Path.Combine(Orchestrator.ProjectFilesDirFor(workspaceRoot, slug), safe);
    var pdf = await Thumbnails.PdfOfPageAsync(workspaceRoot, $"project-{slug}", path, safe, ct);
    return pdf is null
        ? Results.NotFound(new { error = "that file can't be turned into a PDF" })
        : Results.File(pdf, "application/pdf", Path.GetFileNameWithoutExtension(safe) + ".pdf");
});

app.MapGet("/api/session/{id}/files/{name}/pdf", async (string id, string name, CancellationToken ct) =>
{
    var safe = Path.GetFileName(name);
    var dir = Orchestrator.FilesDirFor(workspaceRoot, id);
    var pdf = await Thumbnails.PdfOfPageAsync(workspaceRoot, $"chat-{id}", Path.Combine(dir, safe), safe, ct);
    return pdf is null
        ? Results.NotFound(new { error = "that file can't be turned into a PDF" })
        : Results.File(pdf, "application/pdf", Path.GetFileNameWithoutExtension(safe) + ".pdf");
});

// Remove a file from a project. The project's copy goes and its claim on the name goes; the conversation that
// produced it keeps its own, because tidying a project shelf should not rewrite an old chat.
app.MapDelete("/api/projects/{slug}/files/{name}", (string slug, string name) =>
{
    var safe = Path.GetFileName(name);
    int touched = projectRuns.ForgetFile(slug, safe);

    var shelved = Path.Combine(Orchestrator.ProjectFilesDirFor(workspaceRoot, slug), safe);
    bool onDisk = File.Exists(shelved);
    if (onDisk)
    {
        try { File.Delete(shelved); } catch (IOException) { onDisk = false; }
    }

    return touched > 0 || onDisk
        ? Results.Ok(new { removed = safe })
        : Results.NotFound(new { error = "that project doesn't have a file by that name" });
});

// Lists, editable from the project page: the user changes their mind about a pool as readily as the assistant does.
app.MapPost("/api/projects/{slug}/lists", async (string slug, HttpContext ctx) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body).ConfigureAwait(false);
    var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
    if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "a list needs a title" });
    return Results.Json(projectLists.Create(slug, title));
});

app.MapPatch("/api/lists/{id}", async (string id, HttpContext ctx) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body).ConfigureAwait(false);
    IEnumerable<string> Read(string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "")
            : Array.Empty<string>();

    var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    var updated = projectLists.Update(id, Read("add"), Read("remove"), title);
    if (updated is null) return Results.NotFound(new { error = "no such list" });

    // Ticking rides on the same call: a box the user clicks and a box a worker ticks are the same edit, and
    // splitting them into two endpoints is how the two drift apart.
    if (projectLists.Check(id, Read("done"), Read("undone")) is { } checked_) updated = checked_.List;

    // And so does dating. `on` is item/date pairs, an empty date clearing one.
    if (doc.RootElement.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.Array)
    {
        var wanted = new Dictionary<string, DateOnly?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in on.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Object) continue;
            var item = pair.TryGetProperty("item", out var i) ? i.GetString()?.Trim() ?? "" : "";
            var date = pair.TryGetProperty("date", out var d) ? d.GetString()?.Trim() ?? "" : "";
            if (item.Length == 0) continue;
            if (date.Length == 0) wanted[item] = null;
            else if (DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var day)) wanted[item] = day;
            else return Results.BadRequest(new { error = $"couldn't read the date \"{date}\"" });
        }
        if (wanted.Count > 0 && projectLists.Schedule(id, wanted) is { } dated) updated = dated.List;
    }

    return Results.Json(updated);
});

// ---- The home page ---------------------------------------------------------------------------------------
//
// Panels, in the order they should be read. Built-in sources are resolved HERE, on every request, rather than
// stored: the agenda and the running tasks are already in memory, and a panel showing a cached copy of something
// we hold live would be the one thing on the page that could be wrong. Only agent panels carry stored data,
// because going to the internet on page load is four blank seconds and a bill.
app.MapGet("/api/widgets", () =>
{
    // Opening the page is what triggers a refresh, not only the timer. A panel with nothing to show, or one whose
    // cadence is tomorrow morning, fills in within seconds of being looked at instead of staying blank — and what
    // is on screen meanwhile is whatever it last knew, with its age on it.
    if (widgets.Nudge(DateTimeOffset.UtcNow) is > 0 and var woken)
        Console.Error.WriteLine($"[widget] home page opened — {woken} panel(s) due a load");

    var rows = widgets.All().Select(w => new
    {
        id = w.Id,
        title = w.Title,
        size = w.Size,
        priority = w.Priority,
        pinned = w.Pinned,
        status = w.Status,
        why = w.Why,
        error = w.Error,
        taskId = w.TaskId,
        session = w.Session,
        fixes = w.Fixes,
        refresh = w.Refresh,
        fetchedAt = w.FetchedAt,
        kind = w.Kind,
        // Only the MODE, for the panel's menu. The url, headers and expression never leave the server: one of them
        // may be a key, and the page has no use for any of them.
        loader = widgetKinds.Get(w.Kind)?.Loader.Mode,
        // Whether there is anything to fetch, which is not the same question as which mode it is. Sent because
        // the menu offered "Refresh now" on a panel that has no feed, the server answered "that panel has no feed
        // to fetch", and the page threw the answer away — so the panel that could never change was also the one
        // that gave no sign of it when you asked it to.
        fetches = w.ServerLoads,
        // Resolved through the library rather than stored per panel, so a fix to a kind reaches every panel made
        // from it without touching any of them.
        code = w.OwnCode ?? widgetKinds.Get(w.Kind)?.Code,
        // What it looks like before it works, and what is being done to it — the two together are what let the page
        // show a panel being built instead of a grey line for eight minutes.
        design = w.Design,
        sample = w.Sample is { Length: > 0 } sm ? JsonDocument.Parse(sm).RootElement : (JsonElement?)null,
        stage = w.Stage,
        // The parameter values, because a panel usually has to SAY what it is about and the loaded data often
        // doesn't carry it — an endpoint keyed on a flight number tends not to echo the number back.
        parameters = w.Params,
        history = WidgetMemory.Shape(w.History),
        // The question its build has stopped on, if it has stopped on one.
        //
        // Without this a build that needs something — the camera's credentials, which model it is — asks into the
        // conversation it happens to belong to and waits there forever. The panel said "Building…" and there was no
        // way to answer it from the one place the user was actually looking. A question belongs where the thing is.
        question = w.Asks,
        // Already in the kind's declared shape, so the component renders it directly. Inline here so the first
        // paint has it; /api/widgets/{id}/data is the live one.
        data = w.Data is { Length: > 0 } d ? JsonDocument.Parse(d).RootElement : (JsonElement?)null,
        // Something has been noticed about it that a repair could address, and NOTHING has been started about it.
        // This is what puts "Fix what's wrong" in the panel's menu — the decision that used to be taken for the user,
        // by whichever check noticed first, on a panel that was very often working.
        ailing = w.Ailing,
        // What was noticed, in the words to show them, so the menu item is a choice rather than a leap of faith.
        wrong = w.Ailing ? w.Wrong : null,
    });
    return Results.Json(rows, json);
});

// Accept or decline something the system offered. Accepting is what STARTS the build — an offer nobody said yes to
// should never have cost a worker. Declining removes it, because an offer left in a list gets made again.
/*
 * Yes or no to a proposed panel — and, if yes, with whatever the user changed about it first.
 *
 * A proposal used to be a bare yes/no, which meant the only way to influence what got built was to say no and ask
 * again differently. But the two things most worth adjusting are known before a single line is written: how big it
 * should be, and what it should actually show. Both arrive here, and the size is applied BEFORE the build starts, so
 * the component is written for the box it will end up in rather than resized afterwards and clipped.
 */
app.MapPost("/api/widgets/{id}/answer", async (string id, WidgetAnswer body) =>
{
    if (widgets.Get(id) is not { } w) return Results.NotFound(new { error = "no such panel" });
    if (body?.Accept != true)
    {
        // Recorded, so the next look knows how this one landed. A declined idea that returns is worse than one that
        // never came, and a decline is also the clearest signal there is about what NOT to offer next.
        if (w.Status == WidgetStatus.Proposed) widgetSuggester.Answered(w.Title, accepted: false, w.Why);

        // Saying no has to stop the work as well as take the panel away. A rebuild of an existing panel can already be
        // running when it is declined — that is exactly what happened here: the panel went, the build did not, and it
        // spent the next hour asking how to publish to something that no longer existed.
        if (w.Session is { Length: > 0 } asked && sessions.Get(asked) is { } chat)
            orchestrator.CancelBuildsFor(chat, id);

        widgets.Remove(id);
        return Results.Ok(new { declined = id });
    }

    if (w.Status == WidgetStatus.Proposed) widgetSuggester.Answered(w.Title, accepted: true, w.Why);

    // Applied first: Footprint() in the brief describes the box by its real size, so the build has to know it now.
    if (body.Size is { Length: > 0 } size && WidgetSizes.Known(size))
        widgets.Edit(id, null, size, null, null, null, null);

    var shows = body.Shows is { Length: > 0 } edited ? edited.Trim() : w.Why ?? w.Title;
    var session = OpenConversation(WidgetWork.SessionFor(id));
    var taskId = await orchestrator.BuildWidgetAsync(session, widgets.Get(id)!, shows);
    return Results.Ok(new { accepted = id, task = taskId });
});

/*
 * The kind library, and retiring something from it.
 *
 * Neither route existed. WidgetLibrary.Remove has been there all along with nothing calling it, which is why two
 * kinds that CANNOT work — a camera stream and a local photo folder, both client-mode, both impossible from a page —
 * were still being offered as reusable shapes long after the panels made from them were deleted. A dead kind is worse
 * than a missing one: it makes the next request an instant widget_add and an instant blank box.
 */
app.MapGet("/api/widgets/kinds", () => Results.Json(widgetKinds.All().Select(k => new
{
    k.Name,
    k.Title,
    k.Description,
    mode = k.Loader.Mode,
    parameters = k.Params.Select(p => p.Name),
    track = k.Track,
    k.Uses,
    k.DefaultSize,
    instances = widgets.Instances(k.Name).Count,
}), json));

app.MapDelete("/api/widgets/kinds/{name}", (string name) =>
{
    var instances = widgets.Instances(name).Count;
    if (!widgetKinds.Remove(name)) return Results.NotFound(new { error = $"no kind called \"{name}\"" });

    Console.WriteLine($"[widget] retired the \"{name}\" kind" +
                      (instances > 0 ? $" — {instances} panel(s) were made from it and will stop loading" : ""));
    return Results.Ok(new { retired = name, orphaned = instances });
});

app.MapPatch("/api/widgets/{id}", (string id, WidgetEdit body) =>
{
    if (widgets.Get(id) is null) return Results.NotFound(new { error = "no such panel" });
    return widgets.Edit(id, body?.Title, body?.Size, body?.Priority, body?.Pinned, body?.Status, body?.Refresh)
        ? Results.Json(widgets.Get(id), json)
        : Results.BadRequest(new { error = "couldn't read the refresh cadence" });
});

/*
 * One panel's data, in the shape its component was written against.
 *
 * The endpoint the client actually lives on. Everything about WHERE the data comes from — an API, a page loaded in
 * the user's Chrome, something we already hold — and everything about turning that into the declared model happens
 * behind here. The page asks for a panel's data and gets an object it can render; it cannot tell the difference
 * between a JSON API and a scrape, which is exactly the point, because that means a source can move without the
 * component changing a character.
 *
 * Served from the last load rather than loading on request: a panel that goes to the internet every time the page
 * opens is a panel that is blank for four seconds and costs money to look at. `?fresh=true` forces one, which is
 * what the menu's Refresh does.
 */
app.MapGet("/api/widgets/{id}/data", async (string id, bool? fresh) =>
{
    if (widgets.Get(id) is not { } panel) return Results.NotFound(new { error = "no such panel" });
    if (widgetKinds.Get(panel.Kind) is not { } kind && panel.OwnCode is null)
        return Results.NotFound(new { error = "that panel isn't attached to anything that produces data" });

    if (fresh == true) await widgetRefresher.LoadOneAsync(panel, CancellationToken.None);

    var after = widgets.Get(id)!;
    return Results.Json(new
    {
        id = after.Id,
        // The model, so a client can render a field it has never seen before without being redeployed.
        model = widgetKinds.Get(after.Kind)?.Model,
        data = after.Data is { Length: > 0 } d ? JsonDocument.Parse(d).RootElement : (JsonElement?)null,
        // What it has seen before now, already differenced. A panel that can only say what is true this second can
        // never say what changed, and what changed is the interesting half.
        history = WidgetMemory.Shape(after.History),
        fetchedAt = after.FetchedAt,
        error = after.Error,
    }, json);
});

// Logged, because a panel disappearing is otherwise untraceable. One vanished mid-build and it took reading three
// logs to establish that nothing server-side had removed it — the user had, from the page, which is entirely their
// right and exactly the sort of thing that should leave a line behind.
app.MapDelete("/api/widgets/{id}", (string id) =>
{
    if (widgets.Get(id) is not { } panel) return Results.NotFound(new { error = "no such panel" });
    var (title, origin) = (panel.Title, panel.Session);
    widgets.Remove(id);
    Console.Error.WriteLine($"[widget] {id} \"{title}\" removed from the page");

    // And the conversation that made it is told, so its own history stops claiming the thing still exists. Only a
    // live session — a cold one is replayed from the recording, and re-opening it to add a note nobody has asked
    // for would be a write on every delete.
    if (origin is { Length: > 0 } && sessions.Get(origin) is { } chat)
    {
        // Before the note, because this is the part that was missing: a build already in flight carried on against a
        // panel that no longer existed, failed to publish to it, and asked the user what to do.
        orchestrator.CancelBuildsFor(chat, id);
        orchestrator.PanelRemoved(chat, title);
    }

    return Results.Ok(new { removed = id });
});

// Fetch the feed again now — the panel's pull-to-refresh.
/*
 * Put a panel up from a kind that already exists.
 *
 * The one thing the control surface could not do. It could list, edit, delete, refresh, rebuild and inspect a panel,
 * but creating one existed only as the model's widget_add tool — so a panel deleted by accident could only be restored
 * by asking the assistant to do it, and a library full of working kinds had no direct way to be used. Instant by
 * design: the kind carries the component and the loader, so this is a row in a file, not a build.
 */
app.MapPost("/api/widgets/from-kind", (WidgetFromKind body) =>
{
    if (widgetKinds.Get(body?.Kind) is not { } kind)
        return Results.NotFound(new { error = $"no kind called \"{body?.Kind}\"" });

    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in body?.Params ?? new()) values[kv.Key] = kv.Value;

    // Refused rather than put up half-working: an unsubstituted {itemId} in a feed url fetches nothing, and the panel
    // would sit there looking built while showing an error nobody can act on.
    if (kind.Missing(values) is { Count: > 0 } missing)
        return Results.BadRequest(new
        {
            error = $"\"{kind.Name}\" still needs {string.Join(", ", missing.Select(p => $"{p.Name} ({p.Description})"))}",
        });

    var panel = widgets.Reserve(
        (body?.Title ?? kind.Title) is { Length: > 0 } t ? t : kind.Name,
        body?.Size ?? kind.DefaultSize,
        body?.Priority ?? 70,
        body?.Why,
        body?.Session,
        proposed: false);

    widgets.Attach(panel.Id, kind, values);
    widgetKinds.Used(kind.Name);
    Console.WriteLine($"[widget] {panel.Id} \"{panel.Title}\" put up from the \"{kind.Name}\" kind");

    return Results.Json(widgets.Get(panel.Id), json);
});

/*
 * A panel doing something, because somebody pressed a button on it.
 *
 * The panel's own component calls this through `act` in its scope. Nothing here is gated on which command or
 * which parameters: a panel that can show a vacuum's battery can start it, stop it and send it to a named room,
 * because a panel that can only READ is a dashboard, and the point of a button is that it does the thing.
 *
 * The panel doesn't act on its own, though — that rule stands. A person presses the button; the panel is the
 * button. Nothing here ever fires without a click behind it.
 */
app.MapPost("/api/widgets/{id}/act", async (string id, WidgetAct body, CancellationToken ct) =>
{
    if (widgets.Get(id) is null) return Results.NotFound(new { error = "no such panel" });
    if (body is null || string.IsNullOrWhiteSpace(body.Plugin) || string.IsNullOrWhiteSpace(body.Command))
        return Results.BadRequest(new { error = "which plugin and which command" });

    try
    {
        var ran = await plugins.RunAsync(body.Plugin!, body.Command!, body.Parameters, ct);
        return Results.Json(new { ok = true, text = ran.Text, data = ran.Data }, json);
    }
    catch (Exception ex)
    {
        // Said back to the panel rather than swallowed: a button that reports nothing is a button nobody trusts.
        return Results.Json(new { ok = false, text = ex.Message }, json, statusCode: 400);
    }
});

app.MapPost("/api/widgets/{id}/refresh", (string id) =>
    widgets.Wake(id)
        ? Results.Ok(new { refreshing = id })
        : Results.BadRequest(new { error = "that panel has no feed to fetch" }));

// The component threw in the browser. Sent straight back to be fixed, with the exception in the brief.
//
// Nothing has to notice a dead panel for this to happen: the error boundary that catches the throw reports it, and
// the worker that wrote the code gets the message. Capped at two attempts, after which the panel keeps the error on
// its face rather than looping.
app.MapPost("/api/widgets/{id}/broken", (string id, WidgetBroken body) =>
{
    if (widgets.Get(id) is not { } panel) return Results.NotFound(new { error = "no such panel" });

    var message = (body?.Error ?? "").Trim();
    if (message.Length == 0) return Results.BadRequest(new { error = "no error message" });

    // Recorded, and that is all. This used to send the panel straight back to be rewritten, on the word of whichever
    // tab reported first — and a tab is not a reliable witness: one running an older bundle complains about names the
    // current scope has, and a panel that had just been made to look right was rebuilt over an error that existed
    // nowhere but that window. The fault is kept, shown on the panel, and offered in its menu.
    if (widgets.Broke(id, message))
        Console.Error.WriteLine($"[widget] {id} \"{panel.Title}\" threw: {message}");

    return Results.Ok(new { recorded = id, fixing = false });
});

// What the panel couldn't load, straight from the browser that tried.
//
// Reported by the real page as well as by the verification screenshot, so a panel that breaks a week after it was
// built — a camera that moves, a url that expires — says so from wherever it is being looked at, rather than waiting
// for something to come round and check.
app.MapPost("/api/widgets/{id}/faults", (string id, WidgetFaults body) =>
{
    if (widgets.Get(id) is not { } panel) return Results.NotFound(new { error = "no such panel" });

    var faults = body?.Faults ?? new List<string>();
    if (widgets.Faulted(id, faults))
        Console.Error.WriteLine($"[widget] {id} \"{panel.Title}\" couldn't load: {string.Join("; ", faults)}");

    return Results.Ok(new { recorded = id });
});

/*
 * Look at a panel and say whether it works.
 *
 * Called at the end of a build, so a panel is never declared finished on the strength of "the code compiled and the
 * feed answered". Those were both true of the camera panel while it showed a broken-image icon.
 *
 * A bad verdict is recorded on the panel and — once — sent back to be fixed, with what was actually seen in the brief.
 * That description is the whole value: "a grey box with a torn-page icon" tells a builder something no exception could.
 */
app.MapPost("/api/widgets/{id}/look", async (string id) =>
{
    if (widgets.Get(id) is not { } panel) return Results.NotFound(new { error = "no such panel" });

    // Nothing to photograph yet. A panel that has never loaded is showing its own loading branch, so the picture can
    // only ever say "it is loading" — which then gets recorded as a fault. A panel that never loads AT ALL is a load
    // failure, reported by the loader with the actual reason, and no photograph improves on that.
    if (panel.Data is not { Length: > 0 })
    {
        Console.WriteLine($"[look] {panel.Id} hasn't loaded yet — waiting for it to show something");
        return Results.Ok(new { looked = false, reason = "it hasn't loaded anything yet" });
    }

    // And nothing to photograph while it is being CHANGED. A panel mid-build shows its design behind a spinner and the
    // words of whatever stage it is at, so the photograph comes back as "a loading state with the text Testing it…" —
    // a true description of a build in progress, recorded as a fault against a panel that has not been finished yet.
    // It happened to the first panel adjusted after this check came back to life.
    if (panel.Status != WidgetStatus.Live || panel.Design is { Length: > 0 })
    {
        Console.WriteLine($"[look] {panel.Id} is mid-build — nothing to judge until it settles");
        return Results.Ok(new { looked = false, reason = "it is still being worked on" });
    }

    var verdict = await widgetLook!.AtAsync(panel, panel.Why ?? panel.Title, CancellationToken.None);

    // The browser's own report wins. It is the same fault the photograph shows, named exactly rather than described:
    // a url and a verb instead of "a grey box with a torn-page symbol". Read AFTER the look, because the render that
    // the look performs is what produces it.
    if (widgets.Get(id)!.Faults is { Count: > 0 } faults)
        verdict = new LookVerdict(false, verdict?.Seen ?? "(not photographed)", string.Join("; ", faults));

    if (verdict is null)
        return Results.Ok(new { looked = false, reason = "couldn't photograph it" });

    if (verdict.Working)
    {
        widgets.Looked(id, true, verdict.Seen);
        return Results.Ok(new { looked = true, working = true, seen = verdict.Seen });
    }

    // Recorded and left alone. The photograph is the best evidence in the system and still not good enough to act on
    // unasked: it catches a panel mid-load, or a page whose fonts have not arrived, and calls it broken — and the
    // remedy it used to trigger was a full REBUILD, which throws away a working loader and a working component to
    // re-derive them from a sentence. The panel says something is wrong; its menu offers to mend it.
    var wrong = verdict.Wrong ?? "it doesn't show what it should";
    widgets.Looked(id, false, $"{wrong} (seen: {verdict.Seen})");

    return Results.Ok(new { looked = true, working = false, seen = verdict.Seen, wrong, fixing = false });
});

// A panel is photographed the first time it shows anything — not when its build finishes. Those are the same moment
// only for a panel that loads instantly, and the ones that do not are exactly the ones worth checking.
widgets.FirstShowed = panel => _ = LookAtWidget(panel);

// Look at a panel and act on what was seen. Shared by the endpoint above and by the end of every build.
async Task LookAtWidget(Widget panel)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        // Our own pass. Without it this is a request from nobody to a locked API, and the 401 it gets back is not an
        // exception — so the check went quiet the day the password went on and nothing said a word.
        client.DefaultRequestHeaders.TryAddWithoutValidation(OurPass.Header, ourPass.Token);

        var response = await client.PostAsync(
            $"http://localhost:{Environment.GetEnvironmentVariable("PORT") ?? "5179"}" +
            $"/api/widgets/{panel.Id}/look", content: null);

        if (!response.IsSuccessStatusCode)
            Console.Error.WriteLine($"[look] {panel.Id} couldn't be looked at: the API answered " +
                                    $"{(int)response.StatusCode}");
    }
    catch (Exception ex)
    {
        // Looking is a check, not the work. It must never be the thing that fails a finished build.
        Console.Error.WriteLine($"[look] couldn't look at {panel.Id}: {ex.Message}");
    }
}

/*
 * Adjust a panel: change one thing about a component that already works.
 *
 * Separate from /rebuild on purpose. A rebuild is briefed to find a source and write a panel; an adjustment is briefed
 * with the component that is already rendering real data and told to change the smallest thing that satisfies the
 * note. Sending an adjustment down the rebuild path discarded a working loader to re-derive it from a sentence, which
 * is slower and a gamble on something that was already correct.
 */
app.MapPost("/api/widgets/{id}/adjust", async (string id, WidgetRebuild body) =>
{
    if (widgets.Get(id) is not { } panel) return Results.NotFound(new { error = "no such panel" });

    var note = (body?.Shows ?? "").Trim();
    if (note.Length == 0) return Results.BadRequest(new { error = "say what should change" });

    // Nothing to edit yet: a panel with no component is a build, not an adjustment, and pretending otherwise would
    // hand the builder an empty code block and an instruction not to write one.
    var kind = widgetKinds.Get(panel.Kind);
    if (kind is null && panel.OwnCode is null)
        return Results.BadRequest(new { error = "this panel hasn't been built yet — there's nothing to adjust" });

    var session = OpenConversation(WidgetWork.SessionFor(id));
    var taskId = await orchestrator.AdjustWidgetAsync(session, panel, kind, note);
    return Results.Ok(new { adjusting = id, task = taskId });
});

/*
 * Mend what has been noticed about a panel — because the user asked, which is the only way this happens.
 *
 * Every check still runs: the error boundary catches a throw, the browser reports a resource it couldn't load, the
 * loader reports a failure, and the panel gets photographed and looked at. What none of them do any more is act. They
 * record, the panel shows it, and this is the door.
 *
 * The reason is one afternoon's evidence. A panel was adjusted, looked right, and then rewrote itself twice: once
 * because the load straight after the build came back oddly shaped, and once because a browser tab running the previous
 * bundle threw about a name the current one has. Both faults were gone within a minute; both had already triggered a
 * full rebuild of something that worked. Automatic repair is only as good as its evidence, and the evidence here is a
 * single reading from a single tab.
 *
 * An ADJUSTMENT, not a rebuild, and that difference is most of the point: the loader and the component are kept and the
 * smallest thing that answers the fault is changed. The old automatic path went through the build brief, which is told
 * to go and find a source and write a panel — so a transient fault cost the whole thing.
 */
app.MapPost("/api/widgets/{id}/fix", async (string id) =>
{
    if (widgets.Get(id) is not { } panel) return Results.NotFound(new { error = "no such panel" });
    if (!panel.Ailing)
        return Results.BadRequest(new { error = "nothing has been noticed about this panel — say what to change instead" });

    var kind = widgetKinds.Get(panel.Kind);
    if (kind is null && panel.OwnCode is null)
        return Results.BadRequest(new { error = "this panel hasn't been built yet — there's nothing to fix" });

    // Everything that was noticed, not just the newest of it. A throw and a broken image are usually one fault seen
    // from two directions, and whoever mends it should have both.
    var noticed = new List<string>();
    if (panel.RenderError is { Length: > 0 } threw)
        noticed.Add($"The component THREW while rendering, in the browser: {threw}");
    if (panel.Error is { Length: > 0 } failed && !string.Equals(failed, panel.RenderError, StringComparison.Ordinal))
        noticed.Add($"The last load failed: {failed}");
    if (panel.Faults.Count > 0)
        noticed.Add("The page couldn't load: " + string.Join("; ", panel.Faults));
    if (panel.Seen is { Length: > 0 } seen)
        noticed.Add($"It was photographed, and what was on the screen was: {seen}");

    var session = OpenConversation(WidgetWork.SessionFor(id));
    var taskId = await orchestrator.AdjustWidgetAsync(session, panel, kind,
        "SOMETHING IS WRONG WITH THIS PANEL and the user has asked for it to be mended. Here is everything that was " +
        "noticed about it:\n\n" + string.Join("\n", noticed.Select(n => $"  - {n}")) + "\n\n" +
        "Work out which of the three parts is at fault and change only that. A thrown exception is the COMPONENT. A " +
        "load that failed, or data arriving in the wrong shape, is the LOADER — go and look at what the source " +
        "actually returns now. A picture or a stream that didn't load is a url that isn't what it was thought to be. " +
        "If what was noticed no longer reproduces — a load that came back oddly once, a page read a moment too early " +
        "— then the honest answer is a wait or a guard, not a rewrite: say so and change the smallest thing that " +
        "makes it not happen again.");

    Console.Error.WriteLine($"[widget] {id} \"{panel.Title}\" — the user asked for it to be fixed (task {taskId})");
    return Results.Ok(new { fixing = id, task = taskId });
});

// Rebuild one: same panel, same place, a worker sent to find its feed and write it again. What the menu's
// "Rebuild" does, and the answer when a feed has broken or the user wants it to show something else.
app.MapPost("/api/widgets/{id}/rebuild", async (string id, WidgetRebuild body) =>
{
    if (widgets.Get(id) is not { } w) return Results.NotFound(new { error = "no such panel" });
    var session = OpenConversation(WidgetWork.SessionFor(id));
    var shows = (body?.Shows ?? "").Trim();
    if (shows.Length == 0) shows = w.Why ?? w.Title;
    var taskId = await orchestrator.BuildWidgetAsync(session, w, shows);
    return Results.Ok(new { rebuilding = id, task = taskId });
});

/*
 * Local sources: what has been granted, and the files inside.
 *
 * Read-only and contained. Registering one is the deliberate act — it makes everything under that folder readable by
 * anything that can reach this API, tunnel included — so it is done here, by the user, and not by a worker that read a
 * web page suggesting it.
 */
app.MapGet("/api/sources", () => Results.Json(sources.All().Select(s => new
{
    s.Id,
    s.Name,
    s.Path,
    s.Recursive,
    types = s.Extensions,
    files = sources.Files(s).Count,
    s.AddedAt,
}), json));

app.MapPost("/api/sources", (SourceGrant body) =>
{
    var (source, error) = sources.Add(body?.Name ?? "", body?.Path ?? "", body?.Include, body?.Recursive ?? false);
    if (source is null) return Results.BadRequest(new { error });

    var found = sources.Files(source);
    Console.WriteLine($"[source] granted {source.Id} -> {source.Path} ({found.Count} file(s) visible)");
    return Results.Ok(new
    {
        source.Id,
        source.Name,
        source.Path,
        files = found.Count,
        // Said back, because "it worked" is not the question — "can you see my photos?" is.
        newest = found.FirstOrDefault()?.Name,
        use = $"internal: source:{source.Id}",
    });
});

app.MapDelete("/api/sources/{id}", (string id) =>
    sources.Remove(id) ? Results.Ok(new { removed = id }) : Results.NotFound(new { error = "no such source" }));

// The listing, as a feed. Same shape the internal loader produces, for looking at directly.
app.MapGet("/api/sources/{id}/files", (string id) =>
    sources.Get(id) is { } source
        ? Results.Json(new { files = sources.Files(source) }, json)
        : Results.NotFound(new { error = "no such source" }));

// One file. Range processing on, so a video in a source can be scrubbed rather than only downloaded whole.
app.MapGet("/api/sources/{id}/file", (string id, string? path) =>
{
    if (sources.Get(id) is not { } source) return Results.NotFound(new { error = "no such source" });
    if (sources.Resolve(source, path) is not { } file)
        return Results.NotFound(new { error = "not in this source" });

    return Results.File(file, MimeOf(file), enableRangeProcessing: true);
});

// Our own feeds, as the JSON a component sees. Same contract as an external feed — a component reading `data`
// cannot tell the difference, and shouldn't be able to.
/*
 * What an internal feed currently returns, as JSON.
 *
 * The brief tells a builder to PROVE its source answers before publishing, and an internal feed had no url to call —
 * so a build asked to show this week's dinners went looking for one, probing http://localhost:3000 and scraping local
 * files in Chrome. It was following the instruction; there was simply nothing to point it at.
 *
 * Now there is. One url on our own origin, returning exactly what the component will receive.
 */
// Catch-all, because an internal name can carry an argument and that argument can contain a slash:
// "plugin:roborock/status" is one name, not two route segments.
app.MapGet("/api/widgets/internal/{*name}", async (string name, CancellationToken ct) =>
{
    if (!WidgetInternals.Known(name))
        return Results.NotFound(new
        {
            error = $"no internal feed called \"{name}\"",
            available = WidgetInternals.Options,
        });

    return await InternalFeed(name, ct) is { } payload
        ? Results.Content(payload, "application/json")
        : Results.Problem(detail: $"the \"{name}\" feed couldn't be produced", statusCode: 502);
});

async Task<string?> InternalFeed(string name, CancellationToken ct)
{
    // A plugin command, as a feed. Same trick as source: below — a name that carries an argument, so a panel
    // reading a vacuum needs no new loader mode and no new publish-time contract. The command runs server-side
    // and its STRUCTURED half is what the panel gets; the sentence is for a conversation, and a panel that had
    // to parse one would break the moment the wording changed.
    if (name.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
    {
        var wanted = name["plugin:".Length..].Split('/', 2);
        if (wanted.Length != 2) return null;
        try
        {
            var ran = await plugins.RunAsync(wanted[0].Trim(), wanted[1].Trim(), null, ct);
            return JsonSerializer.Serialize(ran.Data ?? new { text = ran.Text }, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[widgets] plugin feed {name} failed: {ex.Message}");
            return null;
        }
    }

    // A granted folder, as a feed. Handled before the switch because it is the one internal name that carries an
    // argument — and because a panel consuming a source needs no new loader mode, no new plumbing and no new
    // publish-time contract: it is an internal feed that happens to list files.
    if (name.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
    {
        var wanted = name["source:".Length..].Trim();
        if (sources.Get(wanted) is not { } source) return null;
        return JsonSerializer.Serialize(new { files = sources.Files(source) }, json);
    }

    try
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        object payload = name switch
        {
            WidgetInternals.Agenda => new
            {
                today = projectLists.Agenda(today, 1).Select(e => new
                {
                    item = e.Item, list = e.ListTitle, project = e.Project, done = e.Done,
                }),
                tomorrow = projectLists.Agenda(today.AddDays(1), 1).Select(e => new
                {
                    item = e.Item, list = e.ListTitle, project = e.Project, done = e.Done,
                }),
            },

            WidgetInternals.Lists => new
            {
                lists = projectLists.All().Select(l => new
                {
                    id = l.Id, title = l.Title, project = l.Project, checklist = l.Checklist,
                    total = l.Items.Count, done = l.DoneCount,
                    items = l.Items.Take(12).Select(i => new
                    {
                        text = i, done = l.IsDone(i), date = l.DateOf(i)?.ToString("yyyy-MM-dd"),
                    }),
                }),
            },

            WidgetInternals.Schedule => new
            {
                tasks = schedules.All()
                    .Where(t => t.Status == "pending" && !t.Paused)
                    .OrderBy(t => t.FireAt)
                    .Take(12)
                    .Select(t => new
                    {
                        task = t.TaskText, at = t.FireAt, repeat = t.Repeat, runs = t.Runs,
                        dueInMinutes = (int)Math.Max(0, (t.FireAt - DateTimeOffset.UtcNow).TotalMinutes),
                    }),
            },

            WidgetInternals.Tasks => new
            {
                running = controlHub.Runs()
                    .Where(r => r.Status is "running" or "waiting")
                    .Take(12)
                    .Select(r => new
                    {
                        task = r.Task, status = r.Status, note = r.LatestNote,
                        waiting = r.Status == "waiting", startedAt = r.StartedAt,
                    }),
            },

            WidgetInternals.Projects => new
            {
                projects = projects.ActiveProjects()
                    .Select(p => new { slug = p.Slug, title = p.Title, description = p.Description }),
            },

            _ => new { },
        };
        return JsonSerializer.Serialize(payload, json);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[widget] internal feed \"{name}\" failed: {ex.Message}");
        return null;
    }
}

// ---- The day ---------------------------------------------------------------------------------------------
//
// What's actually on, across every list and project. This is the whole reason list items can carry a date: a meal
// plan went in, seven dinners were written down, and "what's for dinner tonight" had nowhere to be answered from —
// the data was there and nothing could ask it a question about today.
app.MapGet("/api/agenda", (int? days) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var span = Math.Clamp(days ?? 2, 1, 14);

    var rows = projectLists.Agenda(today, span).Select(e => new
    {
        listId = e.ListId,
        list = e.ListTitle,
        project = e.Project,
        projectTitle = projects.Get(e.Project)?.Title ?? Readable(e.Project),
        item = e.Item,
        on = e.On.ToString("yyyy-MM-dd"),
        // Computed here so a client never has to work out what "today" is against a clock that may disagree.
        offset = e.On.DayNumber - today.DayNumber,
        done = e.Done,
    });
    return Results.Json(rows, json);
});

app.MapDelete("/api/lists/{id}", (string id) =>
    projectLists.Delete(id) ? Results.Ok(new { deleted = id }) : Results.NotFound(new { error = "no such list" }));

// What deleting a project takes with it, and what it deliberately does not.
//
// The record alone would leave the rest orphaned and still reachable: lists have no slug of their own so nothing could
// ever name them again, runs stay keyed to a project that no longer resolves, and the brain keeps answering with a thing
// the owner has just deleted. So the caller joins the stores up, because none of them knows about the others.
//
// Conversations are left alone on purpose. A chat that happened is a record of what was said, and reaching back to
// rewrite old transcripts because a container was tidied away is the one cascade that destroys something irreplaceable.
// Which brain node belongs to a project, when one does.
//
// Title first, then the slug, because graph ids are slugs too and the two stores were built independently — nothing has
// ever linked them but the name. That is a real limit and it is visible here rather than hidden: told "I want to sell my
// car", the reconciler wrote a node called "sell my car" while the project got titled "Selling the car", and no exact
// lookup joins those. Deliberately no fuzzy matching — a near-match would eventually delete the wrong subject's facts,
// which is worse than reporting none. What the preview shows is exactly what the delete will take.
static Smarty.Brain.Node? ProjectNode(Smarty.Brain.Graph g, Project p) =>
    g.Resolve(p.Title).Node ?? g.Get(p.Slug);

app.MapGet("/api/projects/{slug}/removal", (string slug) =>
{
    if (projects.Get(slug) is not { } p) return Results.NotFound(new { error = "no such project" });

    var node = ProjectNode(graph, p);

    return Results.Json(new
    {
        slug = p.Slug, title = p.Title, sort = p.Sort, goal = p.Goal ?? "",
        lists = projectLists.ForProject(p.Slug).Count,
        runs = projectRuns.CountFor(p.Slug),
        // Panels built on it. Named rather than counted and deliberately NOT deleted: a panel is somebody's home page,
        // and one pointing at a list that has gone is a panel to rebuild or drop by choice, not a casualty. Leaving it
        // silent is what produced a panel failing for an hour over a topic that had been tidied away.
        panels = widgets.All()
            .Where(w => w.Params.TryGetValue("project", out var on)
                        && string.Equals(on, p.Slug, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Title)
            .ToArray(),
        // What the brain would lose. Named rather than counted, because "3 facts" tells you nothing about whether you
        // mind losing them and "Alex — joined → Fenwick Harriers" tells you everything.
        node = node?.Id,
        facts = node is null
            ? Array.Empty<string>()
            : graph.Around(node.Id, includeEnded: true).Select(s => s.Sentence()).ToArray(),
        files = node is null ? 0 : contexts.For(node.Id).Count,
    }, json);
});

app.MapDelete("/api/projects/{slug}", (string slug, string? confirm) =>
{
    if (projects.Get(slug) is not { } p) return Results.NotFound(new { error = "no such project" });
    if (confirm != "delete") return Results.BadRequest(new { error = "send confirm=delete" });

    var node = ProjectNode(graph, p);
    var facts = node is null ? 0 : graph.Around(node.Id, includeEnded: true).Count;
    var files = 0;

    if (node is not null)
    {
        foreach (var held in contexts.For(node.Id).ToList())
            if (contexts.Forget(held.Id)) files++;

        graph.Forget(node.Id);
    }

    var lists = projectLists.DeleteFor(p.Slug);
    var runs = projectRuns.DeleteFor(p.Slug);
    projects.Delete(p.Slug);

    // A conversation pinned to it would otherwise stay scoped to something that no longer exists, offering its tools
    // and refusing to talk about anything else.
    var unpinned = sessions.Unpin(p.Slug);

    Console.WriteLine($"[projects] deleted {p.Sort} \"{p.Title}\" — {lists} list(s), {runs} run(s), " +
                      $"{facts} fact(s), {files} file(s), {unpinned} chat(s) unpinned");

    return Results.Ok(new { deleted = p.Slug, sort = p.Sort, lists, runs, facts, files, unpinned });
});

// Drop one thing the UI is showing. Retired rather than erased, like everything else here — a fact removed by
// hand is still a fact that was once true, and the reason it went is worth as much as the fact was.
app.MapDelete("/api/memory/{id}", (string id) =>
    graph.Retire(id, "removed by hand")
        ? Results.Ok(new { forgotten = id })
        : Results.NotFound(new { error = "no such memory" }));

app.MapGet("/api/projects/{slug}", (string slug) =>
{
    var p = projects.Get(slug);
    if (p is null) return Results.NotFound(new { error = "no such project" });
    return Results.Ok(new
    {
        slug = p.Slug,
        title = p.Title,
        description = p.Description,
        status = p.Status,
        summary = p.Summary,
        startsOn = p.StartsOn,
        endsOn = p.EndsOn,
        window = p.WindowNote(DateOnly.FromDateTime(DateTime.Now.Date)),
        ended = p.HasEnded(DateOnly.FromDateTime(DateTime.Now.Date)),
        // id travels so the UI can delete one: a memory you can see and can't remove is worse than one you never
        // saw, and this list fills up with things nobody asked to keep.
        memories = ProjectKnowledge(graph, p.Title)
            .Select(e => new
            {
                id = e.Id, subject = e.From, key = e.Label,
                value = e.To is null ? e.Value : graph.Get(e.To)?.Name ?? e.Value,
                context = e.Note, asserted = e.Asserted,
            }),
        runs = projectRuns.ForProject(p.Slug)
            .Select(r => new { id = r.Id, task = r.Task, title = r.Title, status = r.Status, startedAt = r.StartedAt, endedAt = r.EndedAt, steps = r.Steps, result = r.Result }),
        lists = projectLists.ForProject(p.Slug)
            .Select(l => new
            {
                id = l.Id, title = l.Title, items = l.Items, updated = l.Updated,
                checklist = l.Checklist, done = l.Done,
            }),
        // Everything this project has produced, newest first, each with the address it is served from — so a deck
        // is something the project can hand you rather than something buried in whichever chat happened to make it.
        // Two ways a file gets here, and both belong in the list: something a RUN produced (served from the
        // conversation that made it), and something FILED here directly with promote_file — a document the user
        // handed over that belongs to this project. Only the first was listed, so a promoted file landed on the
        // shelf and never appeared on the page it was filed to.
        files = projectRuns.ForProject(p.Slug)
            .Where(r => r.Files.Count > 0 && !string.IsNullOrEmpty(r.Session))
            .SelectMany(r => r.Files.Select(f => new ProjectFileEntry(
                f,
                Run: r.Id,
                ProducedAt: r.EndedAt,
                Url: $"/api/session/{r.Session}/files/{Uri.EscapeDataString(f)}")))
            .Concat(ShelvedFiles(p.Slug))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()),
    });
});

// Open passthrough of the available Ollama models, so the UI can offer a picker.
app.MapGet("/api/models", async () =>
{
    try
    {
        var body = await http.GetStringAsync($"{ollamaBaseUrl}/api/tags");
        return Results.Content(body, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

// Transcribe a voice note (16 kHz mono WAV) to text via local Whisper.
/*
 * What listening is actually doing, from the device that is doing it.
 *
 * Written because three rounds of fixing this were done by inference. A phone or a tablet reports "nothing happened"
 * and nothing on this side can tell you whether the microphone opened, what rate it opened at, whether the level moves
 * when somebody talks, whether audio reached the transcriber, or what it made of it. Every one of those is knowable ON
 * the device and unknowable here — so the device says, and it lands in this log where it can be read without plugging
 * anything in.
 *
 * Only sent when the page is opened with ?listen=debug, so it costs nothing the rest of the time.
 */
app.MapPost("/api/voice/diagnostic", async (HttpRequest req, CancellationToken ct) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync(ct);
    if (body.Length > 4000) body = body[..4000] + "…";

    Console.WriteLine($"[listen] {body}");
    return Results.Ok(new { logged = true });
});

app.MapPost("/api/transcribe", async (HttpRequest req, WhisperTranscriber whisper, CancellationToken ct) =>
{
    Stream audio;
    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync(ct);
        var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.BadRequest(new { error = "no audio file" });
        audio = file.OpenReadStream();
    }
    else
    {
        audio = req.Body;
    }

    /*
     * Anything, not just the 16 kHz WAV the browser used to be required to send.
     *
     * The requirement was a quiet tax on every remote listener. Uncompressed 16 kHz mono is 32 KB a second, and
     * always-on listening uploads a rolling window over and over — from a phone, through a tunnel, to this machine.
     * Compressed, the same audio is a tenth of that, which is the difference between a wake word noticed in a second
     * and one noticed in four. ffmpeg is already here for Slack's voice clips; it converts anything.
     *
     * Whisper is left strict on purpose: it is handed exactly what it wants, and the guessing happens out here.
     */
    var buffered = new MemoryStream();
    await audio.CopyToAsync(buffered, ct);
    buffered.Position = 0;

    if (!WhisperReady(buffered))
    {
        var converted = await ToWhisperWav(buffered, ct);
        if (converted is null)
            return Results.Json(new { error = "that audio couldn't be converted for transcription" }, statusCode: 400);
        buffered = converted;
    }

    try
    {
        /*
         * What the transcriber should expect to hear.
         *
         * Always-on listening is mostly one short phrase — the assistant's own name — said out of any context, which is
         * the hardest thing for a speech model and the easiest thing to help it with. Told to expect it, the same
         * recording came back as "Hey Pip." instead of "a pep", three times faster. The caller says when it applies;
         * the name comes from here so the page does not have to know it.
         */
        var expecting = req.Query["expecting"].FirstOrDefault();
        if (expecting == "name")
        {
            /*
             * A list of NAMES, not a sentence, and not the phrase being listened for.
             *
             * Measured on the same recording three ways. A names list — "Pip, Alex." — is the fastest and cleanest of
             * the lot. The obvious choice, the phrase itself ("Hey Pip."), is a trap: a prompt is treated as text that
             * came BEFORE this audio, so the model can decide the name has already been said and leave it out of the
             * transcript — which it did, turning "Hey Pip. What is the capital of France?" into ", What is the capital
             * of France?" and taking the wake word with it. And a prompt written as a sentence is worse again: handed
             * five seconds of silence it produced "She is a person who is not a person who is not a person…" for two
             * hundred words. Names bias the vocabulary and suggest nothing.
             */
            var names = new[] { naming.Assistant, people.NameOf(people.Self) }
                .Where(n => n is { Length: > 0 })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            expecting = names.Length > 0 ? string.Join(", ", names) + "." : null;
        }

        var text = await whisper.TranscribeAsync(buffered, ct, expecting);
        return Results.Json(new { text });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

/// <summary>Is this already the 16 kHz mono PCM WAV Whisper insists on? Read from the header rather than a filename.</summary>
static bool WhisperReady(MemoryStream audio)
{
    var head = audio.GetBuffer();
    if (audio.Length < 44) return false;
    if (head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F') return false;

    var channels = BitConverter.ToUInt16(head, 22);
    var rate = BitConverter.ToUInt32(head, 24);
    var bits = BitConverter.ToUInt16(head, 34);
    return channels == 1 && rate == 16000 && bits == 16;
}

/// <summary>Anything else, through ffmpeg. Null when it could not be converted, which is not a crash.</summary>
async Task<MemoryStream?> ToWhisperWav(MemoryStream source, CancellationToken ct)
{
    var scratch = Path.Combine(Path.GetTempPath(), $"smarty-audio-{Guid.NewGuid():N}");
    var inPath = scratch + ".in";
    var outPath = scratch + ".wav";
    try
    {
        await File.WriteAllBytesAsync(inPath, source.ToArray(), ct);
        if (!await new AudioTranscoder(AudioTranscoder.Resolve()).ToWhisperWavAsync(inPath, outPath, ct)) return null;

        var wav = new MemoryStream(await File.ReadAllBytesAsync(outPath, ct));
        return wav;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[audio] couldn't prepare uploaded audio: {ex.Message}");
        return null;
    }
    finally
    {
        foreach (var path in new[] { inPath, outPath })
            try { if (File.Exists(path)) File.Delete(path); } catch { /* a temp file */ }
    }
}

// ---- Conversational session (orchestrator + async workers) ----

// Send a user message. Returns immediately; the orchestrator's reply (and any later results pushed
// back from background workers) arrive asynchronously on the session's event stream.
// Files the user hands to a conversation.
//
// There was no way to do this from the web chat at all, which made every "here's the spreadsheet, work from this"
// impossible — the one thing a personal assistant is most obviously for. Uploaded here first, then the next message
// picks them up: the file goes into the conversation's own library like anything a worker writes, and the model is
// told it exists in a note the user never sees.
// Where the user is. The browser posts a fix it already had permission for; the place name is resolved here
// rather than in the page so the key-less geocoder and its cache live in one place.
app.MapPost("/api/location", async (LocationReport body, CancellationToken ct) =>
{
    if (body is null) return Results.BadRequest(new { error = "no fix supplied" });

    // Resolved before storing, so the note never has a coordinate with no name attached. A failed lookup keeps
    // whatever name the previous nearby fix had, rather than blanking it.
    var place = await geocoder.PlaceFor(body.Latitude, body.Longitude, ct);
    if (!location.Set(body.Latitude, body.Longitude, body.Accuracy, place))
        return Results.BadRequest(new { error = "that isn't a valid coordinate" });

    Console.WriteLine($"[location] {place ?? "(unnamed)"} ({body.Latitude:0.###}, {body.Longitude:0.###})");
    return Results.Ok(new { place, at = location.Current!.At });
});

// What it currently thinks, so it's inspectable rather than invisible.
app.MapGet("/api/location", () =>
    location.Current is { } fix
        ? Results.Ok(new { fix.Latitude, fix.Longitude, fix.AccuracyMetres, fix.Place, fix.At, note = location.Note() })
        : Results.Ok(new { note = "" }));

// Theirs to forget. Revoking the browser permission stops new fixes; this removes the one we have.
app.MapDelete("/api/location", () =>
    location.Clear() ? Results.Ok(new { cleared = true }) : Results.Ok(new { cleared = false }));

// The retained conversations, for the sidebar.
//
// Two sources, because there are two eras. The recorder writes a file per conversation and is the authority
// going forward; everything from before it existed survives only as the transcript the control hub kept for its
// dashboard. Both can be reopened (see the session stream's replay), so both belong in the list — otherwise the
// sidebar claims your history began the day the recorder was switched on.
//
// Web chats only: a Slack thread is tracked in the same hub, but its id addresses a thread in Slack, and
// "opening" one here would fabricate an empty web session under that id.
app.MapGet("/api/chats", () =>
{
    var conversations = controlHub.Conversations()
        .Where(c => c.Surface is "chat")
        .ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    // Which projects each conversation actually touched, from the runs that happened on it. A chat's own
    // project is only the one it was pinned to (usually none), so the pills come from the work instead — that's
    // what makes them worth showing: the chat where you did three things has three of them.
    var touched = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var run in controlHub.Runs())
    {
        if (string.IsNullOrWhiteSpace(run.ConversationId) || string.IsNullOrWhiteSpace(run.Project)) continue;
        var list = touched.TryGetValue(run.ConversationId, out var existing) ? existing : touched[run.ConversationId] = new();
        if (!list.Contains(run.Project!, StringComparer.OrdinalIgnoreCase)) list.Add(run.Project!);
    }

    // Recorded files first (they carry a real last-write time), then any tracked conversation that has a
    // transcript but no file yet.
    var recorded = chatHistory.List();
    var known = new HashSet<string>(recorded.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
    var entries = recorded
        .Concat(conversations.Values
            .Where(c => !known.Contains(c.Id) && c.Transcript.Count > 0)
            .Select(c => (Id: c.Id, LastWrite: c.LastActivityAt, Bytes: 0L)))
        // Proact's own errands are not the person's conversations.
        //
        // Each run needs a conversation to hold its task, and nobody ever types in one — so it has no first user
        // message to take a title from and arrives here as "Untitled chat". A dozen a day makes the sidebar useless.
        // They are not deleted and stay openable by id: the Proact timeline links straight to them, which is where
        // somebody looking for one would actually go.
        //
        // A TICKED PROPOSAL is deliberately left visible. That is work they authorised with a click, it can come back
        // with a question, and the line worth drawing is who asked rather than which subsystem started it.
        .Where(e => !Proact.Looking(e.Id))
        .OrderByDescending(e => e.LastWrite)
        .ToList();

    var chats = entries.Select(entry =>
    {
        conversations.TryGetValue(entry.Id, out var conv);
        var slugs = touched.TryGetValue(entry.Id, out var ps) ? new List<string>(ps) : new List<string>();
        if (conv?.Project is { Length: > 0 } pinned && !slugs.Contains(pinned, StringComparer.OrdinalIgnoreCase))
            slugs.Insert(0, pinned);

        // The hub's title is the good one (it's summarised); the opening line is the fallback for a chat this
        // process never tracked — after a restart that's most of them.
        var title = conv?.Title;
        if (string.IsNullOrWhiteSpace(title) || title == "(new conversation)")
            title = chatHistory.FirstUserMessage(entry.Id);

        return new
        {
            id = entry.Id,
            title = string.IsNullOrWhiteSpace(title) ? "Untitled chat" : Trim(title!, 120),
            projects = slugs.Select(slug => new { slug, title = projects.Get(slug)?.Title ?? Readable(slug) }),
            messageCount = conv?.MessageCount ?? 0,
            lastActivityAt = conv?.LastActivityAt ?? entry.LastWrite,
            live = sessions.Get(entry.Id) is not null,
        };
    });

    return Results.Json(chats, json);

    static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";
});

// ---- Feeds and watchers ------------------------------------------------------------------------------------
//
// Ingest, and what to do about it. A feed is an address on a timer that produces items with topics; a watcher is a
// filter over those items and an instruction. When one matches, the assistant starts a conversation by itself — which
// is the first thing in here that happens without anybody asking.

app.MapGet("/api/feeds", () => Results.Json(feeds.All().Select(f => new
{
    id = f.Id,
    name = f.Name,
    source = f.Source,
    url = f.Url,
    every = f.Every,
    split = f.Split,
    paused = f.Paused,
    // What it has actually produced, which is the only honest measure of whether it works.
    seen = f.Seen,
    topics = f.Topics,
    polledAt = f.PolledAt,
    nextPoll = f.NextPoll,
    error = f.Error,
    items = f.Recent.Take(10).Select(i => new
    {
        key = i.Key, topic = i.Topic, title = i.Title, url = i.Url, at = i.At, arrived = i.Arrived,
    }),
}), json));

// Everything a feed has kept, for looking at what it is actually bringing in.
app.MapGet("/api/feeds/{id}/items", (string id) =>
    feeds.Get(id) is { } feed
        ? Results.Json(feed.Recent.Select(i => new
        {
            key = i.Key, topic = i.Topic, thread = i.Thread, title = i.Title, body = i.Body,
            url = i.Url, reference = i.Ref, at = i.At, arrived = i.Arrived,
        }), json)
        : Results.NotFound(new { error = "no such feed" }));

// Look now, whatever the cadence says — and act on anything new, exactly as the tick would.
app.MapPost("/api/feeds/{id}/poll", async (string id, CancellationToken ct) =>
{
    if (feeds.Get(id) is not { } feed) return Results.NotFound(new { error = "no such feed" });
    var read = await feedTick.PollOneAsync(feed, ct);
    return Results.Json(new { items = read.Items.Count, error = read.Error }, json);
});

app.MapPost("/api/feeds", (FeedSetup body) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name)) return Results.BadRequest(new { error = "a feed needs a name" });
    if (!FeedSources.Known(body!.Source)) return Results.BadRequest(new { error = $"source must be one of: {string.Join(", ", FeedSources.All)}" });

    var feed = feeds.Add(new Feed
    {
        Name = body.Name!.Trim(),
        Source = body.Source!.Trim().ToLowerInvariant(),
        Url = body.Url,
        Expression = body.Expression,
        Items = body.Items ?? "",
        Map = body.Map is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(body.Map, StringComparer.OrdinalIgnoreCase),
        Headers = body.Headers is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(body.Headers, StringComparer.OrdinalIgnoreCase),
        Every = string.IsNullOrWhiteSpace(body.Every) ? "every 15 minutes" : body.Every!,
        Split = FeedSplits.Known(body.Split) ? body.Split!.ToLowerInvariant() : FeedSplits.Field,
        Topic = body.Topic,
    });
    return Results.Json(new { id = feed.Id }, json);
});

app.MapPatch("/api/feeds/{id}", (string id, FeedEdit body) =>
    feeds.Edit(id, body?.Name, body?.Every, body?.Paused)
        ? Results.Ok()
        : Results.NotFound(new { error = "no such feed" }));

app.MapDelete("/api/feeds/{id}", (string id) =>
    feeds.Remove(id) ? Results.Ok() : Results.NotFound(new { error = "no such feed" }));

app.MapGet("/api/watchers", () => Results.Json(new
{
    watchers = watchers.All().Select(w => new
    {
        id = w.Id,
        name = w.Name,
        feed = w.Feed,
        feedName = feeds.Get(w.Feed)?.Name,
        topic = w.Topic,
        when = w.When.Select(t => new { field = t.Field, op = t.Op, value = t.Value }),
        about = w.About,
        act = w.Do,
        paused = w.Paused,
        limit = w.Limit,
        fired = w.Fired,
        lastFiredAt = w.LastFiredAt,
        lastError = w.LastError,
    }),
    // What they have actually done. A watcher's list of fires is the only way to tell one that is working from one
    // whose filter has never matched anything.
    fires = watchers.Fires().Take(30).Select(f => new
    {
        watcher = f.Watcher, watcherName = f.WatcherName, session = f.Session,
        title = f.Title, topic = f.Topic, url = f.Url, at = f.At, opened = f.Opened,
    }),
}, json));

app.MapPost("/api/watchers", (WatchSetup body) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name)) return Results.BadRequest(new { error = "a watcher needs a name" });
    if (string.IsNullOrWhiteSpace(body!.Act)) return Results.BadRequest(new { error = "say what to do when it fires" });
    if (body.Feed is { Length: > 0 } named && feeds.Get(named) is null)
        return Results.BadRequest(new { error = $"there's no feed \"{named}\"" });

    var watcher = watchers.Add(new Watcher
    {
        Name = body.Name!.Trim(),
        Feed = feeds.Get(body.Feed)?.Id,
        Topic = body.Topic,
        When = (body.When ?? new List<WatchTestSetup>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Value))
            .Select(t => new WatchTest
            {
                Field = t.Field ?? "",
                Op = WatchOps.Known(t.Op) ? t.Op!.ToLowerInvariant() : WatchOps.Has,
                Value = t.Value!,
            }).ToList(),
        About = body.About,
        Do = body.Act!.Trim(),
        Limit = body.Limit is > 0 ? body.Limit!.Value : 6,
    });
    return Results.Json(new { id = watcher.Id }, json);
});

app.MapPatch("/api/watchers/{id}", (string id, WatchEdit body) =>
    watchers.Edit(id, body?.Name, body?.Act, body?.Paused, body?.Limit)
        ? Results.Ok()
        : Results.NotFound(new { error = "no such watcher" }));

app.MapDelete("/api/watchers/{id}", (string id) =>
    watchers.Remove(id) ? Results.Ok() : Results.NotFound(new { error = "no such watcher" }));

// The conversations the assistant started by itself and nobody has read.
//
// A watcher that fires into a chat nobody knows exists has done half a job. This is what the page shows as a nudge,
// and opening the chat is what clears it.
app.MapGet("/api/nudges", () => Results.Json(watchers.Unopened().Select(f => new
{
    session = f.Session,
    watcher = f.WatcherName,
    title = f.Title,
    topic = f.Topic,
    at = f.At,
}), json));

app.MapPost("/api/chats/{id}/opened", (string id) => Results.Json(new { cleared = watchers.Opened(id) }, json));

// ---- Proact ------------------------------------------------------------------------------------------------
//
// The timeline, the toggle, the dial, and the tick or the cross. Its own area rather than a corner of an existing
// one: this is a history, where the home page is live state and a nudge means "the thing you asked to be told
// about". See PROACT_SPEC.md.

app.MapGet("/api/proact", () =>
{
    var now = DateTimeOffset.UtcNow;
    var settings = proact.Settings;
    var actions = proact.Actions(120);

    return Results.Json(new
    {
        on = settings.On,
        every = settings.Every,
        intervals = ProactIntervals.All,
        pausedUntil = settings.PausedUntil,
        running = settings.Running(now),

        // WHEN IT NEXT GOES OUT, for the pill in the chat header. From the loop rather than worked out again here,
        // so what the page counts down to and what the loop actually waits for cannot drift apart.
        nextAt = proactLoop.NextAt(now),

        // And whether it is out RIGHT NOW, with the conversation to follow. Present whenever a run is in flight,
        // not only just after somebody pressed the button: a sheet reopened mid-run should show the run.
        doing = proact.Out(now) is { } went ? new { task = went.Task, session = went.Session } : null,

        // The figure that says whether the quality gate is holding. There is no cap on how much it may do, so this
        // is the instrument rather than a statistic — a number climbing here is the only early warning of the
        // failure mode most likely to actually happen.
        todayCount = proact.ActionsSince(new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset)),
        waiting = proact.Waiting().Count,
        roundupHour = settings.RoundupHour,

        // The one thing about Proact that reaches out rather than waiting to be visited. One a day, whether it did
        // two things or twenty, which is what stops the summary becoming the noise it summarises.
        roundups = proact.Roundups(14).Select(r => new
        {
            day = r.Day, at = r.At, text = r.Text,
            did = r.Did, waiting = r.Waiting, looks = r.Looks, seen = r.Seen,
        }),

        actions = actions.Select(a => new
        {
            id = a.Id, at = a.At, kind = a.Kind, mode = a.Mode,
            what = a.What, body = a.Body, why = a.Why, produced = a.Produced,
            dismissed = a.Dismissed,
            plan = a.Plan, risk = a.Catch, expires = a.Expires,
            subject = a.Subject,
            verdict = a.Verdict,
            links = a.Links.Select(l => new { label = l.Label, url = l.Url }),
            images = a.Images.Select(i => new { url = i.Url, caption = i.Caption }),
            answer = a.Answer, answeredAt = a.AnsweredAt,
            outcome = a.Outcome, session = a.Session, task = a.TaskId,
            seen = a.Seen,
        }),

        // Liveness. A silent Proact and a broken Proact look identical from outside, and this system has already
        // shipped that confusion twice — so the ticks are on the page whether they did anything or not.
        ticks = proact.Ticks(40).Select(t => new
        {
            at = t.At, mode = t.Mode, changed = t.Changed, acted = t.Acted, note = t.Note, error = t.Error,
        }),
    }, json);
});

app.MapPatch("/api/proact", (ProactSetup body) =>
{
    if (body?.Every is { Length: > 0 } every && !ProactIntervals.Known(every))
        return Results.BadRequest(new { error = $"how often must be one of: {string.Join(", ", ProactIntervals.All)}" });

    var settings = proact.Set(
        on: body?.On,
        every: body?.Every,
        roundupHour: body?.RoundupHour,
        // "Not now" rather than "never", which is the realistic reason for reaching for a switch at all.
        pausedUntil: body?.PauseHours is > 0 ? DateTimeOffset.UtcNow.AddHours(body!.PauseHours!.Value) : null,
        clearPause: body?.PauseHours is 0);

    Console.Error.WriteLine($"[proact] {(settings.On ? "on" : "off")}, {settings.Every}" +
                            (settings.PausedUntil is { } until ? $", paused until {until:HH:mm}" : ""));
    return Results.Json(new { on = settings.On, every = settings.Every, pausedUntil = settings.PausedUntil }, json);
});

/*
 * What it is doing, right now, step by step.
 *
 * The button used to answer with a sentence %s "Gone. It'll show up here when it's done." %s which is the least
 * interesting thing about pressing it. A run takes minutes, reads pages and asks machines questions, and all of
 * that was happening behind a closed door: a Proact conversation is hidden from the chat list by design, so there
 * was nowhere to go and watch.
 *
 * Reads from the same run tracker the Control app's live view uses rather than opening a second stream. Polled
 * rather than SSE because a step list that lags two seconds is indistinguishable from one that does not, and this
 * is a panel somebody has open for ninety seconds.
 */
app.MapGet("/api/proact/doing", () =>
{
    if (proact.Out(DateTimeOffset.UtcNow) is not { } went)
        return Results.Json(new { running = false }, json);

    var runs = controlHub.RunsFor(went.Session);
    var run = runs.FirstOrDefault(r => r.TaskId == went.Task) ?? runs.FirstOrDefault();

    return Results.Json(new
    {
        running = true,
        session = went.Session,
        note = run?.LatestNote,
        status = run?.Status,
        steps = (run?.Steps ?? new List<RunStep>()).Select(st => new
        {
            kind = st.Kind, tool = st.Tool, args = st.Args,

            // The thinking, but not all of it. This is a glance-at list, and a paragraph of reasoning per row turns
            // it into something to read instead of something to watch.
            text = st.Text is { Length: > 0 } t ? (t.Length > 160 ? t[..160] + "\u2026" : t) : null,
        }),
    }, json);
});

// Look now, whatever the dial says — and act on anything it finds, exactly as a tick would.
app.MapPost("/api/proact/look", async (bool? deep, CancellationToken ct) =>
{
    // Attend by default, because somebody pressing a button wants a quick answer. `deep=true` asks for the
    // expensive look on purpose, which is otherwise only reachable by waiting for it to come round.
    var acted = await proactLoop.TickAsync(ct, force: true, deep: deep == true);

    // Where it went, so the button that sent it can show what it is doing.
    var went = proact.Out(DateTimeOffset.UtcNow);
    return Results.Json(new { acted, session = went?.Session }, json);
});

/*
 * The tick and the cross.
 *
 * The tick is the COMMIT, and this is where it happens — as an ordinary task with an ordinary toolset, never as a
 * Proact one. That is the whole architecture: Proact prepares an irreversible act and is permanently incapable of
 * performing it, and one click hands the plan it wrote to the assistant that does that work when asked. So the rule
 * holds on both sides of the click; permission changes who acts, not what Proact may do.
 */
app.MapPost("/api/proact/{id}/answer", async (string id, ProactAnswer body) =>
{
    if (proact.Get(id) is not { } proposal) return Results.NotFound(new { error = "no such action" });
    if (!proposal.IsProposal) return Results.BadRequest(new { error = "that isn't a proposal" });

    var ticked = body?.Ticked == true;
    if (proact.Answered(id, ticked, DateTimeOffset.UtcNow) is not { } answered)
        return Results.Conflict(new { error = $"that proposal has already been {proposal.Answer}" });

    if (!ticked)
    {
        // A cross needs no reason — asking for one is how people learn to avoid the cross altogether. It is still
        // signal, and it is in the 24-hour log the next tick reads, which is what stops the same offer reworded.
        Console.Error.WriteLine($"[proact] crossed: {answered.What}");
        return Results.Json(new { answer = answered.Answer }, json);
    }

    var chat = OpenConversation(Proact.ConversationForTicked(answered.Id));
    var taskId = await orchestrator.RunTickedProposalAsync(chat, answered);
    proact.Dispatched(answered.Id, chat.Id, taskId);

    Console.Error.WriteLine($"[proact] ticked: {answered.What} -> task {taskId}");
    return Results.Json(new { answer = answered.Answer, session = chat.Id, task = taskId }, json);
});

// Reading the timeline clears the unseen marks on everything except the proposals, which are cleared by being
// ANSWERED — looking at a decision is not making it.
/*
 * A thumb up or down.
 *
 * A different question from a proposal's tick, and the more useful one: a cross says "not this thing", a thumb says
 * "not this KIND of thing". It is the only signal the user volunteers that generalises, so it outlives the 24-hour
 * log and goes into every later decision as taste — see ProactStore.Taste.
 */
app.MapPost("/api/proact/{id}/vote", (string id, ProactVote body) =>
{
    // Null clears it, because a mis-tap must be undoable.
    var verdict = body?.Verdict?.Trim().ToLowerInvariant();
    if (verdict is { Length: > 0 } given && !ProactVerdicts.Known(given))
        return Results.BadRequest(new { error = "a verdict is \"up\", \"down\", or nothing at all" });

    if (proact.Vote(id, verdict, DateTimeOffset.UtcNow) is not { } voted)
        return Results.NotFound(new { error = "no such action" });

    Console.Error.WriteLine($"[proact] {(voted.Verdict ?? "un-")}voted: {voted.What}");
    return Results.Json(new { verdict = voted.Verdict }, json);
});

app.MapPost("/api/proact/seen", () =>
{
    proact.SeenAll();
    return Results.Ok();
});

/*
 * Say something back about one of these.
 *
 * A notice with no way to reply is a dead end — the obvious thing to do with "a new place has opened near you" is to
 * say "add that to my list", and until now there was nowhere to type it. So this hands back a conversation that
 * already knows what the notice was, and the ordinary message endpoint takes it from there: no second turn-running
 * path, and the reply streams into a chat the user can watch like any other.
 *
 * Idempotent by id, so a second follow-up continues the same thread rather than starting a stranger who has to be
 * told about the notice all over again.
 */
app.MapPost("/api/proact/{id}/thread", (string id) =>
{
    if (proact.Get(id) is not { } action) return Results.NotFound(new { error = "no such action" });

    var chat = OpenConversation($"proact-about-{action.Id}");

    // Seeded once. A system message rather than words in the user's mouth: they have not said anything yet, and
    // putting a sentence there they did not write is a lie visible on their own screen.
    if (chat.History.Count == 0)
    {
        var about = new System.Text.StringBuilder();
        about.Append("The user is replying to something YOU brought them without being asked. They are looking at " +
                     "it right now, so do not re-explain it — answer what they say about it.\n\n");
        about.Append($"--- what you told them ---\n{action.What}\n");
        if (action.Body is { Length: > 0 }) about.Append($"\n{action.Body}\n");
        if (action.Links.Count > 0)
            about.Append("\nSources: " + string.Join(", ", action.Links.Select(l => $"{l.Label} ({l.Url})")) + "\n");
        if (action.Why is { Length: > 0 }) about.Append($"\nWhy you brought it to them: {action.Why}\n");
        about.Append("\nIf they ask you to do something with it — add it to a list, look into it further, put it in " +
                     "a project — just do it, and say in one line what you did.");

        chat.History.Add(Message.System(about.ToString()));
    }

    proact.Dispatched(action.Id, chat.Id, action.TaskId ?? "");
    return Results.Json(new { session = chat.Id }, json);
});

// Put one away for good. Refused on a proposal nobody has answered — that is a decision they still owe, and the
// answer is the tick or the cross rather than a third way out of it.
app.MapPost("/api/proact/{id}/dismiss", (string id, ProactDismiss? body) =>
    proact.Dismiss(id, body?.Undo == true)
        ? Results.Ok()
        : Results.BadRequest(new { error = "that can't be dismissed — answer it with a yes or a no" }));

/*
 * Start the day again.
 *
 * Everything that stops Proact repeating itself is built on what it did earlier, which is right in normal use and
 * means a wasted morning suppresses a useful afternoon: a subject it covered badly is covered all the same. This is
 * the way out — and it lives on the settings page rather than the timeline, because it destroys the record.
 */
app.MapPost("/api/proact/clear", (ProactClear? body) =>
{
    var from = body?.All == true
        ? DateTimeOffset.MinValue
        : new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset);

    var (actions, ticks) = proact.Clear(from);
    Console.Error.WriteLine($"[proact] cleared {actions} action(s) and {ticks} look(s)" +
                            (body?.All == true ? " — everything" : " from today"));
    return Results.Json(new { actions, ticks }, json);
});

app.MapPost("/api/proact/roundup/{day}/seen", (string day) =>
{
    proact.RoundupSeen(day);
    return Results.Ok();
});

// ---- Scheduled tasks ---------------------------------------------------------------------------------------
//
// Standing work, as a list you can see. Everything here is deliberately the SAME object the chat's schedule_task
// creates: one store, one tick, one shape. A task set by saying "every morning check the orders" and one typed
// into the page are the same row, so the page can edit either and the model can see both.

// The list. Each row carries what a person needs to decide whether it is working: when it next goes off, how
// long that is, how often it comes back, how many times it has, and what it said last time.
app.MapGet("/api/schedules", () =>
{
    var now = DateTimeOffset.UtcNow;
    var rows = schedules.All().Select(t => new
    {
        id = t.Id,
        session = t.SessionId,
        // A schedule is only meaningful next to the conversation it belongs to, so the page can link there.
        // Same title logic as the chats list: the hub's summarised one, falling back to the opening line.
        title = ConversationTitle(t.SessionId),
        task = t.TaskText,
        status = t.Paused && t.Status == "pending" ? "paused" : t.Status,
        repeat = t.Repeat,
        recurring = t.Recurring,
        nextAt = t.FireAt,
        // Sent computed, because "in 6 hours" is the thing being asked and every client would otherwise work it
        // out from two clocks that don't agree.
        dueInSeconds = t.Status == "pending" && !t.Paused ? (long?)Math.Max(0, (t.FireAt - now).TotalSeconds) : null,
        runs = t.Runs,
        lastFiredAt = t.LastFiredAt,
        lastResult = t.LastResult,
        createdAt = t.CreatedAt,
    });
    return Results.Json(rows, json);
});

// New standing work, straight from the page — no conversation needed to start one. It still needs a thread to
// live in, because the answer has to land somewhere and a recurring job with nowhere to report is a job nobody
// reads; when none is named it gets its own, which is what the UI does for an ad-hoc "every day, check this".
app.MapPost("/api/schedules", (ScheduleRequest body) =>
{
    var text = (body?.Task ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { error = "task is required" });

    var repeat = (body?.Repeat ?? "").Trim();
    if (repeat.Length > 0 && !ScheduleStore.TryParseRepeat(repeat, DateTimeOffset.Now, out _))
        return Results.BadRequest(new { error = $"couldn't read the recurrence \"{repeat}\"" });

    DateTimeOffset fireAt;
    if (!string.IsNullOrWhiteSpace(body?.When))
    {
        if (!ScheduleStore.TryParseWhen(body!.When!.Trim(), DateTimeOffset.Now, out fireAt))
            return Results.BadRequest(new { error = $"couldn't read the time \"{body.When}\"" });
    }
    else if (repeat.Length > 0) ScheduleStore.TryParseRepeat(repeat, DateTimeOffset.Now, out fireAt);
    else return Results.BadRequest(new { error = "give either when (one-off) or repeat (recurring)" });

    var sessionId = (body?.Session ?? "").Trim();
    if (sessionId.Length == 0) sessionId = $"task-{Guid.NewGuid():N}"[..13];

    ScheduleStore.TryParseSlackSessionId(sessionId, out var channel, out var threadTs);
    var created = schedules.Add(sessionId, channel, threadTs, text, fireAt, null, null, repeat);
    Console.WriteLine($"[schedule] #{created.Id} added → {sessionId} at {fireAt.ToLocalTime():ddd d MMM HH:mm}" +
                      (repeat.Length > 0 ? $" repeating {repeat}" : ""));
    return Results.Json(new { id = created.Id, nextAt = created.FireAt, repeat = created.Repeat }, json);
});

// Change when, how often, what, or whether it's paused. Pausing rather than only cancelling because a daily job
// you want to stop for a week is the common case, and cancelling it loses the wording you'd have to retype.
app.MapPatch("/api/schedules/{id}", (string id, ScheduleRequest body) =>
{
    if (schedules.Get(id) is null) return Results.NotFound(new { error = "no such scheduled task" });

    DateTimeOffset? fireAt = null;
    if (!string.IsNullOrWhiteSpace(body?.When))
    {
        if (!ScheduleStore.TryParseWhen(body!.When!.Trim(), DateTimeOffset.Now, out var parsed))
            return Results.BadRequest(new { error = $"couldn't read the time \"{body.When}\"" });
        fireAt = parsed;
    }

    // An empty (but present) repeat means "stop repeating", which is different from not mentioning it at all.
    string? repeat = body?.Repeat is null ? null : body.Repeat.Trim();

    if (!schedules.Edit(id, fireAt, repeat, body?.Task, body?.Paused))
        return Results.BadRequest(new { error = $"couldn't read the recurrence \"{repeat}\"" });

    var after = schedules.Get(id)!;
    return Results.Json(new { id = after.Id, nextAt = after.FireAt, repeat = after.Repeat, paused = after.Paused }, json);
});

app.MapDelete("/api/schedules/{id}", (string id) =>
    schedules.Cancel(id) ? Results.Ok(new { cancelled = id }) : Results.NotFound(new { error = "no such pending task" }));

// A conversation's name, however it can be found: the hub's summarised title, then the chat's opening line, then
// nothing — a task created FROM the tasks page has a thread of its own that has never been spoken in.
string? ConversationTitle(string sessionId)
{
    var title = controlHub.Conversation(sessionId)?.Title;
    if (string.IsNullOrWhiteSpace(title) || title == "(new conversation)")
        title = chatHistory.Has(sessionId) ? chatHistory.FirstUserMessage(sessionId) : null;
    return string.IsNullOrWhiteSpace(title) ? null : title;
}

// Forget a conversation: its recorded history goes, and so does the live session if it's still in memory —
// otherwise the chat reappears the moment anything touches it again.
app.MapDelete("/api/chats/{id}", (string id) =>
{
    // All three, or it comes back: the recording on disk, the hub's tracked copy (which can rebuild it), and the
    // live session (which would start recording again the moment anything appended to it).
    var hadFile = chatHistory.Delete(id);
    var wasTracked = controlHub.Forget(id);
    sessions.Remove(id);
    // And any nudge pointing at it. A conversation that has been deleted cannot be the thing somebody has not read
    // yet, and a nudge that opens nothing is worse than no nudge.
    watchers.Opened(id);

    return hadFile || wasTracked
        ? Results.Ok(new { deleted = id })
        : Results.NotFound(new { error = "no such chat" });
});

app.MapPost("/api/session/{id}/upload", async (string id, HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "expected a multipart upload" });

    var form = await ctx.Request.ReadFormAsync();
    if (form.Files.Count == 0) return Results.BadRequest(new { error = "no files" });

    var session = sessions.GetOrCreate(id);
    var dir = Orchestrator.FilesDirFor(workspaceRoot, id);
    Directory.CreateDirectory(dir);

    var saved = new List<Attachment>();
    foreach (var file in form.Files)
    {
        // Name only: a browser can send a path, and a path is how an upload escapes the folder it belongs in.
        var name = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(name) || file.Length == 0) continue;
        if (file.Length > 64L * 1024 * 1024) continue;

        var path = Path.Combine(dir, name);
        await using (var stream = File.Create(path))
            await file.CopyToAsync(stream);

        saved.Add(new Attachment(name, path, file.ContentType, file.Length));
    }

    if (saved.Count == 0) return Results.BadRequest(new { error = "nothing usable in that upload" });

    // Held against the session so the NEXT message carries them, which is how the Slack path already works —
    // one mechanism, so a file behaves the same whichever surface it arrives on.
    session.PendingAttachments = saved;
    Console.WriteLine($"[upload] {id}: {saved.Count} file(s) — {string.Join(", ", saved.Select(a => a.Name))}");

    return Results.Ok(new { files = saved.Select(a => new { name = a.Name, url = $"/api/session/{id}/files/{Uri.EscapeDataString(a.Name)}" }) });
});

app.MapPost("/api/session/{id}/message", (string id, SessionMessage body) =>
{
    string text = (body?.Content ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { error = "empty message" });

    // OpenConversation, not GetOrCreate: this has to REPLAY the conversation, exactly as the stream and the
    // snapshot do. Getting a bare session here meant that after a restart, the first thing said in an existing
    // chat arrived into an empty room — numbered from zero on top of history the client still had on screen,
    // with no memory of what had been discussed and no tasks to continue. Asked to carry on with task 3, it
    // had never heard of task 3 and invented a different job instead.
    var session = OpenConversation(id);
    _ = Task.Run(async () =>
    {
        try { await orchestrator.HandleMessageAsync(session, text, CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine($"[orchestrator] {ex}"); }
    });
    return Results.Ok();
});

// Pin a session to a project: it becomes that project's dedicated chat — the orchestrator stays scoped
// to the project (topic, tools, memory). Called once when the project's chat is opened. Pass null to unpin.
app.MapPost("/api/session/{id}/project", (string id, ProjectPin body) =>
{
    var slug = string.IsNullOrWhiteSpace(body?.Slug) ? null : body!.Slug!.Trim().ToLowerInvariant();
    if (slug is not null && !projects.Exists(slug)) return Results.NotFound(new { error = "no such project" });
    var session = sessions.GetOrCreate(id);
    session.PinnedProject = slug;
    session.CurrentProject = slug;
    return Results.Ok();
});

// Thumbs up/down on an assistant message — the label that turns a logged interaction into a good/bad
// training example. Joins back to the interaction log by (session, msg_id).
app.MapPost("/api/session/{id}/feedback", (string id, FeedbackMessage body) =>
{
    if (body is null) return Results.BadRequest();
    trainingLog.Feedback(new
    {
        ts = DateTimeOffset.UtcNow,
        session = id,
        msg_id = body.MessageId,
        rating = body.Rating,
        note = body.Note,
    });
    return Results.Ok();
});

// Quietly cancel a running background task from the UI. This intentionally does not add a chat
// message; the task pill is the control surface for this.
app.MapDelete("/api/session/{id}/task/{taskId}", (string id, string taskId) =>
{
    // OpenConversation, not Get: a task outlives the process that ran it, exactly as a question does. With a cold
    // lookup this answered "session not found" for anything started before a restart — so the one task most in need
    // of stopping, the stray still showing a progress bar, was the one task that could not be stopped.
    var session = OpenConversation(id);

    taskId = taskId.TrimStart('#').Trim();
    if (!session.Tasks.TryGetValue(taskId, out var task))
    {
        // Nothing in memory knows about it, and the pill is drawn from the event log rather than from memory — so
        // write the ending there. Otherwise "stop" reports success and the bar keeps moving.
        session.Append("working_done", JsonSerializer.Serialize(
            new { id = taskId, status = "cancelled", at = DateTimeOffset.UtcNow }, json));
        return Results.Ok(new { id = taskId, status = "cancelled" });
    }

    if (!task.IsRunning)
    {
        // Already over, but say so on the stream anyway: a task whose ending was never recorded is precisely the
        // one that shows as running for ever.
        session.Append("working_done", JsonSerializer.Serialize(
            new { id = task.Id, status = task.Status is { Length: > 0 } ? task.Status : "cancelled", at = DateTimeOffset.UtcNow }, json));
        return Results.Ok(new { id = task.Id, status = task.Status });
    }

    task.Status = "cancelled";
    task.Cts.Cancel();
    // Cancel any hidden child tasks too (a multi-discipline plan's steps run as children with their own
    // tokens) — otherwise an in-flight step keeps running and delivers its result after the user stopped it.
    foreach (var child in session.Tasks.Values.Where(c => c.ParentTaskId == task.Id && c.IsRunning))
    {
        child.Status = "cancelled";
        try { child.Cts.Cancel(); } catch { }
    }
    session.Append("working_done", JsonSerializer.Serialize(new { id = task.Id, status = task.Status }, json));
    return Results.Ok(new { id = task.Id, status = task.Status });
});

// Answer a task that paused to ask a question. The worker resumes from where it left off (seeded with its
// own prior transcript), so it continues with full context. The answer, reply, and any further result all
// arrive on the session stream.
// Put a question down without answering it. A question stops mattering for two reasons, and only one of them
// is an answer: work moves on, another task covers the same ground, the thing it asked about is no longer true.
// Without this the only way to clear a card was to answer a question that had lost its point.
app.MapDelete("/api/session/{id}/task/{taskId}/question", (string id, string taskId) =>
{
    var session = OpenConversation(id);
    taskId = taskId.TrimStart('#').Trim();

    // Recorded as an event so it survives a reload the same way the question did — the snapshot is rebuilt
    // from this log, so anything not written here comes straight back.
    session.Append("question_done", JsonSerializer.Serialize(new { id = taskId }, json));

    /*
     * Cleared, and STOPPED.
     *
     * Marking the task interrupted was not enough on two counts, and both showed up as a question that would not go
     * away. Its cancellation token was never tripped, so the worker carried on and asked the next thing a minute
     * later. And the control centre learns a run's state from the event log, which never heard about this — so
     * /api/questions went on reporting it as waiting however many times it was dismissed.
     */
    var stopped = orchestrator.DismissQuestion(session, taskId);

    // What the hub reads. Without it the run stays "waiting" for ever and the question comes straight back.
    session.Append("working_done", JsonSerializer.Serialize(
        new { id = taskId, status = "cancelled", at = DateTimeOffset.UtcNow }, json));

    return Results.Ok(new { dismissed = taskId, stopped });
});

/*
 * Everything waiting on an answer, from every conversation.
 *
 * The home page had an "Awaiting your answer" section already, and it only ever saw the conversation that happened
 * to be open — questions live on a session's own event stream. So a background job that stopped to ask something in
 * a thread the user was not looking at waited for ever, and from the outside the system had simply gone quiet.
 * "It says it needs me but there's no way for me to communicate with it" is precisely what that looks like.
 *
 * Only questions with nowhere else to appear. A panel build's ask is left out, because the panel is on this same
 * page and asks in its own box — listing it here as well showed the identical paragraph twice, once with the whole
 * build brief as its title. The panel is the better place for it: it is the thing that cannot finish without it.
 */
app.MapGet("/api/questions", () =>
{
    // Whose questions are already being asked by a panel of their own.
    var onThePage = widgets.All()
        .Where(w => !string.IsNullOrWhiteSpace(w.Asks) && w.TaskId is { Length: > 0 } && w.Session is { Length: > 0 })
        .Select(w => $"{w.Session}#{w.TaskId}")
        .ToHashSet(StringComparer.Ordinal);

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var asking = new List<object>();

    foreach (var run in controlHub.Runs().Where(r => r.Status == "waiting"))
    {
        if (string.IsNullOrWhiteSpace(run.PendingQuestion) || string.IsNullOrEmpty(run.TaskId)) continue;
        var key = $"{run.ConversationId}#{run.TaskId}";
        if (onThePage.Contains(key) || !seen.Add(key)) continue;
        asking.Add(new
        {
            session = run.ConversationId,
            taskId = run.TaskId,
            task = run.Task,
            question = run.PendingQuestion,
            surface = run.Surface,
            since = run.StartedAt,
        });
    }

    return Results.Json(asking, json);
});

app.MapPost("/api/session/{id}/task/{taskId}/answer", (string id, string taskId, SessionMessage body) =>
{
    string text = (body?.Content ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { error = "empty answer" });

    // OpenConversation, not Get: a question outlives the process that asked it. This 404'd for anything asked
    // before a restart — the session was cold and its tasks had not been rebuilt — so clicking an option or
    // typing an answer did nothing at all, silently, which is exactly how it looked from the outside.
    var session = OpenConversation(id);

    taskId = taskId.TrimStart('#').Trim();
    if (!session.Tasks.TryGetValue(taskId, out var task))
        return Results.NotFound(new { error = $"task #{taskId} not found" });
    if (task.Status != "waiting")
        return Results.Conflict(new { error = $"task #{task.Id} isn't waiting for an answer (status: {task.Status})" });

    // Whatever a panel was asking on this task's behalf, it has now been answered. Done here as well as in the
    // orchestrator because this is the one place that always knows both ids, and an ask that outlives its answer
    // leaves the panel asking a question the user has already dealt with.
    foreach (var panel in widgets.All().Where(w => w.TaskId == taskId && w.Session == id))
        widgets.Asked(panel.Id, null);

    _ = Task.Run(async () =>
    {
        try { await orchestrator.AnswerTaskAsync(session, task, text); }
        catch (Exception ex) { Console.Error.WriteLine($"[answer-task] {ex}"); }
    });
    return Results.Ok();
});

// The session's persistent event stream (SSE). The client keeps this open continuously and replays
// from `from` on reconnect, so async pushes are never missed. This stream never ends on its own.
// Reopening a conversation this process never saw: replay what was written down, so the chat is there rather
// than being an empty room with a title. Loaded into the session's own buffer, which means the normal ?from=N
// streaming, the client's reconnect logic and the replay all keep working unchanged.
//
// Called by BOTH ways a client reads a conversation. It has to be: whichever endpoint is asked first is the one
// that creates the session, and a session created empty is no longer cold — so when replay lived only in the
// stream, a client that polled first (the fallback used through a buffering proxy) got an empty chat, and so did
// the stream that followed it.
Session OpenConversation(string id)
{
    bool cold = sessions.Get(id) is null;
    var session = sessions.GetOrCreate(id);
    if (!cold) return session;

    var recorded = chatHistory.Has(id) ? chatHistory.Read(id) : Array.Empty<(string, string)>();

    // Nothing recorded, because the conversation predates the recorder. The control hub kept a transcript of
    // it all along for its own dashboard, so rebuild the messages from that rather than opening the empty
    // room — every conversation before this feature existed would otherwise be a title with nothing behind it.
    if (recorded.Count == 0 && controlHub.Conversation(id) is { Transcript.Count: > 0 } tracked)
    {
        recorded = TranscriptEvents(tracked.Transcript);
        Console.WriteLine($"[chat] {id}: no recording; rebuilt {tracked.Transcript.Count} message(s) from the hub");
    }

    if (recorded.Count > 0)
    {
        recorded = TaskLifecycle.CloseDangling(recorded);
        session.Replay(recorded);
        // The events are what the CLIENT renders. Give the model back its side of the same conversation, or it
        // reopens the chat with no memory of anything in it.
        Orchestrator.RestoreHistory(session);
        // And the tasks it ran, so "carry on with that" can find them rather than starting something new.
        orchestrator.RestoreTasks(session);
        Console.WriteLine($"[chat] {id}: replayed {recorded.Count} event(s), {session.History.Count} message(s), {session.Tasks.Count} task(s)");
    }
    return session;
}

app.MapGet("/api/session/{id}", async (string id, int? from, HttpContext ctx) =>
{
    var session = OpenConversation(id);

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    int next = Math.Max(0, from ?? 0);
    try
    {
        await ctx.Response.WriteAsync(":" + new string(' ', 2048) + "\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        while (true)
        {
            var change = session.WaitForChangeAsync();
            bool wrote = false;
            while (session.TryGet(next, out var ev))
            {
                await ctx.Response.WriteAsync($"event: {ev.Event}\n", ctx.RequestAborted);
                await ctx.Response.WriteAsync($"data: {ev.Data}\n\n", ctx.RequestAborted);
                wrote = true;
                next++;
            }
            if (wrote) await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            await Task.WhenAny(change, Task.Delay(Timeout.Infinite, ctx.RequestAborted));
            if (ctx.RequestAborted.IsCancellationRequested) break;
        }
    }
    catch (OperationCanceledException)
    {
        // client went away — session lives on
    }
});

// The same event stream as plain JSON, for clients that can't stream.
//
// A Cloudflare quick tunnel (and plenty of other proxies) BUFFERS a text/event-stream response: the request
// reaches this origin, the events are written and flushed, and the edge holds them anyway — so the browser sees
// nothing and the chat looks frozen. The padding-comment trick on the SSE endpoint isn't enough to defeat it.
// Rather than pretend, the clients race the stream against a short timer and fall back to polling this, which is
// an ordinary short response no proxy has an opinion about. Same offsets, same events, same order.
// The conversation as it stands, in one request: messages with their files and cards, and each task with the
// outcome it reached. The client renders this as history and then opens the live stream from `next`, so a reload
// is a page of content rather than a re-run of everything that ever happened in the room.
app.MapGet("/api/session/{id}/snapshot", (string id) =>
{
    var session = OpenConversation(id);

    var events = new List<(string Event, string Data)>(session.Count);
    for (var i = 0; session.TryGet(i, out var ev); i++) events.Add((ev.Event, ev.Data));

    return Results.Json(ConversationSnapshot.Of(events), json);
});

app.MapGet("/api/session/{id}/events", (string id, int? from) =>
{
    var session = OpenConversation(id);
    int next = Math.Max(0, from ?? 0);
    var events = new List<object>();
    while (session.TryGet(next, out var ev))
    {
        events.Add(new { @event = ev.Event, data = ev.Data });
        next++;
    }
    return Results.Json(new { next, events }, json);
});

// Start an agent run in the BACKGROUND and return its id immediately. The run keeps going whether
// or not anyone is connected — disconnecting never loses the answer.
app.MapPost("/api/chat", (ChatRequest request, AgentRunStore store) =>
{
    var (input, prompt) = BuildAgent(request);
    var session = store.Create();
    _ = Task.Run(() => RunAgentAsync(session, input, prompt));
    return Results.Json(new { runId = session.Id });
});

// Subscribe to a run's event stream over SSE, starting at offset `from`. Replays buffered events
// then streams live ones. If the client disconnects, the run continues; reconnect with `from` set
// to the number of events already received to resume exactly where you left off.
app.MapGet("/api/chat/{id}", async (string id, int? from, AgentRunStore store, HttpContext ctx) =>
{
    var session = store.Get(id);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    int next = Math.Max(0, from ?? 0);
    try
    {
        // Defeat CDN/proxy buffering (e.g. Cloudflare quick tunnels otherwise hold the whole SSE
        // response and deliver it all at once): a padding comment + immediate flush forces the edge
        // to start streaming straight away. The ":" prefix makes it an SSE comment clients ignore.
        await ctx.Response.WriteAsync(":" + new string(' ', 2048) + "\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        while (true)
        {
            var change = session.WaitForChangeAsync();

            // Drain everything currently buffered, then flush ONCE. On a reconnect/replay this sends
            // the whole backlog as a single batch (instead of one flush per event), so the client gets
            // it in one chunk and renders once — avoiding the "frozen then boom" catch-up on refresh.
            bool wrote = false;
            while (session.TryGet(next, out var ev))
            {
                await ctx.Response.WriteAsync($"event: {ev.Event}\n", ctx.RequestAborted);
                await ctx.Response.WriteAsync($"data: {ev.Data}\n\n", ctx.RequestAborted);
                wrote = true;
                next++;
            }
            if (wrote)
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            if (session.Completed && next >= session.Count)
                break;

            await Task.WhenAny(change, Task.Delay(Timeout.Infinite, ctx.RequestAborted));
            if (ctx.RequestAborted.IsCancellationRequested)
                break;
        }
    }
    catch (OperationCanceledException)
    {
        // Client went away — the run keeps running server-side. Nothing to do.
    }
});

// Explicitly stop a run (the UI's Stop button). A disconnect does NOT do this.
app.MapDelete("/api/chat/{id}", (string id, AgentRunStore store) =>
{
    store.Get(id)?.Cancel();
    return Results.Ok();
});

// ======================= Smarty.Control — the command centre =======================
// Read-mostly views over everything Smarty is doing: live conversations (this chat + Slack), tasks/runs,
// files in every bucket, memories, and personas (with the tools each can call — never the system prompt).

object ConvSummary(ControlConversation c) => new
{
    id = c.Id, surface = c.Surface, title = c.Title ?? "(new conversation)", subtitle = c.Subtitle,
    project = c.Project, persona = c.Persona, userName = c.UserName, status = c.Status,
    messageCount = c.MessageCount, startedAt = c.StartedAt, lastActivityAt = c.LastActivityAt,
};
object RunSummary(ControlRun r) => new
{
    id = r.Id, conversationId = r.ConversationId, surface = r.Surface, taskId = r.TaskId,
    task = r.Task, project = r.Project, persona = r.Persona, status = r.Status,
    latestNote = r.LatestNote, pendingQuestion = r.PendingQuestion, result = r.Result,
    startedAt = r.StartedAt, endedAt = r.EndedAt, steps = r.Steps.Count,
    // Spend, folded to a single line per run — the tasks list shows the total, the detail view breaks it down.
    cost = r.Spend.Sum(sp => sp.Cost),
    costKnown = r.Spend.Count > 0 && r.Spend.All(sp => sp.PriceKnown),
    inputTokens = r.Spend.Sum(sp => sp.InputTokens),
    outputTokens = r.Spend.Sum(sp => sp.OutputTokens),
    cachedInputTokens = r.Spend.Sum(sp => sp.CachedInputTokens),
    models = r.Spend.Select(sp => sp.Model).Distinct(),
};

// All tracked conversations, newest activity first.
app.MapGet("/api/control/conversations", () => Results.Json(controlHub.Conversations().Select(ConvSummary), json));

// One conversation: its reconstructed transcript, files, and the runs that happened on it.
app.MapGet("/api/control/conversations/{id}", (string id) =>
{
    var c = controlHub.Conversation(id);
    if (c is null) return Results.NotFound(new { error = "no such conversation" });
    return Results.Json(new
    {
        summary = ConvSummary(c),
        files = c.Files,
        transcript = c.Transcript.Select(m => new { role = m.Role, text = m.Text, at = m.At }),
        runs = controlHub.RunsFor(id).Select(r => new
        {
            id = r.Id, taskId = r.TaskId, task = r.Task, persona = r.Persona, status = r.Status,
            latestNote = r.LatestNote, pendingQuestion = r.PendingQuestion, result = r.Result,
            startedAt = r.StartedAt, endedAt = r.EndedAt,
            steps = r.Steps.Select(s => new { kind = s.Kind, text = s.Text, tool = s.Tool, args = s.Args, result = s.Result }),
        }),
    }, json);
});

// The live activity stream: a snapshot first, then every event from every conversation as it happens.
app.MapGet("/api/control/stream", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    await ctx.Response.WriteAsync(":" + new string(' ', 2048) + "\n\n", ctx.RequestAborted);
    var snapshot = JsonSerializer.Serialize(new
    {
        conversations = controlHub.Conversations().Select(ConvSummary),
        runs = controlHub.Runs().Take(100).Select(RunSummary),
    }, json);
    await ctx.Response.WriteAsync($"event: snapshot\ndata: {snapshot}\n\n", ctx.RequestAborted);
    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

    var sub = controlHub.Subscribe();
    try
    {
        await foreach (var frame in sub.Channel.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"event: activity\ndata: {frame}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { /* dashboard disconnected */ }
    finally { controlHub.Unsubscribe(sub); }
});

// Cross-process ingest: Smarty.Slack forwards its conversation events here. Token-guarded when a token is set.
app.MapPost("/api/control/ingest", (IngestPayload body, HttpContext ctx) =>
{
    if (!string.IsNullOrEmpty(controlToken) &&
        ctx.Request.Headers["X-Control-Token"].ToString() != controlToken)
        return Results.Unauthorized();
    if (body is null || string.IsNullOrWhiteSpace(body.ConversationId)) return Results.BadRequest();
    var meta = new ConversationMeta(body.Title, body.Subtitle, body.Project, body.Persona, body.UserName);
    controlHub.Ingest(body.ConversationId, string.IsNullOrWhiteSpace(body.Surface) ? "slack" : body.Surface,
        body.Event ?? "", body.Data ?? "", meta);
    return Results.Ok();
});

// Every run across every surface (running + past). Optional ?status= filter.
app.MapGet("/api/control/tasks", (string? status) =>
{
    var runs = controlHub.Runs();
    if (!string.IsNullOrWhiteSpace(status))
        runs = runs.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
    return Results.Json(runs.Select(RunSummary), json);
});

// Cancel a running task on a local web conversation (Slack tasks can't be cancelled cross-process yet).
app.MapDelete("/api/control/conversations/{id}/tasks/{taskId}", (string id, string taskId) =>
{
    var session = sessions.Get(id);
    if (session is null) return Results.Conflict(new { error = "can only cancel tasks on local chat conversations" });
    taskId = taskId.TrimStart('#').Trim();
    if (!session.Tasks.TryGetValue(taskId, out var task)) return Results.NotFound(new { error = "no such task" });
    if (!task.IsRunning) return Results.Ok(new { id = task.Id, status = task.Status });
    task.Status = "cancelled";
    task.Cts.Cancel();
    foreach (var child in session.Tasks.Values.Where(c => c.ParentTaskId == task.Id && c.IsRunning))
    {
        child.Status = "cancelled";
        try { child.Cts.Cancel(); } catch { }
    }
    session.Append("working_done", JsonSerializer.Serialize(new { id = task.Id, status = task.Status }, json));
    return Results.Ok(new { id = task.Id, status = task.Status });
});

// ---- spend ----
// What Smarty has cost, and where it went. Grouped by model with the split providers actually bill on: fresh
// input, CACHED input (about a quarter the price), and output — of which reasoning tokens are called out because
// a model that thinks in the output channel is charged for it whether or not the user ever sees it.
app.MapGet("/api/control/spend", () =>
{
    var runs = controlHub.Runs();
    var spend = runs.SelectMany(r => r.Spend).ToList();

    var byModel = spend.GroupBy(sp => sp.Model, StringComparer.OrdinalIgnoreCase).Select(g => new
    {
        model = g.Key,
        priceKnown = g.All(sp => sp.PriceKnown),
        price = ModelPrices.For(g.Key) is { } p
            ? new { inputPerMillion = p.InputPerMillion, outputPerMillion = p.OutputPerMillion, cachedInputPerMillion = p.CachedInputPerMillion }
            : null,
        runs = g.Count(),
        calls = g.Sum(sp => sp.Calls),
        inputTokens = g.Sum(sp => sp.InputTokens),
        cachedInputTokens = g.Sum(sp => sp.CachedInputTokens),
        outputTokens = g.Sum(sp => sp.OutputTokens),
        reasoningTokens = g.Sum(sp => sp.ReasoningTokens),
        cost = g.Sum(sp => sp.Cost),
    }).OrderByDescending(m => m.cost).ToList();

    long input = spend.Sum(sp => (long)sp.InputTokens);
    long cached = spend.Sum(sp => (long)sp.CachedInputTokens);

    return Results.Json(new
    {
        total = new
        {
            cost = spend.Sum(sp => sp.Cost),
            costKnown = spend.Count > 0 && spend.All(sp => sp.PriceKnown),
            runs = runs.Count(r => r.Spend.Count > 0),
            inputTokens = input,
            cachedInputTokens = cached,
            outputTokens = spend.Sum(sp => (long)sp.OutputTokens),
            reasoningTokens = spend.Sum(sp => (long)sp.ReasoningTokens),
            // The number that tells you whether the prompt prefix is being reused, which is where the savings are.
            cacheHitRate = input > 0 ? (double)cached / input : 0d,
        },
        byModel,
        // The costliest runs, so an expensive task is findable rather than averaged away.
        topRuns = runs.Where(r => r.Spend.Count > 0)
            .OrderByDescending(r => r.Spend.Sum(sp => sp.Cost))
            .Take(20)
            .Select(r => new
            {
                id = r.Id, taskId = r.TaskId, task = r.Task, surface = r.Surface, persona = r.Persona,
                status = r.Status, startedAt = r.StartedAt,
                cost = r.Spend.Sum(sp => sp.Cost),
                inputTokens = r.Spend.Sum(sp => sp.InputTokens),
                cachedInputTokens = r.Spend.Sum(sp => sp.CachedInputTokens),
                outputTokens = r.Spend.Sum(sp => sp.OutputTokens),
                models = r.Spend.Select(sp => new
                {
                    model = sp.Model, calls = sp.Calls, cost = sp.Cost, priceKnown = sp.PriceKnown,
                    inputTokens = sp.InputTokens, cachedInputTokens = sp.CachedInputTokens,
                    outputTokens = sp.OutputTokens, reasoningTokens = sp.ReasoningTokens,
                }),
            }),
    }, json);
});

// ---- memories ----
app.MapGet("/api/control/memories", () => Results.Json(
    graph.Edges.Where(e => e.State == Smarty.Brain.EdgeState.Active).Select(e => new
    {
        id = e.Id, type = graph.Get(e.From)?.Kind ?? "topic", key = e.Label,
        value = e.To is null ? e.Value : graph.Get(e.To)?.Name ?? e.Value,
        context = e.Note, scope = e.Source ?? "", subject = graph.Get(e.From)?.Name ?? e.From,
        asserted = e.Asserted,
    }), json));

app.MapPost("/api/control/memories", (ControlMemoryWrite body) =>
{
    if (body is null) return Results.BadRequest();

    // A write from the control centre goes through the same queue as anything said in conversation, so it gets the
    // same resolving. A hand-written fact that quietly forks a node is worse than no editor at all.
    //
    // The old three-field form still works and is composed into the sentence it was always describing: nothing on
    // this side of the API had to change, and the operator gets the reconciler's judgement for free.
    var statement = body.Statement is { Length: > 0 } said
        ? said
        : $"{body.Type?.Split(':').Last() ?? "I"} {body.Key} is {body.Value}"
          + (string.IsNullOrWhiteSpace(body.Context) ? "" : $" ({body.Context})");

    if (statement.Trim().Length < 4) return Results.BadRequest(new { error = "say what to record" });

    brain.Note(statement, "control centre", here);
    return Results.Ok(new { message = "Filed — it'll be reconciled against what's already known." });
});

app.MapDelete("/api/control/memories/{id}", (string id) =>
    graph.Retire(id, "removed from the control centre")
        ? Results.Ok() : Results.NotFound(new { error = "no such active memory" }));

// ---- personas (full management; system prompt never exposed or accepted) ----
app.MapGet("/api/control/personas", () => Results.Json(controlCatalog.Personas(), json));

app.MapPost("/api/control/personas", (PersonaWrite body) =>
{
    if (body is null) return Results.BadRequest();
    var p = personas.Upsert(body.Id, body.Name ?? "", body.Description ?? "", body.CapabilityIds ?? new List<string>());
    return p is null
        ? Results.BadRequest(new { error = "a name is required" })
        : Results.Json(controlCatalog.View(p), json);
});

app.MapDelete("/api/control/personas/{id}", (string id) =>
    personas.Delete(id) ? Results.Ok() : Results.Conflict(new { error = "built-in personas can't be deleted" }));

// ---- capabilities (what tools exist, what they do, and whether they're configured) ----
app.MapGet("/api/control/capabilities", () => Results.Json(controlCatalog.Capabilities(), json));
app.MapGet("/api/control/base-tools", () => Results.Json(controlCatalog.BaseTools(), json));

// ---- the brain ----
// Everything in it, for inspecting what Smarty knows. Deliberately not reachable by a conversation.
app.MapGet("/api/control/brain", () => Results.Json(new
{
    // The vocabulary, with counts. The one place an open kind list can be checked by a human: a kind holding one node
    // beside a kind holding forty is either brand new or the same idea under a second name.
    kinds = graph.KindsInUse().Select(k => new { kind = k.Kind, count = k.Count }),

    // The names first, because that is the shape of the thing — the edges only make sense once you can see what
    // they join.
    nodes = graph.Nodes.Select(n => new
    {
        id = n.Id, name = n.Name, kind = n.Kind, aliases = n.Aliases, created = n.Created,
        connections = graph.Around(n.Id).Count,
        // Nothing has confirmed this is a distinct thing rather than a second name for one already here.
        provisional = n.Provisional,
    }).OrderByDescending(n => n.connections),

    // Two names that might be one thing, waiting on an answer nobody but the user can give. Kept on the nodes, so
    // this survives every restart between being asked and being answered — which is most of the time.
    maybes = graph.Maybes().Select(pair => new
    {
        id = pair.Node.Id,
        name = pair.Node.Name,
        kind = pair.Node.Kind,
        connections = graph.Around(pair.Node.Id).Count,
        otherId = pair.Maybe.Id,
        other = pair.Maybe.Name,
        otherConnections = graph.Around(pair.Maybe.Id).Count,
    }),

    edges = graph.Edges.Select(e => new
    {
        id = e.Id,
        from = graph.Get(e.From)?.Name ?? e.From,
        relation = e.Label,
        to = e.To is null ? null : graph.Get(e.To)?.Name ?? e.To,
        // The ids as well as the names. A reader wants the names; anything DRAWING the graph needs to join edges to
        // nodes, and names cannot do that — two people may share one, and the id is what the edge actually holds.
        fromId = graph.Get(e.From)?.Id ?? e.From,
        toId = e.To is null ? null : graph.Get(e.To)?.Id ?? e.To,
        value = e.Value,
        note = e.Note,
        // The dead are included on purpose. "Why didn't we book that hotel" is answered by an edge that ended, and
        // an inspector that hides them cannot show you the most interesting thing in here.
        state = e.State.ToString().ToLowerInvariant(),
        because = e.Because,
        asserted = e.Asserted,
        ended = e.Ended,
        source = e.Source,
        // The boundary, visible. A fact with no audience is readable everywhere, which is right for one person and wrong
        // the moment it is not — so it has to be inspectable rather than inferred.
        audience = e.Audience,
    }).OrderByDescending(e => e.asserted),

    // What the nodes hold. Named and counted, never dumped — a document's text belongs in an answer that asked for it.
    context = contexts.All.Select(c => new
    {
        id = c.Id, node = graph.Get(c.NodeId)?.Name ?? c.NodeId, sort = c.Sort, name = c.Name,
        readable = c.Readable, bytes = c.Bytes, added = c.Added,
    }),

    // Things with no path back to you. Reported rather than removed: a memory of one person's world should not contain
    // anything nothing connects to them, but deleting on that test takes real facts with it when a plan simply forgot the
    // connection. Visible while the pile is small is enough.
    stranded = brain.Stranded().Select(n => new { id = n.Id, name = n.Name, kind = n.Kind }),

    // Probable values stored as things — a time or an amount written as a node. Reported rather than removed, because
    // the judgement is a guess and a wrong one loses a fact.
    loose = graph.Loose().Select(n => new { id = n.Id, name = n.Name }),

    // Things that look like the same thing twice, by name or by pointing at the same neighbours.
    duplicates = graph.Duplicates().Select(d => new { a = d.A.Name, b = d.B.Name, why = d.Why }),

    // What has been said and not yet worked through. Normally empty; a queue that stays full is the signal that
    // reconciliation is failing, and it is the only place that shows up.
    waiting = brain.Waiting.Select(w => new
    {
        id = w.Id, text = w.Text, retract = w.Retract, filed = w.Filed, tried = w.Tried, error = w.Error,
        source = w.Source,
    }),
}, json));

// Reconcile on demand, for when you have just told it something and do not want to wait for the loop.
app.MapPost("/api/control/brain/reconcile", async () =>
{
    var done = await brain.ReconcileAsync().ConfigureAwait(false);
    return Results.Json(new
    {
        settled = done.Settled.Select(x => new { text = x.Text, wrote = x.Wrote, questions = x.Questions }),
        dropped = done.Dropped.Select(x => new { text = x.Text, error = x.Error }),
    }, json);
});

// ---- who this is ----
// Asked on load. Unknown means the app shows a setup page rather than the operator editing a config file and restarting,
// which was the only way to say it before and is a poor first thing to ask of anybody.
app.MapGet("/api/identity", () => Results.Json(new
{
    // BOTH halves, because an assistant that knows exactly who you are while calling itself a placeholder is worse than
    // one that knows neither. Setup is not finished until both are answered.
    known = brain.Self is not null && naming.Chosen,
    name = brain.Self?.Name ?? "",
    assistant = naming.Assistant,
    // The local stand-in identity is not an address and must not be offered back as one — prefilling a setup form with
    // a fake email invites somebody to accept it.
    email = RealEmail(people.Self.Value),
}, json));

static string RealEmail(string? value) =>
    value is { Length: > 0 } && value.Contains('@') && !value.Equals(PeopleStore.LocalSelfEmail, StringComparison.OrdinalIgnoreCase)
        ? value
        : "";

app.MapPost("/api/identity", (IdentityWrite body) =>
{
    var name = (body?.Name ?? "").Trim();
    if (name.Length < 2) return Results.BadRequest(new { error = "what should I call you?" });

    var assistant = (body?.Assistant ?? "").Trim();
    if (assistant.Length < 2) return Results.BadRequest(new { error = "and what should you call me?" });

    var email = (body?.Email ?? "").Trim();

    // The directory first, so every other surface agrees who this is, then the graph. Called again later this renames
    // rather than adds, so correcting it never splits the memory in two.
    people.SetSelf(email.Length > 0 ? email : null, name);
    var me = brain.WhoAmI(name, people.Self.IsEmpty ? null : new[] { people.Self.Value });
    var mine = naming.Choose(assistant);

    Console.WriteLine($"[identity] {me.Name} [{me.Id}] calls me {mine}");
    return Results.Json(new
    {
        known = true, name = me.Name, assistant = mine, email = RealEmail(people.Self.Value),
    }, json);
});

// Hand back a document the memory is holding. The id travels in every answer that mentions one, so a reply can offer the
// file rather than only describing it.
app.MapGet("/api/brain/context/{id}", (string id) =>
{
    if (contexts.Get(id) is not { } held) return Results.NotFound(new { error = "no such context" });
    if (held.Sort == Smarty.Brain.Sorts.Note) return Results.Text(held.Value ?? "");

    return contexts.PathOf(held) is { Length: > 0 } path
        ? Results.File(path, contentType: null, fileDownloadName: held.Name)
        : Results.NotFound(new { error = "the file has gone" });
});

// Ask it something without going through a conversation. The same call the chat tool makes, so what comes back here
// is exactly what the model would have been handed — which is the only way to tell a recall problem from a wording one.
app.MapPost("/api/control/brain/ask", async (ControlAsk body) =>
{
    if (body?.Question is not { Length: > 2 } question) return Results.BadRequest(new { error = "ask a question" });
    return Results.Json(new { answer = await brain.RecallAsync(question, here).ConfigureAwait(false) }, json);
});

// What the model is handed every turn: the names, not the facts.
/*
 * Settle an identity question: are these two one thing, or two?
 *
 * Yes merges them and the name in doubt becomes another name for the survivor, so it resolves for ever afterwards. No
 * records that they are different — which is the half that makes asking bearable, because a question that comes back
 * after it has been answered is one nobody answers twice.
 */
app.MapPost("/api/control/brain/same", (BrainSame body) =>
{
    var one = graph.Get(body?.Id);
    var other = graph.Get(body?.Other);
    if (one is null || other is null) return Results.NotFound(new { error = "one of those isn't in the graph" });

    if (body!.Same)
    {
        var winner = one.Created <= other.Created ? one : other;
        var loser = winner.Id == one.Id ? other : one;
        return graph.TheSame(one.Id, other.Id)
            ? Results.Json(new { merged = true, into = winner.Name, was = loser.Name }, json)
            : Results.BadRequest(new { error = "couldn't merge those" });
    }

    return graph.NotTheSame(one.Id, other.Id)
        ? Results.Json(new { merged = false, kept = new[] { one.Name, other.Name } }, json)
        : Results.BadRequest(new { error = "couldn't record that" });
});

app.MapGet("/api/control/brain/index", () => Results.Json(new { index = brain.Index(200, here) }, json));

// Remove a node and everything joined to it.
//
// Erased, not retired, and that is the difference from /api/memory/{id}. Retiring a fact keeps a true thing that stopped
// being true; a node deleted by hand is one that should never have existed, and leaving its edges behind as history
// would mean the graph still answers questions using a thing the owner has just said is not real.
app.MapDelete("/api/control/brain/node/{id}", (string id) =>
{
    if (graph.Get(id) is not { } node) return Results.NotFound(new { error = "no such node" });

    var joined = graph.Around(node.Id, includeEnded: true).Count;
    var name = node.Name;

    return graph.Forget(node.Id)
        ? Results.Ok(new { deleted = node.Id, name, edges = joined })
        : Results.NotFound(new { error = "no such node" });
});

// Empty the brain. Nodes, edges, the queue waiting to be read, and the files kept against nodes.
//
// Guarded by a phrase the caller has to send rather than by a flag, because a flag is one stray click and this is the
// one action here with nothing behind it. The identity goes with everything else — it is a fact in the graph like any
// other — so the setup page comes back afterwards, which is the honest consequence of asking for a clean slate.
app.MapDelete("/api/control/brain", (string? confirm) =>
{
    if (confirm != "wipe") return Results.BadRequest(new { error = "send confirm=wipe" });

    var (nodes, edges) = graph.Wipe();
    var waiting = brain.Waiting.Count;
    brain.Clear();
    var files = contexts.Wipe();

    Console.WriteLine($"[brain] wiped by hand — {nodes} node(s), {edges} edge(s), {waiting} waiting, {files} file(s)");
    return Results.Ok(new { nodes, edges, waiting, files });
});

// Who Smarty knows, and which surface identities are the same human.
app.MapGet("/api/control/people", () => Results.Json(people.All.Select(p => new
{
    email = p.Email, name = p.Name, aliases = p.Aliases, isSelf = p.Id == people.Self,
}), json));

// NOTE: there is no per-room recall filter any more. The old store checked every fact against the room's audience
// before returning it; the graph does not, so every fact in it is visible to every conversation. Each edge keeps
// its Source, so who said it is recorded and a filter is still possible — but nothing enforces one today, and that
// matters the moment this serves more than one person.

// ---- MCP servers: add, edit, remove and restart them from the control centre, live ----
// The command line is shown; the environment it was started with — where a server's credentials live — is not
// returned by the status view. The editor sends env values, it never receives them back.

app.MapGet("/api/control/mcp", () => Results.Json(mcp.Status, json));

// The editable form of a server, so the UI can populate its fields. Env KEYS are returned, values are not —
// a token pasted into the editor stays in the file, not in every page load.
app.MapGet("/api/control/mcp/{name}/config", (string name) =>
{
    var server = mcp.Server(name);
    if (server is null) return Results.NotFound(new { error = "no such MCP server" });
    var c = server.Config;
    return Results.Json(new
    {
        name = c.Name, command = c.Command, args = c.Args, cwd = c.WorkingDirectory,
        envKeys = c.Env.Keys, enabled = c.Enabled, prefix = c.ToolPrefix, tools = c.AllowedTools,
        functions = c.Functions, promptHint = c.PromptHint,
        startupTimeoutSeconds = (int)c.StartupTimeout.TotalSeconds,
        callTimeoutSeconds = (int)c.CallTimeout.TotalSeconds,
        maxResultChars = c.MaxResultChars,
    }, json);
});

// The servers Smarty already knows how to run: a toggle instead of a command line. Reports whether each is
// installed, and whether the machine-specific bit (a checkout, a directory) could be found — so the UI can
// offer a straight toggle when it can, and ask for a path when it can't.
app.MapGet("/api/control/mcp/catalogue", () => Results.Json(McpCatalog.BuiltIns.Select(t =>
{
    var installed = mcp.Server(t.Name);
    var discovered = t.PathArgument is null ? null : t.Discover();
    return new
    {
        key = t.Key, name = t.Name, summary = t.Summary, function = t.Function,
        command = t.Command, requires = t.Requires, homepage = t.Homepage,
        pathArgument = t.PathArgument,
        discoveredPath = discovered,
        ready = t.PathArgument is null || discovered is not null,
        installed = installed is not null,
        enabled = installed?.Config.Enabled ?? false,
        connected = installed?.Connected ?? false,
        toolCount = installed?.Tools.Count ?? 0,
        error = installed?.Error,
    };
}), json));

// Toggle a built-in on. Materialises the template (with a supplied path when discovery couldn't find it), then
// goes through exactly the same install path as a hand-written server — a built-in is not a special case once
// it's on.
app.MapPost("/api/control/mcp/catalogue/{key}", async (string key, McpCatalogueInstall? body, CancellationToken ct) =>
{
    var template = McpCatalog.Find(key);
    if (template is null) return Results.NotFound(new { error = $"no built-in server \"{key}\"" });

    var config = template.ToConfig(body?.Path, enabled: true);
    if (config is null)
        return Results.BadRequest(new
        {
            error = $"Couldn't find it automatically — {template.PathArgument}.",
            requires = template.Requires,
        });

    var connection = await mcp.AddAsync(config, ct);
    capabilities.Register(new McpCapability(connection), config.Functions);
    McpServerConfig.Save(mcpConfigPath, mcp.Connections.Select(c => c.Config), json);

    return Results.Json(new
    {
        saved = true,
        connected = connection.Connected,
        tools = connection.Tools.Select(t => t.Name),
        error = connection.Error,
        usedPath = config.Args.LastOrDefault(),
        message = connection.Connected
            ? $"{template.Name} is on — {connection.Tools.Count} tool(s) available now."
            : $"Turned on, but it didn't start: {connection.Error ?? "no reason given"}. {template.Requires}",
    }, json);
});

// Enable/disable without losing the entry or any edits made to it — what the toggle does when switching off.
app.MapPost("/api/control/mcp/{name}/enabled", async (string name, McpEnabledWrite body, CancellationToken ct) =>
{
    var server = mcp.Server(name);
    if (server is null) return Results.NotFound(new { error = "no such MCP server" });

    var connection = await mcp.AddAsync(server.Config with { Enabled = body?.Enabled ?? true }, ct);
    if (connection.Config.Enabled) capabilities.Register(new McpCapability(connection), connection.Config.Functions);
    else capabilities.Unregister(McpCapability.IdFor(connection.Config));
    McpServerConfig.Save(mcpConfigPath, mcp.Connections.Select(c => c.Config), json);

    return Results.Json(new
    {
        enabled = connection.Config.Enabled,
        connected = connection.Connected,
        tools = connection.Tools.Select(t => t.Name),
        error = connection.Error,
        message = !connection.Config.Enabled ? $"{name} is off — its process was stopped and its tools withdrawn."
            : connection.Connected ? $"{name} is on — {connection.Tools.Count} tool(s)."
            : $"{name} is on but didn't start: {connection.Error ?? "no reason given"}",
    }, json);
});

// Create or replace a server: persist it, start it, and register its tools — no restart. The response carries
// the connection result, so the editor can say "connected, 16 tools" or show exactly why it didn't.
app.MapPost("/api/control/mcp", async (McpServerWrite body, CancellationToken ct) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "a name is required" });
    if (string.IsNullOrWhiteSpace(body.Command))
        return Results.BadRequest(new { error = "a command is required (e.g. node, npx, python)" });

    var config = new McpServerConfig
    {
        Name = body.Name.Trim(),
        Command = body.Command.Trim(),
        Args = (body.Args ?? new()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
        Env = (body.Env ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value ?? "", StringComparer.Ordinal),
        WorkingDirectory = string.IsNullOrWhiteSpace(body.Cwd) ? null : body.Cwd!.Trim(),
        Enabled = body.Enabled ?? true,
        ToolPrefix = string.IsNullOrWhiteSpace(body.Prefix) ? null : body.Prefix!.Trim(),
        AllowedTools = (body.Tools ?? new()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList(),
        Functions = (body.Functions ?? new()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim().ToLowerInvariant()).ToList(),
        PromptHint = string.IsNullOrWhiteSpace(body.PromptHint) ? null : body.PromptHint!.Trim(),
        StartupTimeout = body.StartupTimeoutSeconds is > 0 ? TimeSpan.FromSeconds(body.StartupTimeoutSeconds.Value) : TimeSpan.FromSeconds(30),
        CallTimeout = body.CallTimeoutSeconds is > 0 ? TimeSpan.FromSeconds(body.CallTimeoutSeconds.Value) : TimeSpan.FromMinutes(2),
        MaxResultChars = body.MaxResultChars is > 0 ? body.MaxResultChars.Value : 20_000,
    };

    var connection = await mcp.AddAsync(config, ct);

    // Register it so the very next task can call its tools, and remember it for next boot.
    capabilities.Register(new McpCapability(connection), config.Functions);
    McpServerConfig.Save(mcpConfigPath, mcp.Connections.Select(c => c.Config), json);

    return Results.Json(new
    {
        saved = true,
        connected = connection.Connected,
        tools = connection.Tools.Select(t => t.Name),
        offered = connection.OfferedToolNames,
        error = connection.Error,
        // Said plainly, because a server that starts but offers nothing looks identical to a working one.
        message = !config.Enabled ? "Saved, and left disabled."
            : connection.Connected
                ? $"Connected — {connection.Tools.Count} tool(s) available now, no restart needed."
                : $"Saved, but it didn't start: {connection.Error ?? "no reason given"}",
    }, json);
});

app.MapDelete("/api/control/mcp/{name}", async (string name) =>
{
    var server = mcp.Server(name);
    if (server is null) return Results.NotFound(new { error = "no such MCP server" });

    capabilities.Unregister(McpCapability.IdFor(server.Config));
    await mcp.RemoveAsync(name);
    McpServerConfig.Save(mcpConfigPath, mcp.Connections.Select(c => c.Config), json);
    return Results.Ok(new { removed = name });
});

// Kick a server that's gone quiet — kills the process and dials again.
app.MapPost("/api/control/mcp/{name}/reconnect", async (string name, CancellationToken ct) =>
{
    var connection = await mcp.ReconnectAsync(name, ct);
    if (connection is null) return Results.NotFound(new { error = "no such MCP server" });

    capabilities.Register(new McpCapability(connection), connection.Config.Functions);
    return Results.Json(new
    {
        connected = connection.Connected,
        tools = connection.Tools.Select(t => t.Name),
        error = connection.Error,
    }, json);
});

// ---- plugins: a DLL somebody wrote, uploaded here, answering in the next task ----
// The same live contract the MCP servers honour — install, configure, toggle and remove without a restart —
// and the same honesty about failure: a package that won't load reports why and stays listed, so the fix is a
// decision rather than a silent retry.

app.MapGet("/api/control/plugins", () => Results.Json(plugins.Status, json));

// Upload a .zip holding the plugin's DLL and anything it needs. Multipart because it is a file, and because
// a base64 body of a 20 MB package is 27 MB of JSON nobody can read in a log.
app.MapPost("/api/control/plugins", async (HttpRequest req, CancellationToken ct) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { ok = false, message = "expected a multipart file upload" });
    var form = await req.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file is null) return Results.BadRequest(new { ok = false, message = "no file was uploaded" });
    if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { ok = false, message = $"\"{file.FileName}\" isn't a zip — a plugin is packaged as a zip holding its DLL." });

    await using var stream = file.OpenReadStream();
    var result = await plugins.InstallAsync(stream, ct);
    return Results.Json(result, json, statusCode: result.Ok ? 200 : 400);
});

// Answer whatever setup stage a plugin is waiting on. Values travel IN and are handed straight to the plugin;
// nothing typed here is ever sent back out, and nothing typed here ever becomes a command parameter — which is
// what keeps a password or a one-time code out of a model's reach entirely.
app.MapPost("/api/control/plugins/{id}/setup", async (string id, PluginSetupWrite? body, CancellationToken ct) =>
{
    var result = await plugins.SubmitSetupAsync(id, body?.Stage, body?.Values, ct);
    return Results.Json(result, json, statusCode: result.Ok ? 200 : 400);
});

// Forget everything it learned and ask again from the first stage — the way back from a revoked sign-in, or
// the wrong account.
app.MapPost("/api/control/plugins/{id}/setup/reset", async (string id, CancellationToken ct) =>
{
    var result = await plugins.ResetSetupAsync(id, ct);
    return Results.Json(result, json, statusCode: result.Ok ? 200 : 404);
});

app.MapPost("/api/control/plugins/{id}/enabled", async (string id, PluginEnabledWrite? body, CancellationToken ct) =>
{
    var result = await plugins.SetEnabledAsync(id, body?.Enabled ?? true, ct);
    return Results.Json(result, json, statusCode: result.Ok ? 200 : 404);
});

app.MapDelete("/api/control/plugins/{id}", (string id) =>
{
    var result = plugins.Remove(id);
    return Results.Json(result, json, statusCode: result.Ok ? 200 : 404);
});

// ---- buckets (files) ----
app.MapGet("/api/control/buckets", () => Results.Json(controlBuckets.List(), json));

app.MapPost("/api/control/buckets/{kind}/{id}/files", async (string kind, string id, HttpRequest req, CancellationToken ct) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "expected multipart form upload" });
    var form = await req.ReadFormAsync(ct);
    if (form.Files.Count == 0) return Results.BadRequest(new { error = "no files" });
    var saved = new List<BucketFile>();
    foreach (var file in form.Files)
    {
        await using var s = file.OpenReadStream();
        var info = await controlBuckets.SaveAsync(kind, id, file.FileName, s, ct);
        if (info is null) return Results.BadRequest(new { error = "invalid bucket" });
        saved.Add(info);
    }
    return Results.Json(new { saved }, json);
});

app.MapGet("/api/control/buckets/{kind}/{id}/files/{*name}", (string kind, string id, string name) =>
{
    var path = controlBuckets.ResolveFile(kind, id, name);
    return path is null ? Results.NotFound() : Results.File(path, fileDownloadName: Path.GetFileName(path));
});

app.MapDelete("/api/control/buckets/{kind}/{id}/files/{*name}", (string kind, string id, string name) =>
    controlBuckets.DeleteFile(kind, id, name) ? Results.Ok() : Results.NotFound());

// MCP servers are our child processes; stop them with the host instead of leaving them running against a dead API.
app.Lifetime.ApplicationStopping.Register(() => mcp.DisposeAsync().AsTask().GetAwaiter().GetResult());

app.Run();

// ---- helpers ----

// Authoritative date/time + environment + truthfulness context, rebuilt fresh each call so the date
// is always current. Shared by the worker agents and the legacy /api/chat path.
// The STATIC host rules — shell + honesty + language. No clock here on purpose: anything that changes per
// call (the date/time) would sit in the system-prompt PREFIX and break prompt-cache reuse on every request.
// The current time is injected separately as a late message (NowLine), so the cacheable prefix stays stable.
/// <summary>
/// Finish off any task that was still running when the recording stopped.
/// <para>
/// A conversation is only replayed into a session this process has never seen, so by definition nothing in it is
/// running now — the work died with whatever process was serving it (a restart, a crash). But the recording ends
/// mid-task: a <c>working</c> with no <c>working_done</c>, which every reader takes at face value. That is why
/// reopening a chat showed a job as still going, spinner and all, hours after it stopped, and why it looked like
/// a reload had started the job again.
/// </para>
/// <para>
/// Interrupted rather than done: it did not finish, and saying "done" would be a lie about work that never
/// delivered.
/// </para>
/// </summary>
/// <summary>
/// What may be cached, and for how long.
/// <para>
/// Nothing said before, and a response with no Cache-Control is cached HEURISTICALLY — the browser guesses from
/// Last-Modified and may reuse it without asking. For a hashed asset that's ideal; for index.html it is the worst
/// possible outcome, because the shell is what NAMES the hashed bundle. A stale shell keeps loading a stale
/// bundle, so a rebuilt UI never arrives and every fix looks like it didn't work. That is exactly what happened:
/// a finished job kept showing as running for a browser holding an older build.
/// </para>
/// <para>
/// So: the shell must be revalidated every time (ETag makes that a cheap 304), and the fingerprinted assets can
/// be kept for a year — their names change whenever their contents do, which is what makes that safe.
/// </para>
/// </summary>
static void CacheHeaders(StaticFileResponseContext ctx)
{
    var isHashedAsset = ctx.File.PhysicalPath?.Replace('\\', '/').Contains("/assets/", StringComparison.OrdinalIgnoreCase) == true;

    ctx.Context.Response.Headers.CacheControl = isHashedAsset
        ? "public, max-age=31536000, immutable"
        : "no-cache"; // revalidate, don't refuse to store — a 304 is still fast
}

/// <summary>Files sitting on a project's own shelf — put there by promote_file rather than produced by a run.</summary>
IEnumerable<ProjectFileEntry> ShelvedFiles(string slug)
{
    var dir = Orchestrator.ProjectFilesDirFor(workspaceRoot, slug);
    if (!Directory.Exists(dir)) yield break;

    string[] names;
    try { names = Directory.GetFiles(dir); }
    catch (IOException) { yield break; }

    foreach (var path in names.OrderByDescending(File.GetLastWriteTimeUtc))
    {
        var name = Path.GetFileName(path);
        yield return new ProjectFileEntry(
            name,
            Run: "",
            ProducedAt: File.GetLastWriteTimeUtc(path),
            Url: $"/api/projects/{Uri.EscapeDataString(slug)}/files/{Uri.EscapeDataString(name)}");
    }
}

// The content type for a served file. Small and explicit: a browser given the wrong type for an image shows a
// download prompt, and for a video refuses to scrub.
static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".png" => "image/png",
    ".gif" => "image/gif",
    ".webp" => "image/webp",
    ".avif" => "image/avif",
    ".bmp" => "image/bmp",
    ".heic" => "image/heic",
    ".mp4" => "video/mp4",
    ".webm" => "video/webm",
    ".mov" => "video/quicktime",
    ".pdf" => "application/pdf",
    ".json" => "application/json",
    ".csv" => "text/csv; charset=utf-8",
    ".md" or ".txt" => "text/plain; charset=utf-8",
    _ => "application/octet-stream",
};

// A tracked transcript, as the events a client already knows how to render. Deliberately the same three-event
// shape a live message emits (start, content, end) so replay needs no special case anywhere downstream.
static List<(string Event, string Data)> TranscriptEvents(List<ConversationMessage> transcript)
{
    var events = new List<(string, string)>(transcript.Count * 3);
    for (var i = 0; i < transcript.Count; i++)
    {
        var m = transcript[i];
        if (string.IsNullOrWhiteSpace(m.Text)) continue;
        var role = m.Role == "user" ? "user" : "assistant";
        var payload = JsonSerializer.Serialize(new { id = i, text = m.Text });
        events.Add(("msg_start", JsonSerializer.Serialize(new { id = i, role })));
        events.Add(("content", payload));
        events.Add(("msg_end", payload));
    }
    return events;
}

string HostRules()
{
    string shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "PowerShell" : "/bin/sh";
    return
        $"\n\nYour shell ({shell}) is a FULL shell WITH internet — fetch live data yourself (e.g. Invoke-RestMethod); " +
        "never say you lack access. If a command fails, diagnose and retry until you get a real answer.\n" +
        "Report ONLY what a tool actually returned this turn — never fabricate news, prices, results or dates, " +
        "or claim a source you didn't use. If the tools can't get it after honest tries, say so plainly " +
        "(\"I couldn't retrieve that\"). Always reply in English.";
}

// The volatile clock anchor — injected as a late conversation message (NOT the system prompt) so it never
// invalidates the cached prefix. Rebuilt per call so it's always current.
//
// Location rides along with it for exactly the same reason: it changes, so it must stay out of the cacheable head
// of the prompt. Sent every turn rather than only on change — it costs a line, and "only when it changes" would
// mean a turn that happens to follow a move is the one turn that knows where they are.
string NowLine()
{
    var now = DateTime.Now;
    var clock = $"It is {now:dddd, d MMMM yyyy, HH:mm} ({TimeZoneInfo.Local.StandardName}), year {now.Year} — the " +
                "real present from the system clock, not the future; never call this date impossible or fictional.";

    return location.Note() is { Length: > 0 } where ? clock + "\n" + where : clock;
}

// System prompt for a worker (the "hands"): a capable, relentless task-doer with real tools.
string WorkerSystemPrompt() =>
    "You are a capable, relentless assistant. Complete the task with your tools, and base every factual " +
    "claim ONLY on what a tool returned this turn — if the tools don't get it, say so; never fill from memory.\n" +
    // What survives here is only what a worker cannot work out from the tools in front of it. Anything that
    // explains a tool has been moved onto that tool, where it is already in context and cannot go stale against
    // the thing it describes: the shell's two rules live on the shell, the deck refusal is enforced by
    // write_file, and the browser's own guidance arrives with the browser.
    "- Answer from what a tool returned THIS turn. Reach the page, read it, and say what it said — never a " +
    "snippet, never memory. One or two pages you've genuinely read is enough; then stop.\n" +
    "- A LOGIN WALL is not a dead end and not something to work around. It is the user's OWN browser, so they " +
    "can sign in and you cannot. LEAVE the page open on screen and finish by asking them to, INCLUDING THE FULL " +
    "URL — the chat's \"browser\" button opens straight onto it and accepts their typing, so they can do it from " +
    "wherever they are. Never tell them to go to their computer. The task pauses holding everything you found " +
    "and resumes when they answer.\n" +
    "- A NAMED SERVICE IS A WEBSITE, not a configuration. \"Send an email from my Proton\", \"check my eBay\", " +
    "\"put it in my calendar\" all mean: open that site, where the user is already signed in, and use it the way " +
    "a person does. Do NOT hunt for an API key, an SMTP setting or a config file, and NEVER search the disk or " +
    "the environment for their credentials. The browser IS the account.\n" +
    "- CITE, don't dump. Recommending specific things means their real URLs as markdown links, and a real image " +
    "shown with ![alt](url) rather than described. Never guess an address: no link beats a wrong link.\n" +
    "- Answer IN the message. Write a file only when the user asked for a document or it genuinely is one. Never " +
    "write a file and point at it instead of telling them, and never mention a file you didn't produce.\n" +
    "- To RECORD what you've been GIVEN, just save it — don't web-search to 'confirm' what the user told you.\n" +
    // The brief said £150 and nothing about returns; a 30-day refund promise went into the live listing anyway.
    "- Do what you were told, not what would be reasonable. A setting or term the brief doesn't specify, that " +
    "has consequences — returns, offers, spend, visibility, anything sent or published — is not yours to pick: " +
    "search_memory for a standing preference, and if there isn't one, STOP and ask. Filling a field or writing " +
    "a line is asserting a decision in their name; a sensible default is still a decision they didn't make.\n" +
    "Give a clear, complete answer to the task.\n" +
    "If you genuinely CAN'T proceed without something only the user can decide (a missing choice, a real " +
    "ambiguity, a go/no-go) — don't guess and don't give up. Stop and clearly state what you need them to " +
    "decide, and suggest the few answers you think most likely. Ask ONLY when truly blocked — never to " +
    "confirm something you can just do. When they answer, you'll continue with everything you've found still " +
    "in context." +
    HostRules();

(AgentInput input, string prompt) BuildAgent(ChatRequest request)
{
    var messages = request.Messages ?? new List<ChatMessage>();

    int lastUser = messages.FindLastIndex(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
    string prompt = lastUser >= 0 ? messages[lastUser].Content : (messages.LastOrDefault()?.Content ?? "");

    var conversation = new List<Message>();
    for (int i = 0; i < messages.Count; i++)
    {
        if (i == lastUser) continue; // the prompt is passed to AnswerStream, not seeded
        var m = messages[i];
        switch (m.Role.ToLowerInvariant())
        {
            case "user": conversation.Add(Message.User(m.Content)); break;
            case "assistant": conversation.Add(Message.Assistant(m.Content)); break;
        }
    }

    string baseSystem = string.IsNullOrWhiteSpace(request.System)
        ? "You are a helpful assistant."
        : request.System!;

    // The clock rides as a late seed (not the system prompt) so a stable baseSystem keeps a cacheable prefix.
    conversation.Add(Message.System(NowLine()));

    // Routed by the model id, like every other path — this one used to assume Ollama, which sent a Together
    // model id (the default now) to the local gateway.
    string modelName = string.IsNullOrWhiteSpace(request.Model) ? defaultModel : request.Model!;

    var input = new AgentInput
    {
        SystemPrompt = baseSystem + HostRules(),
        Model = ModelRouting.Spec(modelName, ollamaBaseUrl),
        Conversation = conversation,
    };

    if (request.EnableTools ?? true)
    {
        input.Tools.Add(ShellTool.Create());
        // The web, via the connected browser — same as every other path.
        foreach (var tool in mcp.ServersFor("browser").SelectMany(c => c.Tools))
            input.Tools.Add(tool);
    }

    return (input, prompt);
}

async Task RunAgentAsync(AgentRunSession session, AgentInput input, string prompt)
{
    void Emit(string @event, object data) => session.Append(@event, JsonSerializer.Serialize(data, json));

    try
    {
        var agent = new SmartyAgent(input);
        await foreach (var ev in agent.AnswerStream(prompt, session.Token))
        {
            switch (ev)
            {
                case AgentEvent.ContentDelta d: Emit("content", new { text = d.Text }); break;
                case AgentEvent.ContentCleared: Emit("content_cleared", new { }); break;
                case AgentEvent.ReasoningDelta r: Emit("reasoning", new { text = r.Text }); break;
                case AgentEvent.ToolStarted s: Emit("tool_started", new { name = s.ToolName, arguments = s.Arguments }); break;
                case AgentEvent.ToolCompleted c: Emit("tool_completed", new { name = c.ToolName, result = c.Result }); break;
                case AgentEvent.Completed done: Emit("completed", new { answer = done.Answer }); break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        Emit("cancelled", new { });
    }
    catch (Exception ex)
    {
        Emit("error", new { message = ex.Message });
    }
    finally
    {
        // Always end with a terminal "done" so subscribers know the run is finished.
        Emit("done", new { });
        session.Complete();
    }
}

// ---- request DTOs ----

internal sealed record ChatRequest(
    string? System,
    string? Model,
    bool? EnableTools,
    List<ChatMessage>? Messages);

internal sealed record ChatMessage(string Role, string Content);

/// <summary>Setting up a feed: where it is, how to read items out of it, and how it splits them into topics.</summary>
/// <summary>An answer to "are these two the same thing?".</summary>
internal sealed record BrainSame(string? Id, string? Other, bool Same);

/// <summary>One attempt at the password.</summary>
internal sealed record SignInAttempt(string? Password);

internal sealed record FeedSetup(string? Name, string? Source, string? Url, string? Expression, string? Items,
    Dictionary<string, string>? Map, Dictionary<string, string>? Headers, string? Every, string? Split, string? Topic);

internal sealed record FeedEdit(string? Name, string? Every, bool? Paused);

/// <summary>The toggle and the dial. PauseHours of 0 clears a pause; null leaves it alone.</summary>
internal sealed record ProactSetup(bool? On, string? Every, int? PauseHours, int? RoundupHour);

/// <summary>The tick or the cross. Nothing else — a cross deliberately carries no reason.</summary>
internal sealed record ProactAnswer(bool Ticked);

/// <summary>"up", "down", or nothing at all to clear it.</summary>
internal sealed record ProactVote(string? Verdict);

/// <summary>Undo puts a dismissed one back — a one-tap action next to a thumb needs a way back.</summary>
internal sealed record ProactDismiss(bool Undo);

/// <summary>Wipe the record. Today by default; All for the lot.</summary>
internal sealed record ProactClear(bool All);

/// <summary>One test on an arriving item, as it comes off the wire.</summary>
internal sealed record WatchTestSetup(string? Field, string? Op, string? Value);

/// <summary>What to watch for, and what to do when it happens.</summary>
internal sealed record WatchSetup(string? Name, string? Feed, string? Topic, List<WatchTestSetup>? When,
    string? About, string? Act, int? Limit);

internal sealed record WatchEdit(string? Name, string? Act, bool? Paused, int? Limit);

internal sealed record SessionMessage(string Content);

/// <summary>Creating or editing standing work. Every field optional on a PATCH; a present-but-empty
/// <c>Repeat</c> means "stop repeating", which is why it isn't a plain string.</summary>
internal sealed record ScheduleRequest(string? Task, string? When, string? Repeat, string? Session, bool? Paused);

/// <summary>Yes or no to a panel the system offered.</summary>
/// <summary>Yes or no to a proposed panel, with the two things worth changing before it is built.</summary>
internal sealed record WidgetAnswer(bool Accept, string? Size, string? Shows);

/// <summary>Moving a panel around, from the page itself.</summary>
internal sealed record WidgetEdit(
    string? Title, string? Size, int? Priority, bool? Pinned, string? Status, string? Refresh);

/// <summary>Rebuild a panel, optionally changing what it should show.</summary>
internal sealed record WidgetRebuild(string? Shows);

/// <summary>A component threw in the browser. The exception, as the boundary caught it.</summary>
internal sealed record WidgetBroken(string? Error);

/// <summary>Making a project's period repeat, with a standing instruction for each new one.</summary>
internal sealed record ProjectRepeat(string? Repeat, string? RenewPrompt);

/// <summary>A panel to put up from a kind already in the library.</summary>
internal sealed record WidgetFromKind(string? Kind, string? Title, Dictionary<string, string>? Params,
    string? Size, int? Priority, string? Why, string? Session);

/// <summary>A folder to grant access to: what to call it, where it is, and which file types.</summary>
internal sealed record SourceGrant(string? Name, string? Path, List<string>? Include, bool? Recursive);

/// <summary>Resources a panel tried to load and couldn't, as the browser reported them.</summary>
internal sealed record WidgetFaults(List<string>? Faults);

/// <summary>A file a project holds: produced by one of its runs, or filed against it directly.</summary>
internal sealed record ProjectFileEntry(string Name, string Run, DateTimeOffset? ProducedAt, string Url);

/// <summary>A geolocation fix from the browser. Accuracy is the radius it claims, in metres.</summary>
internal sealed record LocationReport(double Latitude, double Longitude, double? Accuracy);
internal sealed record ProjectPin(string? Slug);

internal sealed record FeedbackMessage(int MessageId, string Rating, string? Note);

// Smarty.Control write DTOs.
/// <summary>
/// A memory written from the control centre.
/// </summary>
/// <remarks>
/// <c>Statement</c> is the one that matters now — a plain sentence, reconciled like anything else. The other fields are
/// the old three-field form, kept so the existing editor keeps working; they are composed into a sentence rather than
/// written straight to a slot, because a hand-written fact should not get to skip the resolving that stops it forking a
/// node.
/// </remarks>
internal sealed record ControlMemoryWrite(string? Type, string? Key, string? Value, string? Context, string? Scope,
    string? Statement = null);
internal sealed record ControlAsk(string? Question);

/// <summary>
/// Who the brain belongs to, and what it is called back.
/// </summary>
/// <remarks>Both names are required. The email is optional — it only matters when other surfaces have to agree.</remarks>
internal sealed record IdentityWrite(string? Name, string? Email, string? Assistant);
internal sealed record PersonaWrite(string? Id, string? Name, string? Description, List<string>? CapabilityIds);

/// <summary>Turning a built-in on. <c>Path</c> is only needed when discovery couldn't find it.</summary>
internal sealed record McpCatalogueInstall(string? Path);

/// <summary>Flipping a server's toggle without discarding it.</summary>
internal sealed record McpEnabledWrite(bool? Enabled);

/// <summary>A panel doing something: which plugin, which command, and whatever that command takes.</summary>
internal sealed record WidgetAct(string? Plugin, string? Command, Dictionary<string, string>? Parameters);

/// <summary>Switching an installed plugin on or off without uninstalling it.</summary>
internal sealed record PluginEnabledWrite(bool? Enabled);

/// <summary>An answer to the setup stage a plugin is waiting on. The stage id is sent back with it so a page
/// left open on a step that has since moved on can be told to reload rather than answering the wrong one.</summary>
internal sealed record PluginSetupWrite(string? Stage, Dictionary<string, string>? Values);

/// <summary>An MCP server as the control centre submits it. Env values travel IN (a token being set) but are
/// never sent back out.</summary>
internal sealed record McpServerWrite(
    string? Name,
    string? Command,
    List<string>? Args,
    Dictionary<string, string>? Env,
    string? Cwd,
    bool? Enabled,
    string? Prefix,
    List<string>? Tools,
    List<string>? Functions,
    string? PromptHint,
    int? StartupTimeoutSeconds,
    int? CallTimeoutSeconds,
    int? MaxResultChars);
