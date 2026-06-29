using System.Text;
using System.Threading;

namespace Smarty.Agents;

/// <summary>
/// Two file tools for a worker, mirroring <see cref="WebResearch"/>'s shape but pointed at the local disk:
///
/// 1. <c>read_file(path, offset, limit)</c> — extracts a file's text (via <see cref="FileText"/>) and returns
///    a BOUNDED window of it, so a big document is read in sensible pages rather than dumped whole.
/// 2. <c>file_summary(path, question)</c> — the file analogue of get_page_answer: extract the text, chunk it
///    with overlap, rank the chunks against the question (BM25), and have the model write a short grounded
///    answer from the best excerpts. Returns the ANSWER, already distilled — ideal for "tldr this".
///
/// Both reuse <see cref="WebResearch"/>'s chunking/ranking internals, so the two surfaces stay consistent.
/// </summary>
public static class FileTools
{
    private const int DefaultWindow = 4000; // chars returned by one read_file call when no limit is given
    private const int SummaryBudgetChars = 24000; // text fed to a single summary call (~6k tokens, fits num_ctx)

    // ---- read_file ----------------------------------------------------------------------------

    public static AgentTool ReadFileTool(string name = "read_file")
    {
        return new AgentTool(
            name,
            "Reads the text of a local file (text-based formats and PDFs) and returns a portion of it. Use " +
            "offset/limit to page through a large file rather than reading it all at once.",
            new[]
            {
                ToolParameter.String("path", "The path of the file to read.", required: true),
                ToolParameter.Integer("offset", "Character offset to start reading from. Defaults to 0.", required: false),
                ToolParameter.Integer("limit", $"How many characters to return. Defaults to {DefaultWindow}.", required: false),
            },
            (args, _) => Task.FromResult(ReadFile(args)));
    }

    private static ToolOutput ReadFile(ToolCallArguments args)
    {
        string path = args.GetString("path").Trim();
        var extracted = FileText.Extract(path);
        if (!extracted.Ok)
            // A wrong format / scanned PDF won't read on a retry — route the model elsewhere rather than loop.
            return ToolOutput.DeadEnd(extracted.Reason ?? "Couldn't read that file.");

        string text = extracted.Text;
        if (text.Length == 0)
            return ToolOutput.Ok($"'{Path.GetFileName(path)}' is empty.");

        int offset = Math.Clamp(args.GetInt("offset", 0), 0, text.Length);
        int limit = Math.Clamp(args.GetInt("limit", DefaultWindow), 1, 50_000);
        int take = Math.Min(limit, text.Length - offset);
        string window = text.Substring(offset, take);
        int end = offset + take;

        var header = new StringBuilder($"{Path.GetFileName(path)} — characters {offset}–{end} of {text.Length}");
        if (end < text.Length)
            header.Append($" (more remains; call read_file again with offset={end} to continue)");
        return ToolOutput.Ok($"{header}\n\n{window}");
    }

    // ---- file_summary -------------------------------------------------------------------------

    public static AgentTool SummaryTool(IModelProvider provider, string model, string name = "file_summary")
    {
        return new AgentTool(
            name,
            "Reads a local file (text-based formats and PDFs) and answers a question about it — or summarises " +
            "it if no question is given. Returns a short answer grounded in the file's contents.",
            new[]
            {
                ToolParameter.String("path", "The path of the file to read.", required: true),
                ToolParameter.String("question", "What to find out from the file. Omit to get a general summary.", required: false),
            },
            (args, ct) => SummaryAsync(args, provider, model, ct));
    }

