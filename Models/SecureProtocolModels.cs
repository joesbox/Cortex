namespace Cortex.Models
{
    public enum SecureControlOperation : byte
    {
        Info = 0,
        Provision = 1,
        Hello = 2,
        Unlock = 3,
        SetPin = 4,
        Recover = 5,
        RotateRecovery = 6,
        Disable = 7,
        Lock = 8,
        Status = 9,
    }

    public enum SecureControlResult : byte
    {
        Ok = 0,
        Invalid = 1,
        Locked = 2,
        Throttled = 3,
        StorageError = 4,
        NotProvisioned = 5,
        Busy = 6,
        Pending = 7,
    }

    public sealed record SecureInfoResponse(
        byte ProtocolVersion,
        bool PinEnabled,
        bool IsProvisioned,
        bool StorageFault,
        uint Pbkdf2Iterations,
        uint RetryAfterMs,
        byte[] DevicePublicKey,
        SecureControlResult Result)
    {
        public bool SupportsPlaintextLogBulk { get; init; }
    }

    public sealed record SecureManagementResponse(
        SecureControlOperation Operation,
        SecureControlResult Result,
        uint RetryAfterMs,
        bool PinEnabled,
        bool IsAuthorized,
        bool StorageFault,
        string? NewRecoveryKey);

    public sealed record PinSetupResult(
        bool Success,
        string? RecoveryKey,
        string? ErrorMessage,
        uint RetryAfterMs,
        SecureControlResult Result);
}
