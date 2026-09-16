using Helmsman.Core.Diagnostics;

namespace Helmsman.Core.Tests.Diagnostics;

public sealed class BuildInfoTests
{
    [Fact]
    public void Version_is_resolved_without_the_source_revision_suffix()
    {
        BuildInfo.Version.ShouldNotBeNullOrWhiteSpace();
        BuildInfo.Version.ShouldNotContain("+");
    }

    [Fact]
    public void Target_framework_is_known()
    {
        // Guards the central TargetFramework property in Directory.Build.props:
        // an accidental per-project override would surface here.
        BuildInfo.TargetFramework.ShouldContain(".NETCoreApp");
    }

    [Fact]
    public void Fingerprint_identifies_engine_runtime_and_framework()
    {
        string fingerprint = BuildInfo.Fingerprint;

        fingerprint.ShouldStartWith("helmsman/");
        fingerprint.ShouldContain(BuildInfo.Version);
        fingerprint.ShouldContain(BuildInfo.RuntimeVersion);
    }
}
