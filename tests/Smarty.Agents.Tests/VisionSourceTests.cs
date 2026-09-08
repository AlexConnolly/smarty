using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Working out WHICH image is meant. The model may name one three ways — the bare file name it was told the user
/// attached, an /api/media/… address of something stored earlier, or a full path another tool wrote — and only one
/// of those used to resolve, so "describe this screenshot" failed on the most ordinary request there is.
/// </summary>
public class VisionSourceTests : IDisposable
{
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"vision-files-{Guid.NewGuid():N}");
    private readonly string _media = Path.Combine(Path.GetTempPath(), $"vision-media-{Guid.NewGuid():N}");

    public VisionSourceTests()
    {
        Directory.CreateDirectory(_files);
        Directory.CreateDirectory(_media);
        File.WriteAllText(Path.Combine(_files, "Screenshot 2026-07-17 225628.png"), "not really a png");
        File.WriteAllText(Path.Combine(_media, "abc123.jpg"), "nor this");
    }

    private string? Resolve(string src) => Vision.ResolveLocal(src, _media, _files);

    [Fact]
    public void An_attached_file_resolves_by_the_name_the_model_was_given()
    {
        // The regression, exactly: a name with spaces, in the conversation's own library, and nothing else to go on.
        var resolved = Resolve("Screenshot 2026-07-17 225628.png");
        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void A_stored_media_address_resolves_to_the_media_directory()
    {
        Assert.Equal(Path.Combine(_media, "abc123.jpg"), Resolve("/api/media/abc123.jpg"));
        // Absolute form of the same address — a worker that saw it in a page reads it back off disk, not over HTTP.
        Assert.Equal(Path.Combine(_media, "abc123.jpg"), Resolve("http://localhost:5179/api/media/abc123.jpg"));
        // And with a cache-buster on the end.
        Assert.Equal(Path.Combine(_media, "abc123.jpg"), Resolve("/api/media/abc123.jpg?v=2"));
    }

    [Fact]
    public void A_remote_url_is_left_alone_so_it_gets_fetched()
    {
        // Must not be mistaken for a local file, even though its last segment looks like a name in our directories.
        Assert.Null(Resolve("https://example.com/photos/abc123.jpg"));
        Assert.Null(Resolve("https://hotel.example/img/Screenshot 2026-07-17 225628.png"));
    }

    [Fact]
    public void A_name_cannot_walk_out_of_the_directories_it_is_looked_up_in()
    {
        // Only the file NAME is used against each candidate directory, so traversal collapses to a lookup that
        // simply doesn't exist — never a read of something outside.
        Assert.Null(Resolve("../../../Windows/System32/config/SAM"));
        Assert.Null(Resolve("..\\..\\secrets.png"));
    }

    [Fact]
    public void Something_that_is_nowhere_resolves_to_nothing()
    {
        Assert.Null(Resolve("no-such-image.png"));
        Assert.Null(Resolve(""));
    }

    [Fact]
    public void A_full_path_is_taken_as_given()
    {
        // What a tool that wrote the file hands back — matching what read_file and file_summary already accept.
        var path = Path.Combine(_files, "Screenshot 2026-07-17 225628.png");
        Assert.Equal(path, Resolve(path));
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _files, _media })
            try { Directory.Delete(dir, true); } catch { }
    }
}
