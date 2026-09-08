using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Where the cost figures come from.
///
/// The price table was written by hand and correct on the day. Nothing announces the drift afterwards — the
/// cost column keeps showing a confident number and the number is quietly wrong. Together publishes the real
/// figures in the same /v1/models call that lists the models, so the table is now a fallback rather than the
/// source. The fixture is a verbatim capture of that endpoint, so this tests the shape the API really sends.
/// </summary>
public class ModelPriceCatalogueTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void The_real_payload_yields_the_prices_that_were_hand_written()
    {
        // The seeded table said 0.14 / 0.28 / 0.03 for the working model. If the parse disagrees with that,
        // one of the two is wrong and it matters which.
        var prices = ModelPriceCatalogue.Parse(Fixture("together-models.json"));

        var flash = prices.Single(p => p.Model == "deepseek-ai/DeepSeek-V4-Flash-0731").Price;

        Assert.Equal(0.14m, flash.InputPerMillion, 2);
        Assert.Equal(0.28m, flash.OutputPerMillion, 2);
        Assert.Equal(0.03m, flash.CachedInputPerMillion, 2);
    }

    [Fact]
    public void A_model_with_no_cached_price_is_billed_as_fresh_not_as_free()
    {
        // gemma publishes no cached_input. Recording an unknown discount as zero understates real spend, and
        // understating is the direction that misleads.
        var prices = ModelPriceCatalogue.Parse(Fixture("together-models.json"));

        var vision = prices.Single(p => p.Model == "google/gemma-3n-E4B-it").Price;

        Assert.Equal(0.06m, vision.InputPerMillion, 2);
        Assert.Equal(vision.InputPerMillion, vision.CachedInputPerMillion);
    }

    [Fact]
    public void An_hourly_billed_endpoint_is_skipped_rather_than_priced_per_token()
    {
        // A dedicated endpoint bills by the hour, so its per-token figures describe nothing anyone pays.
        const string json = """
        [{"id":"someone/dedicated-thing","pricing":{"hourly":3.5,"input":0.2,"output":0.4}}]
        """;

        Assert.Empty(ModelPriceCatalogue.Parse(json));
    }

    [Fact]
    public void An_entry_with_no_usable_price_is_left_alone()
    {
        const string json = """
        [{"id":"a/no-pricing"},
         {"id":"a/half-priced","pricing":{"input":0.2}},
         {"id":"a/priced","pricing":{"input":0.2,"output":0.4}}]
        """;

        var prices = ModelPriceCatalogue.Parse(json);

        Assert.Single(prices);
        Assert.Equal("a/priced", prices[0].Model);
    }

    [Fact]
    public void The_openai_style_envelope_is_accepted_too()
    {
        const string json = """{"data":[{"id":"a/b","pricing":{"input":1,"output":2}}]}""";

        Assert.Single(ModelPriceCatalogue.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"error":"unauthorised"}""")]
    public void Rubbish_yields_nothing_rather_than_throwing(string json)
    {
        // A failed refresh must leave the seeded table standing, not take the process down at startup.
        Assert.Empty(ModelPriceCatalogue.Parse(json));
    }

    [Fact]
    public void A_refreshed_price_reaches_the_thing_that_does_the_arithmetic()
    {
        ModelPrices.Set("test/refreshed", 1.00m, 2.00m, 0.50m);

        // FreshInputTokens is Input minus Cached, so 2M in with 1M cached is 1M charged at each rate.
        var usage = new ModelUsage("test/refreshed")
        {
            InputTokens = 2_000_000,
            CachedInputTokens = 1_000_000,
            OutputTokens = 1_000_000,
        };

        Assert.Equal(3.50m, ModelPrices.CostOf(usage), 2);
    }
}
