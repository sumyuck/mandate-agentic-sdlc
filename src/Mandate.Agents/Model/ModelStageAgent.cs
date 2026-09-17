using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Decisions;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Llm;
using Mandate.Core.Time;
using Mandate.Core.Workflow;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;

namespace Mandate.Agents.Model;

/// <summary>
/// A lifecycle stage executed by a language model.
/// </summary>
/// <remarks>
/// <para>
/// One class for all eleven agents, and that is the point rather than a shortcut. What
/// distinguishes a requirements analyst from a security scanner is the instruction it is
/// given, the model it is given it on, the outputs the workflow declares it must produce
/// and the gates its output must pass — all four of which are data, in
/// <c>prompts/</c> and <c>workflows/sdlc.v1.yaml</c>. Eleven near-identical classes would
/// move that difference into code, where a reviewer would have to read eleven files to
/// find out that ten of them do the same thing.
/// </para>
/// <para>
/// The declaration in the workflow is <em>enforced</em> against what the model returns. A
/// node that declares it produces a requirement spec and an ambiguity report fails if the
/// model returns one of them, and fails if it returns a third thing nobody declared. This
/// is what stops a gate from being decorative: <c>artifact-exists: ambiguity-report</c>
/// only means something if the stage cannot pass without actually producing one.
/// </para>
/// <para>
/// The prompt is rendered from the requirement, the scoped context and the tree — never
/// from the run id or the attempt number. A prompt that varied per run could never be
/// replayed, and a prompt that varied per attempt would make the second attempt a different
/// question with a different answer, which is not what "retry" means (ADR-0007).
/// </para>
/// </remarks>
public sealed class ModelStageAgent : IStageAgent
{
    /// <summary>The largest amount of workspace text a prompt will carry, in characters.</summary>
    /// <remarks>
    /// A ceiling, not a target. Without one, the implementation stage's prompt grows with
    /// the repository it is building and eventually costs more than the work is worth.
    /// Raised from 60,000 after a real run: the tree reached 90 kB, a third of it was
    /// withheld, and the test stage wrote thirty-odd tests against an API it had been shown
    /// only part of. A withheld file is cheaper than a wrong one only until the stage
    /// guesses at it.
    /// </remarks>
    public const int MaxWorkspaceCharacters = 200_000;

    /// <summary>How far a claimed coverage figure may exceed the measured one.</summary>
    /// <remarks>
    /// The model is asked for an estimate, so a small optimism is an estimate being an
    /// estimate. Ten points is a different claim about the work.
    /// </remarks>
    public const double CoverageTolerance = 0.10;

    private const string BuildsKey = "implementation.builds";
    private const string FailuresKey = "test.failures";
    private const string CoverageKey = "test.coverage";

    private readonly PromptTemplate _prompt;
    private readonly ILlmClient _client;
    private readonly ModelPriceBook _prices;
    private readonly IClock _clock;
    private readonly IWorkspaceVerifier _verifier;

