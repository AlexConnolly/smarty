namespace Smarty.Agents;

/// <summary>
/// What one model did, and what it cost: tokens split the way providers bill them, per model.
/// </summary>
/// <remarks>
/// Accumulated per agent run rather than globally, because "what did that task cost?" is the question worth
/// answering — a single global counter can't tell you whether the expensive thing was the research worker or the
/// planner asking a cheap gate question forty times.
/// </remarks>
public sealed class ModelSpend
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ModelUsage> _byModel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record one request's usage against a model.</summary>
    public void Record(string model, ModelResponse response) =>
        Record(model, response.InputTokens, response.OutputTokens, response.CachedInputTokens, response.ReasoningTokens);

    public void Record(string model, int input, int output, int cachedInput = 0, int reasoning = 0, int calls = 1)
    {
        if (string.IsNullOrWhiteSpace(model)) model = "(unknown)";
        lock (_lock)
        {
            if (!_byModel.TryGetValue(model, out var usage)) _byModel[model] = usage = new ModelUsage(model);
            usage.Calls += calls;
            usage.InputTokens += input;
            usage.OutputTokens += output;
            usage.CachedInputTokens += cachedInput;
            usage.ReasoningTokens += reasoning;
        }
    }

    /// <summary>Fold another tally in — a leg's totals rolling up to its task, or a plan's steps to their parent.
    /// The call COUNT is carried across too: folding ten calls in as one would make a busy task look like a
    /// single cheap request.</summary>
    public void Add(ModelSpend other)
    {
        foreach (var u in other.Snapshot())
            Record(u.Model, u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.ReasoningTokens, u.Calls);
    }

    public IReadOnlyList<ModelUsage> Snapshot()
    {
        lock (_lock) return _byModel.Values.Select(u => u.Clone()).OrderByDescending(u => u.InputTokens).ToList();
    }

    public bool Any { get { lock (_lock) return _byModel.Count > 0; } }

    public long TotalInputTokens { get { lock (_lock) return _byModel.Values.Sum(u => (long)u.InputTokens); } }

    public long TotalOutputTokens { get { lock (_lock) return _byModel.Values.Sum(u => (long)u.OutputTokens); } }

    /// <summary>Total cost in USD, using whatever prices <see cref="ModelPrices"/> knows. Zero when the model's
    /// price is unknown — reported as unknown rather than guessed.</summary>
    public decimal TotalCost => Snapshot().Sum(u => ModelPrices.CostOf(u));
}

/// <summary>One model's tally within a run.</summary>
public sealed class ModelUsage
{
    public ModelUsage(string model) => Model = model;

    public string Model { get; }

    public int Calls { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int CachedInputTokens { get; set; }

    public int ReasoningTokens { get; set; }

    /// <summary>Input tokens actually charged at the full rate.</summary>
    public int FreshInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    public ModelUsage Clone() => new(Model)
    {
        Calls = Calls,
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        CachedInputTokens = CachedInputTokens,
        ReasoningTokens = ReasoningTokens,
    };
}

/// <summary>
/// Per-million-token prices, so spend can be shown in money rather than raw counts.
/// </summary>
/// <remarks>
/// Seeded with the models this project actually runs, and refreshable from a provider's own catalogue (Together's
/// <c>/v1/models</c> returns pricing per model) so the numbers don't quietly rot as prices change. A model we have
/// no price for costs <c>null</c> — shown as "—" rather than as zero, because a confident £0.00 on real spend is
/// worse than admitting we don't know.
/// </remarks>
public static class ModelPrices
{
    private static readonly object Lock = new();

    private static readonly Dictionary<string, ModelPrice> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        // USD per 1M tokens, from Together's catalogue (August 2026).
        ["deepseek-ai/DeepSeek-V4-Flash-0731"] = new(0.14m, 0.28m, 0.03m),
        ["deepseek-ai/DeepSeek-V4-Pro"] = new(0.40m, 1.60m, 0.10m),
        // The vision model behind describe_image. Read back from Together's own /v1/models (input 0.06, output
        // 0.12, hourly 0 — i.e. serverless). Without an entry here every image it looks at reported as "—", so
        // the one tool whose cost anyone actually worries about was the one contributing nothing to the total.
        // Cached input is priced as fresh input rather than free: it isn't known to cache, and a wrong zero
        // understates real spend, which is the direction that misleads.
        ["google/gemma-3n-E4B-it"] = new(0.06m, 0.12m, 0.06m),
        // A local model costs nothing per token — electricity isn't billed here, and pretending otherwise would
        // make the local option look artificially expensive.
        ["local"] = new(0m, 0m, 0m),
    };

    public static void Set(string model, decimal inputPerMillion, decimal outputPerMillion, decimal cachedInputPerMillion)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        lock (Lock) Prices[model] = new ModelPrice(inputPerMillion, outputPerMillion, cachedInputPerMillion);
    }

    public static ModelPrice? For(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        lock (Lock)
        {
            if (Prices.TryGetValue(model, out var price)) return price;
            // An Ollama tag (no "/" and no known entry) runs on this machine: free per token.
            return model.Contains('/') ? null : Prices["local"];
        }
    }

    /// <summary>What a tally cost, or 0 when the price is unknown (check <see cref="For"/> to tell them apart).</summary>
    public static decimal CostOf(ModelUsage usage)
    {
        if (For(usage.Model) is not { } price) return 0m;
        return (usage.FreshInputTokens * price.InputPerMillion
                + usage.CachedInputTokens * price.CachedInputPerMillion
                + usage.OutputTokens * price.OutputPerMillion) / 1_000_000m;
    }
}

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, decimal CachedInputPerMillion);
