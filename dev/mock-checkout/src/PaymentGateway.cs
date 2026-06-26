using System;
using System.Net.Http;

namespace Shop.Checkout;

/// <summary>Client for the external payment-gateway service.</summary>
public class PaymentGateway
{
    // Each charge attempt waits up to 30s for the gateway before giving up.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(30000);

    private readonly HttpClient _http;
    private readonly ILogger _log;

    public PaymentGateway(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Charge the card via the payment-gateway. Returns the gateway response on success.
    /// IMPORTANT: on a timeout this returns <c>null</c> (it does not throw) so the caller can decide how to
    /// handle an unavailable gateway.
    /// </summary>
    public GatewayResponse? Charge(decimal amount, Card card)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(RequestTimeout);
            var resp = _http.PostAsync("https://payment-gateway/charge", BuildBody(amount, card), cts.Token)
                .GetAwaiter().GetResult();
            return GatewayResponse.Parse(resp);
        }
        catch (OperationCanceledException)
        {
            _log.Error("Timeout calling payment-gateway after 30000ms");
            return null; // gateway unavailable / timed out
        }
    }

    private static HttpContent BuildBody(decimal amount, Card card) =>
        new StringContent($"{{\"amount\":{amount},\"card\":\"{card.Last4}\"}}");
}
