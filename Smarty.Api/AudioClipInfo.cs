using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// Classifies a Slack file as a recorded voice/video CLIP (spoken words that ARE a message to act on) versus an
/// ordinary uploaded audio file (a payload — e.g. an audio producer's <c>.wav</c> — whose contents must NOT be
/// treated as spoken instructions). The discriminator is PROVENANCE, not format: a <c>.wav</c> tells you nothing
/// about intent, so we never key off mimetype/extension. Slack attaches media markers — a <c>transcription</c>
/// object, waveform samples (<c>audio_wave_samples</c>), or a clip <c>subtype</c> — only to clips it recorded and
/// processed, never to a plain upload. When it's a clip we also lift Slack's OWN transcript if it's ready
/// (<c>status</c> "complete"), so the common case needs no local Whisper at all.
///
/// Lives in Smarty.Api alongside <see cref="WhisperTranscriber"/>/<see cref="AudioTranscoder"/> (the transcription
/// pipeline) so it's unit-testable; <c>Detect</c> parses Slack's file-object shape specifically. Slack
/// under-documents these fields, so confirm the exact subtype string/transcription shape against a real payload
/// (the Slack gateway trace-logs the raw file object on first sight). Detection is deliberately conservative: it
/// requires a strong Slack-only marker, so an ordinary upload is never mis-read as a command — the failure mode
/// is "treat a real clip as a plain file", not the reverse.
/// </summary>
public sealed record AudioClipInfo(bool IsClip, string? NativeTranscript, string? TranscriptionStatus)
{
    public static readonly AudioClipInfo NotAClip = new(false, null, null);

    /// <summary>Classify one Slack file object (an element of a message's <c>files</c> array, or a
    /// <c>files.info</c> result's <c>file</c>).</summary>
    public static AudioClipInfo Detect(JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object) return NotAClip;

        // Markers Slack only sets on media it recorded/processed as a clip — any one is enough.
        bool hasWaveform = file.TryGetProperty("audio_wave_samples", out var w)
            && w.ValueKind == JsonValueKind.Array && w.GetArrayLength() > 0;

        bool hasTranscription = file.TryGetProperty("transcription", out var tr)
            && tr.ValueKind == JsonValueKind.Object;

        // The exact value is undocumented; real clips have been seen as "slack_audio"/"slack_video". Match loosely
        // on the family rather than a literal, so a rename doesn't silently turn clips back into mute files.
        string? subtype = Str(file, "subtype");
        bool clipSubtype = subtype is not null &&
            (subtype.Contains("audio", StringComparison.OrdinalIgnoreCase)
             || subtype.Contains("video", StringComparison.OrdinalIgnoreCase)
             || subtype.Contains("clip", StringComparison.OrdinalIgnoreCase));

        if (!(hasWaveform || hasTranscription || clipSubtype)) return NotAClip;

        string? status = null, transcript = null;
        if (hasTranscription)
        {
            status = Str(tr, "status");
            // preview.content holds the (possibly truncated) text once processing completes. Tolerate either a
            // {preview:{content}} object or a bare {preview:"..."} string.
            if (tr.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.Object)
                transcript = Str(pv, "content");
            else
                transcript = Str(tr, "preview");
        }

        transcript = string.IsNullOrWhiteSpace(transcript) ? null : transcript!.Trim();

        // Only surface the transcript when Slack says it's done (or doesn't report a status); a half-finished one
        // is left null so the caller falls back to a fresh files.info / local Whisper rather than acting on a
        // partial sentence.
        bool ready = status is null || status.Equals("complete", StringComparison.OrdinalIgnoreCase);
        return new AudioClipInfo(true, ready ? transcript : null, status);
    }

    // A string property's value, or null if absent/not a string.
    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
