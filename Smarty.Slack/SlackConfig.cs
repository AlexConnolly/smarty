namespace Smarty.Slack;

/// <summary>
/// Everything Smarty.Slack needs to run, read from environment variables so no secrets live in the repo.
/// Two Slack tokens are required (a bot token for the Web API, an app-level token for Socket Mode); the
/// rest have sensible defaults. Set the company name so Smarty knows who it's working with.
/// </summary>
public sealed class SlackConfig
{
    /// <summary>Bot user OAuth token (starts <c>xoxb-</c>) — authorises chat.postMessage, users.info, etc.</summary>
    public required string BotToken { get; init; }

    /// <summary>App-level token (starts <c>xapp-</c>, scope <c>connections:write</c>) — opens the Socket Mode
    /// WebSocket. Socket Mode means no public URL is needed; the bot dials out from this machine.</summary>
    public required string AppToken { get; init; }

    /// <summary>The company/team Smarty is embedded in — injected into the system prompt so it knows who it's
    /// working with and addresses people as colleagues.</summary>
    public string CompanyName { get; init; } = "the team";

    /// <summary>Optional extra context about the company/workspace for the system prompt (what you do, tone).</summary>
    public string? CompanyContext { get; init; }

    public string OllamaBaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "qwen3.5:latest";

    /// <summary>Isolated data directory for Slack — its own memory/projects/training, NEVER the web app's
    /// real data dir. Defaults to a sibling folder so a test run can't touch the user's personal data.</summary>
    public string DataDir { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "slack-data");

    /// <summary>Base URL of the Smarty.Api command-centre hub to forward live conversation events to, so
    /// Smarty.Control can show Slack threads streaming alongside the web chat. Null = don't forward.</summary>
    public string? ControlHubUrl { get; init; }

    /// <summary>Shared token sent as X-Control-Token on forwarded events (must match the API's
    /// SMARTY_CONTROL_TOKEN). Optional, but recommended if the API isn't purely local.</summary>
    public string? ControlToken { get; init; }

    public static SlackConfig FromEnvironment()
    {
        string Require(string key) =>
            Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException(
                $"Missing required environment variable {key}. Set the Slack tokens before starting " +
                "(see Smarty.Slack/README.md).");

        string? Opt(string key) => Environment.GetEnvironmentVariable(key);

        return new SlackConfig
        {
            BotToken = Require("SLACK_BOT_TOKEN"),
            AppToken = Require("SLACK_APP_TOKEN"),
            CompanyName = Opt("SMARTY_COMPANY_NAME") is { Length: > 0 } c ? c : "the team",
            CompanyContext = Opt("SMARTY_COMPANY_CONTEXT"),
            OllamaBaseUrl = Opt("OLLAMA_BASE_URL") is { Length: > 0 } u ? u : "http://localhost:11434",
            Model = Opt("SMARTY_MODEL") is { Length: > 0 } m ? m : "qwen3.5:latest",
            DataDir = Opt("SMARTY_SLACK_DATA_DIR") is { Length: > 0 } d ? d
                : Path.Combine(AppContext.BaseDirectory, "slack-data"),
            ControlHubUrl = Opt("SMARTY_CONTROL_HUB_URL"),
            ControlToken = Opt("SMARTY_CONTROL_TOKEN"),
        };
    }
}
