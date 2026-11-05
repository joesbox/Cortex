using Cortex.Models;
using Cortex.Services;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Cortex.Tests;

public sealed class PinLockHardwareFactAttribute : FactAttribute
{
    public PinLockHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEX_PIN_LOCK_TEST_CONFIRM") != "DISABLE_AND_REENABLE_PIN")
            Skip = "Requires explicit permission to disable and re-enable the PIN (rotates the recovery key).";
    }
}

// Opt-in: cycles the PIN on and off while live polling runs, as Cortex does, then restores the
// starting state. Each enable rotates the recovery key; the latest is written to CORTEX_PIN_LOCK_TEST_RECOVERY_FILE.
public class PinLockHardwareTests(ITestOutputHelper output)
{
    [PinLockHardwareFact]
    public async Task RealClient_DisableAndReenablePinWithLivePolling()
    {
        string pin = Required("CORTEX_PIN_LOCK_TEST_PIN");
        string recoveryFile = Required("CORTEX_PIN_LOCK_TEST_RECOVERY_FILE");
        int cycles = int.TryParse(Environment.GetEnvironmentVariable("CORTEX_PIN_LOCK_TEST_CYCLES"), out int c) ? c : 1;

        using var service = new SerialPortService(Required("CORTEX_PIN_LOCK_TEST_PORT"));
        Assert.True(service.Open(), service.LastError);
        using var pollingStop = new CancellationTokenSource();
        int liveUpdates = 0;
        bool commsEstablished = false;
        service.DataUpdated += _ => Interlocked.Increment(ref liveUpdates);
        Task polling = Task.Run(async () =>
        {
            while (!pollingStop.IsCancellationRequested)
            {
                if (!commsEstablished)
                    commsEstablished = service.InitComms();
                else
                    service.EnsureLiveRequestPolling();
                await Task.Delay(100);
            }
        });

        bool pinEnabled = true;
        bool initiallyEnabled = true;
        try
        {
            await ExpectLiveData("after comms established");
            SecureInfoResponse? info = await service.GetSecureInfoAsync();
            Assert.NotNull(info);
            Assert.Equal(Required("CORTEX_PIN_LOCK_TEST_IDENTITY"),
                Convert.ToHexString(SHA256.HashData(info.DevicePublicKey)), ignoreCase: true);
            initiallyEnabled = pinEnabled = info.PinEnabled;
            Assert.True(await service.EnsureSecureSessionAsync());
            if (pinEnabled)
                Assert.Equal(SecureControlResult.Ok, (await service.SecureUnlockAsync(pin))?.Result);
            await ExpectLiveData("authorised before cycle 1");

            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                if (pinEnabled)
                {
                    await Disable(cycle);
                    await Enable(cycle);
                }
                else
                {
                    await Enable(cycle);
                    await Disable(cycle);
                }

                output.WriteLine($"Cycle {cycle}: PIN disable/re-enable with live polling OK.");
            }

            if (pinEnabled)
                Assert.True(await service.SecureLockAsync());
        }
        finally
        {
            if (initiallyEnabled && !pinEnabled)
            {
                output.WriteLine("PIN left disabled; attempting to re-enable.");
                if (await service.EnsureSecureSessionAsync())
                {
                    PinSetupResult restore = await service.SecureSetPinAsync(pin);
                    if (restore.Success)
                        SaveRecoveryKey(recoveryFile, restore.RecoveryKey!);
                    output.WriteLine($"Re-enable {(restore.Success ? "succeeded" : "FAILED: " + restore.ErrorMessage)}.");
                }
            }
            else if (!initiallyEnabled && pinEnabled)
            {
                output.WriteLine("PIN left enabled; attempting to disable.");
                SecureManagementResponse? restore = await service.SecureDisablePinAsync();
                output.WriteLine($"Disable {(restore?.Result == SecureControlResult.Ok ? "succeeded" : "FAILED")}.");
            }

            pollingStop.Cancel();
            await polling;
            service.Close();
        }

        async Task Disable(int cycle)
        {
            SecureManagementResponse? disable = await service.SecureDisablePinAsync();
            Assert.Equal(SecureControlResult.Ok, disable?.Result);
            pinEnabled = false;
            Assert.False(service.IsSecureSessionOpen);
            await ExpectLiveData($"cycle {cycle} with PIN disabled");
        }

        async Task Enable(int cycle)
        {
            Assert.True(await service.EnsureSecureSessionAsync(), $"cycle {cycle}: secure session before set PIN");
            await ExpectLiveData($"cycle {cycle} on secure session with PIN disabled");
            PinSetupResult setup = await service.SecureSetPinAsync(pin);
            Assert.True(setup.Success, $"cycle {cycle}: {setup.ErrorMessage}");
            pinEnabled = true;
            SaveRecoveryKey(recoveryFile, setup.RecoveryKey!);
            Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);
            await ExpectLiveData($"cycle {cycle} after enabling PIN");
        }

        async Task ExpectLiveData(string stage)
        {
            int start = Volatile.Read(ref liveUpdates);
            var timeout = Task.Delay(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref liveUpdates) < start + 3 && !timeout.IsCompleted)
                await Task.Delay(50);
            Assert.True(Volatile.Read(ref liveUpdates) >= start + 3, $"No live data {stage}.");
        }
    }

    private void SaveRecoveryKey(string path, string recoveryKey)
    {
        File.WriteAllText(path, recoveryKey + Environment.NewLine);
        output.WriteLine("New recovery key saved to the recovery file.");
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");
}