    /// <summary>Creates an agent bound to one prompt.</summary>
    public ModelStageAgent(
        string id,
        PromptTemplate prompt,
        ILlmClient client,
        ModelPriceBook prices,
        IClock clock,
        IWorkspaceVerifier? verifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(clock);

        Id = id;
        _prompt = prompt;
        _client = client;
        _prices = prices;
        _clock = clock;
        _verifier = verifier ?? IWorkspaceVerifier.Disabled;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>The prompt this agent executes.</summary>
    public PromptTemplate Prompt => _prompt;

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        StageExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        WorkflowNode node = execution.Node;

        if (string.IsNullOrWhiteSpace(node.Model))
        {
            // A configuration mistake, not a model failure, and worth saying so: retrying
            // it eleven times would produce eleven identical messages.
            return StageResult.Failed(
                $"Node '{node.Id}' names no model, and the workflow declares no default. "
                + "Model provenance is part of the evidence, so a stage may not run without "
                + "one.");
        }

        LlmRequest request;

        try
        {
            request = _prompt.Render(node.Model, InputsFor(execution));

            // Effort is declared by the prompt but supported by the model, and the two are
            // chosen independently — a stage can be pointed at a smaller model without its
            // instructions changing. Dropped rather than sent and refused: the reasoning
            // level is a preference, and the smaller model simply has no dial for it.
            if (request.Effort != LlmEffort.Unspecified && !_prices.SupportsEffort(node.Model))
            {
                request = request with { Effort = LlmEffort.Unspecified };
            }
        }
        catch (PromptRenderException exception)
        {
            return StageResult.Failed($"The prompt could not be rendered: {exception.Message}");
        }

        long startedTicks = Stopwatch.GetTimestamp();
        LlmResponse response;

        try
        {
            response = await _client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmException exception)
        {
            return StageResult.Failed($"The model call failed: {exception.Message}");
        }

        ModelCall call = CallOf(request, response, Stopwatch.GetElapsedTime(startedTicks));

        // Reported even when the stage then fails. The call happened and, in live mode, was
        // billed; a cost figure that only counted useful answers would understate every run
        // that had to try twice.
        ImmutableArray<ModelCall> calls = [call];

        // Checked before truncation, because the two have different fixes and the empty
        // case is the more surprising one. A model that reasons adaptively spends thinking
        // tokens from the same ceiling as its answer; on a hard stage it can exhaust the
        // whole budget and return nothing. The first live implementation run did exactly
        // that — 48,000 output tokens, not one character of answer — and reporting it as
        // "raise the ceiling" would have sent the next person the wrong way.
        if (response.IsEmpty)
        {
            return StageResult.Failed(
                $"The model returned no answer at all, having spent "
                + $"{response.Usage.OutputTokens} output token(s). On a model that reasons "
                + $"before answering, that budget went to reasoning. Lower 'effort' or raise "
                + $"'max-output-tokens' in {_prompt.Identity}.",
                modelCalls: calls);
        }

        if (response.WasTruncated)
        {
            return StageResult.Failed(
                $"The model ran out of room after {response.Usage.OutputTokens} tokens. A "
                + $"truncated answer is a failed stage, not a short one, so raise "
                + $"'max-output-tokens' in {_prompt.Identity}.",
                modelCalls: calls);
        }

        try
        {
            AgentResponse answer = AgentResponseParser.Parse(response.Text);
            RefuseUndeclaredOutput(node, answer);

            return await InterpretAsync(execution, answer, calls, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is AgentResponseException or ArgumentException)
        {
            // ArgumentException too: the domain types refuse a decision with one option or
            // an unexplained rejection, and a model that produces one has failed the stage
            // in exactly the way the domain says it has.
            return StageResult.Failed(exception.Message, modelCalls: calls);
        }
    }

    /// <summary>
    /// Refuses output the workflow did not ask for, and output it asked for and did not get.
    /// </summary>
    /// <remarks>
    /// Checked in both directions. A missing declared output would fail the node's exit gate
    /// anyway, but several stages later and with a worse message; an <em>undeclared</em>
    /// output would never be checked at all, because gates are written against what the
    /// workflow says a stage produces. A stage that can quietly emit a fact no guard was
    /// validated against can route the run somewhere nobody reviewed.
    /// </remarks>
    private static void RefuseUndeclaredOutput(WorkflowNode node, AgentResponse answer)
    {
        ImmutableArray<string> produced =
            [.. answer.Documents.Select(document => document.Kind)];

        foreach (ArtifactKind required in node.Produces)
        {
            string wanted = Hyphenate(required.ToString());

            if (!produced.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            {
                throw new AgentResponseException(
                    $"Node '{node.Id}' must produce '{wanted}', and the answer did not "
                    + $"contain it. Returned: {Describe(produced)}.");
            }
        }

        foreach (string kind in produced)
        {
            if (!node.Produces.Any(declared =>
                string.Equals(Hyphenate(declared.ToString()), kind, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AgentResponseException(
                    $"Node '{node.Id}' returned a '{kind}' document, which it does not "
                    + "declare that it produces. Output no gate was written against cannot "
                    + "be accepted.");
            }
        }

        foreach (string key in node.ProducesContext)
        {
            if (!answer.Facts.ContainsKey(key))
            {
                throw new AgentResponseException(
                    $"Node '{node.Id}' must contribute the context fact '{key}', and the "
                    + $"answer did not. Returned: {Describe([.. answer.Facts.Keys])}.");
            }
        }

        foreach (string key in answer.Facts.Keys)
        {
            if (!node.ProducesContext.Contains(key, StringComparer.Ordinal))
            {
                throw new AgentResponseException(
                    $"Node '{node.Id}' contributed the context fact '{key}', which it does "
                    + "not declare. A guard could branch on it without validation ever "
                    + "having seen it.");
            }
        }
    }

    private async Task<StageResult> InterpretAsync(
        StageExecution execution,
        AgentResponse answer,
        ImmutableArray<ModelCall> calls,
        CancellationToken cancellationToken)
    {
        WorkflowNode node = execution.Node;
        DateTimeOffset now = _clock.UtcNow;

        ImmutableArray<Sha256Hash> derivedFrom =
            [.. execution.Inputs.Select(artifact => artifact.Hash)];

        ImmutableArray<Artifact>.Builder artifacts = ImmutableArray.CreateBuilder<Artifact>();
        ImmutableArray<WorkspaceFile>.Builder files = ImmutableArray.CreateBuilder<WorkspaceFile>();

        foreach (AgentDocument document in answer.Documents)
        {
            ArtifactKind kind = ParseKind(document.Kind);

            // Every document needs somewhere to live. Without a path the content is hashed
            // into the audit log and kept nowhere, so the one artifact a reviewer most
            // wants to read — the review, the security report — is the one the run cannot
            // show them. Found the hard way on the first live run.
            if (document.Path is not { } path)
            {
                throw new AgentResponseException(
                    $"The '{document.Kind}' document has no path. Every document is written "
                    + "into the tree; records about the run belong under 'docs/mandate/'.");
            }

            if (!WorkspaceFile.IsSafeRelativePath(path))
            {
                throw new AgentResponseException(
                    $"'{path}' is not a path this stage may write. Absolute paths, parent "
                    + "traversal and the git directory are refused.");
            }

            string name = path;

            byte[] bytes = Encoding.UTF8.GetBytes(document.Content);
            Sha256Hash own = Sha256Hash.OfBytes(bytes);

            artifacts.Add(Artifact.FromContent(
                kind,
                name,
                MediaTypeFor(name),
                bytes,
                node.Id,
                execution.Actor,
                now,
                // A stage that returns a file unchanged produces content identical to one
                // of its own inputs, and content-addressing makes them the same artifact.
                // That is not self-derivation, it is a no-op — but listing the artifact as
                // its own ancestor would be, and the domain rightly refuses it. The test
                // stage hit this by returning a project file it had not needed to alter.
                derivedFrom.Where(hash => hash != own)));

            files.Add(new WorkspaceFile(path, document.Content));
        }

        ImmutableArray<WorkspaceFile> proposed = files.ToImmutable();

        // Measured facts replace claimed ones. Everything downstream — the exit gate, the
        // release evidence pack, the metrics — reads these values, so if they are the
        // model's opinion of its own work then so is everything built on them.
        (ImmutableDictionary<string, string> values, string? overclaim) =
            await MeasureAsync(execution, answer.Facts, proposed, cancellationToken)
                .ConfigureAwait(false);

        if (overclaim is not null)
        {
            // Distinct from the gate simply failing. A stage that reports a passing build it
            // does not have has not merely failed; it has reported something untrue, and the
            // failure message is the only place that distinction survives into the log.
            return StageResult.Failed(overclaim, artifacts.ToImmutable(), modelCalls: calls);
        }

        ImmutableArray<Sha256Hash> evidence = [.. artifacts.Select(artifact => artifact.Hash)];

        ImmutableArray<ContextFact> facts =
        [
            .. values
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => ContextFact.Create(
                    pair.Key, pair.Value, node.Id, execution.Actor, now, evidence)),
        ];

        ImmutableArray<Decision> decisions =
        [
            .. answer.Decisions.Select(decision => Decision.Record(
                id: $"{node.Id}-{decision.Id}",
                runId: execution.RunId,
                nodeId: node.Id,
                question: decision.Question,
                options:
                [
                    .. decision.Options.Select(option => new DecisionOption(
                        option.Name, option.Summary, option.RejectedBecause)),
                ],
                rationale: decision.Rationale,
                confidence: decision.Confidence,
                authority: DecisionAuthority.Agent,
                decidedBy: execution.Actor,
                decidedAt: now,
                evidence: evidence)),
        ];

        return StageResult.Success(
            artifacts.ToImmutable(), facts, decisions, proposed, calls);
    }

    /// <summary>
    /// Runs the real toolchain where the node's declared facts call for it, and replaces
    /// the model's claims with what was measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which check to run is derived from what the node declares it produces, not from the
    /// agent's name. A workflow that adds a build-verified stage gets verification without
    /// anyone editing C#, and a stage that declares none of these facts never pays the cost
    /// of a toolchain invocation.
    /// </para>
    /// <para>
    /// The model is still asked for these values, and is still expected to answer honestly.
    /// That is not redundant: asking makes the claim explicit and comparable, and the
    /// comparison is what turns "the tests failed" into "the stage said the tests passed".
    /// </para>
    /// </remarks>
    private async Task<(ImmutableDictionary<string, string> Values, string? Overclaim)> MeasureAsync(
        StageExecution execution,
        ImmutableDictionary<string, string> claimed,
        ImmutableArray<WorkspaceFile> proposed,
        CancellationToken cancellationToken)
    {
        VerificationKind kind = KindFor(execution.Node);

        if (kind == VerificationKind.Unknown || !_verifier.IsAvailable)
        {
            return (claimed, null);
        }

        VerificationOutcome measured = await _verifier
            .VerifyAsync(execution.Workspace, proposed, kind, cancellationToken)
            .ConfigureAwait(false);

        if (!measured.Executed)
        {
            // The toolchain could not be run. Fail rather than fall back to the claim: a
            // stage whose evidence silently degrades from a measurement to an assertion is
            // the exact failure this whole mechanism exists to prevent.
            return (claimed, $"Verification did not run: {measured.Summary}");
        }

        ImmutableDictionary<string, string>.Builder values = claimed.ToBuilder();
        List<string> overclaims = [];
        List<string> unmeasured = [];

        if (claimed.ContainsKey(BuildsKey))
        {
            values[BuildsKey] = measured.Succeeded ? "true" : "false";

            if (!measured.Succeeded && IsTrue(claimed[BuildsKey]))
            {
                overclaims.Add(
                    $"the stage reported '{BuildsKey}' as true, and the tree does not build: "
                    + measured.Summary);
            }
        }

        if (claimed.ContainsKey(FailuresKey))
        {
            if (measured.TestsFailed is { } failed)
            {
                values[FailuresKey] = failed.ToString(CultureInfo.InvariantCulture);

                if (failed > 0 && ClaimedNumber(claimed[FailuresKey]) == 0)
                {
                    overclaims.Add(
                        $"the stage reported '{FailuresKey}' as 0, and {failed} test(s) failed");
                }
            }
            else
            {
                unmeasured.Add(FailuresKey);
            }
        }

        if (claimed.ContainsKey(CoverageKey))
        {
            if (measured.LineCoverage is { } coverage)
            {
                values[CoverageKey] = coverage.ToString("0.0000", CultureInfo.InvariantCulture);

                // A tolerance, because the model is asked for an estimate and an estimate
                // that is a little optimistic is not dishonesty. A wide miss is.
                if (ClaimedNumber(claimed[CoverageKey]) is { } claimedCoverage
                    && claimedCoverage > coverage + CoverageTolerance)
                {
                    overclaims.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the stage reported '{CoverageKey}' as {claimedCoverage:0.00} and the measured figure is {coverage:0.00}"));
                }
            }
            else
            {
                unmeasured.Add(CoverageKey);
            }
        }

        // The toolchain ran but did not produce a figure the node declares. Falling back to
        // the model's number here would be the whole defect this mechanism exists to
        // prevent, arrived at by a quieter route: the gate would pass on an unverified
        // claim while the run's evidence said verification was on.
        if (unmeasured.Count > 0)
        {
            return (
                claimed,
                $"Verification ran but produced no figure for {string.Join(" or ", unmeasured)}. "
                + $"The toolchain reported: {measured.Summary} "
                + Excerpt(measured.Output));
        }

        // The toolchain's own output goes with it. "37 tests failed" tells a retry that it
        // was wrong but not what to change, which is the same defect as a retry that cannot
        // see its previous failure at all — it just fails one level further in.
        return (
            values.ToImmutable(),
            overclaims.Count == 0
                ? null
                : "Measured verification contradicts what the stage reported: "
                  + string.Join("; ", overclaims) + ". " + Excerpt(measured.Output));
    }

    /// <summary>The tail of the toolchain's output, which is where its errors are.</summary>
    private static string Excerpt(string output)
    {
        string trimmed = output.Trim();

        if (trimmed.Length == 0)
        {
            return "It produced no output.";
        }

        // Generous, because this is what a retry has to work from: a compiler diagnostic
        // or a failing assertion is only actionable with the surrounding lines.
        const int limit = 6000;
        return "Output: " + (trimmed.Length <= limit ? trimmed : "…" + trimmed[^limit..]);
    }

    /// <summary>Which check a node's declared facts call for.</summary>
    private static VerificationKind KindFor(WorkflowNode node) =>
        node.ProducesContext.Contains(FailuresKey, StringComparer.Ordinal)
        || node.ProducesContext.Contains(CoverageKey, StringComparer.Ordinal)
            ? VerificationKind.Test
            : node.ProducesContext.Contains(BuildsKey, StringComparer.Ordinal)
                ? VerificationKind.Build
                : VerificationKind.Unknown;

    private static bool IsTrue(string value) =>
        bool.TryParse(value.Trim(), out bool parsed) && parsed;

    private static double? ClaimedNumber(string value) =>
        double.TryParse(value.Trim(), CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;

    private ModelCall CallOf(LlmRequest request, LlmResponse response, TimeSpan elapsed) =>
        new(
            request.PromptId,
            request.PromptVersion,
            response.Model,
            response.Source,
            request.Fingerprint,
            response.Usage,
            _prices.NanoUsdFor(response.Model, response.Usage),
            response.StopReason,
            (long)elapsed.TotalMilliseconds);

    /// <summary>
    /// The values this agent can offer a prompt, narrowed to the ones it declares.
    /// </summary>
    /// <remarks>
    /// Narrowed rather than passed wholesale because <see cref="PromptTemplate.Render"/>
    /// refuses a value the prompt does not take. That strictness is what keeps a prompt's
    /// declared inputs honest, and it means a prompt can drop an input it no longer uses
    /// without a code change — and cannot silently keep receiving it.
    /// </remarks>
    private Dictionary<string, string> InputsFor(StageExecution execution)
    {
        Dictionary<string, string> available = new(StringComparer.Ordinal)
        {
            ["request"] = execution.Context.Latest(WorkflowContextKeys.Request)?.Value
                          ?? "(the request was not recorded)",
            ["scenario"] = execution.Context.Latest(WorkflowContextKeys.Scenario)?.Value
                           ?? "unknown",
            ["existing-code"] = execution.Context.Latest(WorkflowContextKeys.HasExistingCode)?.Value
                                ?? "false",
            ["node"] = execution.Node.Id.Value,
            ["stage"] = execution.Node.Stage.ToString(),
            ["context"] = RenderContext(execution.Context),
            ["workspace"] = RenderWorkspace(execution.Workspace),
            ["produces"] = string.Join(
                ", ", execution.Node.Produces.Select(kind => Hyphenate(kind.ToString()))),
            ["produces-context"] = string.Join(", ", execution.Node.ProducesContext),
            ["inputs"] = RenderInputs(execution.Inputs),
            ["previous-failure"] = execution.PreviousFailure is { } failure
                ? failure
                : "(this is the first attempt)",
        };

        Dictionary<string, string> supplied = new(StringComparer.Ordinal);

        foreach (string declared in _prompt.Inputs)
        {
            if (!available.TryGetValue(declared, out string? value))
            {
                throw new AgentResponseException(
                    $"Prompt '{_prompt.Identity}' declares an input '{declared}' that no "
                    + $"stage can supply. Available: {string.Join(", ", available.Keys.Order(StringComparer.Ordinal))}.");
            }

            supplied[declared] = value;
        }

        return supplied;
    }

    /// <summary>
    /// The scoped context, as lines a model can read.
    /// </summary>
    /// <remarks>
    /// <c>run.id</c> is excluded. It is in scope for every stage, it changes on every run,
    /// and including it would make each run ask a question no cassette has ever answered —
    /// replay would degrade to a permanent miss. The prompt library refuses run identifiers
    /// outright, so leaving this in would not be a subtle bug; it would be a hard failure on
    /// the first stage of the first run.
    /// </remarks>
    private static string RenderContext(RunContext context)
    {
        StringBuilder rendered = new();

        foreach (string key in context.Keys.Order(StringComparer.Ordinal))
        {
            if (string.Equals(key, WorkflowContextKeys.RunId, StringComparison.Ordinal))
            {
                continue;
            }

            rendered.Append("- ").Append(key).Append(": ")
                .AppendLine(context.Latest(key)!.Value);
        }

        return rendered.Length == 0 ? "(no context yet)" : rendered.ToString().TrimEnd();
    }

    /// <summary>
    /// The tree, as a file list followed by as much content as the budget allows.
    /// </summary>
    /// <remarks>
    /// The full list always, contents until the budget runs out, and an explicit note when
    /// it does. A stage told "here is the repository" when it was actually given a third of
    /// it will reason confidently about code it never saw; a stage told which files were
    /// withheld can say so.
    /// </remarks>
    private static string RenderWorkspace(IWorkspaceReader workspace)
    {
        ImmutableArray<string> paths = workspace.Files;

        if (paths.IsEmpty)
        {
            return "(the workspace is empty)";
        }

        StringBuilder rendered = new();
        rendered.AppendLine("Files:");

        foreach (string path in paths)
        {
            rendered.Append("- ").AppendLine(path);
        }

        rendered.AppendLine();
        int budget = MaxWorkspaceCharacters;
        List<string> omitted = [];

        foreach (string path in paths)
        {
            string? content = workspace.TryRead(path);

            if (content is null)
            {
                omitted.Add($"{path} (not readable as text, or too large)");
                continue;
            }

            if (content.Length > budget)
            {
                omitted.Add($"{path} ({content.Length} characters, over the remaining budget)");
                continue;
            }

            budget -= content.Length;

            rendered.Append("--- ").Append(path).AppendLine(" ---");
            rendered.AppendLine(content);
            rendered.AppendLine();
        }

        if (omitted.Count > 0)
        {
            rendered.AppendLine("Contents withheld for these files. Do not assume anything "
                                + "about what they contain:");

            foreach (string note in omitted)
            {
                rendered.Append("- ").AppendLine(note);
            }
        }

        return rendered.ToString().TrimEnd();
    }

    private static string RenderInputs(ImmutableArray<Artifact> inputs)
    {
        if (inputs.IsEmpty)
        {
            return "(no upstream artifacts)";
        }

        StringBuilder rendered = new();

        foreach (Artifact artifact in inputs)
        {
            rendered.Append("- ")
                .Append(Hyphenate(artifact.Kind.ToString()))
                .Append(": ")
                .Append(artifact.Name)
                .Append(" (")
                .Append(artifact.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" bytes)");
        }

        return rendered.ToString().TrimEnd();
    }

    private static ArtifactKind ParseKind(string hyphenated)
    {
        string candidate = hyphenated.Replace("-", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse(candidate, ignoreCase: true, out ArtifactKind kind)
               && kind != ArtifactKind.Unknown
            ? kind
            : throw new AgentResponseException($"'{hyphenated}' is not an artifact kind.");
    }

    private static string MediaTypeFor(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".yaml" or ".yml" => "application/yaml",
            ".cs" or ".csproj" or ".sln" => "text/plain",
            ".html" => "text/html",
            _ => "text/plain",
        };

    private static string Describe(ImmutableArray<string> values) =>
        values.IsEmpty ? "nothing" : string.Join(", ", values.Order(StringComparer.Ordinal));

    private static string Hyphenate(string pascalCase)
    {
        IEnumerable<string> parts = pascalCase
            .Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? "-" + char.ToLowerInvariant(character)
                    : char.ToLowerInvariant(character).ToString());

        return string.Concat(parts);
    }
}
