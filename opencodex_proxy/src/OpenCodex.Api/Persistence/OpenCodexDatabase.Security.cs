using System.Security.Cryptography;
using System.Text;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private const string AccessKeyPrefix = "ocx_";
    private const int PasswordHashIterations = 200_000;

    public static string HashPassword(string? password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var digest = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? string.Empty),
            salt,
            PasswordHashIterations,
            HashAlgorithmName.SHA256,
            32);
        return $"pbkdf2_sha256${PasswordHashIterations}${ToLowerHex(salt)}${ToLowerHex(digest)}";
    }

    public static bool VerifyPassword(string? password, string? storedHash)
    {
        try
        {
            var parts = (storedHash ?? string.Empty).Split('$', 4);
            if (parts.Length != 4 || parts[0] != "pbkdf2_sha256")
            {
                return false;
            }

            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromHexString(parts[2]);
            var digest = Convert.FromHexString(parts[3]);
            var candidate = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password ?? string.Empty),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                digest.Length);
            return CryptographicOperations.FixedTimeEquals(candidate, digest);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    public static string GenerateAccessApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return AccessKeyPrefix + token;
    }

    public static string HashAccessApiKey(string? rawKey)
    {
        return ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey ?? string.Empty)));
    }

    private static string ToLowerHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
