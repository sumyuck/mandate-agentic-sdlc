namespace Service.Tests;

public sealed class LinkServiceTests
{
    private readonly LinkService _service;
    private readonly LinkRepository _repository;

    public LinkServiceTests()
    {
        string connectionString = $"Data Source={Path.GetTempFileName()}";
        SchemaInitializer.Initialize(connectionString);
        _repository = new LinkRepository(connectionString);
        _service = new LinkService(_repository, "http://short.test/");
    }

    [Fact]
    public async Task CreateAsync_with_invalid_url_scheme_returns_invalid()
    {
        var request = new CreateLinkRequest("ftp://example.com", null, null);
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Invalid, outcome.Kind);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task CreateAsync_with_loopback_url_returns_invalid()
    {
        var request = new CreateLinkRequest("http://127.0.0.1/path", null, null);
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Invalid, outcome.Kind);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task CreateAsync_with_valid_url_and_no_alias_returns_created()
    {
        var request = new CreateLinkRequest("https://example.com/long/path", null, null);
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Created, outcome.Kind);
        Assert.NotNull(outcome.Code);
        Assert.NotNull(outcome.ShortUrl);
        Assert.True(outcome.ShortUrl.StartsWith("http://short.test/"));
    }

    [Fact]
    public async Task CreateAsync_with_valid_url_and_available_alias_returns_created()
    {
        var request = new CreateLinkRequest("https://example.com", "myalias", null);
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Created, outcome.Kind);
        Assert.Equal("myalias", outcome.Code);
    }

    [Fact]
    public async Task CreateAsync_with_taken_alias_returns_alias_taken()
    {
        var first = new CreateLinkRequest("https://example.com", "myalias", null);
        await _service.CreateAsync(first);

        var second = new CreateLinkRequest("https://different.com", "myalias", null);
        CreateOutcome outcome = await _service.CreateAsync(second);

        Assert.Equal(CreateOutcomeKind.AliasTaken, outcome.Kind);
    }

    [Fact]
    public async Task CreateAsync_with_invalid_alias_format_returns_invalid()
    {
        var request = new CreateLinkRequest("https://example.com", "invalid-alias!", null);
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Invalid, outcome.Kind);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task CreateAsync_with_valid_iso8601_expiry_returns_created()
    {
        var request = new CreateLinkRequest("https://example.com", null, "2099-01-01T00:00:00Z");
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Created, outcome.Kind);
    }

    [Fact]
    public async Task CreateAsync_with_invalid_expiry_format_returns_invalid()
    {
        var request = new CreateLinkRequest("https://example.com", null, "2099-01-01T00:00:00");
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Invalid, outcome.Kind);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task CreateAsync_with_expiry_missing_utc_designator_returns_invalid()
    {
        var request = new CreateLinkRequest("https://example.com", null, "2099-01-01T00:00:00-05:00");
        CreateOutcome outcome = await _service.CreateAsync(request);

        Assert.Equal(CreateOutcomeKind.Invalid, outcome.Kind);
    }

    [Fact]
    public async Task RedirectAsync_for_existing_unexpired_code_returns_redirect()
    {
        var request = new CreateLinkRequest("https://example.com/target", "testcode", null);
        await _service.CreateAsync(request);

        RedirectResult result = await _service.RedirectAsync("testcode");
        
        Assert.Equal(RedirectKind.Redirect, result.Kind);
        Assert.Equal("https://example.com/target", result.Url);
    }

    [Fact]
    public async Task RedirectAsync_for_nonexistent_code_returns_not_found()
    {
        RedirectResult result = await _service.RedirectAsync("nonexistent");
        
        Assert.Equal(RedirectKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task RedirectAsync_returns_expired_for_past_expiry()
    {
        var request = new CreateLinkRequest("https://example.com", "expiredcode", "2020-01-01T00:00:00Z");
        await _service.CreateAsync(request);

        RedirectResult result = await _service.RedirectAsync("expiredcode");
        
        Assert.Equal(RedirectKind.Expired, result.Kind);
    }

    [Fact]
    public async Task GetStatsAsync_for_existing_code_returns_stats()
    {
        var request = new CreateLinkRequest("https://example.com/page", "statcode", "2099-01-01T00:00:00Z");
        await _service.CreateAsync(request);

        LinkStats? stats = await _service.GetStatsAsync("statcode");

        Assert.NotNull(stats);
        Assert.Equal("statcode", stats.Code);
        Assert.Equal("https://example.com/page", stats.Url);
        Assert.Equal(0L, stats.ClickCount);
        Assert.NotNull(stats.ExpiresAt);
    }

    [Fact]
    public async Task GetStatsAsync_for_nonexistent_code_returns_null()
    {
        LinkStats? stats = await _service.GetStatsAsync("nonexistent");
        Assert.Null(stats);
    }
}