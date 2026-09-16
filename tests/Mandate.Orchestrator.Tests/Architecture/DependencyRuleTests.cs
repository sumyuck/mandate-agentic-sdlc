using System.Xml.Linq;

namespace Mandate.Orchestrator.Tests.Architecture;

/// <summary>
/// Executable enforcement of the structural decisions in docs/adr.
/// </summary>
/// <remarks>
/// An architecture decision that is only written down decays. These tests make the two
/// load-bearing structural rules fail the build when they are broken, which is the
/// difference between a documented constraint and an actual one.
/// </remarks>
public sealed class DependencyRuleTests
{
    /// <summary>ADR-0003: the engine may depend on the domain and its ports, and nothing else.</summary>
    [Fact]
    public void Orchestrator_references_only_the_core_domain()
    {
        XDocument project = XDocument.Parse(
            RepositoryLayout.ReadProject("src/Mandate.Orchestrator/Mandate.Orchestrator.csproj"));

        IReadOnlyList<string> references = project
            .Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(
                (element.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        references.ShouldBe(["Mandate.Core"]);
    }

    /// <summary>ADR-0003: only the CLI composition root may bind adapters to ports.</summary>
    [Theory]
    [InlineData("src/Mandate.Policy/Mandate.Policy.csproj")]
    [InlineData("src/Mandate.Persistence/Mandate.Persistence.csproj")]
    [InlineData("src/Mandate.Observability/Mandate.Observability.csproj")]
    [InlineData("src/Mandate.Llm/Mandate.Llm.csproj")]
    [InlineData("src/Mandate.Agents/Mandate.Agents.csproj")]
    [InlineData("src/Mandate.Workflows/Mandate.Workflows.csproj")]
    public void Adapters_do_not_depend_on_the_orchestration_engine(string adapterProject)
    {
        XDocument project = XDocument.Parse(RepositoryLayout.ReadProject(adapterProject));

        IEnumerable<string> references = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty);

        references.ShouldNotContain(reference => reference.Contains("Mandate.Orchestrator", StringComparison.Ordinal));
    }

    /// <summary>ADR-0002: the target framework is declared once, centrally.</summary>
    [Fact]
    public void No_project_declares_its_own_target_framework()
    {
        IReadOnlyList<string> offenders = RepositoryLayout
            .ProjectFiles("src", "tests", "services")
            .Where(path => RepositoryLayout.ReadProject(path)
                .Contains("<TargetFramework", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty(
            "TargetFramework belongs only in Directory.Build.props (ADR-0002); "
            + $"found overrides in: {string.Join(", ", offenders)}");
    }

    /// <summary>ADR-0002: warnings are errors in production code, everywhere.</summary>
    [Fact]
    public void No_source_project_opts_out_of_warnings_as_errors()
    {
        IReadOnlyList<string> offenders = RepositoryLayout
            .ProjectFiles("src")
            .Where(path => RepositoryLayout.ReadProject(path)
                .Contains("TreatWarningsAsErrors", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    /// <summary>ADR-0001 and ADR-0008: no framework may supply the assessed capability, and no
    /// expression evaluator may turn the workflow file into executable code.</summary>
    [Theory]
    [InlineData("LangChain")]
    [InlineData("Elsa")]
    [InlineData("WorkflowCore")]
    [InlineData("Temporalio")]
    [InlineData("Microsoft.SemanticKernel")]
    // ADR-0008: no expression evaluator or scripting engine may become a dependency, or the
    // workflow file stops being configuration and becomes a code-execution surface.
    [InlineData("NCalc")]
    [InlineData("DynamicExpresso")]
    [InlineData("Jint")]
    [InlineData("NLua")]
    [InlineData("CS-Script")]
    public void No_orchestration_framework_is_taken_as_a_dependency(string forbiddenPackage)
    {
        string packages = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root.FullName, "Directory.Packages.props"));

        packages.ShouldNotContain(forbiddenPackage, Case.Insensitive);
    }

    /// <summary>
    /// ADR-0003 and ADR-0007: exactly one project may know a model vendor exists.
    /// </summary>
    /// <remarks>
    /// The claim that swapping providers, or replacing live calls with recordings, touches
    /// one adapter and the composition root is only true while this holds. A second
    /// reference to the SDK would be an easy, reasonable-looking change that quietly makes
    /// the claim false — so it fails the build instead.
    /// </remarks>
    [Fact]
    public void Only_the_model_adapter_references_the_vendor_sdk()
    {
        IReadOnlyList<string> referencing = RepositoryLayout
            .ProjectFiles("src", "tests", "services")
            .Where(path => RepositoryLayout.ReadProject(path)
                .Contains("PackageReference Include=\"Anthropic\"", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        referencing.ShouldBe(["Mandate.Llm.csproj"]);
    }
}
