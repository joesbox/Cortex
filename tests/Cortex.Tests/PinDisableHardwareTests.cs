using Cortex.Models;
using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace Cortex.Tests;

public sealed class PinDisableHardwareFactAttribute : FactAttribute
{
    public PinDisableHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEX_PIN_DISABLE_CONFIRM") != "DISABLE")
            Skip = "Requires explicit permission to disable the controller PIN.";
    }
}

public sealed class SecurePollingHardwareFactAttribute : FactAttribute
{
    public SecurePollingHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEX_SECURE_POLL_TEST") != "YES")
            Skip = "Requires an explicitly configured controller for secure polling.";
    }
}

public sealed class PinDisableHardwareTests(ITestOutputHelper output)
{
    [SecurePollingHardwareFact]
    public async Task SecurePollingWithCortexStartupWorkload()
    {
        void Progress(string message)
        {
            output.WriteLine(message);
            string? path = Environment.GetEnvironmentVariable("CORTEX_SECURE_POLL_TRACE");
            if (!string.IsNullOrEmpty(path))
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }

        string port = Environment.GetEnvironmentVariable("CORTEX_PIN_DISABLE_PORT")
            ?? throw new InvalidOperationException("CORTEX_PIN_DISABLE_PORT is required.");
        string pin = Environment.GetEnvironmentVariable("CORTEX_PIN_DISABLE_PIN")
            ?? throw new InvalidOperationException("CORTEX_PIN_DISABLE_PIN is required.");
        using var service = new SerialPortService(port);
        Assert.True(service.Open(), service.LastError);
        using var stop = new CancellationTokenSource();
        bool established = false;
        int updates = 0;
        service.DataUpdated += _ => Interlocked.Increment(ref updates);
        Task polling = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!established)
                    established = service.InitComms();
                else
                    service.EnsureLiveRequestPolling();
                await Task.Delay(200);
            }
        });
        try
        {
            await WaitUntilAsync(() => established, "controller handshake");
            Progress("Handshake complete.");
            SecureInfoResponse info = Assert.IsType<SecureInfoResponse>(await service.GetSecureInfoAsync());
            Assert.True(info.PinEnabled, "PIN lock must be enabled for this test.");
            Assert.True(await service.EnsureSecureSessionAsync(), service.LastSecureSessionError);
            Assert.True((await service.SecureUnlockAsync(pin))?.IsAuthorized);
            Progress("Secure session unlocked.");
            service.SetIoFlowStatusPollingEnabled(true);
            service.SetCustomCanStatusPollingEnabled(true);
            service.RequestStaticSnapshot();
            await WaitUntilAsync(() => service.HasLoadedStaticSnapshot, "static snapshot");
            Progress("Static snapshot loaded; requesting firmware version.");
            string? firmware = await service.RequestFirmwareVersionAsync();
            Progress($"Firmware version received: {firmware != null}");
            for (int attempt = 1; attempt <= 12; attempt++)
            {
                await Task.Delay(5000);
                int previousUpdates = Volatile.Read(ref updates);
                Progress($"Starting poll {attempt}: connected={service.foundECU}, authorized={service.IsSecureSessionAuthorized}, updates={previousUpdates}");
                Task<(bool WasRequested, string? Diagnostic)> pending = service.TryPollCellularHealthAsync();
                while (!pending.IsCompleted)
                {
                    await Task.WhenAny(pending, Task.Delay(1000));
                    if (!pending.IsCompleted)
                    {
                        object session = Field(service, "_secureSession");
                        long lastLive = (long)Field(service, "_lastLiveStatusFrameTick");
                        Progress($"Waiting: liveAge={Environment.TickCount64 - lastLive}, updates={Volatile.Read(ref updates)}, suspended={Field(service, "_suspendLiveRequestPolling")}, tx={session.GetType().GetProperty("TxSequence")!.GetValue(session)}, rx={session.GetType().GetProperty("RxSequence")!.GetValue(session)}, frameBytes={((System.Collections.ICollection)Field(service, "receivedDataBuffer")).Count}, secureBytes={((System.Collections.ICollection)Field(service, "_secureIncomingBuffer")).Count}");
                    }
                }
                var (requested, diagnostic) = await pending;
                Progress($"Poll {attempt}: requested={requested}, response={diagnostic != null}, connected={service.foundECU}, authorized={service.IsSecureSessionAuthorized}, updates={Volatile.Read(ref updates)}");
                Assert.True(service.IsSecureSessionAuthorized, "Secure session was lost.");
                Assert.True(requested);
                Assert.NotNull(diagnostic);
                await WaitUntilAsync(() => Volatile.Read(ref updates) > previousUpdates, "live data after health poll");
            }
        }
        finally
        {
            Progress("Stopping polling.");
            stop.Cancel();
            await polling;
            Progress("Polling stopped.");
        }
    }

    [PinDisableHardwareFact]
    public async Task DisablePinAfterLivePolling()
    {
        string port = Environment.GetEnvironmentVariable("CORTEX_PIN_DISABLE_PORT")
            ?? throw new InvalidOperationException("CORTEX_PIN_DISABLE_PORT is required.");
        string pin = Environment.GetEnvironmentVariable("CORTEX_PIN_DISABLE_PIN")
            ?? throw new InvalidOperationException("CORTEX_PIN_DISABLE_PIN is required.");
        using var service = new SerialPortService(port);
        Assert.True(service.Open(), service.LastError);
        using var stop = new CancellationTokenSource();
        bool established = false;
        int updates = 0;
        service.DataUpdated += _ => Interlocked.Increment(ref updates);
        Task polling = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!established)
                    established = service.InitComms();
                else
                    service.EnsureLiveRequestPolling();
                await Task.Delay(100);
            }
        });

        try
        {
            await WaitUntilAsync(() => established, "controller handshake");
            SecureInfoResponse info = Assert.IsType<SecureInfoResponse>(await service.GetSecureInfoAsync());
            Assert.True(info.PinEnabled, "PIN lock is not enabled; no disable was attempted.");
            Assert.True(await service.EnsureSecureSessionAsync(), service.LastSecureSessionError);
            SecureManagementResponse unlock = Assert.IsType<SecureManagementResponse>(await service.SecureUnlockAsync(pin));
            Assert.Equal(SecureControlResult.Ok, unlock.Result);
            Assert.True(unlock.IsAuthorized);
            service.RequestStaticSnapshot();
            await WaitUntilAsync(() => service.HasLoadedStaticSnapshot, "static snapshot");

            var elapsed = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                await Task.Delay(5000);
                int previousUpdates = Volatile.Read(ref updates);
                var (requested, diagnostic) = await service.TryPollCellularHealthAsync();
                object session = Field(service, "_secureSession");
                long live = (long)Field(service, "_lastLiveStatusFrameTick");
                output.WriteLine($"Poll {attempt} at {elapsed.Elapsed.TotalSeconds:F0}s: requested={requested}, response={diagnostic != null}, connected={service.foundECU}, secure={service.IsSecureSessionOpen}, authorized={service.IsSecureSessionAuthorized}, updates={Volatile.Read(ref updates)}, liveAgeMs={Environment.TickCount64 - live}, tx={session.GetType().GetProperty("TxSequence")!.GetValue(session)}, rx={session.GetType().GetProperty("RxSequence")!.GetValue(session)}");
                Assert.True(requested, "Health poll was not sent.");
                Assert.NotNull(diagnostic);
                Assert.True(service.IsSecureSessionAuthorized, "Secure session was lost during health polling.");
                await WaitUntilAsync(() => Volatile.Read(ref updates) > previousUpdates, "live data after health poll");
            }

            Assert.True(service.IsSecureSessionAuthorized, "Secure session was lost before the disable request.");
            SecureManagementResponse result = Assert.IsType<SecureManagementResponse>(await service.SecureDisablePinAsync());
            Assert.Equal(SecureControlResult.Ok, result.Result);
            Assert.False(result.PinEnabled);
            output.WriteLine("Controller confirmed PIN disabled.");
        }
        finally
        {
            stop.Cancel();
            await polling;
        }
    }

    private static object Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static async Task WaitUntilAsync(Func<bool> condition, string stage)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(50);
        Assert.True(condition(), $"Timed out waiting for {stage}.");
    }
}
