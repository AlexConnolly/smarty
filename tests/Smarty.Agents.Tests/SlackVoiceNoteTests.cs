using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The crux of the voice-note feature: telling a RECORDED CLIP (spoken words to act on) apart from an ordinary
/// audio UPLOAD (a payload — e.g. an audio producer's .wav — that must never be read as a command). The
/// discriminator is provenance (Slack's media markers), never format, so a .wav with no markers stays mute.
/// </summary>
public class SlackVoiceNoteTests
{
    private static AudioClipInfo Detect(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AudioClipInfo.Detect(doc.RootElement);
    }

    [Fact]
    public void Producer_wav_upload_is_not_a_clip()
    {
        // A real WAV file dropped in — correct mimetype/filetype, but NONE of Slack's clip markers. Must NOT be
        // treated as spoken instructions; it stays an ordinary attachment.
        var clip = Detect("""
            { "id": "F1", "name": "master_final.wav", "mimetype": "audio/wav", "filetype": "wav", "size": 9000000 }
            """);
        Assert.False(clip.IsClip);
        Assert.Null(clip.NativeTranscript);
    }

    [Fact]
    public void Document_upload_is_not_a_clip()
    {
        var clip = Detect("""{ "id": "F2", "name": "spec.pdf", "mimetype": "application/pdf", "filetype": "pdf" }""");
        Assert.False(clip.IsClip);
    }

    [Fact]
    public void Recorded_clip_with_complete_transcript_is_detected_and_text_lifted()
    {
        var clip = Detect("""
            {
              "id": "F3", "name": "audio_message.mp4", "mimetype": "audio/mp4", "subtype": "slack_audio",
              "duration_ms": 4200,
              "transcription": { "status": "complete", "preview": { "content": "check this out", "has_more": false } }
            }
            """);
        Assert.True(clip.IsClip);
        Assert.Equal("check this out", clip.NativeTranscript);
        Assert.Equal("complete", clip.TranscriptionStatus);
    }

    [Fact]
    public void Clip_still_processing_is_a_clip_but_yields_no_text_yet()
    {
        // Transcription is async — the first event often arrives before it's ready. We must recognise the clip
        // (so the fallback runs) but NOT surface a half-finished transcript.
        var clip = Detect("""
            {
              "id": "F4", "name": "audio_message.mp4", "mimetype": "audio/mp4",
              "audio_wave_samples": [1,2,3,4],
              "transcription": { "status": "processing" }
            }
            """);
        Assert.True(clip.IsClip);
        Assert.Null(clip.NativeTranscript);
        Assert.Equal("processing", clip.TranscriptionStatus);
    }

    [Fact]
    public void Clip_detected_by_waveform_alone()
    {
        // No subtype, no transcription object — but Slack attached a waveform, which it only does for clips.
        var clip = Detect("""{ "id": "F5", "name": "x.mp4", "mimetype": "audio/mp4", "audio_wave_samples": [0,1,2] }""");
        Assert.True(clip.IsClip);
        Assert.Null(clip.NativeTranscript);
    }

    [Fact]
    public void Bare_preview_string_shape_is_tolerated()
    {
        // Defensive: if Slack ever sends preview as a plain string rather than {content}, still lift it.
        var clip = Detect("""
            { "id": "F6", "subtype": "slack_audio", "transcription": { "status": "complete", "preview": "hello there" } }
            """);
        Assert.True(clip.IsClip);
        Assert.Equal("hello there", clip.NativeTranscript);
    }

    [Fact]
    public void Whitespace_only_transcript_is_treated_as_absent()
    {
        var clip = Detect("""
            { "id": "F7", "subtype": "slack_audio", "transcription": { "status": "complete", "preview": { "content": "   " } } }
            """);
        Assert.True(clip.IsClip);
        Assert.Null(clip.NativeTranscript);
    }

    [Fact]
    public void Non_object_element_is_not_a_clip()
    {
        using var doc = JsonDocument.Parse("\"just a string\"");
        Assert.False(AudioClipInfo.Detect(doc.RootElement).IsClip);
    }
}
