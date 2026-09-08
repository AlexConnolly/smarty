using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Reading a file without swallowing it, and saying what kind of thing was read.
///
/// The window has always been there; what was missing is context about it. "week_of_dinners.pdf — characters
/// 0–4000" doesn't say whether that's extracted prose or the file itself, and for source, characters are the
/// wrong unit entirely — nobody navigates code by byte offset.
/// </summary>
public class ReadFileKindTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string FileWith(string name, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _paths.Add(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Read(string path, int? limit = null)
    {
        var tool = FileTools.ReadFileTool();
        var args = limit is null
            ? $"{{\"path\":{JsonSerializer.Serialize(path)}}}"
            : $"{{\"path\":{JsonSerializer.Serialize(path)},\"limit\":{limit}}}";
        var result = tool.InvokeAsync(new ToolCallArguments(JsonDocument.Parse(args).RootElement),
            CancellationToken.None).GetAwaiter().GetResult();
        return result.Content;
    }

    [Fact]
    public void Source_reports_the_lines_it_covered_not_just_bytes()
    {
        var code = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"var line{i} = {i};"));
        var path = FileWith("Program.cs", code);

        var output = Read(path, limit: 200);

        Assert.Contains("lines 1–", output);
        Assert.Contains("characters 0–200", output);
    }

    [Fact]
    public void A_big_file_comes_back_windowed_with_the_rest_accounted_for()
    {
        // The whole point: the file does not enter the conversation, and the model is told what it's missing and
        // pointed at the tool that finds things rather than invited to page through.
        var big = string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i} of a long file"));
        var path = FileWith("big.log", big);

        var output = Read(path);

        Assert.True(output.Length < big.Length / 2, "the window has to be a small fraction of the file");
        Assert.Contains("not shown", output);
        Assert.Contains("find_in_file", output);
    }

    [Fact]
    public void A_web_page_is_named_as_one_and_comes_back_as_text()
    {
        var path = FileWith("deck.html", "<html><body><h1>A Week of Dinners</h1><p>Monday: gnocchi</p></body></html>");

        var output = Read(path);

        Assert.Contains("(web page)", output);
        Assert.Contains("A Week of Dinners", output);
        Assert.DoesNotContain("<h1>", output); // the markup is stripped, not handed over raw
    }

    [Fact]
    public void An_image_refuses_and_names_the_tool_that_can_see_it()
    {
        // Without this it goes looking for a way to read a screenshot as text — or writes Python to do it.
        var path = FileWith("screenshot.png", "not really a png");

        var output = Read(path);

        Assert.Contains("image", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("describe_image", output);
    }

    [Fact]
    public void A_format_with_no_reader_says_so_plainly()
    {
        var path = FileWith("accounts.xlsx", "PK-not-really");

        Assert.Contains("can't read", Read(path));
    }

    [Fact]
    public void A_small_file_comes_back_whole_with_nothing_hidden()
    {
        var path = FileWith("note.txt", "two lines\nis all there is");

        var output = Read(path);

        Assert.Contains("two lines", output);
        Assert.DoesNotContain("not shown", output);
    }

    public void Dispose()
    {
        foreach (var dir in _paths) try { Directory.Delete(dir, true); } catch { }
    }
}
