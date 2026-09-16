using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Mandate.Core.Llm;
using Mandate.Core.Time;
using Mandate.Llm;
using Mandate.Llm.Cassettes;
using Mandate.Llm.Clients;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>Settings shared by every command that touches the model layer.</summary>
internal abstract class LlmSettings : CommandSettings
{
    [CommandOption("--llm")]
    [Description("live, record, replay or stub. Defaults to replay, which spends nothing.")]
    public string Mode { get; init; } = nameof(LlmMode.Replay).ToLowerInvariant();

    [CommandOption("--prompts")]
    [Description("Directory holding the prompt library. Defaults to prompts.")]
    public string Prompts { get; init; } = PromptLibrary.DefaultDirectory;

    [CommandOption("--cassettes")]
    [Description("Directory holding recorded model exchanges. Defaults to cassettes.")]
    public string Cassettes { get; init; } = FileCassetteStore.DefaultRoot;

    [CommandOption("--pricing")]
    [Description("Model price list. Defaults to config/model-pricing.yaml.")]
    public string Pricing { get; init; } = ModelPriceBook.DefaultPath;

    /// <summary>The mode, parsed.</summary>
    public LlmMode ParsedMode =>
        Enum.TryParse(Mode, ignoreCase: true, out LlmMode parsed) ? parsed : LlmMode.Unknown;

    /// <inheritdoc />
    public override ValidationResult Validate() =>
        ParsedMode == LlmMode.Unknown
            ? ValidationResult.Error(
                $"'{Mode}' is not a model mode. Expected live, record, replay or stub.")
            : ValidationResult.Success();

    /// <summary>The options these settings describe.</summary>
    public LlmOptions ToOptions(LlmBudget? budget = null) => new(
        Mode: ParsedMode,
        PromptDirectory: Prompts,
        CassetteRoot: Cassettes,
        PricingPath: Pricing,
        Budget: budget);
}

/// <summary>
/// Proves the model layer works, with one small call.
/// </summary>
/// <remarks>
/// Worth its own command because the alternative is finding out mid-run. A lifecycle run
/// creates a workspace, opens a journal and plans eleven stages before the first prompt is
/// sent; discovering there that no key is set wastes the operator's time and leaves a
/// half-finished run in the store. This asks the smallest question the system has, and
/// reports exactly what it cost.
/// </remarks>
internal sealed class LlmCheckCommand : AsyncCommand<LlmCheckCommand.Settings>
{
    /// <summary>The word the model is asked to echo back.</summary>
    /// <remarks>
    /// Fixed rather than random. A random token would produce a different prompt on every
    /// invocation, so the check could never be replayed — which is exactly the mistake the
    /// prompt library refuses elsewhere, and it would be embarrassing to make it here.
    /// </remarks>
    private const string Token = "mandate-ok";

    /// <summary>
    /// What the cost row means, which depends on where the answer came from.
    /// </summary>
    /// <remarks>
    /// A replayed call costs nothing to make; the figure is what the recorded exchange cost
    /// when it was live, and what the same call would cost again. Labelling both "cost"
    /// would let a replayed run's total be read as money actually spent.
    /// </remarks>
    private static string CostLabel(LlmResponseSource source) => source switch
    {
        LlmResponseSource.Live => "cost",
        LlmResponseSource.Replay => "cost if live",
        _ => "cost (none spent)",
    };

