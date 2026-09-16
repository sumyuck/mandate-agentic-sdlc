using System.Collections.Immutable;
using System.Text;
using Mandate.Core.Execution;

namespace Mandate.Persistence.Workspaces;

/// <summary>
/// Reads a run workspace off disk, and refuses to read anything else.
/// </summary>
/// <remarks>
/// <para>
/// The path checks here are a trust boundary, not tidiness. A stage's requested path may
/// have come from a model, so <c>../../.ssh/id_rsa</c> is a request this type has to expect
/// rather than one it can assume will never arrive. Every path is validated by the same
/// rule the engine applies to proposed writes, and then the resolved absolute path is
/// checked to be inside the root — because symlinks make the first check insufficient on
/// its own.
/// </para>
/// <para>
/// Sizes are capped. A stage that reads a 400 MB file into a prompt does not produce a
/// better answer; it produces a request that is refused by the provider after the tokens
/// have been counted.
/// </para>
/// </remarks>
public sealed class FileWorkspaceReader : IWorkspaceReader
{
    /// <summary>The largest file a stage may read, in bytes.</summary>
    public const int MaxFileBytes = 256 * 1024;

    private readonly string _root;

    /// <summary>Creates a reader over a workspace root.</summary>
    public FileWorkspaceReader(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <inheritdoc />
    public ImmutableArray<string> Files
    {
        get
        {
            if (!Directory.Exists(_root))
            {
                return [];
            }

            return
            [
                .. Directory
                    .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
                    .Where(relative => !relative.StartsWith(".git/", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal),
            ];
        }
    }

    private bool IsInsideRoot(string absolutePath) =>
        absolutePath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <inheritdoc />
    public string? TryRead(string relativePath)
    {
        // The same rule the engine applies to writes. A reader that accepted paths the
        // writer rejects would be the obvious place to go looking for a way out of the tree.
        if (!WorkspaceFile.IsSafeRelativePath(relativePath))
        {
            return null;
        }

        string resolved = Path.GetFullPath(Path.Combine(_root, relativePath));

        if (!IsInsideRoot(resolved))
        {
            return null;
        }

        FileInfo file = new(resolved);

        if (!file.Exists || file.Length > MaxFileBytes)
        {
            return null;
        }

        // Path.GetFullPath normalises a string; it does not follow links. A symlink inside
        // the tree pointing anywhere on the host passes every textual check, so the link is
        // resolved to its final target and the containment check is made again against that.
        if (file.ResolveLinkTarget(returnFinalTarget: true) is { } target
            && !IsInsideRoot(Path.GetFullPath(target.FullName)))
        {
            return null;
        }

        try
        {
            // Strict UTF-8, with byte-order-mark sniffing switched off. Left on, a binary
            // file beginning with what looks like a UTF-16 mark is decoded as UTF-16 —
            // silently overriding the strict encoding and handing a prompt plausible-looking
            // nonsense, which is worse than a file the stage knows it could not read.
            using StreamReader reader = new(
                resolved,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false);

            string text = reader.ReadToEnd();

            // A UTF-8 byte-order mark is stripped rather than sniffed. Sniffing would let a
            // binary file choose its own decoding; leaving the mark in place would put an
            // invisible character at the head of every file an editor saved with one, where
            // it eventually breaks a parser for reasons nobody can see in a diff.
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }
}
