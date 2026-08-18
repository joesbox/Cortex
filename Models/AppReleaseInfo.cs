namespace Cortex.Models
{
    public sealed class AppReleaseInfo
    {
        public string Version { get; set; } = string.Empty;

        public string GitHubUrl { get; set; } = string.Empty;
    }
}