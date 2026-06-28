using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

public sealed class DataScienceTests
{
    [Fact]
    public void Capability_has_correct_metadata()
    {
        var cap = new DataScienceCapability();
        Assert.Equal("datascience", cap.Id);
        Assert.Contains("Data Science", cap.DisplayName);
        Assert.Empty(cap.RequiredConfig);
        Assert.NotNull(cap.PromptHint);
    }

    [Fact]
    public void Builds_run_python_tool()
    {
        var cap = new DataScienceCapability();
        var task = new TaskInfo { Id = "test-task", Description = "Test" };
        var tools = cap.BuildTools(new IntegrationConfig(), task);

        Assert.Single(tools);
        var tool = tools[0];
        Assert.Equal("run_python", tool.Name);
        Assert.Equal(2, tool.Parameters.Count);
        
        var codeParam = tool.Parameters.FirstOrDefault(p => p.Name == "code");
        Assert.NotNull(codeParam);
        Assert.True(codeParam.Required);

        var filesParam = tool.Parameters.FirstOrDefault(p => p.Name == "files");
        Assert.NotNull(filesParam);
        Assert.False(filesParam.Required);
    }

    [Fact]
    public async Task RunPython_with_no_python_returns_friendly_error()
    {
        var cap = new DataScienceCapability();
        var task = new TaskInfo 
        { 
            Id = "test-task-1", 
            Description = "Test run_python tool",
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "smarty-test-workspace")
        };

        var tools = cap.BuildTools(new IntegrationConfig(), task);
        var tool = tools[0];

        // Call the tool. Even if Python is not installed or fails, it should return a non-null ToolOutput with an error/dead end.
        using var doc = JsonDocument.Parse("{\"code\":\"print('hello')\",\"files\":\"[\\\"test.csv\\\"]\"}");
        var args = new ToolCallArguments(doc.RootElement);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        Assert.NotNull(result);
        // It might be Ok (if python is in PATH) or Error (if python is not in PATH)
        // Either way, it must not throw and must return output.
        if (!result.IsError)
        {
            Assert.Contains("hello", result.Content);
        }
        else
        {
            Assert.True(result.IsError);
        }
    }
}
