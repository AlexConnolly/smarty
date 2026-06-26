using System.Net.Http;

namespace Shop.Checkout;

public record Card(string Last4);

public record Order(string Id, decimal Amount, Card Card);

public class CheckoutResult
{
    public string ReceiptId { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal ChargedAmount { get; set; }
}

public class GatewayResponse
{
    public bool Success { get; init; }
    public string ReceiptId { get; init; } = "";

    public static GatewayResponse? Parse(HttpResponseMessage resp) =>
        resp.IsSuccessStatusCode ? new GatewayResponse { Success = true, ReceiptId = "rcpt_xxx" } : null;
}

public interface ILogger
{
    void Warn(string message);
    void Error(string message);
}
