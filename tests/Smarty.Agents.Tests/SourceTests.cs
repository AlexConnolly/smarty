using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Granting a folder, and the boundary around it.
///
/// <para>
/// A source makes every matching file under one folder readable by anything that can reach the API — through the
/// tunnel included. The containment is the whole safety story, so it is what gets tested: upwards, sideways, and by
/// extension.
/// </para>
/// </summary>
public class SourceTests
{
    private static (SourceStore Store, string Root) Store()
    {
        var root = Path.Combine(Path.GetTempPath(), $"src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return (new SourceStore(Path.Combine(root, "sources.json"), new System.Text.Json.JsonSerializerOptions()), root);
    }

    [Fact]
    public void A_granted_folder_lists_the_files_in_it_newest_first()
    {
        var (store, root) = Store();
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);

        File.WriteAllText(Path.Combine(photos, "old.jpg"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(photos, "old.jpg"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(photos, "new.png"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(photos, "new.png"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        // Not an allowed type, so it should not appear at all.
        File.WriteAllText(Path.Combine(photos, "secrets.env"), "TOKEN=1");

        var (source, error) = store.Add("Photos", photos);
        Assert.Null(error);

        var files = store.Files(source!);
        Assert.Equal(new[] { "new.png", "old.jpg" }, files.Select(f => f.Name).ToArray());
        Assert.DoesNotContain(files, f => f.Name.EndsWith(".env"));
        // The url is on our own origin and serves the file, which is what lets an <img> point straight at it.
        Assert.StartsWith("/api/sources/photos/file?path=", files[0].Url);
    }

    [Fact]
    public void A_path_that_walks_out_of_the_folder_is_refused()
    {
        var (store, root) = Store();
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "in.jpg"), "x");
        // A real file just outside the grant, which is exactly what an escape would be reaching for.
        File.WriteAllText(Path.Combine(root, "outside.jpg"), "x");

        var (source, _) = store.Add("Photos", photos);

        Assert.NotNull(store.Resolve(source!, "in.jpg"));
        Assert.Null(store.Resolve(source!, "../outside.jpg"));
        Assert.Null(store.Resolve(source!, @"..\outside.jpg"));
        Assert.Null(store.Resolve(source!, "subdir/../../outside.jpg"));
    }

    [Fact]
    public void A_type_outside_the_list_is_refused_even_by_name()
    {
        var (store, root) = Store();
        var docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "photo.jpg"), "x");
        File.WriteAllText(Path.Combine(docs, "accounts.xlsx"), "x");

        // Granted for images only. Guessing the spreadsheet's name must not be enough to read it.
        var (source, _) = store.Add("Docs", docs, new[] { "jpg", "png" });

        Assert.NotNull(store.Resolve(source!, "photo.jpg"));
        Assert.Null(store.Resolve(source!, "accounts.xlsx"));
    }

    [Fact]
    public void A_whole_drive_or_home_directory_is_too_much_to_grant()
    {
        var (store, _) = Store();

        var drive = Path.GetPathRoot(Path.GetTempPath())!;
        var (source, error) = store.Add("Everything", drive);
        Assert.Null(source);
        Assert.Contains("drive", error);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home is { Length: > 0 })
        {
            var (also, why) = store.Add("Home", home);
            Assert.Null(also);
            Assert.Contains("home directory", why);
        }
    }

    [Fact]
    public void A_folder_that_isnt_there_is_a_refusal_rather_than_an_empty_source()
    {
        var (store, root) = Store();
        var (source, error) = store.Add("Ghost", Path.Combine(root, "nope"));

        Assert.Null(source);
        Assert.Contains("no folder", error);
    }

    [Fact]
    public void With_nothing_granted_it_still_says_what_the_mechanism_is()
    {
        // The hole that cost seven rebuilds. Told nothing, the builder wrote its own photo server, pointed a panel at
        // http://localhost:8765 and had no shell to start it with — so the panel fetched a dead port for ever.
        var (store, _) = Store();

        var note = store.Describe();

        Assert.NotEqual("", note);
        Assert.Contains("no folder has been granted", note);
        Assert.Contains("source:", note);          // how it works once granted
        Assert.Contains("ask", note);              // granting is the user's action
        Assert.Contains("Do NOT write one", note); // and the failure mode, named
    }

    [Fact]
    public void The_model_is_told_which_sources_exist_and_never_asked_to_go_looking()
    {
        var (store, root) = Store();
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        store.Add("Photos", photos);

        var note = store.Describe();

        Assert.Contains("source:photos", note);
        Assert.Contains("internal", note);
        // The important half: a folder that hasn't been granted is the user's decision, not something to hunt for.
        Assert.Contains("do not go looking", note);
    }
}
