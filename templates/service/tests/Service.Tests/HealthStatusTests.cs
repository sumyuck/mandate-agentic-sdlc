namespace Service.Tests;

/// <summary>
/// The seed test. A run's testing stage adds to this project; this one exists so the tree
/// arrives with a passing suite, and so an agent has a working example of the conventions
/// to follow rather than having to invent them.
/// </summary>
public sealed class HealthStatusTests
{
    [Fact]
    public void The_liveness_endpoint_reports_live()
    {
        Assert.Equal("live", HealthStatus.Live.Status);
    }
}
