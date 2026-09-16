using System.ComponentModel;
using Mandate.Core.Diagnostics;
using Mandate.Core.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Reports the engine fingerprint and host environment.
/// </summary>
/// <remarks>
/// Exists so that a run captured on one machine can be reconciled against the
/// environment that produced it when the audit trail is reviewed later.
/// </remarks>
internal sealed class InfoCommand : Command<InfoCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a table.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = BuildInfo.Version,
            ["targetFramework"] = BuildInfo.TargetFramework,
            ["runtime"] = BuildInfo.RuntimeVersion,
            ["platform"] = BuildInfo.Platform,
            ["fingerprint"] = BuildInfo.Fingerprint,
            ["workingDirectory"] = Environment.CurrentDirectory,
        };

        if (settings.Json)
        {
            // Written straight to stdout, not through AnsiConsole: the console renderer
            // hard-wraps at terminal width, which would corrupt piped JSON.
            Console.Out.WriteLine(
                System.Text.Json.JsonSerializer.Serialize(facts, MandateJson.Pretty));
            return 0;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]property[/]")
            .AddColumn("[bold]value[/]");

        foreach ((string key, string value) in facts)
        {
            table.AddRow(key.EscapeMarkup(), value.EscapeMarkup());
        }

        AnsiConsole.Write(new Rule("[bold]mandate[/]").LeftJustified());
        AnsiConsole.Write(table);
        return 0;
    }
}
