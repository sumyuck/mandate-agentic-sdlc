using Mandate.Cli.Commands;
using Spectre.Console.Cli;

namespace Mandate.Cli;

/// <summary>
/// Composition root for the <c>mandate</c> CLI.
/// </summary>
/// <remarks>
/// This is the only place in the system where adapters (persistence, policy, LLM,
/// telemetry) are bound to the ports the engine depends on. The orchestration engine
/// itself references no infrastructure — see docs/adr/0003-hexagonal-layering.md.
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        CommandApp app = new();

        app.Configure(config =>
        {
            config.SetApplicationName("mandate");
            config.UseStrictParsing();
            config.ValidateExamples();

            config.AddCommand<InfoCommand>("info")
                .WithDescription("Show engine identity and host diagnostics.")
                .WithExample("info")
                .WithExample("info", "--json");
        });

        return app.Run(args);
    }
}
