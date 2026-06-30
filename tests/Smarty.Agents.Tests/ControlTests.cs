using System.Text;
using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>Tests for the Smarty.Control backend: the persisted persona store, the activity hub, the
/// tool catalogue (which must never leak a system prompt), memory enumeration, and bucket sandboxing.</summary>
public class ControlTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smarty-control-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- PersonaStore (file-backed) ----

    [Fact]
    public void PersonaStore_seeds_builtins_and_persists_user_personas()
    {
        var path = Path.Combine(TempDir(), "personas.json");
        var store = new PersonaStore(path, Json);

        // Built-ins are present and flagged.
        var se = store.Get("software_engineer");
        Assert.NotNull(se);
        Assert.True(se!.Builtin);

        // Create a user persona; it gets a synthesised, non-empty prompt and is not built-in.
        var created = store.Upsert(null, "Legal Advisor", "Reviews contracts.", new[] { "github" });
        Assert.NotNull(created);
        Assert.False(created!.Builtin);
        Assert.False(string.IsNullOrWhiteSpace(created.SystemPrompt));
        Assert.Contains("github", created.CapabilityIds);

        // A fresh store from the same file keeps the user persona AND the built-ins.
        var reloaded = new PersonaStore(path, Json);
        Assert.NotNull(reloaded.Get(created.Id));
        Assert.NotNull(reloaded.Get("software_engineer"));
    }

    [Fact]
    public void PersonaStore_protects_builtins_prompt_and_blocks_builtin_delete()
    {
        var path = Path.Combine(TempDir(), "personas.json");
        var store = new PersonaStore(path, Json);
        var original = store.Get("software_engineer")!.SystemPrompt;

        // Editing a built-in's name/caps must NOT change its curated prompt, and must keep it built-in.
        var edited = store.Upsert("software_engineer", "SWE", "tweaked", new[] { "code" });
        Assert.Equal(original, edited!.SystemPrompt);
        Assert.True(edited.Builtin);

        // Built-ins can't be deleted; user personas can.
        Assert.False(store.Delete("software_engineer"));
        var user = store.Upsert(null, "Temp", "x", Array.Empty<string>())!;
        Assert.True(store.Delete(user.Id));
    }

    // ---- ControlCatalog: tools surfaced, system prompt never ----

    [Fact]
    public void Catalog_exposes_tools_but_never_the_system_prompt()
    {
        var personas = new PersonaStore();
        var caps = new CapabilityRegistry(new ICapability[]
        {
            new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability(),
            new DataScienceCapability(), new FigmaCapability(),
        });
        var catalog = new ControlCatalog(personas, caps, new IntegrationConfig(), "http://localhost:11434", "qwen3:4b");

        Assert.NotEmpty(catalog.BaseTools());

        var view = catalog.View(personas.Get("software_engineer")!);
        Assert.NotEmpty(view.Tools);
        // Base file/web/memory tools are always present.
        Assert.Contains(view.Tools, t => t.Name == "web_search");

        // The serialised catalogue must not contain any curated system-prompt text.
        var serialised = JsonSerializer.Serialize(catalog.Personas(), Json);
        Assert.DoesNotContain("SENIOR SOFTWARE ENGINEER", serialised);
        Assert.DoesNotContain("systemPrompt", serialised, StringComparison.OrdinalIgnoreCase);
    }

    // ---- ControlHub: reconstructing conversations + runs from the event stream ----

    [Fact]
    public void Hub_reconstructs_conversation_transcript_and_run_from_events()
    {
        var hub = new ControlHub(null, Json);
        void Ev(string e, object d) => hub.Ingest("c1", "chat", e, JsonSerializer.Serialize(d, Json));

        Ev("msg_start", new { id = "0", role = "user" });
        Ev("content", new { id = "0", text = "Find the bug" });
        Ev("msg_end", new { id = "0", text = "Find the bug" });
        Ev("working", new { id = "1", task = "investigate", persona = "software_engineer" });
        Ev("tool_started", new { id = "1", name = "web_search", arguments = "{\"q\":\"x\"}" });
        Ev("tool_completed", new { id = "1", name = "web_search", result = "found it" });
        Ev("working_done", new { id = "1", status = "done" });

        var conv = hub.Conversation("c1");
        Assert.NotNull(conv);
        Assert.Equal("Find the bug", conv!.Title);
        Assert.Equal("idle", conv.Status); // no active task after working_done
        Assert.Contains(conv.Transcript, m => m.Role == "user" && m.Text == "Find the bug");

        var run = Assert.Single(hub.RunsFor("c1"));
        Assert.Equal("done", run.Status);
        Assert.Equal("software_engineer", run.Persona);
        Assert.Contains(run.Steps, s => s.Tool == "web_search" && s.Result == "found it");
    }

    [Fact]
    public void Hub_marks_conversation_waiting_on_a_question()
    {
        var hub = new ControlHub(null, Json);
        void Ev(string e, object d) => hub.Ingest("c2", "slack", e, JsonSerializer.Serialize(d, Json));

        Ev("working", new { id = "1", task = "book flights" });
        Ev("question", new { id = "1", question = "Which date?", options = new[] { "Mon", "Tue" } });

        Assert.Equal("waiting", hub.Conversation("c2")!.Status);
        Assert.Equal("waiting", hub.RunsFor("c2").Single().Status);
    }

    // ---- MemoryStore enumeration + retire ----

    [Fact]
    public void Memory_enumerates_all_scopes_and_retires()
    {
        var path = Path.Combine(TempDir(), "memory.json");
        var mem = new MemoryStore(path, Json);
        mem.Set("location", "home", "London", null);
        mem.Set("destination", "trip", "Lisbon", null, "holiday");

        var all = mem.AllActive();
        Assert.Equal(2, all.Count);

        var london = all.First(f => f.Key == "home");
        Assert.True(mem.Retire(london.Id));
        Assert.DoesNotContain(mem.AllActive(), f => f.Id == london.Id);
        Assert.False(mem.Retire("does-not-exist"));
    }

    // ---- ControlBuckets: sandboxing ----

    [Fact]
    public async Task Buckets_save_list_and_reject_traversal()
    {
        var ws = TempDir();
        var buckets = new ControlBuckets(ws, new PersonaStore());

        // Path-traversal ids are rejected.
        Assert.Null(buckets.ResolveDir("persona", "../../evil"));
        Assert.Null(buckets.ResolveDir("nonsense", "x"));

        // A valid upload lands, lists, downloads, and deletes.
        var bytes = Encoding.UTF8.GetBytes("hello brand");
        var saved = await buckets.SaveAsync("brand", "house", "../escape.txt", new MemoryStream(bytes), CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal("escape.txt", saved!.Name); // name flattened, no traversal

        var brand = buckets.List().First(b => b.Kind == "brand" && b.Id == "house");
        Assert.Contains(brand.Files, f => f.Name == "escape.txt");

        Assert.NotNull(buckets.ResolveFile("brand", "house", "escape.txt"));
        Assert.True(buckets.DeleteFile("brand", "house", "escape.txt"));
        Assert.Null(buckets.ResolveFile("brand", "house", "escape.txt"));
    }
}
