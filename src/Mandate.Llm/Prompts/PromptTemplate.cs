using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mandate.Core.Identifiers;
using Mandate.Core.Llm;

namespace Mandate.Llm.Prompts;

/// <summary>
/// A versioned prompt, loaded from a file and rendered against declared inputs.
/// </summary>
/// <remarks>
/// <para>
/// Prompts live on disk, under version control, with a version in the file name. That is
/// not tidiness: a prompt is the specification of what a stage is asked to do, so a change
/// to one is a change to the system's behaviour and has to be reviewable as a diff. A
/// prompt built by string concatenation in C# is a behaviour change hidden in a refactor.
/// </para>
/// <para>
/// The inputs a prompt takes are declared in its front matter and checked against the
/// placeholders in its body, in both directions. A placeholder with no declaration, or a
/// declaration with no placeholder, is a load error rather than a silently empty prompt.
/// </para>
/// </remarks>
public sealed partial class PromptTemplate
{
    private readonly string _system;
    private readonly string _user;

    private PromptTemplate(
        string id,
        string version,
        string description,
        ImmutableArray<string> inputs,
        ImmutableArray<string> verbatim,
        int maxOutputTokens,
        LlmEffort effort,
        string system,
        string user,
        string sourcePath)
    {
        Id = id;
        Version = version;
        Description = description;
        Inputs = inputs;
        Verbatim = verbatim;
        MaxOutputTokens = maxOutputTokens;
        Effort = effort;
        SourcePath = sourcePath;
        _system = system;
        _user = user;

        Fingerprint = Sha256Hash.OfUtf8(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{id}\n{version}\n{maxOutputTokens}\n{effort}\n{system}\n--- user ---\n{user}"));
    }

    /// <summary>The prompt's stable name, matching the first part of its file name.</summary>
    public string Id { get; }

    /// <summary>The version, in the form <c>v1</c>.</summary>
    public string Version { get; }

    /// <summary>What the prompt is for, for the reader rather than the model.</summary>
    public string Description { get; }

    /// <summary>The inputs the prompt requires, in declaration order.</summary>
    public ImmutableArray<string> Inputs { get; }

    /// <summary>
    /// Inputs exempt from the repeatability scan, because they carry upstream content.
    /// </summary>
    /// <remarks>
    /// Declared in the prompt file so the exemption is visible to whoever reviews the
    /// prompt, rather than buried in a list of special-cased names in C#.
    /// </remarks>
    public ImmutableArray<string> Verbatim { get; }

    /// <summary>The output ceiling this prompt's answers need.</summary>
    public int MaxOutputTokens { get; }

    /// <summary>How much reasoning this stage is allowed before answering.</summary>
    public LlmEffort Effort { get; }

    /// <summary>Where the prompt was loaded from.</summary>
    public string SourcePath { get; }

    /// <summary>The content address of this prompt's text.</summary>
    public Sha256Hash Fingerprint { get; }

    /// <summary><c>id.version</c>, the way a prompt is referred to in evidence.</summary>
    public string Identity => $"{Id}.{Version}";

    /// <summary>Parses a prompt file.</summary>
    /// <exception cref="PromptFormatException">The file is not a usable prompt.</exception>
    public static PromptTemplate Parse(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        (PromptFrontMatter matter, string body) = PromptFrontMatter.Split(path, text);
        (string system, string user) = SplitSections(path, body);

        ImmutableArray<string> declared = [.. matter.Inputs];

        FrozenSet<string> used = PlaceholdersIn(system)
            .Concat(PlaceholdersIn(user))
            .ToFrozenSet(StringComparer.Ordinal);

        // Checked in both directions. A placeholder nobody declared renders as literal
        // braces in the prompt the model reads; a declaration nobody uses means an input the
        // caller assembles and the model never sees. Both are silent, so both fail here.
        foreach (string placeholder in used.Order(StringComparer.Ordinal))
        {
            if (!declared.Contains(placeholder, StringComparer.Ordinal))
            {
                throw new PromptFormatException(
                    $"{path}: the body uses '{{{{{placeholder}}}}}' but the front matter does "
                    + "not declare it as an input.");
            }
        }

        foreach (string input in declared)
        {
            if (!used.Contains(input))
            {
                throw new PromptFormatException(
                    $"{path}: input '{input}' is declared but never used in the body.");
            }
        }

        string expected = $"{matter.Id}.{matter.Version}{PromptLibrary.Extension}";

        if (!string.Equals(Path.GetFileName(path), expected, StringComparison.Ordinal))
        {
            throw new PromptFormatException(
                $"{path}: front matter declares '{matter.Id}.{matter.Version}', so the file "
                + $"must be named '{expected}'.");
        }

        return new PromptTemplate(
            matter.Id, matter.Version, matter.Description, declared, matter.Verbatim,
            matter.MaxOutputTokens, matter.Effort, system, user, path);
    }

    /// <summary>
    /// Renders the prompt into a model request.
    /// </summary>
    /// <param name="model">The model id the workflow node declares for this stage.</param>
    /// <param name="values">A value for every declared input, and nothing else.</param>
    /// <exception cref="PromptRenderException">
    /// The supplied values do not match the declared inputs, or a value would make the
    /// prompt unrepeatable.
    /// </exception>
    public LlmRequest Render(string model, IReadOnlyDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(values);

        foreach (string input in Inputs)
        {
            if (!values.ContainsKey(input))
            {
                throw new PromptRenderException(
                    $"{Identity}: no value supplied for input '{input}'.");
            }
        }

        foreach (string supplied in values.Keys.Order(StringComparer.Ordinal))
        {
            if (!Inputs.Contains(supplied, StringComparer.Ordinal))
            {
                throw new PromptRenderException(
                    $"{Identity}: '{supplied}' is not an input of this prompt. Declared: "
                    + string.Join(", ", Inputs));
            }

            if (!Verbatim.Contains(supplied, StringComparer.Ordinal))
            {
                RefuseUnrepeatableValue(supplied, values[supplied]);
            }
        }

        return LlmRequest.Create(
            Id,
            Version,
            model,
            Substitute(_system, values),
            [LlmMessage.User(Substitute(_user, values))],
            MaxOutputTokens,
            Effort);
    }

    /// <summary>
    /// Refuses a value that would make the same question look like a different one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replay works because a prompt is a pure function of the requirement and of what
    /// upstream stages produced. Put a run id or a wall-clock timestamp into a prompt and
    /// every run asks a question no cassette has ever seen: replay degrades to "always a
    /// miss", and it surfaces as a broken demo rather than as a mistake where it was made.
    /// So it is refused where it is made.
    /// </para>
    /// <para>
    /// This scans what the <em>caller</em> injects, which is why an input carrying upstream
    /// content can declare itself <c>verbatim</c> and opt out. A design document quite
    /// properly contains dates — the first real run tripped on an ISO timestamp in an
    /// example <c>expiresAt</c> value — and refusing that would be refusing the stage's
    /// actual input. Content produced upstream is fixed once recorded; it is the engine
    /// interpolating the current time that would make a prompt unrepeatable.
    /// </para>
    /// </remarks>
    private void RefuseUnrepeatableValue(string input, string value)
    {
        if (RunIdentifier().IsMatch(value))
        {
            throw new PromptRenderException(
                $"{Identity}: the value for '{input}' contains a run identifier. Prompts must "
                + "be a function of the requirement and of upstream output only, or the same "
                + "question is never asked twice and recordings can never be replayed.");
        }

        if (Timestamp().IsMatch(value))
        {
            throw new PromptRenderException(
                $"{Identity}: the value for '{input}' contains a wall-clock timestamp. Prompts "
                + "must not vary with the time they are rendered, or recordings can never be "
                + "replayed.");
        }
    }

    private static string Substitute(string template, IReadOnlyDictionary<string, string> values)
    {
        StringBuilder rendered = new(template);

        foreach ((string key, string value) in values)
        {
            rendered.Replace($"{{{{{key}}}}}", value);
        }

        return rendered.ToString();
    }

    private static IEnumerable<string> PlaceholdersIn(string text) =>
        Placeholder().Matches(text).Select(match => match.Groups[1].Value);

    private static (string System, string User) SplitSections(string path, string body)
    {
        Match system = SystemSection().Match(body);
        Match user = UserSection().Match(body);

        if (!system.Success)
        {
            throw new PromptFormatException($"{path}: no '## system' section.");
        }

        if (!user.Success)
        {
            throw new PromptFormatException($"{path}: no '## user' section.");
        }

        if (user.Index < system.Index)
        {
            throw new PromptFormatException(
                $"{path}: '## system' must come before '## user', as it does in the request.");
        }

        string systemText = body[(system.Index + system.Length)..user.Index].Trim();
        string userText = body[(user.Index + user.Length)..].Trim();

        if (systemText.Length == 0)
        {
            throw new PromptFormatException($"{path}: the '## system' section is empty.");
        }

        if (userText.Length == 0)
        {
            throw new PromptFormatException($"{path}: the '## user' section is empty.");
        }

        return (systemText, userText);
    }

    [GeneratedRegex(
        @"\{\{([a-z][a-z0-9-]*)\}\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();

    [GeneratedRegex(
        @"^##[ \t]+system[ \t]*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SystemSection();

    [GeneratedRegex(
        @"^##[ \t]+user[ \t]*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserSection();

    [GeneratedRegex(
        @"run_\d{8}T\d{6}Z_[0-9a-f]{6}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RunIdentifier();

    [GeneratedRegex(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Timestamp();
}
