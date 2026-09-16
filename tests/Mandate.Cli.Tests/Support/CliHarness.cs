using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Mandate.Cli.Tests.Support;

/// <summary>What a command produced when it ran.</summary>
/// <param name="ExitCode">The process exit code the command returned.</param>
/// <param name="Rendered">Output written through the console renderer.</param>
/// <param name="Plain">
/// Output written straight to stdout, bypassing the renderer. Machine-readable output goes
/// here on purpose: the renderer hard-wraps at terminal width, which would corrupt JSON and
/// split run ids across lines.
/// </param>
internal sealed record CliResult(int ExitCode, string Rendered, string Plain)
{
    /// <summary>Everything the command emitted, from either channel.</summary>
    public string All => Rendered + Plain;
}

/// <summary>
/// Runs the CLI exactly as the binary does.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="CommandApp"/> configured by <see cref="Program.Configure"/>, rather than
/// a harness that declares its own commands — so these tests exercise the surface that
/// actually ships, including argument parsing, validation and exit codes, none of which the
/// engine's own tests touch.
/// </para>
/// <para>
/// Both output channels are captured. The renderer is swapped for a test console; plain
/// stdout is redirected. Both are process-global, which is why this assembly runs its tests
/// serially — the honest cost of asserting on the real output channels rather than on
/// stand-ins for them.
/// </para>
/// </remarks>
internal static class CliHarness
{
    public static CliResult Run(params string[] args)
    {
        TestConsole console = new();

        // Wide enough that assertions are about content rather than about where the renderer
        // happened to wrap a line.
        console.Profile.Width = 240;

        IAnsiConsole originalConsole = AnsiConsole.Console;
        TextWriter originalOut = Console.Out;
        StringBuilder captured = new();

        try
        {
            AnsiConsole.Console = console;
            Console.SetOut(new StringWriter(captured));

            CommandApp app = new();

            app.Configure(config =>
            {
                Program.Configure(config);
                config.Settings.Console = console;
            });

            int exitCode = app.Run(args);

            return new CliResult(exitCode, console.Output, captured.ToString());
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            Console.SetOut(originalOut);
        }
    }
}
