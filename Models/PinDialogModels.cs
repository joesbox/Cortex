namespace Cortex.Models
{
    public sealed record PinSetupDialogResult(bool Confirmed, string Pin);

    public sealed record PinUnlockDialogResult(bool Confirmed, string Pin, string? RecoveryKey, string? NewPin);
}
