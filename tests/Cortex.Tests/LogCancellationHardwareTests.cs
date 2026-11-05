using Cortex.Models;
using Cortex.Services;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Cortex.Tests;

public sealed class LogCancellationHardwareFactAttribute : FactAttribute
{
    public LogCancellationHardwareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CORTEX_LOG_CANCEL_TEST_PORT")))
            Skip = "Requires explicit CORTEX_LOG_CANCEL_TEST_PORT, PIN and trusted identity; reads logs only.";
    }
}

public class LogCancellationHardwareTests(ITestOutputHelper output)
{
    [LogCancellationHardwareFact]
    public async Task RealClient_CancelRestartAndSwitchFilesWithoutReconnectOrUnlock()
    {
        string port = Environment.GetEnvironmentVariable("CORTEX_LOG_CANCEL_TEST_PORT")!;
        string pin = Environment.GetEnvironmentVariable("CORTEX_LOG_CANCEL_TEST_PIN")
            ?? throw new InvalidOperationException("Hardware test PIN is required.");
        string identity = Environment.GetEnvironmentVariable("CORTEX_LOG_CANCEL_TEST_IDENTITY")
            ?? throw new InvalidOperationException("Trusted hardware identity SHA256 is required.");
        using var service = new SerialPortService(port);
        Assert.True(service.Open(), service.LastError);
        try
        {
            SecureInfoResponse? info = await service.GetSecureInfoAsync();
            Assert.NotNull(info);
            Assert.Equal(identity, Convert.ToHexString(SHA256.HashData(info.DevicePublicKey)), ignoreCase: true);
            Assert.True(info.IsProvisioned && info.PinEnabled && info.SupportsPlaintextLogBulk);
            Assert.True(await service.EnsureSecureSessionAsync());
            Assert.Equal(SecureControlResult.Ok, (await service.SecureUnlockAsync(pin))?.Result);
            service.BeginLogTransferSession();
            var files = (await service.RequestLogFileListAsync()).ToList();
            int large = files.FindIndex(file => file.FileName == "2026-10-27_07-53-36.csv");
            int small = files.FindIndex(file => file.FileName == "2026-10-27_07-46-09.csv");
            Assert.True(large >= 0 && small >= 0, "Known closed benchmark logs must be available.");
            object session = typeof(SerialPortService).GetField("_secureSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            byte[] sessionId = ((byte[])session.GetType().GetProperty("SessionId")!.GetValue(session)!).ToArray();

            for (int attempt = 0; attempt < 4; attempt++)
            {
                Assert.True(await service.OpenLogTransferAsync((byte)(attempt == 3 ? small : large)));
                using var cancel = new CancellationTokenSource();
                bool requested = false;
                var elapsed = Stopwatch.StartNew();
                TimeSpan cancelledAt = default;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadLogBulkAsync(progress =>
                {
                    if (!requested && progress >= (attempt == 3 ? 100 : attempt == 2 ? 5 : 1))
                    {
                        requested = true;
                        cancelledAt = elapsed.Elapsed;
                        if (attempt == 1) service.CancelLogTransfer();
                        else cancel.Cancel();
                    }
                }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.True(requested);
                Assert.True(elapsed.Elapsed - cancelledAt < TimeSpan.FromSeconds(2));
                double cancelMilliseconds = (elapsed.Elapsed - cancelledAt).TotalMilliseconds;
                Assert.True(service.IsSecureSessionAuthorized);
                // Match the screen's repeated finally/catch cleanup calls.
                service.CancelLogTransfer();
                service.CancelLogTransfer();
                Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);

                var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnUpdate(DataStructures _)
                {
                    service.BeginLogTransferSession();
                    live.TrySetResult();
                }
                service.DataUpdated += OnUpdate;
                try
                {
                    service.EndLogTransferSession();
                    await live.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    service.DataUpdated -= OnUpdate;
                }

                Assert.True(await service.OpenLogTransferAsync((byte)small));
                string text = await service.ReadLogBulkAsync(null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(files[small].FileSizeBytes, Encoding.ASCII.GetByteCount(text));
                Assert.Equal("994774FF54F199995021FCAD9C29ACEE6B4C22B854B73F5AD58C9A8E4FDAF0AF",
                    Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text))));
                Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);
                Assert.Equal(sessionId, (byte[])session.GetType().GetProperty("SessionId")!.GetValue(session)!);
                output.WriteLine($"Cycle {attempt + 1}: cancel ACK drained in {cancelMilliseconds:F0} ms; live status resumed; different log hash matched; same authorized session.");
            }

            // Restart the original file too, not just a different selection.
            Assert.True(await service.OpenLogTransferAsync((byte)large));
            using var finalCancel = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ReadLogBulkAsync(_ => finalCancel.Cancel(), finalCancel.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);
            Assert.True(await service.SecureLockAsync());
        }
        finally
        {
            service.Close();
        }
    }
}
