namespace Service.Tests;

/// <summary>
/// Tests for <see cref="LinkService.DeleteAsync(string)"/>, exercising the service-layer
/// delete behavior, which is a passthrough to the repository but must still verify that
/// the service correctly exposes delete success/failure and that the post-delete state
/// is observable through other service methods.
/// </summary>
public sealed class LinkServiceDeleteTests
{
    private readonly LinkService _service;
    private readonly LinkRepository _repository;

    public LinkServiceDeleteTests()
    {
        string connectionString = $"Data Source={Path.GetTempFileName()}";
        SchemaInitializer.Initialize(connectionString);
        _repository = new LinkRepository(connectionString);
        _service = new LinkService(_repository, "http://short.test/");
    }

    [Fact]
    public async Task DeleteAsync_for_existing_code_returns_true()
    {
        var request = new CreateLinkRequest("https://example.com", "willdelete", null);
        await _service.CreateAsync(request);

        bool result = await _service.DeleteAsync("willdelete");

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteAsync_for_nonexistent_code_returns_false()
    {
        bool result = await _service.DeleteAsync("nosuchcode");

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteAsync_twice_on_same_code_returns_false_second_time()
    {
        var request = new CreateLinkRequest("https://example.com", "doubledeelte", null);
        await _service.CreateAsync(request);

        bool first = await _service.DeleteAsync("doubledeelte");
        bool second = await _service.DeleteAsync("doubledeelte");

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task After_delete_GetStatsAsync_returns_null()
    {
        var request = new CreateLinkRequest("https://example.com/page", "statscode", null);
        await _service.CreateAsync(request);

        await _service.DeleteAsync("statscode");

        LinkStats? stats = await _service.GetStatsAsync("statscode");

        Assert.Null(stats);
    }

    [Fact]
    public async Task After_delete_RedirectAsync_returns_not_found()
    {
        var request = new CreateLinkRequest("https://example.com/target", "redirecttest", null);
        await _service.CreateAsync(request);

        await _service.DeleteAsync("redirecttest");

        RedirectResult result = await _service.RedirectAsync("redirecttest");

        Assert.Equal(RedirectKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task Deleted_code_can_be_reused_via_CreateAsync()
    {
        var first = new CreateLinkRequest("https://example.com/first", "reuse", null);
        await _service.CreateAsync(first);

        await _service.DeleteAsync("reuse");

        var second = new CreateLinkRequest("https://example.com/second", "reuse", null);
        CreateOutcome result = await _service.CreateAsync(second);

        Assert.Equal(CreateOutcomeKind.Created, result.Kind);
        Assert.Equal("reuse", result.Code);
    }

    [Fact]
    public async Task After_delete_redirect_and_stats_are_both_not_found_for_same_code()
    {
        var request = new CreateLinkRequest("https://example.com", "allgone", null);
        await _service.CreateAsync(request);

        await _service.DeleteAsync("allgone");

        RedirectResult redirect = await _service.RedirectAsync("allgone");
        LinkStats? stats = await _service.GetStatsAsync("allgone");

        Assert.Equal(RedirectKind.NotFound, redirect.Kind);
        Assert.Null(stats);
    }
}