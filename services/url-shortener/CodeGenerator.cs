using System.Text;

namespace Service;

/// <summary>Encodes a monotonic identifier as base62, using a fixed alphanumeric alphabet.</summary>
public static class CodeGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Encode(long id)
    {
        if (id <= 0)
        {
            return Alphabet[0].ToString();
        }

        var builder = new StringBuilder();
        ulong value = (ulong)id;
        while (value > 0)
        {
            builder.Insert(0, Alphabet[(int)(value % 62)]);
            value /= 62;
        }

        return builder.ToString();
    }
}