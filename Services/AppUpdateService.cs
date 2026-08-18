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

        private const string CortexGitHubApiLatestReleaseUrl = "https://api.github.com/repos/joesbox/Cortex/releases/latest";

        private static readonly HttpClient HttpClient = new HttpClient();

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
            using var request = new HttpRequestMessage(HttpMethod.Get, CortexGitHubApiLatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("Cortex");

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(responseStream, cancellationToken: cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.TagName))
            {
                return null;
            }

            return new AppReleaseInfo
            {
                Version = NormalizeVersion(payload.TagName),
                GitHubUrl = string.IsNullOrWhiteSpace(payload.HtmlUrl) ? CortexGitHubReleasesUrl : payload.HtmlUrl,
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

        private sealed class GitHubReleaseResponse
        {
            [JsonPropertyName("tag_name")]
            public string? TagName { get; set; }

            [JsonPropertyName("html_url")]
            public string? HtmlUrl { get; set; }
        }
    }

}