    private static async Task<ToolOutput> SummaryAsync(
        ToolCallArguments args, IModelProvider provider, string model, CancellationToken ct)
    {
        string path = args.GetString("path").Trim();
        string? rawQuestion = args.GetStringOrNull("question")?.Trim();
        bool isSummary = string.IsNullOrEmpty(rawQuestion);
        string question = isSummary ? "Summarise the main points of this document." : rawQuestion!;

        var extracted = FileText.Extract(path);
        if (!extracted.Ok)
            return ToolOutput.DeadEnd(extracted.Reason ?? "Couldn't read that file.");
        if (extracted.Text.Length == 0)
            return ToolOutput.Ok($"'{Path.GetFileName(path)}' is empty — nothing to summarise.");

        var chunks = WebResearch.Chunk(extracted.Text, size: 1200, overlap: 200);

        if (isSummary)
        {
            // A summary is ONE model call. A small file goes in whole; a large one is distilled to an
            // evenly-sampled digest that fits a single call's budget. The old path map-reduced the entire
            // document — up to 16 sequential ~6k-token calls on a local backend that serialises requests,
            // which is what turned a summary into minutes. Sampling keeps coverage (beginning→end) at a
            // fraction of the cost.
            bool sampled = extracted.Text.Length > SummaryBudgetChars;
            string body = sampled ? BuildDigest(chunks, SummaryBudgetChars) : extracted.Text;
            string note = sampled
                ? " The text below is a set of excerpts sampled across a long document (gaps marked […])."
                : "";

            var singleRequest = new ModelRequest
            {
                Model = model,
                SystemPrompt = "You are a concise summarizer. Write a clean, cohesive summary of the following document. " +
                    "Focus on main topics, key decisions, metrics, and outcomes. Keep it brief and professional." + note,
                Messages = new[]
                {
                    Message.User($"Document {Path.GetFileName(path)}:\n\n{body}\n\nWrite the summary:"),
                },
                Think = false,
                RepeatPenalty = 1.0,
                MaxOutputTokens = 500,
                TurnTimeout = TimeSpan.FromSeconds(90),
            };
            try
            {
                var response = await provider.CompleteAsync(singleRequest, ct).ConfigureAwait(false);
                var answer = response.Content?.Trim() ?? "";
                return answer.Length > 0
                    ? ToolOutput.Ok($"{answer}\n\n(source: {Path.GetFileName(path)})")
                    : ToolOutput.Error($"Read '{Path.GetFileName(path)}' but couldn't produce a summary.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ToolOutput.Error($"Read '{Path.GetFileName(path)}' but couldn't summarise it: {ex.Message}");
            }
        }

        // For a specific question, rank chunks by relevance using BM25 and take the top 5 excerpts.
        var top = WebResearch.RankByQuestion(chunks, question, take: 5);
        if (top.Count == 0) top = chunks.Take(4).ToList();
        if (top.Count == 0)
            return ToolOutput.Ok($"Read '{Path.GetFileName(path)}', but found no readable content to work from.");

        var excerpts = new StringBuilder();
        for (int i = 0; i < top.Count; i++)
            excerpts.Append($"[Excerpt {i + 1}]\n{top[i]}\n\n");

        var request = new ModelRequest
        {
            Model = model,
            SystemPrompt =
                "You answer using ONLY the excerpts provided from a document. Be concise, factual, and " +
                "specific — quote concrete figures, names, or dates when they appear. If the excerpts don't " +
                "contain the answer, say so plainly; do not invent anything.",
            Messages = new[]
            {
                Message.User($"Question: {question}\n\nExcerpts from {Path.GetFileName(path)}:\n\n" +
                             $"{excerpts.ToString().TrimEnd()}\n\nAnswer the question:"),
            },
            Think = false,
            RepeatPenalty = 1.0,
            MaxOutputTokens = 500,
            TurnTimeout = TimeSpan.FromSeconds(60),
        };

        try
        {
            var response = await provider.CompleteAsync(request, ct).ConfigureAwait(false);
            var answer = response.Content?.Trim() ?? "";
            return answer.Length > 0
                ? ToolOutput.Ok($"{answer}\n\n(source: {Path.GetFileName(path)})")
                : ToolOutput.Error($"Read '{Path.GetFileName(path)}' but couldn't produce an answer from it.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolOutput.Error($"Read '{Path.GetFileName(path)}' but couldn't summarise it: {ex.Message}");
        }
    }

    // ---- scoped write / list / send (thread-rooted) -------------------------------------------
    //
    // These three tools are each CONSTRUCTED bound to one directory (a single conversation's file area). They
    // flatten any path to a bare file name under that root, so a worker holding them can only ever touch THIS
    // conversation's files — never another thread's. That structural rooting (not a prompt rule) is the
    // context-scoping boundary: a worker is handed tools for its own thread and has no handle to any other.

    /// <summary>Reduce any caller-supplied path to a safe bare file name (no directories, no traversal),
    /// sanitising characters the filesystem rejects. Combined with a fixed root, escaping is impossible.</summary>
    private static string SafeFileName(string name) =>
        string.Concat(Path.GetFileName(name.Trim()).Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>write_file(name, content): author a text file in this conversation's area (create/overwrite).</summary>
    public static AgentTool WriteFileTool(string rootDir, string name = "write_file")
    {
        return new AgentTool(
            name,
            "Writes a text file into THIS conversation's file area (creating it, or overwriting one of the same " +
            "name). Use it to produce a document, note, or data file you can then send with send_file. The file " +
            "stays scoped to this conversation.",
            new[]
            {
                ToolParameter.String("name", "File name to write, e.g. \"summary.md\". Kept to this conversation.", required: true),
                ToolParameter.String("content", "The full text content of the file.", required: true),
            },
            (args, _) =>
            {
                string fileName = SafeFileName(args.GetString("name"));
                if (fileName.Length == 0) return Task.FromResult(ToolOutput.Error("A file name is required."));
                string content = args.GetStringOrNull("content") ?? "";
                try
                {
                    Directory.CreateDirectory(rootDir);
                    File.WriteAllText(Path.Combine(rootDir, fileName), content);
                    return Task.FromResult(ToolOutput.Ok(
                        $"Wrote {fileName} ({content.Length} chars) to this conversation. Send it with send_file(\"{fileName}\")."));
                }
                catch (Exception ex) { return Task.FromResult(ToolOutput.Error($"Couldn't write {fileName}: {ex.Message}")); }
            });
    }

    /// <summary>A read-only file bucket mounted alongside a conversation's own files — e.g. a global company
    /// area or a persona's brand kit. Its files are listed (with their real on-disk path) so a worker can read
    /// them with read_file or reference them by path inside run_python; they are never writable or sendable
    /// from here (writes always land in the conversation).</summary>
    public sealed record FileMount(string Label, string Dir);

    /// <summary>list_files(): the files in THIS conversation (shared in OR written here), plus any read-only
    /// buckets mounted for this worker (a global area, the persona's brand kit). Conversation files are shown by
    /// bare name (use them with write/send); bucket files are shown with their full path (read-only — reference
    /// that path in read_file or run_python).</summary>
    public static AgentTool ListFilesTool(string rootDir, IReadOnlyList<FileMount>? mounts = null, string name = "list_files")
    {
        bool hasMounts = mounts is { Count: > 0 };
        string desc = hasMounts
            ? "Lists the files available to you: THIS conversation's files (shared here or written here), plus " +
              "read-only reference buckets (e.g. a brand kit). Conversation files are referred to by bare name; " +
              "bucket files are read-only and shown with their full path — use that path with read_file or inside " +
              "run_python (e.g. to place a logo). You cannot write to or send a bucket file."
            : "Lists the files available in THIS conversation — files the user shared here and files you've " +
              "written here. Only this conversation's files are ever visible; nothing from any other thread.";
        return new AgentTool(
            name,
            desc,
            Array.Empty<ToolParameter>(),
            (_, __) =>
            {
                try
                {
                    var sb = new StringBuilder();
                    var convo = Directory.Exists(rootDir)
                        ? new DirectoryInfo(rootDir).GetFiles().OrderBy(f => f.Name).ToList()
                        : new List<FileInfo>();
                    sb.Append("Files in this conversation:\n");
                    if (convo.Count == 0) sb.Append("- (none yet)\n");
                    else foreach (var f in convo) sb.Append($"- {f.Name} ({HumanSize(f.Length)})\n");

                    if (hasMounts)
                        foreach (var m in mounts!)
                        {
                            if (!Directory.Exists(m.Dir)) continue;
                            var files = new DirectoryInfo(m.Dir).GetFiles("*", SearchOption.AllDirectories)
                                .OrderBy(f => f.FullName).ToList();
                            if (files.Count == 0) continue;
                            sb.Append($"\n{m.Label} (read-only — reference by full path):\n");
                            foreach (var f in files) sb.Append($"- {f.FullName} ({HumanSize(f.Length)})\n");
                        }

                    sb.Append("\nUse read_file / file_summary to read one, or send_file to send a conversation file to the user.");
                    return Task.FromResult(ToolOutput.Ok(sb.ToString().TrimEnd()));
                }
                catch (Exception ex) { return Task.FromResult(ToolOutput.Error($"Couldn't list files: {ex.Message}")); }
            });
    }

    /// <summary>send_file(name, caption): send one of THIS conversation's files back to the user. The
    /// <paramref name="emit"/> callback hands the resolved path to the host (which uploads it into the
    /// thread); it returns false if the file couldn't be queued.</summary>
    public static AgentTool SendFileTool(string rootDir, Func<string, string?, bool> emit, string name = "send_file")
    {
        return new AgentTool(
            name,
            "Sends a file from THIS conversation back to the user (uploads it into the thread). Use the exact " +
            "name shown by list_files. You can only send files that exist in this conversation — files the user " +
            "shared here or that you wrote here.",
            new[]
            {
                ToolParameter.String("name", "Name of the file to send, exactly as shown by list_files.", required: true),
                ToolParameter.String("caption", "Optional short message to send alongside the file.", required: false),
            },
            (args, _) =>
            {
                string fileName = SafeFileName(args.GetString("name"));
                string path = Path.Combine(rootDir, fileName);
                if (fileName.Length == 0 || !File.Exists(path))
                    return Task.FromResult(ToolOutput.DeadEnd(
                        $"There's no file called '{fileName}' in this conversation. Use list_files to see what's here."));
                string? caption = args.GetStringOrNull("caption")?.Trim();
                bool ok = emit(path, string.IsNullOrWhiteSpace(caption) ? null : caption);
                return Task.FromResult(ok
                    ? ToolOutput.Ok($"Sent {fileName} to the user.")
                    : ToolOutput.Error($"Couldn't send {fileName} just now."));
            });
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    // Distil a long document into excerpts that fit one summary call by sampling chunks evenly across it.
    // Even spacing (always including the first chunk) gives coverage of the whole document — beginning,
    // middle and end — rather than just the leading N characters, while keeping the prompt to one cheap call.
    private static string BuildDigest(IReadOnlyList<string> chunks, int budgetChars)
    {
        if (chunks.Count == 0) return "";

        int perChunk = chunks[0].Length > 0 ? chunks[0].Length : 1200;
        int want = Math.Max(1, budgetChars / perChunk);
        if (want >= chunks.Count)
            return string.Join("\n\n", chunks);

        var picked = new List<string>(want);
        double step = (double)chunks.Count / want;
        for (int i = 0; i < want; i++)
            picked.Add(chunks[Math.Min(chunks.Count - 1, (int)(i * step))]);

        return string.Join("\n\n[…]\n\n", picked);
    }
}
