using System.Security.Cryptography;
using System.Text;

namespace SceneGallery.Plugin.PixivAuthors;

internal static class DpapiSecretProtector
{
    internal const string Prefix = "dpapi:v1:";

    /// <summary>
    /// Protects a non-empty secret for the current Windows user.
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// Returns plaintext from a protected value, or passes legacy plaintext through and marks it for migration.
    /// </summary>
    /// <remarks>
    /// Any non-empty legacy plaintext that starts with <c>dpapi:v1:</c> is assumed to be a protected payload.
    /// Cookies and API keys are assumed not to collide with this reserved prefix.
    /// </remarks>
    public static string? Unprotect(
        string? storedValue,
        string fieldName,
        Action<string> log,
        out bool needsMigration)
    {
        needsMigration = false;
        if (string.IsNullOrEmpty(storedValue))
            return storedValue;

        if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            needsMigration = true;
            return storedValue;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(storedValue[Prefix.Length..]);
            var plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (FormatException)
        {
            LogDecryptionFailure(fieldName, "invalid base64", log);
            return null;
        }
        catch (CryptographicException)
        {
            LogDecryptionFailure(fieldName, "DPAPI decryption failed", log);
            return null;
        }
    }

    private static void LogDecryptionFailure(string fieldName, string reason, Action<string> log)
        => log($"WARNING: Secret setting '{fieldName}' could not be decrypted ({reason}). It may belong to a different computer or Windows user; configure it again.");
}
