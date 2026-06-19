using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// Append-only JSONL capture of real model interactions + user feedback, so a fine-tune dataset builds
/// passively from actual usage. Each interaction record is already close to a training example
/// (system + messages + tools → assistant output); feedback records label which turns were good/bad and
/// join back by (session, msg_id). Best-effort: it must NEVER throw into the request path.
/// </summary>
public sealed class TrainingLog
{
    private readonly object _lock = new();
    private readonly string _dir;
    private readonly JsonSerializerOptions _json;

    public TrainingLog(string dir, JsonSerializerOptions json)
    {
        _dir = dir;
        _json = json;
        try { Directory.CreateDirectory(_dir); } catch { /* ignore */ }
    }

    /// <summary>Log one model inference (orchestrator turn or a worker run) as a training-shaped record.</summary>
    public void Interaction(object record) => Write("interactions", record);

    /// <summary>Log a user thumbs up/down on an assistant message.</summary>
    public void Feedback(object record) => Write("feedback", record);

    private void Write(string stream, object record)
    {
        try
        {
            string line = JsonSerializer.Serialize(record, _json);
            string path = Path.Combine(_dir, $"{stream}-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            lock (_lock) File.AppendAllText(path, line + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[traininglog] {stream} write failed: {ex.Message}");
        }
    }
}
