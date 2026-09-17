namespace Service.Tests;

public sealed class LinkRepositoryTests
{
    private readonly LinkRepository _repository;
    private readonly string _connectionString;

    public LinkRepositoryTests()
    {
        _connectionString = $"Data Source={Path.GetTempFileName()}";
        SchemaInitializer.Initialize(_connectionString);
        _repository = new LinkRepository(_connectionString);
    }

    [Fact]
    public async Task CreateAsync_with_alias_returns_success()
    {
        RepositoryCreateResult result = await _repository.CreateAsync("https://example.com", "myalias", null);

        Assert.True(result.Success);
        Assert.Equal("myalias", result.Code);
    }

    [Fact]
    public async Task CreateAsync_with_taken_alias_returns_failure()
    {
        await _repository.CreateAsync("https://example.com", "taken", null);
        RepositoryCreateResult result = await _repository.CreateAsync("https://different.com", "taken", null);

        Assert.False(result.Success);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task CreateAsync_without_alias_generates_code()
    {
        RepositoryCreateResult result = await _repository.CreateAsync("https://example.com", null, null);

        Assert.True(result.Success);
        Assert.NotNull(result.Code);
        Assert.NotEmpty(result.Code);
    }

    [Fact]
    public async Task CreateAsync_multiple_generated_codes_are_unique()
    {
        RepositoryCreateResult first = await _repository.CreateAsync("https://example.com/1", null, null);
        RepositoryCreateResult second = await _repository.CreateAsync("https://example.com/2", null, null);

        Assert.NotEqual(first.Code, second.Code);
    }

    [Fact]
    public async Task CreateAsync_stores_expiry_timestamp()
    {
        DateTimeOffset expiry = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await _repository.CreateAsync("https://example.com", "expiring", expiry);

        LinkStats? stats = await _repository.GetStatsAsync("expiring");

        Assert.NotNull(stats);
        Assert.Equal(expiry, stats.ExpiresAt);
    }

    [Fact]
    public async Task CreateAsync_without_expiry_stores_null_expiry()
    {
        await _repository.CreateAsync("https://example.com", "noexpiry", null);

        LinkStats? stats = await _repository.GetStatsAsync("noexpiry");

        Assert.NotNull(stats);
        Assert.Null(stats.ExpiresAt);
    }

    [Fact]
    public async Task RedirectAsync_increments_click_count()
    {
        await _repository.CreateAsync("https://example.com", "counter", null);

        RedirectResult result1 = await _repository.RedirectAsync("counter");
        Assert.Equal(RedirectKind.Redirect, result1.Kind);

        LinkStats? stats = await _repository.GetStatsAsync("counter");
        Assert.NotNull(stats);
        Assert.Equal(1L, stats.ClickCount);
    }

    [Fact]
    public async Task RedirectAsync_each_call_increments_click_count()
    {
        await _repository.CreateAsync("https://example.com", "multi", null);

        for (int i = 0; i < 5; i++)
        {
            await _repository.RedirectAsync("multi");
        }

        LinkStats? stats = await _repository.GetStatsAsync("multi");
        Assert.NotNull(stats);
        Assert.Equal(5L, stats.ClickCount);
    }

    [Fact]
    public async Task RedirectAsync_for_expired_link_does_not_increment()
    {
        DateTimeOffset past = DateTimeOffset.UtcNow.AddDays(-1);
        await _repository.CreateAsync("https://example.com", "expired", past);

        RedirectResult result = await _repository.RedirectAsync("expired");
        Assert.Equal(RedirectKind.Expired, result.Kind);

        LinkStats? stats = await _repository.GetStatsAsync("expired");
        Assert.NotNull(stats);
        Assert.Equal(0L, stats.ClickCount);
    }

    [Fact]
    public async Task RedirectAsync_for_nonexistent_code_returns_not_found()
    {
        RedirectResult result = await _repository.RedirectAsync("doesnotexist");

        Assert.Equal(RedirectKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task RedirectAsync_returns_original_url()
    {
        string originalUrl = "https://example.com/very/long/path?param=value";
        await _repository.CreateAsync(originalUrl, "urltest", null);

        RedirectResult result = await _repository.RedirectAsync("urltest");

        Assert.Equal(RedirectKind.Redirect, result.Kind);
        Assert.Equal(originalUrl, result.Url);
    }

    [Fact]
    public async Task GetStatsAsync_returns_all_fields()
    {
        string url = "https://example.com/page";
        DateTimeOffset expiry = new DateTimeOffset(2099, 6, 15, 12, 30, 45, TimeSpan.Zero);
        await _repository.CreateAsync(url, "fullstats", expiry);

        LinkStats? stats = await _repository.GetStatsAsync("fullstats");

        Assert.NotNull(stats);
        Assert.Equal("fullstats", stats.Code);
        Assert.Equal(url, stats.Url);
        Assert.Equal(0L, stats.ClickCount);
        Assert.NotNull(stats.CreatedAt);
        Assert.Equal(expiry, stats.ExpiresAt);
    }

    [Fact]
    public async Task GetStatsAsync_for_nonexistent_code_returns_null()
    {
        LinkStats? stats = await _repository.GetStatsAsync("nosuchcode");
        Assert.Null(stats);
    }

    [Fact]
    public async Task GetStatsAsync_with_future_expiry_returns_correct_expiry()
    {
        DateTimeOffset future = DateTimeOffset.UtcNow.AddDays(30);
        await _repository.CreateAsync("https://example.com", "futureexp", future);

        LinkStats? stats = await _repository.GetStatsAsync("futureexp");

        Assert.NotNull(stats);
        Assert.Equal(future, stats.ExpiresAt);
    }

    [Fact]
    public async Task RedirectAsync_with_future_expiry_increments_and_redirects()
    {
        DateTimeOffset future = DateTimeOffset.UtcNow.AddDays(30);
        await _repository.CreateAsync("https://example.com", "willnotexpire", future);

        RedirectResult result = await _repository.RedirectAsync("willnotexpire");

        Assert.Equal(RedirectKind.Redirect, result.Kind);

        LinkStats? stats = await _repository.GetStatsAsync("willnotexpire");
        Assert.NotNull(stats);
        Assert.Equal(1L, stats.ClickCount);
    }

    [Fact]
    public async Task DeleteAsync_for_existing_code_returns_true_and_removes_link()
    {
        await _repository.CreateAsync("https://example.com", "todelete", null);

        bool deleted = await _repository.DeleteAsync("todelete");

        Assert.True(deleted);
        Assert.Null(await _repository.GetStatsAsync("todelete"));
    }

    [Fact]
    public async Task DeleteAsync_for_nonexistent_code_returns_false()
    {
        bool deleted = await _repository.DeleteAsync("neverexisted");

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_called_twice_returns_false_second_time()
    {
        await _repository.CreateAsync("https://example.com", "doubledelete", null);

        bool first = await _repository.DeleteAsync("doubledelete");
        bool second = await _repository.DeleteAsync("doubledelete");

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task DeleteAsync_removes_click_statistics_along_with_link()
    {
        await _repository.CreateAsync("https://example.com", "withclicks", null);
        await _repository.RedirectAsync("withclicks");
        await _repository.RedirectAsync("withclicks");

        bool deleted = await _repository.DeleteAsync("withclicks");

        Assert.True(deleted);
        Assert.Null(await _repository.GetStatsAsync("withclicks"));
    }

    [Fact]
    public async Task RedirectAsync_after_delete_returns_not_found()
    {
        await _repository.CreateAsync("https://example.com", "gonecode", null);
        await _repository.DeleteAsync("gonecode");

        RedirectResult result = await _repository.RedirectAsync("gonecode");

        Assert.Equal(RedirectKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task CreateAsync_can_reuse_code_after_delete()
    {
        await _repository.CreateAsync("https://example.com/first", "reusable", null);
        await _repository.DeleteAsync("reusable");

        RepositoryCreateResult result = await _repository.CreateAsync("https://example.com/second", "reusable", null);

        Assert.True(result.Success);
        Assert.Equal("reusable", result.Code);
    }
}