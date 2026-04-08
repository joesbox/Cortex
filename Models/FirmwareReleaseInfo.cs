namespace Cortex.Models
{
    public sealed class FirmwareReleaseInfo
    {
        public string Version { get; set; } = string.Empty;

        public string FirmwareUrl { get; set; } = string.Empty;

        public string SignatureUrl { get; set; } = string.Empty;

        public string? GitHubUrl { get; set; }

        public long Size { get; set; }
    }
}