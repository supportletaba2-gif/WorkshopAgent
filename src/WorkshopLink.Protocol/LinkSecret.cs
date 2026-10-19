using System.Security.Cryptography;
using System.Text;

namespace WorkshopLink.Protocol;

public static class LinkSecret
{
    public const int Iterations = 100_000;

    public static byte[] KeyFromPassword(string password) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(password));

    public static string Format(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return Iterations + ":" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    public static bool Verify(string? stored, string password)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return false;
        var parts = stored.Trim().Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations < 10_000)
            return false;
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length < 8 || expected.Length < 16)
            return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string Create()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string other = "23456789";
        var alphabet = upper + lower + other;
        var chars = new char[20];
        chars[0] = Pick(upper);
        chars[1] = Pick(lower);
        chars[2] = Pick(other);
        for (var i = 3; i < chars.Length; i++)
            chars[i] = Pick(alphabet);
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
