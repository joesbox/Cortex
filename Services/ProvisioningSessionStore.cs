using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cortex.Services
{
    /// <summary>
    /// Persists an OpenRemote sign-in between Cortex sessions.
    /// Only a Keycloak offline refresh token is stored, never the password, and it is encrypted
    /// with DPAPI so it can only be read back by the same Windows user on the same machine.
    /// </summary>
    public sealed class ProvisioningSessionStore
    {
        private readonly string _sessionPath;

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Cortex.OpenRemote.Provisioning.v1");

        public ProvisioningSessionStore(string? sessionPath = null)
        {
            _sessionPath = sessionPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cortex",
                "provisioning-session.dat");
        }

        private sealed class StoredSession
        {
            public string? Realm { get; set; }

            public string? Username { get; set; }

            public string? RefreshToken { get; set; }
        }

        public bool IsSupported => OperatingSystem.IsWindows();

        public void Save(string realm, string username, string refreshToken)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(refreshToken))
            {
                return;
            }

            try
            {
                var session = new StoredSession { Realm = realm, Username = username, RefreshToken = refreshToken };
                byte[] plain = JsonSerializer.SerializeToUtf8Bytes(session);
                byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);
                File.WriteAllBytes(_sessionPath, encrypted);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
            {
                // A saved session is a convenience; failing to store it must not break sign in.
            }
        }

        public bool TryLoad(out string realm, out string username, out string refreshToken)
        {
            realm = string.Empty;
            username = string.Empty;
            refreshToken = string.Empty;

            if (!OperatingSystem.IsWindows() || !File.Exists(_sessionPath))
            {
                return false;
            }

            try
            {
                byte[] encrypted = File.ReadAllBytes(_sessionPath);
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                StoredSession? session = JsonSerializer.Deserialize<StoredSession>(plain);

                if (string.IsNullOrWhiteSpace(session?.RefreshToken))
                {
                    return false;
                }

                realm = session.Realm ?? string.Empty;
                username = session.Username ?? string.Empty;
                refreshToken = session.RefreshToken;
                return true;
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
            {
                Clear();
                return false;
            }
        }

        public bool Clear()
        {
            try
            {
                File.Delete(_sessionPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.AddLog("Couldn't remove the saved telemetry sign-in. Cortex may sign you in again next time.", exception: ex);
                return false;
            }
        }
    }
}
