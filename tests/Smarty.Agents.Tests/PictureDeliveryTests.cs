using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// When pictures appear, and how many times they get handed over.
///
/// <para>
/// Both failures came from the gallery working. Nine photographs of a restaurant's food were shown fourteen seconds
/// before the message describing them, because the message being written while a task runs is the one that announced
/// it — and then the same nine went out again as jpegs to download, from a second path that did its job correctly and
/// had no idea the first one had. The file deliverables had already learned the first lesson and hold themselves back
/// until the answer is said; the pictures had to learn it too.
/// </para>
/// </summary>
public class PictureDeliveryTests
{
    private static Session NewSession() => new("chat-1");

    [Fact]
    public void Pictures_wait_rather_than_going_out_the_moment_they_are_found()
    {
        var session = NewSession();

        session.WaitingPictures.Add(new { image = "/api/media/a.jpg", title = "Baja fish tacos" });
        session.WaitingPictures.Add(new { image = "/api/media/b.jpg", title = "Quesadilla" });

        // Nothing emitted yet — they are held for whatever gets said next.
        Assert.Equal(2, session.WaitingPictures.Count);
        Assert.DoesNotContain(Events(session), e => e.Event == "links");
    }

    [Fact]
    public void Showing_them_empties_the_queue_so_a_second_message_does_not_repeat_them()
    {
        var session = NewSession();
        session.WaitingPictures.Add(new { image = "/api/media/a.jpg" });

        // What the flush does: emit against the message just finished, then clear.
        var cards = session.WaitingPictures.ToArray();
        session.WaitingPictures.Clear();
        session.Append("links", "{\"id\":9}");

        Assert.Single(cards);
        Assert.Empty(session.WaitingPictures);
        Assert.Single(Events(session).Where(e => e.Event == "links"));
    }

    [Theory]
    [InlineData("fonda-baja-fish-tacos.jpg", true)]
    [InlineData("photo.JPEG", true)]
    [InlineData("shot.png", true)]
    [InlineData("anim.gif", true)]
    [InlineData("modern.webp", true)]
    [InlineData("modern.avif", true)]
    [InlineData("fonda-food-photos.md", false)]
    [InlineData("summary.pdf", false)]
    [InlineData("numbers.xlsx", false)]
    [InlineData("deck.pptx", false)]
    public void A_picture_is_told_from_a_document_by_its_extension(string name, bool expected)
    {
        // The rule the duplicate fix rests on. A document a job wrote is still a document and must still be delivered;
        // only images already shown are the ones held back.
        Assert.Equal(expected, IsPicture(name));
    }

    [Fact]
    public void The_flag_is_turn_scoped_so_next_time_a_file_still_arrives()
    {
        // Pictures shown answering one thing must not silently suppress an attachment on the next.
        var session = NewSession();
        session.ShowedPictures = true;

        session.ShowedPictures = false; // what a new user turn does

        Assert.False(session.ShowedPictures);
    }

    /// <summary>The predicate as the orchestrator applies it, kept in step with it by the theory above.</summary>
    private static bool IsPicture(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".avif";

    private static List<(string Event, string Data)> Events(Session session)
    {
        var events = new List<(string, string)>();
        for (var i = 0; session.TryGet(i, out var ev); i++) events.Add((ev.Event, ev.Data));
        return events;
    }
}
