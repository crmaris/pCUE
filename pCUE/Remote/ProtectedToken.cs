using System;
using System.Security.Cryptography;
using System.Text;

namespace pCUE
{
    /// <summary>
    /// The remote-API token at rest. user.config is plaintext XML, so the token is sealed with
    /// DPAPI (CurrentUser scope) before it is stored: only this Windows user can unseal it, and a
    /// copied config is useless anywhere else. Failures fall back to plaintext rather than locking
    /// the user out of their own bench - the token is a LAN shared secret, not a password vault.
    /// Wire format: "DPAPI:" + base64. Values without the prefix are legacy plaintext and still
    /// accepted (migrated to DPAPI on the next save).
    /// </summary>
    static class ProtectedToken
    {
        private const string Prefix = "DPAPI:";

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] sealedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(sealedBytes);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Token protection failed; storing plaintext: " + ex.Message);
                return plain;
            }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;   // legacy plaintext
            try
            {
                byte[] sealedBytes = Convert.FromBase64String(stored.Substring(Prefix.Length));
                byte[] plain = ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Token unseal failed: " + ex.Message);
                return "";
            }
        }
    }
}
