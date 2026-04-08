namespace Cortex.Models
{
    public sealed class FirmwareUpdateProgressInfo
    {
        public string StatusMessage { get; set; } = string.Empty;

        public double ProgressPercent { get; set; }

        public bool CanCancel { get; set; } = true;
    }
}