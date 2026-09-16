using System.Text.Json;
using Mandate.Core.Llm;
using Mandate.Core.Serialization;

namespace Mandate.Llm.Cassettes;

/// <summary>Where recorded model exchanges are kept.</summary>
public interface ICassetteStore
{
    /// <summary>How this store should be described in a run's evidence.</summary>
    string Description { get; }

    /// <summary>
    /// The recording for a request, or <see langword="null"/> when there is none.
    /// </summary>
    Task<Cassette?> FindAsync(LlmRequest request, CancellationToken cancellationToken);

    /// <summary>Stores a recording, replacing any earlier one for the same request.</summary>
    Task SaveAsync(Cassette cassette, CancellationToken cancellationToken);
}

/// <summary>
/// Cassettes as files in a directory, one per exchange.
/// </summary>
/// <remarks>
/// <para>
/// Files rather than a database because these are review artifacts that belong in version
/// control: a reviewer should be able to read the prompts a change altered as a diff, and
/// a new recording should show up in <c>git status</c> rather than inside an opaque blob.
/// </para>
/// <para>
/// The layout is <c>&lt;root&gt;/&lt;prompt&gt;.&lt;version&gt;/&lt;fingerprint&gt;.json</c>.
/// The path is computed from the request, so a lookup is a single file probe with no index
/// to build, keep current, or get wrong.
/// </para>
/// </remarks>
public sealed class FileCassetteStore : ICassetteStore
{
    /// <summary>Where cassettes live unless told otherwise.</summary>
    public const string DefaultRoot = "cassettes";

    private readonly string _root;

    /// <summary>Creates a store rooted at a directory. The directory need not exist yet.</summary>
    public FileCassetteStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    /// <inheritdoc />
    public string Description => $"file cassettes at '{_root}'";

    /// <summary>How many recordings the store currently holds.</summary>
    public int Count =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories).Count()
            : 0;

    /// <inheritdoc />
    public async Task<Cassette?> FindAsync(
        LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string path = PathFor(request);

        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream file = File.OpenRead(path);

        Cassette? cassette = await JsonSerializer
            .DeserializeAsync<Cassette>(file, MandateJson.Pretty, cancellationToken)
            .ConfigureAwait(false);

        if (cassette is null)
        {
            throw new CassetteCorruptException($"'{path}' is empty.");
        }

        // A cassette whose contents do not hash to its own file name has been edited. That
        // is worth refusing loudly: the whole value of replay is that the answers are the
        // ones the model actually gave, and a hand-tuned recording would make a run's
        // evidence describe a conversation that never happened.
        string actual = cassette.Request.Fingerprint.Hex;

        if (!string.Equals(actual, cassette.Key, StringComparison.Ordinal))
        {
            throw new CassetteCorruptException(
                $"'{path}' records key {cassette.Key[..12]}… but its request hashes to "
                + $"{actual[..12]}…. The recording has been altered.");
        }

        return cassette;
    }

    /// <inheritdoc />
    public async Task SaveAsync(Cassette cassette, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cassette);

        string path = PathFor(cassette.Request);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written pretty-printed: a cassette is read by people at least as often as by the
        // replay client, and a 4 kB prompt on one line is not reviewable.
        await using FileStream file = File.Create(path);

        await JsonSerializer
            .SerializeAsync(file, cassette, MandateJson.Pretty, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Where a request's recording belongs.</summary>
    public string PathFor(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Path.Combine(
            _root,
            $"{request.PromptId}.{request.PromptVersion}",
            $"{request.Fingerprint.Hex}.json");
    }
}

/// <summary>A cassette file could not be trusted.</summary>
public sealed class CassetteCorruptException : LlmException
{
    /// <summary>Creates the exception.</summary>
    public CassetteCorruptException()
        : base("The cassette could not be read.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public CassetteCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public CassetteCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
