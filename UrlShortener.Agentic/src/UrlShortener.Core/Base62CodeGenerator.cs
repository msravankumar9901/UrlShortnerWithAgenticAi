using System.Security.Cryptography;

namespace UrlShortener.Core;

public sealed class Base62CodeGenerator : ICodeGenerator
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public string Generate(int length = 7)
    {
        if (length is < 4 or > 16)
            throw new ArgumentOutOfRangeException(nameof(length));

        // Rejection sampling keeps the distribution uniform: 62 * 4 = 248, so bytes
        // 248-255 are discarded instead of biasing the low residues of modulo 62.
        var chars = new char[length];
        Span<byte> buffer = stackalloc byte[1];
        for (var i = 0; i < length; i++)
        {
            int value;
            do
            {
                RandomNumberGenerator.Fill(buffer);
                value = buffer[0];
            }
            while (value >= 248);

            chars[i] = Alphabet[value % 62];
        }

        return new string(chars);
    }
}
