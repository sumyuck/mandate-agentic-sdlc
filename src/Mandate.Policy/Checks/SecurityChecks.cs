using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Mandate.Core.Policies;

namespace Mandate.Policy.Checks;

/// <summary>
/// No credential material appears in the run's workspace.
/// </summary>
/// <remarks>
/// <para>
/// Scans the committed tree rather than the recorded artifacts, because the tree is what
/// would actually ship. An artifact can be described accurately and still sit alongside a
/// file nobody described.
/// </para>
/// <para>
/// Pattern matching finds the obvious cases — a committed private key, an API token, a
/// connection string with a password. It will not find a secret that does not look like one,
/// and the rule's rationale says so: this is a net with a known mesh size, not a proof.
/// </para>
/// </remarks>
public sealed partial class NoSecretsInWorkspaceCheck : PolicyCheckBase
{
    private static readonly (string Name, Regex Pattern)[] Signatures =
    [
        ("private key block", PrivateKey()),
        ("AWS access key id", AwsAccessKey()),
        ("bearer or API token assignment", TokenAssignment()),
        ("password in a connection string", ConnectionStringPassword()),
    ];

    /// <inheritdoc />
    public override string Kind => "no-secrets-in-workspace";

    /// <inheritdoc />
    public override string Describes =>
        "No committed file in the run workspace matches a known credential pattern.";

    /// <inheritdoc />
    public override async Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string root = context.Workspace.Root;

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            // Fails closed. A scan that could not look is not a scan that found nothing.
            return Violated(
                context,
                "There is no workspace to scan, so the absence of secrets cannot be asserted.");
        }

        List<string> findings = [];

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(root, file);

            if (relative.Split(Path.DirectorySeparatorChar, '/')
                .Any(segment => segment is ".git" or "bin" or "obj"))
            {
                continue;
            }

            string content;

            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            foreach ((string name, Regex pattern) in Signatures)
            {
                if (pattern.IsMatch(content))
                {
                    findings.Add($"{relative}: {name}");
                }
            }
        }

        ImmutableArray<string> found = [.. findings.Distinct().Order(StringComparer.Ordinal)];

        return found.IsEmpty
            ? Satisfied(context, "No committed file matches a known credential pattern.")
            : Violated(context, "Possible credential material: " + string.Join("; ", found));
    }

    [GeneratedRegex(
        "-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(
        @"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(
        @"(?i)\b(api[_-]?key|access[_-]?token|bearer[_-]?token|secret[_-]?key)\b\s*[:=]\s*[""']?[A-Za-z0-9_\-]{16,}",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TokenAssignment();

    [GeneratedRegex(
        @"(?i)\b(password|pwd)\s*=\s*[^;\s""']{6,}", RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex ConnectionStringPassword();
}

/// <summary>The run's audit log has not been altered.</summary>
/// <remarks>
/// Policy evaluation that trusted a tampered log would be worthless, so the integrity of the
/// evidence is itself a rule — and the one that should be read first when anything else is
/// in question.
/// </remarks>
public sealed class AuditChainIntactCheck : PolicyCheckBase
{
    private readonly Func<Core.Identifiers.RunId, CancellationToken, Task<Core.Events.AuditVerification>> _verify;

    /// <summary>Creates the check with a way to read and verify the run's log.</summary>
    public AuditChainIntactCheck(
        Func<Core.Identifiers.RunId, CancellationToken, Task<Core.Events.AuditVerification>> verify) =>
        _verify = verify;

    /// <inheritdoc />
    public override string Kind => "audit-chain-intact";

    /// <inheritdoc />
    public override string Describes => "The run's recorded log verifies against its hash chain.";

    /// <inheritdoc />
    public override async Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        Core.Events.AuditVerification verification =
            await _verify(context.Run.RunId, cancellationToken).ConfigureAwait(false);

        return verification.IsIntact
            ? Satisfied(context, verification.Summary)
            : Violated(context, verification.Summary);
    }
}
