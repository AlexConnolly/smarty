using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

public class MemoryIsolationTests
{
    private readonly string _tempPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public MemoryIsolationTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"smarty_test_memory_{Guid.NewGuid():N}.json");
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    private static ToolCallArguments MakeArgs(Dictionary<string, object?> dict)
    {
        var jsonStr = JsonSerializer.Serialize(dict);
        var doc = JsonDocument.Parse(jsonStr);
        return new ToolCallArguments(doc.RootElement);
    }

    [Fact]
    public async Task When_PersonalMemoryEnabled_Is_True_Reads_And_Writes_Succeed()
    {
        var store = new MemoryStore(_tempPath, _jsonOptions);
        var personalScope = "user:U12345";

        var setTool = MemoryTools.SetPersonalTool(store, personalScope, personalMemoryEnabled: true);
        var searchTool = MemoryTools.SearchPersonalTool(store, personalScope, personalMemoryEnabled: true);

        // 1. Write personal memory
        var setArgs = MakeArgs(new Dictionary<string, object?>
        {
            ["type"] = "food",
            ["key"] = "diet",
            ["value"] = "vegan",
            ["shared"] = false
        });
        var writeRes = await setTool.InvokeAsync(setArgs, CancellationToken.None);
        Assert.Contains("diet: vegan.", writeRes.Content);

        // 2. Search personal memory
        var searchArgs = MakeArgs(new Dictionary<string, object?>
        {
            ["query"] = "diet"
        });
        var searchRes = await searchTool.InvokeAsync(searchArgs, CancellationToken.None);
        Assert.Contains("diet: vegan", searchRes.Content);
        Assert.DoesNotContain("Personal memory is not accessible", searchRes.Content);

        // Clean up
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public async Task When_PersonalMemoryEnabled_Is_False_Reads_And_Writes_Are_Restricted()
    {
        var store = new MemoryStore(_tempPath, _jsonOptions);
        var personalScope = "user:U12345";

        // Seed some personal facts manually or via direct store so we can test reading
        store.Set("food", "diet", "vegan", null, personalScope);
        store.Set("food", "snack", "popcorn", null, null); // shared fact

        var setTool = MemoryTools.SetPersonalTool(store, personalScope, personalMemoryEnabled: false);
        var searchTool = MemoryTools.SearchPersonalTool(store, personalScope, personalMemoryEnabled: false);

        // 1. Write personal memory - should be blocked
        var setArgs = MakeArgs(new Dictionary<string, object?>
        {
            ["type"] = "food",
            ["key"] = "snack",
            ["value"] = "chips",
            ["shared"] = false
        });
        var writeRes = await setTool.InvokeAsync(setArgs, CancellationToken.None);
        Assert.Contains("personal memories cannot be recorded in public/group channels", writeRes.Content);

        // 2. Write shared memory - should succeed
        var setSharedArgs = MakeArgs(new Dictionary<string, object?>
        {
            ["type"] = "food",
            ["key"] = "snack",
            ["value"] = "chips",
            ["shared"] = true
        });
        var writeSharedRes = await setTool.InvokeAsync(setSharedArgs, CancellationToken.None);
        Assert.Contains("chips", writeSharedRes.Content);

        // 3. Search personal memory - should only return shared facts and append warning
        var searchArgs = MakeArgs(new Dictionary<string, object?>
        {
            ["query"] = "diet"
        });
        var searchRes = await searchTool.InvokeAsync(searchArgs, CancellationToken.None);
        Assert.DoesNotContain("diet: vegan", searchRes.Content); // personal fact hidden!
        Assert.Contains("Personal memory is not accessible in public/group channels", searchRes.Content);

        // 4. Search shared memory - should find the shared chips fact
        var searchSharedArgs = MakeArgs(new Dictionary<string, object?>
        {
            ["query"] = "chips"
        });
        var searchSharedRes = await searchTool.InvokeAsync(searchSharedArgs, CancellationToken.None);
        Assert.Contains("snack: chips", searchSharedRes.Content);

        // Clean up
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }
}
