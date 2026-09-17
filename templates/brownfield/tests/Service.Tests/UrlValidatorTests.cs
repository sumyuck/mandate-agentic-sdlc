namespace Service.Tests;

public sealed class UrlValidatorTests
{
    [Fact]
    public void TryValidate_returns_false_for_null_url()
    {
        bool result = UrlValidator.TryValidate(null, out _, out string? error);
        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidate_returns_false_for_empty_url()
    {
        bool result = UrlValidator.TryValidate("", out _, out string? error);
        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidate_returns_false_for_relative_url()
    {
        bool result = UrlValidator.TryValidate("/some/path", out _, out string? error);
        Assert.False(result);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert('xss')")]
    [InlineData("file:///etc/passwd")]
    public void TryValidate_rejects_non_http_schemes(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out string? error);
        Assert.False(result);
        Assert.Equal("url scheme must be http or https", error);
    }

    [Theory]
    [InlineData("http://127.0.0.1/path")]
    [InlineData("https://127.255.255.255/path")]
    public void TryValidate_rejects_ipv4_loopback(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out string? error);
        Assert.False(result);
        Assert.Equal("url host is not permitted", error);
    }

    [Theory]
    [InlineData("http://169.254.0.1/path")]
    [InlineData("http://169.254.255.255/path")]
    public void TryValidate_rejects_ipv4_link_local(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out string? error);
        Assert.False(result);
        Assert.Equal("url host is not permitted", error);
    }

    [Theory]
    [InlineData("http://10.0.0.1/path")]
    [InlineData("http://10.255.255.255/path")]
    [InlineData("http://172.16.0.0/path")]
    [InlineData("http://172.31.255.255/path")]
    [InlineData("http://192.168.0.1/path")]
    [InlineData("http://192.168.255.255/path")]
    public void TryValidate_rejects_rfc1918_private_ranges(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out string? error);
        Assert.False(result);
        Assert.Equal("url host is not permitted", error);
    }

    [Theory]
    [InlineData("http://[::1]/path")]
    public void TryValidate_rejects_ipv6_loopback(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out string? error);
        Assert.False(result);
        Assert.Equal("url host is not permitted", error);
    }

    [Theory]
    [InlineData("http://[fe80::1]/path")]
    [InlineData("http://[fe80::ffff]/path")]
    public void TryValidate_rejects_ipv6_link_local(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out string? error);
        Assert.False(result);
        Assert.Equal("url host is not permitted", error);
    }

    [Theory]
    [InlineData("http://[fc00::1]/path")]
    [InlineData("http://[fd00::1]/path")]
    public void TryValidate_rejects_ipv6_unique_local(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out string? error);
        Assert.False(result);
        Assert.Equal("url host is not permitted", error);
    }

    [Theory]
    [InlineData("http://example.com/path")]
    [InlineData("https://example.com/path")]
    [InlineData("http://sub.example.com")]
    [InlineData("http://8.8.8.8/path")]
    [InlineData("https://1.1.1.1/path")]
    public void TryValidate_accepts_public_and_hostname_urls(string url)
    {
        bool result = UrlValidator.TryValidate(url, out Uri? uri, out string? error);
        Assert.True(result);
        Assert.Null(error);
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("HTTP://example.com")]
    [InlineData("HTTPS://example.com")]
    public void TryValidate_is_case_insensitive_for_scheme(string url)
    {
        bool result = UrlValidator.TryValidate(url, out _, out _);
        Assert.True(result);
    }
}