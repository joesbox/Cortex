using System.Text.Json.Serialization;

namespace Cortex.Models
{
    public sealed class ProvisioningDeviceStatus
    {
        public string? ClientId { get; set; }

        public bool Registered { get; set; }

        public bool RegistrationIncomplete { get; set; }

        public bool ClaimedByAnotherRealm { get; set; }

        public string? Realm { get; set; }

        public string? AssetName { get; set; }

        public string? AssetId { get; set; }

        public string? LastState { get; set; }

        public string? LastDetail { get; set; }
    }

    public sealed class ProvisioningRegistration
    {
        public string? JobId { get; set; }

        public string? ClientId { get; set; }

        public string? Realm { get; set; }

        public string? State { get; set; }

        public string? Detail { get; set; }

        public string? AssetName { get; set; }

        public string? AssetId { get; set; }

        public string? ServiceUser { get; set; }

        public string? CertificateFingerprint { get; set; }
    }

    public sealed class ProvisioningCredentials
    {
        public string? Realm { get; set; }

        public string? ClientId { get; set; }

        public string? ServiceUser { get; set; }

        public string? ServiceUserSecret { get; set; }

        public string? AssetId { get; set; }

        public string? AssetName { get; set; }
    }

    public sealed class ProvisioningErrorResponse
    {
        public string? Error { get; set; }

        public string? Code { get; set; }
    }

    public sealed class ProvisioningUnregisterResult
    {
        public string? ClientId { get; set; }

        public bool Unregistered { get; set; }

        public bool AssetRemoved { get; set; }

        public bool ServiceUserRemoved { get; set; }
    }

    public sealed class KeycloakTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
