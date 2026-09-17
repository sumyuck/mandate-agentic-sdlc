namespace Service.Tests;

public sealed class CodeGeneratorTests
{
    [Theory]
    [InlineData(1, "1")]
    [InlineData(2, "2")]
    [InlineData(10, "A")]
    [InlineData(35, "Z")]
    [InlineData(36, "a")]
    [InlineData(61, "z")]
    [InlineData(62, "10")]
    [InlineData(3844, "100")]
    public void Encode_returns_base62_string(long id, string expected)
    {
        string result = CodeGenerator.Encode(id);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Encode_of_zero_returns_zero()
    {
        string result = CodeGenerator.Encode(0);
        Assert.Equal("0", result);
    }

    [Fact]
    public void Encode_of_negative_returns_zero()
    {
        string result = CodeGenerator.Encode(-1);
        Assert.Equal("0", result);
    }

    [Fact]
    public void Encode_is_consistent_across_calls()
    {
        string first = CodeGenerator.Encode(12345);
        string second = CodeGenerator.Encode(12345);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Encode_produces_longer_strings_for_larger_ids()
    {
        string small = CodeGenerator.Encode(10);
        string medium = CodeGenerator.Encode(100);
        string large = CodeGenerator.Encode(10000);
        
        Assert.True(small.Length < medium.Length);
        Assert.True(medium.Length < large.Length);
    }
}