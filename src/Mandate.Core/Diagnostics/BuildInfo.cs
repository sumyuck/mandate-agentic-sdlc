using System.Reflection;
using System.Runtime.InteropServices;

namespace Mandate.Core.Diagnostics;

/// <summary>
/// Identity of the running orchestrator build.
/// </summary>
/// <remarks>
/// This is not cosmetic. Every run record stamps the engine identity so that a
/// replayed or audited run can be tied to the exact engine that produced it —
/// a run log is only evidence if you can say what executed it.
/// </remarks>
public static class BuildInfo
{
    /// <summary>Informational version of the engine assembly (e.g. <c>0.1.0</c>).</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Target framework moniker the engine was compiled against.</summary>
    public static string TargetFramework { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()
            ?.FrameworkName ?? "unknown";

    /// <summary>.NET runtime actually executing the engine.</summary>
    public static string RuntimeVersion { get; } = RuntimeInformation.FrameworkDescription;

    /// <summary>Operating system and architecture of the executing host.</summary>
    public static string Platform { get; } =
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.ProcessArchitecture})";

    /// <summary>
    /// Single-line engine fingerprint suitable for embedding in run records and audit events.
    /// </summary>
    public static string Fingerprint => $"mandate/{Version} ({TargetFramework}; {RuntimeVersion})";

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(BuildInfo).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // Strip the source-revision suffix the SDK appends (e.g. "0.1.0+abc1234").
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
