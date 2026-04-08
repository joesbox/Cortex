namespace Cortex.Models
{
    public sealed class LocalFirmwareUpdateSelection
    {
        public string PackagePath { get; set; } = string.Empty;

        public string DisplayName { get; set; } = "Local firmware package";
    }
}