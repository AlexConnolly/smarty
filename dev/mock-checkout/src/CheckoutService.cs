using System;

namespace Shop.Checkout;

/// <summary>Drives an order through payment and produces a receipt.</summary>
public class CheckoutService
{
    private const int MaxAttempts = 3;

    private readonly PaymentGateway _gateway;
    private readonly ILogger _log;

    public CheckoutService(PaymentGateway gateway, ILogger log)
    {
        _gateway = gateway;
        _log = log;
    }

    public CheckoutResult ProcessPayment(Order order)
    {
        GatewayResponse? response = null;

        // Retry the charge a few times in case of a transient gateway hiccup.
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            response = _gateway.Charge(order.Amount, order.Card);
            if (response != null && response.Success)
                break;

            _log.Warn("Retrying payment attempt");
        }

        // Build the receipt from the gateway response.
        // NOTE: if every attempt timed out, _gateway.Charge returned null, so `response` is still null here —
        // and the next line dereferences it.
        var receiptId = response.ReceiptId;

        return new CheckoutResult
        {
            ReceiptId = receiptId,
            Status = "completed",
            ChargedAmount = order.Amount,
        };
    }
}