    private static string FormatCost(long? nanos) =>
        nanos is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"${value / 1_000_000_000m:F6}")
            : "[yellow]unpriced[/]";

    internal sealed class Settings : LlmSettings
    {
        [CommandOption("-m|--model")]
        [Description("Model to call. Defaults to the cheapest one the system uses.")]
        public string Model { get; init; } = "claude-haiku-4-5";
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        LlmLayer layer;

        try
        {
            layer = LlmComposition.Build(settings.ToOptions(), SystemClock.Instance);
        }
        catch (Exception exception) when (
            exception is PromptFormatException or ModelPricingException or LlmException)
        {
            AnsiConsole.MarkupLine(
                $"[red]model layer unavailable[/] {exception.Message.EscapeMarkup()}");

            return ExitCode.BadInput;
        }

        using LlmLayer lifetime = layer;

        AnsiConsole.MarkupLine($"[grey]{layer.Description.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"[grey]prices: {layer.Prices.Description.EscapeMarkup()}[/]");

        LlmRequest request = layer.Prompts
            .Get("connectivity-check")
            .Render(
                settings.Model,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = Token });

        long startedTicks = Stopwatch.GetTimestamp();
        LlmResponse response;

        try
        {
            response = await layer.Client
                .CompleteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmException exception)
        {
            AnsiConsole.MarkupLine($"[red]call failed[/] {exception.Message.EscapeMarkup()}");
            return ExitCode.Failed;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTicks);
        long? nanos = layer.Prices.NanoUsdFor(response.Model, response.Usage);

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]property[/]")
            .AddColumn("[bold]value[/]");

        table.AddRow("prompt", $"{request.PromptId}.{request.PromptVersion}");
        table.AddRow("fingerprint", request.Fingerprint.Abbreviated);
        table.AddRow("model requested", settings.Model.EscapeMarkup());
        table.AddRow("model answered", response.Model.EscapeMarkup());
        table.AddRow("source", response.Source.ToString().ToLowerInvariant());
        table.AddRow("stop reason", response.StopReason.EscapeMarkup());
        table.AddRow(
            "tokens",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{response.Usage.InputTokens} in / {response.Usage.OutputTokens} out"));

        table.AddRow(CostLabel(response.Source), FormatCost(nanos));

        table.AddRow(
            "elapsed",
            string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMilliseconds:F0} ms"));

        table.AddRow("answer", response.Text.Trim().EscapeMarkup());

        AnsiConsole.Write(table);

        // The stub is not a model, so there is nothing for it to echo. Reported as wiring
        // verified and connectivity untested, rather than quietly counted as a pass: a
        // green tick that proves nothing is worse than a yellow one that says so.
        if (response.Source == LlmResponseSource.Stub)
        {
            AnsiConsole.MarkupLine(
                "[yellow]wired[/] the layer is assembled, but the stub answered and no model "
                + "was consulted. Run with --llm replay or --llm live to test a real answer.");

            return ExitCode.Success;
        }

        // The point of the check is not that a call succeeded but that the layer is wired
        // end to end, and the echo is what proves the prompt reached the model intact.
        bool echoed = response.Text.Contains(Token, StringComparison.OrdinalIgnoreCase);

        if (!echoed)
        {
            AnsiConsole.MarkupLine(
                $"[red]failed[/] the model did not echo '{Token}'. The layer reached a model, "
                + "but not the one this prompt was written for.");

            return ExitCode.Failed;
        }

        // An answer nobody can put a price on is a half-working layer, and it fails
        // quietly: the run completes, the report shows $0.00, and the budget guardrail
        // never fires. This is how the ApiEnum quoting bug was found, so it stays a check.
        if (nanos is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]failed[/] the model answered as '{response.Model.EscapeMarkup()}', which "
                + $"'{settings.Pricing.EscapeMarkup()}' does not price. Every call this run "
                + "makes would be costed as unknown, and the spend ceiling would not bind.");

            return ExitCode.Failed;
        }

        AnsiConsole.MarkupLine(
            $"[green]ok[/] the model layer works in {layer.Mode.ToString().ToLowerInvariant()} "
            + "mode: prompt rendered, model answered, answer costed.");

        return ExitCode.Success;
    }
}

/// <summary>Lists the prompt library, with the fingerprints a run's evidence cites.</summary>
internal sealed class LlmPromptsCommand : Command<LlmPromptsCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--prompts")]
        [Description("Directory holding the prompt library. Defaults to prompts.")]
        public string Prompts { get; init; } = PromptLibrary.DefaultDirectory;
    }

    protected override int Execute(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        PromptLibrary library;

        try
        {
            library = PromptLibrary.Load(settings.Prompts);
        }
        catch (PromptFormatException exception)
        {
            AnsiConsole.MarkupLine(
                $"[red]cannot load prompts[/] {exception.Message.EscapeMarkup()}");

            return ExitCode.BadInput;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]prompt[/]")
            .AddColumn("[bold]hash[/]")
            .AddColumn("[bold]max out[/]", column => column.RightAligned())
            .AddColumn("[bold]inputs[/]")
            .AddColumn("[bold]purpose[/]");

        foreach (PromptTemplate prompt in library.Prompts)
        {
            table.AddRow(
                prompt.Identity.EscapeMarkup(),
                prompt.Fingerprint.Abbreviated,
                prompt.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
                string.Join(", ", prompt.Inputs).EscapeMarkup(),
                Truncate(prompt.Description).EscapeMarkup());
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"[grey]library {library.Fingerprint.Abbreviated} · {library.Prompts.Length} prompt(s) "
            + $"in {settings.Prompts.EscapeMarkup()}[/]");

        return ExitCode.Success;
    }

    private static string Truncate(string text)
    {
        string single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 70 ? single : single[..67] + "...";
    }
}
