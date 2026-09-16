using System.Collections.Frozen;
using System.Collections.Immutable;
using Mandate.Core.Llm;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mandate.Llm.Pricing;

/// <summary>What one model costs, per million tokens, in US dollars.</summary>
/// <param name="InputPerMillion">Uncached input.</param>
/// <param name="OutputPerMillion">Generated output.</param>
/// <param name="CacheReadPerMillion">Input served from a prompt cache.</param>
/// <param name="CacheWritePerMillion">Input written to a prompt cache.</param>
public sealed record ModelPrice(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion)
{
    /// <summary>What a given usage costs, in billionths of a dollar.</summary>
    /// <remarks>
    /// Rounded once, at the end, to the nearest nano-dollar. Rounding each of the four
    /// components would compound an error across thousands of calls; at nano-dollar
    /// resolution a single rounding is below a millionth of a cent.
    /// </remarks>
    public long NanoUsdFor(LlmUsage usage)
    {
        decimal dollars =
            ((usage.InputTokens * InputPerMillion)
             + (usage.OutputTokens * OutputPerMillion)
             + (usage.CacheReadTokens * CacheReadPerMillion)
             + (usage.CacheWriteTokens * CacheWritePerMillion))
            / 1_000_000m;

        return (long)decimal.Round(dollars * 1_000_000_000m, 0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// The published price of every model this system is allowed to call.
/// </summary>
/// <remarks>
/// <para>
/// Prices are data on disk, not constants in code, and the file carries the date and the
/// source it was copied from. Vendor pricing changes; a number compiled into an assembly
/// would go stale silently and quietly misreport every run afterwards. A dated file at
/// least makes the staleness visible in a diff.
/// </para>
/// <para>
/// An unpriced model yields no cost rather than a zero. "We do not know what this cost" and
/// "this cost nothing" are different statements, and only one of them is ever true.
/// </para>
/// </remarks>
public sealed class ModelPriceBook
{
    /// <summary>Where the price list lives unless told otherwise.</summary>
    public const string DefaultPath = "config/model-pricing.yaml";

    private readonly FrozenDictionary<string, ModelPrice> _prices;

    private ModelPriceBook(
        string asOf, string source, FrozenDictionary<string, ModelPrice> prices)
    {
        AsOf = asOf;
        Source = source;
        _prices = prices;
    }

    /// <summary>The date the prices were copied from the source.</summary>
    public string AsOf { get; }

    /// <summary>Where the prices were copied from.</summary>
    public string Source { get; }

    /// <summary>The models the book prices, ordered.</summary>
    public ImmutableArray<string> Models => [.. _prices.Keys.Order(StringComparer.Ordinal)];

    /// <summary>How the book should be described in a run's evidence.</summary>
    public string Description =>
        $"{_prices.Count} model(s) priced as of {AsOf} ({Source})";

    /// <summary>A book that prices nothing, for tests and for offline modes.</summary>
    public static ModelPriceBook Empty { get; } = new(
        "never",
        "no source",
        FrozenDictionary<string, ModelPrice>.Empty);

    /// <summary>Loads the price list.</summary>
    /// <exception cref="ModelPricingException">The file is missing or unusable.</exception>
    public static ModelPriceBook Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ModelPricingException($"No model price list at '{path}'.");
        }

        IDeserializer yaml = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        Document document;

        try
        {
            document = yaml.Deserialize<Document>(File.ReadAllText(path))
                ?? throw new ModelPricingException($"'{path}' is empty.");
        }
        catch (YamlException exception)
        {
            throw new ModelPricingException(
                $"'{path}' is not valid YAML. {exception.Message}", exception);
        }

        return document.Validate(path);
    }

    /// <summary>The price of a model, or <see langword="null"/> when it is not priced.</summary>
    public ModelPrice? For(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return _prices.TryGetValue(model, out ModelPrice? price) ? price : null;
    }

    /// <summary>
    /// What a call cost, in billionths of a dollar, or <see langword="null"/> when unpriced.
    /// </summary>
    public long? NanoUsdFor(string model, LlmUsage usage) => For(model)?.NanoUsdFor(usage);

    private sealed class Document
    {
        public string? AsOf { get; set; }

        public string? Source { get; set; }

        public Dictionary<string, Entry>? Models { get; set; }

        public ModelPriceBook Validate(string path)
        {
            if (string.IsNullOrWhiteSpace(AsOf))
            {
                throw new ModelPricingException(
                    $"'{path}': 'as-of' is required. A price list without a date cannot be "
                    + "told apart from a stale one.");
            }

            if (string.IsNullOrWhiteSpace(Source))
            {
                throw new ModelPricingException(
                    $"'{path}': 'source' is required, so a reviewer can check the numbers.");
            }

            if (Models is not { Count: > 0 })
            {
                throw new ModelPricingException($"'{path}': no models are priced.");
            }

            Dictionary<string, ModelPrice> prices = new(StringComparer.Ordinal);

            foreach ((string model, Entry entry) in Models)
            {
                prices[model] = entry.Validate(path, model);
            }

            return new ModelPriceBook(
                AsOf.Trim(), Source.Trim(), prices.ToFrozenDictionary(StringComparer.Ordinal));
        }
    }

    private sealed class Entry
    {
        public decimal InputPerMtok { get; set; } = -1m;

        public decimal OutputPerMtok { get; set; } = -1m;

        public decimal CacheReadPerMtok { get; set; } = -1m;

        public decimal CacheWritePerMtok { get; set; } = -1m;

        public ModelPrice Validate(string path, string model)
        {
            Require(path, model, "input-per-mtok", InputPerMtok);
            Require(path, model, "output-per-mtok", OutputPerMtok);
            Require(path, model, "cache-read-per-mtok", CacheReadPerMtok);
            Require(path, model, "cache-write-per-mtok", CacheWritePerMtok);

            return new ModelPrice(
                InputPerMtok, OutputPerMtok, CacheReadPerMtok, CacheWritePerMtok);
        }

        private static void Require(string path, string model, string field, decimal value)
        {
            if (value < 0m)
            {
                throw new ModelPricingException(
                    $"'{path}': '{model}' is missing '{field}'. Every rate must be stated; "
                    + "an omitted one would be read as free.");
            }
        }
    }
}

/// <summary>The model price list could not be loaded.</summary>
public sealed class ModelPricingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ModelPricingException()
        : base("The model price list could not be loaded.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public ModelPricingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ModelPricingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
