using Cortex.Models;
using Cortex.Services;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Cortex.Tests;

public sealed class LogResetHardwareFactAttribute : FactAttribute
{
    public LogResetHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEX_LOG_RESET_TEST_CONFIRM") != "ERASE_ALL_CONTROLLER_LOGS")
            Skip = "Destructive: requires explicit confirmation, port, PIN and trusted identity.";
    }
}

public class LogResetHardwareTests(ITestOutputHelper output)
{
    [LogResetHardwareFact]
    public async Task RealClient_ResetLogsReceivesEncryptedAckAndPreservesLiveSession()
    {
        string port = Required("CORTEX_LOG_RESET_TEST_PORT");
        string pin = Required("CORTEX_LOG_RESET_TEST_PIN");
        string identity = Required("CORTEX_LOG_RESET_TEST_IDENTITY");
        using var service = new SerialPortService(port);
        Assert.True(service.Open(), service.LastError);
        try
        {
            SecureInfoResponse? info = await service.GetSecureInfoAsync();
            Assert.NotNull(info);
            Assert.Equal(identity, Convert.ToHexString(SHA256.HashData(info.DevicePublicKey)), ignoreCase: true);
            Assert.True(info.PinEnabled && info.IsProvisioned);
            Assert.True(await service.EnsureSecureSessionAsync());
            Assert.Equal(SecureControlResult.Ok, (await service.SecureUnlockAsync(pin))?.Result);
            service.BeginLogTransferSession();
            var before = (await service.RequestLogFileListAsync()).ToList();
            Assert.NotEmpty(before);
            object session = typeof(SerialPortService).GetField("_secureSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            byte[] sessionId = ((byte[])session.GetType().GetProperty("SessionId")!.GetValue(session)!).ToArray();

            var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnUpdate(DataStructures _)
            {
                service.BeginLogTransferSession();
                live.TrySetResult();
            }
            service.EndLogTransferSession();
            try
            {
                Assert.True(await service.ResetLogStorageAsync(10000), "Controller reset ACK must be recognized.");
                Assert.False((bool)typeof(SerialPortService).GetField("_suspendLiveRequestPolling",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!);
                service.DataUpdated += OnUpdate;
                service.EnsureLiveRequestPolling(idleThresholdMs: 0);
                await live.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                service.DataUpdated -= OnUpdate;
            }

            Assert.True(service.IsSecureSessionAuthorized);
            Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);
            Assert.Equal(sessionId, (byte[])session.GetType().GetProperty("SessionId")!.GetValue(session)!);
            var after = (await service.RequestLogFileListAsync()).ToList();
            Assert.Single(after);
            Assert.DoesNotContain(after, file => before.Any(old => old.FileName == file.FileName));
            Assert.True(await service.OpenLogTransferAsync(0));
            string text = await service.ReadLogBulkAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("Date,Time,", text);
            Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);
            Assert.True(await service.SecureLockAsync());
            output.WriteLine($"PASS: {before.Count} old log files removed; new log {after[0].FileName} downloaded; encrypted ACK recognized; live telemetry and same authorized session preserved.");
        }
        finally
        {
            service.Close();
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");
}
