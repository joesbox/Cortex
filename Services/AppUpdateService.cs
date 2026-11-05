using Cortex.Models;
using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.Services
{
    public sealed class AppUpdateService
    {
        public const string CortexGitHubReleasesUrl = "https://github.com/joesbox/Cortex/releases";

        public const string LatestReleaseUrl = "https://updates.mannelectronics.uk/cortex/latest";

        private static readonly HttpClient HttpClient = new HttpClient();
        private readonly HttpClient _httpClient;

        public AppUpdateService() : this(HttpClient)
        {
        }

        public AppUpdateService(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public static string GetCurrentVersion()
        {
            Assembly assembly = typeof(AppUpdateService).Assembly;
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return NormalizeVersion(informationalVersion);
            }

            string? assemblyVersion = assembly.GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(assemblyVersion)
                ? "Unknown"
                : NormalizeVersion(assemblyVersion);
        }

        public async Task<AppReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("Cortex");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<LatestReleaseResponse>(responseStream, cancellationToken: cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Version))
            {
                throw new JsonException("The update service returned no Cortex release version.");
            }

            return new AppReleaseInfo
            {
                Version = NormalizeVersion(payload.Version),
                GitHubUrl = string.IsNullOrWhiteSpace(payload.GitHubUrl) ? CortexGitHubReleasesUrl : payload.GitHubUrl,
            };
        }

        public static bool IsVersionNewerThanCurrent(string? latestVersion, string? currentVersion)
        {
            return FirmwareUpdateService.IsVersionNewerThanCurrent(latestVersion, currentVersion);
        }

        private static string NormalizeVersion(string versionText)
        {
            string sanitized = versionText.Trim();
            int metadataSeparatorIndex = sanitized.IndexOf('+');
            if (metadataSeparatorIndex >= 0)
            {
                sanitized = sanitized[..metadataSeparatorIndex];
            }

            return sanitized.Trim();
        }

        private sealed class LatestReleaseResponse
        {
            [JsonPropertyName("version")]
            public string? Version { get; set; }

            [JsonPropertyName("github_url")]
            public string? GitHubUrl { get; set; }
        }
    }

}
