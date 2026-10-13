using System.Security.Cryptography;
using System.Text;

namespace PduManager.Services;

/// <summary>Encrypts secrets with Windows DPAPI, bound to the current Windows user.</summary>
public static class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PduManager.v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>False when the blob was encrypted by another Windows user/computer (or is damaged).</summary>
    public static bool TryUnprotect(string blob, out string plain)
    {
        try { plain = Unprotect(blob); return true; }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            plain = "";
            return false;
        }
    }

    public const string UnreadableHelp =
        "The stored login cannot be read on this computer/Windows user (it was saved on another one). " +
        "Use Edit PDU… to enter the username and password again.";

    public static string Unprotect(string blob) =>
        string.IsNullOrEmpty(blob) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
}
