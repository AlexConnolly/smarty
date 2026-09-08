using System.Net;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Surviving the provider's bad minute.
///
/// Together answers "500 Internal server error" often enough under load, and a 500 used not to count as
/// transient — so the first one ended the task. A five-minute research job was lost in a second to somebody
/// else's server error, and the user was told the work "came back empty". The same request typically succeeds
/// moments later, which is the whole point of retrying it.
///
/// The line these draw: a 5xx is THEIR failure and worth repeating; a 4xx is OUR request being wrong, where
/// repeating it just makes the same mistake more expensively.
/// </summary>
public class ProviderRetryTests
{
    /// <summary>Answers with the given statuses in order, then 200s forever. Counts what it was asked.</summary>
    private sealed class Sequence : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _failures;
        public int Attempts { get; private set; }

        public Sequence(params HttpStatusCode[] failures) => _failures = new Queue<HttpStatusCode>(failures);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            if (_failures.Count > 0)
            {
                var status = _failures.Dequeue();
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"Internal server error\"}}"),
                    // No Retry-After, so the backoff is the provider's own — kept short by the test's statuses
                    // being consumed immediately.
                });
            }

            // A minimal streamed completion, enough for the reader to finish cleanly.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                    "data: [DONE]\n\n"),
            });
        }
    }

    private static async Task<(int Attempts, string? Error)> Run(params HttpStatusCode[] failures)
    {
        var handler = new Sequence(failures);
        var provider = new TogetherModelProvider("test-key", http: new HttpClient(handler));
        var request = new ModelRequest
        {
            Model = "test-model",
            Messages = new[] { Message.User("hello") },
        };

        try
        {
            await foreach (var _ in provider.StreamAsync(request, CancellationToken.None)) { }
            return (handler.Attempts, null);
        }
        catch (Exception ex)
        {
            return (handler.Attempts, ex.Message);
        }
    }

    [Fact]
    public async Task A_single_500_is_retried_and_the_call_succeeds()
    {
        // The regression, exactly: one 500 used to end the task outright.
        var (attempts, error) = await Run(HttpStatusCode.InternalServerError);

        Assert.Null(error);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Every_kind_of_their_problem_is_retried(HttpStatusCode status)
    {
        var (attempts, error) = await Run(status);

        Assert.Null(error);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Our_own_bad_request_fails_immediately(HttpStatusCode status)
    {
        // Retrying these would repeat the same mistake, more slowly and at the same cost.
        var (attempts, error) = await Run(status);

        Assert.NotNull(error);
        Assert.Equal(1, attempts);
        Assert.Contains("Together AI request failed", error);
    }

    [Fact]
    public async Task A_real_outage_still_surfaces_rather_than_retrying_for_ever()
    {
        // Four attempts, then the truth — a provider that is genuinely down must not look like a hang.
        var (attempts, error) = await Run(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError);

        Assert.NotNull(error);
        Assert.Equal(4, attempts);
        Assert.Contains("500", error);
    }
}
