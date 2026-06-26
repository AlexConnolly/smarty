# mock-checkout

A tiny, deliberately-buggy checkout service used to test Smarty's `software_engineer` persona (the `code`
capability). It mirrors the seeded Elasticsearch logs: `CheckoutService.ProcessPayment` calls the
`PaymentGateway`, which returns `null` on a 30s timeout — and the service then dereferences that null,
throwing `NullReferenceException`. The intended fix is to handle the null/unavailable-gateway case with a
friendly fallback instead of erroring.
