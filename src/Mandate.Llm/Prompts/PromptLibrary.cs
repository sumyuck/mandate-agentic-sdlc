using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mandate.Core.Identifiers;

namespace Mandate.Llm.Prompts;

/// <summary>
/// Every prompt the system can issue, loaded from a directory of versioned files.
/// </summary>
/// <remarks>
/// <para>
/// Loaded once and frozen. A prompt that could change between two stages of the same run
/// would make the run's evidence describe a system that no longer exists, and the library's
/// <see cref="Fingerprint"/> would stop meaning anything.
/// </para>
/// <para>
/// Several versions of a prompt may sit side by side. Asking for one by id resolves to the
/// highest version, which is what a stage wants; asking for a specific version is how a
/// recorded run stays reproducible after the prompt has moved on.
/// </para>
/// </remarks>
public sealed partial class PromptLibrary
{
    /// <summary>The suffix every prompt file carries.</summary>
    public const string Extension = ".prompt.md";

    /// <summary>Where prompts live unless told otherwise.</summary>
    public const string DefaultDirectory = "prompts";

    private readonly FrozenDictionary<string, PromptTemplate> _byIdentity;
    private readonly FrozenDictionary<string, PromptTemplate> _latestById;

    private PromptLibrary(
        string directory,
        ImmutableArray<PromptTemplate> prompts,
        FrozenDictionary<string, PromptTemplate> byIdentity,
        FrozenDictionary<string, PromptTemplate> latestById,
        Sha256Hash fingerprint)
    {
        Directory = directory;
        Prompts = prompts;
        Fingerprint = fingerprint;
        _byIdentity = byIdentity;
        _latestById = latestById;
    }

    /// <summary>Where the library was loaded from.</summary>
    public string Directory { get; }

    /// <summary>Every prompt, ordered by id then version.</summary>
    public ImmutableArray<PromptTemplate> Prompts { get; }

    /// <summary>
    /// The content address of the whole library.
    /// </summary>
    /// <remarks>
    /// Recorded on a run so that "which prompts produced this evidence" is answerable from
    /// the evidence itself. Two runs with different fingerprints were produced by different
    /// systems, however similar the diff looks.
    /// </remarks>
    public Sha256Hash Fingerprint { get; }

    /// <summary>The prompt ids present, ordered.</summary>
    public ImmutableArray<string> Ids => [.. _latestById.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Loads every prompt in a directory.</summary>
    /// <exception cref="PromptFormatException">
    /// The directory is missing, or a file in it is not a usable prompt.
    /// </exception>
    public static PromptLibrary Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!System.IO.Directory.Exists(directory))
        {
            throw new PromptFormatException($"No prompt directory at '{directory}'.");
        }

        List<PromptTemplate> loaded = [];

        foreach (string path in System.IO.Directory
            .EnumerateFiles(directory, "*" + Extension, SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal))
        {
            loaded.Add(PromptTemplate.Parse(path, File.ReadAllText(path)));
        }

        if (loaded.Count == 0)
        {
            throw new PromptFormatException(
                $"'{directory}' holds no prompt files. Expected files named "
                + $"'<id>.<version>{Extension}'.");
        }

        ImmutableArray<PromptTemplate> prompts =
        [
            .. loaded
                .OrderBy(prompt => prompt.Id, StringComparer.Ordinal)
                .ThenBy(prompt => VersionNumber(prompt.Version)),
        ];

        Dictionary<string, PromptTemplate> byIdentity = new(StringComparer.Ordinal);

        foreach (PromptTemplate prompt in prompts)
        {
            if (!byIdentity.TryAdd(prompt.Identity, prompt))
            {
                throw new PromptFormatException(
                    $"'{prompt.Identity}' is declared twice: "
                    + $"{byIdentity[prompt.Identity].SourcePath} and {prompt.SourcePath}.");
            }
        }

        Dictionary<string, PromptTemplate> latest = new(StringComparer.Ordinal);

        foreach (PromptTemplate prompt in prompts)
        {
            // Prompts are ordered by ascending version, so the last write wins and is the
            // highest version rather than whichever file the filesystem returned last.
            latest[prompt.Id] = prompt;
        }

        RefuseVersionGaps(prompts);

        return new PromptLibrary(
            directory,
            prompts,
            byIdentity.ToFrozenDictionary(StringComparer.Ordinal),
            latest.ToFrozenDictionary(StringComparer.Ordinal),
            FingerprintOf(prompts));
    }

    /// <summary>The highest version of a prompt.</summary>
    /// <exception cref="PromptFormatException">No prompt has that id.</exception>
    public PromptTemplate Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return _latestById.TryGetValue(id, out PromptTemplate? prompt)
            ? prompt
            : throw new PromptFormatException(
                $"No prompt '{id}' in '{Directory}'. Known: {string.Join(", ", Ids)}.");
    }

    /// <summary>A specific version of a prompt.</summary>
    /// <exception cref="PromptFormatException">That version is not present.</exception>
    public PromptTemplate Get(string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return _byIdentity.TryGetValue($"{id}.{version}", out PromptTemplate? prompt)
            ? prompt
            : throw new PromptFormatException(
                $"No prompt '{id}.{version}' in '{Directory}'.");
    }

    /// <summary>Whether a prompt with this id is present.</summary>
    public bool Contains(string id) => _latestById.ContainsKey(id);

    /// <summary>Whether a string can be a prompt id or an input name.</summary>
    public static bool IsValidId(string? value) => value is not null && Slug().IsMatch(value);

    /// <summary>Whether a string is a version.</summary>
    public static bool IsValidVersion(string? value) =>
        value is not null && VersionPattern().IsMatch(value);

    private static int VersionNumber(string version) =>
        int.Parse(version.AsSpan(1), CultureInfo.InvariantCulture);

    /// <summary>
    /// Refuses a prompt whose versions skip a number.
    /// </summary>
    /// <remarks>
    /// A jump from <c>v1</c> to <c>v3</c> means either a version was deleted — and a
    /// recorded run that cites it can no longer be reproduced — or someone guessed at the
    /// next number. Both are worth a sentence at load time.
    /// </remarks>
    private static void RefuseVersionGaps(ImmutableArray<PromptTemplate> prompts)
    {
        foreach (IGrouping<string, PromptTemplate> group in
            prompts.GroupBy(prompt => prompt.Id, StringComparer.Ordinal))
        {
            int expected = 1;

            foreach (PromptTemplate prompt in group)
            {
                int actual = VersionNumber(prompt.Version);

                if (actual != expected)
                {
                    throw new PromptFormatException(
                        $"{prompt.SourcePath}: prompt '{group.Key}' jumps to v{actual} when "
                        + $"v{expected} is missing. Versions must be consecutive from v1, so "
                        + "a run that cites a version can always be reproduced.");
                }

                expected++;
            }
        }
    }

    private static Sha256Hash FingerprintOf(ImmutableArray<PromptTemplate> prompts)
    {
        StringBuilder combined = new();

        foreach (PromptTemplate prompt in prompts)
        {
            combined.Append(prompt.Identity).Append(' ').Append(prompt.Fingerprint.Hex).Append('\n');
        }

        return Sha256Hash.OfUtf8(combined.ToString());
    }

    [GeneratedRegex(
        "^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Slug();

    [GeneratedRegex("^v[1-9][0-9]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();
}
