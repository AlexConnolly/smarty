using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Smarty.Agents;

namespace Smarty.Slack;

/// <summary>
/// The pre-processor that decides whether Smarty should speak. Once it's been @mentioned in a thread it
/// "starts listening", but it must not jump in on every subsequent line — people are mostly talking to
/// each other. For each new untagged message in a listening thread, this runs ONE fast, schema-constrained
/// classification (think:false, no tools, tiny budget) returning <c>{respond, reason}</c> — the same forced-
/// JSON trick the orchestrator already uses for worker outcomes, so the verdict is a real field, not prose.
/// An explicit @mention skips this entirely (always respond). On any error it stays quiet (under-responding
/// in a busy channel is far less annoying than barging in).
/// </summary>
public sealed class EngagementQualifier
{
    private readonly IModelProvider _provider;
    private readonly string _model;

    public EngagementQualifier(IModelProvider provider, string model)
    {
        _provider = provider;
        _model = model;
    }

    private static JsonNode Schema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["respond"] = new JsonObject { ["type"] = "boolean" },
            ["reason"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("respond"),
    };

    /// <summary>True if the new message is aimed at Smarty / wants it to act, vs. colleagues talking among
    /// themselves. <paramref name="recent"/> is the last few thread lines (author: text) for context.</summary>
    public async Task<bool> ShouldRespondAsync(
        IReadOnlyList<string> recent, string author, string text, CancellationToken ct = default)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("You are \"Smarty\", an assistant present in a Slack thread. You were tagged earlier, so " +
                      "you're following along. Decide whether the LATEST message is directed at you or is asking " +
                      "you to do/answer something — versus the people simply talking among themselves.\n");
            sb.Append("Respond ONLY when it's genuinely for you: a question or request you should answer or act " +
                      "on, a follow-up to what you just said, or someone clearly addressing you. Do NOT respond to " +
                      "colleagues chatting to each other, acknowledgements, or asides. When unsure, do not respond.\n\n");
            if (recent.Count > 0)
            {
                sb.Append("Recent thread:\n");
                foreach (var line in recent.TakeLast(8)) sb.Append(line).Append('\n');
                sb.Append('\n');
            }
            sb.Append($"LATEST message:\n{author}: {text}");

            var request = new ModelRequest
            {
                Model = _model,
                Messages = new[] { Message.User(sb.ToString()) },
                Think = false,
                ResponseFormat = Schema(),
                MaxOutputTokens = 80,
                TurnTimeout = TimeSpan.FromSeconds(20),
            };

            var response = await _provider.CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Content)) return false;

            using var doc = JsonDocument.Parse(response.Content);
            return doc.RootElement.TryGetProperty("respond", out var r)
                   && r.ValueKind is JsonValueKind.True or JsonValueKind.False
                   && r.GetBoolean();
        }
        catch
        {
            return false; // never barge in on an error
        }
    }
}
