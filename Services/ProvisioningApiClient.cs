using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.Services
{
    /// <summary>
    /// Talks to the Synapse provisioning service. The user's password is only ever sent to Keycloak,
    /// never to the provisioning API, which is given the resulting access token instead.
    /// </summary>
    public sealed class ProvisioningApiClient : IDisposable
    {
        private const string DefaultProvisioningBaseUrl = "https://provisioning.mannelectronics.uk";
        private const string DefaultKeycloakBaseUrl = "https://remote.mannelectronics.uk/auth";
        private const string KeycloakClientId = "openremote";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _client;
        private readonly Uri _keycloakBaseUri;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string _accessToken = string.Empty;
        private string _refreshToken = string.Empty;
        private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

        public ProvisioningApiClient()
        {
            string provisioningBaseUrl = Environment.GetEnvironmentVariable("CORTEX_PROVISIONING_API") ?? DefaultProvisioningBaseUrl;
            string keycloakBaseUrl = Environment.GetEnvironmentVariable("CORTEX_KEYCLOAK_URL") ?? DefaultKeycloakBaseUrl;

            _keycloakBaseUri = new Uri(keycloakBaseUrl.TrimEnd('/') + "/");
            _client = new HttpClient
            {
                BaseAddress = new Uri(provisioningBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Cortex");
        }

        public string SignedInRealm { get; private set; } = string.Empty;

        public string SignedInUsername { get; private set; } = string.Empty;

        /// <summary>The current refresh token, so a "stay signed in" session can be persisted.</summary>
        public string RefreshToken => _refreshToken;

        // Access tokens are short lived; the session lasts as long as the refresh token can be redeemed.
        public bool IsSignedIn => !string.IsNullOrEmpty(_refreshToken) || HasUsableAccessToken;

        private bool HasUsableAccessToken =>
            !string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt;

        public async Task SignInAsync(string realm, string username, string password, bool staySignedIn, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Enter your OpenRemote realm, username and password.");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = KeycloakClientId,
                ["username"] = username.Trim(),
                ["password"] = password,
                // An offline token survives session expiry, so Cortex can resume without the password.
                ["scope"] = staySignedIn ? "openid offline_access" : "openid",
            };

            await RequestTokenAsync(realm.Trim(), form, cancellationToken);
            SignedInRealm = realm.Trim();
            SignedInUsername = username.Trim();
        }

        /// <summary>Resumes a stored session. Returns false if the token has been revoked or expired.</summary>
        public async Task<bool> TryResumeAsync(string realm, string username, string refreshToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(refreshToken))
            {
                return false;
            }

            try
            {
                await RequestTokenAsync(realm, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = KeycloakClientId,
                    ["refresh_token"] = refreshToken,
                }, cancellationToken);

                SignedInRealm = realm;
                SignedInUsername = username;
                return true;
            }
            catch (Exception)
            {
                SignOut();
                return false;
            }
        }

        private async Task RequestTokenAsync(string realm, Dictionary<string, string> form, CancellationToken cancellationToken)
        {
            var tokenUri = new Uri(_keycloakBaseUri, $"realms/{Uri.EscapeDataString(realm)}/protocol/openid-connect/token");
            using HttpResponseMessage response = await _client.PostAsync(tokenUri, new FormUrlEncodedContent(form), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(response.StatusCode == HttpStatusCode.Unauthorized
                    ? "OpenRemote rejected that realm, username or password."
                    : $"Sign in failed ({(int)response.StatusCode}).");
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            KeycloakTokenResponse? token = JsonSerializer.Deserialize<KeycloakTokenResponse>(body, JsonOptions);

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException("OpenRemote did not return an access token.");
            }

            _accessToken = token.AccessToken;
            _refreshToken = token.RefreshToken ?? string.Empty;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(20, token.ExpiresIn) - 10);
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (HasUsableAccessToken)
                {
                    return _accessToken;
                }

                if (string.IsNullOrEmpty(_refreshToken))
                {
                    throw new InvalidOperationException("Sign in to OpenRemote before provisioning.");
                }

                try
                {
                    await RequestTokenAsync(SignedInRealm, new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = KeycloakClientId,
                        ["refresh_token"] = _refreshToken,
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    SignOut();
                    throw new InvalidOperationException("Your OpenRemote session expired. Sign in again.", ex);
                }

                return _accessToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        public void SignOut()
        {
            _accessToken = string.Empty;
            _refreshToken = string.Empty;
            _accessTokenExpiresAt = DateTimeOffset.MinValue;
            SignedInRealm = string.Empty;
            SignedInUsername = string.Empty;
        }

        /// <summary>Revokes the stored session at Keycloak so a copied token file is useless.</summary>
        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            string realm = SignedInRealm;
            string refreshToken = _refreshToken;
            SignOut();

            if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(refreshToken))
            {
                return;
            }

            try
            {
                var logoutUri = new Uri(_keycloakBaseUri, $"realms/{Uri.EscapeDataString(realm)}/protocol/openid-connect/logout");
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = KeycloakClientId,
                    ["refresh_token"] = refreshToken,
                });
                using HttpResponseMessage response = await _client.PostAsync(logoutUri, content, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Local sign out has already happened; a failed revoke must not block it.
            }
        }

        public Task<ProvisioningDeviceStatus> GetDeviceStatusAsync(string clientId, CancellationToken cancellationToken = default)
        {
            return SendAsync<ProvisioningDeviceStatus>(HttpMethod.Get, $"v1/devices/{Uri.EscapeDataString(clientId)}", null, cancellationToken);
        }

        public Task<ProvisioningRegistration> CreateRegistrationAsync(string clientId, string pdmName, CancellationToken cancellationToken = default)
        {
            return SendAsync<ProvisioningRegistration>(
                HttpMethod.Post,
                $"v1/devices/{Uri.EscapeDataString(clientId)}/registrations",
                new { name = pdmName },
                cancellationToken);
        }

        public Task<ProvisioningRegistration> GetRegistrationAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return SendAsync<ProvisioningRegistration>(HttpMethod.Get, $"v1/registrations/{Uri.EscapeDataString(jobId)}", null, cancellationToken);
        }

        public async Task<string> GetProvisioningRequestAsync(string jobId, CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await SendRawAsync(
                HttpMethod.Get,
                $"v1/registrations/{Uri.EscapeDataString(jobId)}/provisioning-request",
                null,
                cancellationToken);

            return await ReadSuccessBodyAsync(response, cancellationToken);
        }

        public Task<ProvisioningCredentials> GetCredentialsAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return SendAsync<ProvisioningCredentials>(HttpMethod.Get, $"v1/registrations/{Uri.EscapeDataString(jobId)}/credentials", null, cancellationToken);
        }

        public Task<ProvisioningDeviceStatus> RenameDeviceAsync(string clientId, string pdmName, CancellationToken cancellationToken = default)
        {
            return SendAsync<ProvisioningDeviceStatus>(
                HttpMethod.Patch,
                $"v1/devices/{Uri.EscapeDataString(clientId)}",
                new { name = pdmName },
                cancellationToken);
        }

        public Task<ProvisioningUnregisterResult> UnregisterDeviceAsync(string clientId, CancellationToken cancellationToken = default)
        {
            return SendAsync<ProvisioningUnregisterResult>(
                HttpMethod.Delete,
                $"v1/devices/{Uri.EscapeDataString(clientId)}",
                null,
                cancellationToken);
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendRawAsync(method, path, body, cancellationToken);
            string content = await ReadSuccessBodyAsync(response, cancellationToken);
            T? result = JsonSerializer.Deserialize<T>(content, JsonOptions);

            return result ?? throw new InvalidOperationException("The provisioning service returned an empty response.");
        }

        private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            string accessToken = await GetAccessTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            if (body != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            }

            try
            {
                return await _client.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException("Cortex could not reach the provisioning service. Check your internet connection.", ex);
            }
        }

        private static async Task<string> ReadSuccessBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return content;
            }

            string message = $"The provisioning service returned {(int)response.StatusCode}.";
            try
            {
                ProvisioningErrorResponse? error = JsonSerializer.Deserialize<ProvisioningErrorResponse>(content, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Error))
                {
                    message = error!.Error!;
                }
            }
            catch (JsonException)
            {
            }

            throw new InvalidOperationException(message);
        }

        public void Dispose()
        {
            _tokenLock.Dispose();
            _client.Dispose();
        }
    }
}
