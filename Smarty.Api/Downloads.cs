using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Fetching a file off the web and putting it where the other file tools can see it.
/// <para>
/// This exists because a worker asked to gather pictures spent twenty-five calls inventing it: a directory made
/// through the shell, a User-Agent string typed out by hand, Invoke-WebRequest nine times, and an async fetch
/// tried from inside the page. It had worked out that it needed to look like a browser — it just had no tool that
/// was one. A capability a model keeps rebuilding badly under pressure is a missing tool, not a discipline
/// problem.
/// </para>
/// <para>
/// So the headers here are a browser's, because a bare client is refused by a great deal of the web and the
/// referer is what image CDNs check. Everything lands in the thread's own files directory under a name derived
/// from the URL, which makes it immediately readable by read_file, usable in a presentation, and deliverable.
/// </para>
/// </summary>
public static class Downloads
{
    private const long MaxBytes = 64L * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    public static AgentTool Tool(string directory) => new(
        "download_file",
        "Download a file from a URL into this conversation's files — an image for a presentation, a PDF, a " +
        "spreadsheet, anything you need on disk. Returns the local name to use afterwards. It requests as a " +
        "browser does, so sites that refuse a plain script generally serve this. Use it instead of the shell: " +
        "hand-rolled download commands are how a run burns twenty calls and finishes with nothing.",
        new[]
        {
            ToolParameter.String("url", "The file's URL."),
            ToolParameter.String(
                "name", "Local file name to save as. Optional — taken from the URL when omitted.", required: false),
            ToolParameter.String(
                "referer",
                "The page this file was found on. Optional, but some image servers only serve their own pages.",
                required: false),
        },
        async (args, ct) =>
        {
            var url = args.GetStringOrNull("url")?.Trim();
            if (string.IsNullOrWhiteSpace(url)) return ToolOutput.Error("download_file needs a url.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return ToolOutput.DeadEnd($"\"{url}\" isn't an http(s) URL, so there's nothing to download.");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                // A real browser's headers. Sites reject the obviously-automated, and an image CDN frequently
                // serves only when the referer is a page of its own.
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/128.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
                var referer = args.GetStringOrNull("referer")?.Trim();
                request.Headers.TryAddWithoutValidation("Referer",
                    string.IsNullOrWhiteSpace(referer) ? uri.GetLeftPart(UriPartial.Authority) : referer);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return ToolOutput.DeadEnd(
                        $"{uri.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}. That address " +
                        "won't give you the file — find another source rather than retrying it.");

                if (response.Content.Headers.ContentLength is > MaxBytes)
                    return ToolOutput.DeadEnd($"That file is {response.Content.Headers.ContentLength / 1_048_576}MB, too big to keep.");

                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length == 0) return ToolOutput.Error($"{uri.Host} returned an empty file.");
                if (bytes.Length > MaxBytes) return ToolOutput.DeadEnd("That file is too big to keep.");

                var mime = response.Content.Headers.ContentType?.MediaType ?? "";
                var name = SafeName(args.GetStringOrNull("name"), uri, mime);

                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, ct).ConfigureAwait(false);

                var size = bytes.Length >= 1_048_576
                    ? $"{bytes.Length / 1_048_576.0:0.#}MB"
                    : $"{Math.Max(1, bytes.Length / 1024)}KB";
                return ToolOutput.Ok(
                    $"Saved as {name} ({size}{(mime.Length > 0 ? $", {mime}" : "")}). Refer to it by that name — " +
                    "read_file, build_presentation and the deliverables all take it as-is.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ToolOutput.Error($"Couldn't download {uri.Host}: {ex.Message}");
            }
        });

    /// <summary>A name that is safe to write and recognisable afterwards: the URL's own last segment where it has
    /// one, otherwise the host, with an extension inferred from what came back.</summary>
    private static string SafeName(string? requested, Uri uri, string mime)
    {
        var name = string.IsNullOrWhiteSpace(requested)
            ? Path.GetFileName(uri.AbsolutePath.TrimEnd('/'))
            : Path.GetFileName(requested.Trim());

        if (string.IsNullOrWhiteSpace(name)) name = uri.Host.Replace('.', '_');
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (name.Length > 80) name = name[..80];

        if (!Path.HasExtension(name))
        {
            name += mime switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/avif" => ".avif",
                "application/pdf" => ".pdf",
                "text/html" => ".html",
                "text/csv" => ".csv",
                "application/json" => ".json",
                _ => ".bin",
            };
        }
        return name;
    }
}
