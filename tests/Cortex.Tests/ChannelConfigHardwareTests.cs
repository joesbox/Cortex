using Cortex.Models;
using Cortex.Services;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Cortex.Tests;

public sealed class ChannelConfigHardwareFactAttribute : FactAttribute
{
    public ChannelConfigHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEX_CHANNEL_CONFIG_TEST_CONFIRM") != "TEMPORARY_CHANNEL_NAME")
            Skip = "Requires explicit permission for a temporary channel-name change and restoration.";
    }
}

public sealed class OutputClassHardwareFactAttribute : FactAttribute
{
    public OutputClassHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEX_CHANNEL_CONFIG_TEST_CLASS") != "TEMPORARY_OUTPUT_CLASS")
            Skip = "Requires explicit permission for a temporary output-class change and restoration.";
    }
}

public class ChannelConfigHardwareTests(ITestOutputHelper output)
{
    [OutputClassHardwareFact]
    public async Task RealClient_OutputClassSaveWithLivePollingAndRestore()
    {
        using var service = new SerialPortService(Required("CORTEX_CHANNEL_CONFIG_TEST_PORT"));
        Assert.True(service.Open(), service.LastError);
        try
        {
            var info = await service.GetSecureInfoAsync();
            Assert.NotNull(info);
            Assert.Equal(Required("CORTEX_CHANNEL_CONFIG_TEST_IDENTITY"),
                Convert.ToHexString(SHA256.HashData(info.DevicePublicKey)), ignoreCase: true);
            Assert.True(await service.EnsureSecureSessionAsync());
            Assert.Equal(SecureControlResult.Ok,
                (await service.SecureUnlockAsync(Required("CORTEX_CHANNEL_CONFIG_TEST_PIN")))?.Result);
            service.BeginLogTransferSession();
            DataStructures data = Field<DataStructures>(service, "dataStructures");
            await Snapshot(service);
            int channel = data.ChannelsLiveData.ToList().FindIndex(c => c.Category == OutputChannel.ChannelCategory.HVACBlower);
            if (channel < 0)
                channel = data.ChannelsLiveData.ToList().FindIndex(c => c.Category == OutputChannel.ChannelCategory.HeatedSteeringWheel);
            Assert.True(channel >= 0, "An HVAC blower or heated-steering-wheel channel is required.");
            var original = data.ChannelsLiveData[channel].Category;
            service.EndLogTransferSession();
            try
            {
                if (original != OutputChannel.ChannelCategory.HVACBlower)
                    await SaveClass(service, data, channel, OutputChannel.ChannelCategory.HVACBlower);
                await SaveClass(service, data, channel, OutputChannel.ChannelCategory.HeatedSteeringWheel);
                output.WriteLine($"Channel {channel + 1}: HVAC Blower -> Heated Steering Wheel saved and read back with live polling.");
            }
            finally
            {
                await SaveClass(service, data, channel, original);
                output.WriteLine($"Channel {channel + 1}: original output class restored, saved and read back.");
            }
            service.BeginLogTransferSession();
            await Task.Delay(250);
            Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);
            Assert.True(await service.SecureLockAsync());
        }
        finally { service.Close(); }
    }

    private async Task SaveClass(SerialPortService service, DataStructures data, int channel, OutputChannel.ChannelCategory category)
    {
        int expectedPackets = data.ChannelsLiveData[channel].Category == category ? 0 : 1;
        data.ChannelsStaticData[channel].Category = category;
        service.UpdateSettingsData(data);
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSaved(object? sender, ConfigurationSaveCompletedEventArgs args) => saved.TrySetResult(args.Succeeded);
        void OnData(DataStructures _)
        {
            if (saved.Task.IsCompletedSuccessfully && !service.UpdateStaticData)
            {
                service.BeginLogTransferSession();
                snapshot.TrySetResult();
            }
        }
        service.ConfigurationSaveCompleted += OnSaved;
        service.DataUpdated += OnData;
        try
        {
            service.EndLogTransferSession();
            service.StartSendConfig();
            Assert.True(await saved.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                $"Save failed: lastCommand={Field<char>(service, "lastCommandSent")} sending={Field<bool>(service, "sendingConfig")} packets={Field<int>(service, "_configPacketsSent")}");
            await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(category, data.ChannelsLiveData[channel].Category);
            Assert.Equal(expectedPackets, Field<int>(service, "_configPacketsSent"));
        }
        catch (TimeoutException)
        {
            output.WriteLine($"Timed out: lastCommand={Field<char>(service, "lastCommandSent")} sending={Field<bool>(service, "sendingConfig")} packets={Field<int>(service, "_configPacketsSent")}");
            throw;
        }
        finally
        {
            service.ConfigurationSaveCompleted -= OnSaved;
            service.DataUpdated -= OnData;
        }
    }

    [ChannelConfigHardwareFact]
    public async Task RealClient_ChannelNameSaveReadbackAndRestore()
    {
        using var service = new SerialPortService(Required("CORTEX_CHANNEL_CONFIG_TEST_PORT"));
        Assert.True(service.Open(), service.LastError);
        try
        {
            var info = await service.GetSecureInfoAsync();
            Assert.NotNull(info);
            Assert.Equal(Required("CORTEX_CHANNEL_CONFIG_TEST_IDENTITY"),
                Convert.ToHexString(SHA256.HashData(info.DevicePublicKey)), ignoreCase: true);
            Assert.True(info.PinEnabled && info.IsProvisioned);
            Assert.True(await service.EnsureSecureSessionAsync());
            Assert.Equal(SecureControlResult.Ok,
                (await service.SecureUnlockAsync(Required("CORTEX_CHANNEL_CONFIG_TEST_PIN")))?.Result);
            service.BeginLogTransferSession();
            DataStructures data = Field<DataStructures>(service, "dataStructures");
            await Snapshot(service);
            char[] original = data.ChannelsLiveData[0].Name!.ToArray();
            char[] temporary = original.SequenceEqual("TST".ToCharArray()) ? "CHK".ToCharArray() : "TST".ToCharArray();
            try
            {
                await SaveName(service, data, temporary);
                Assert.Equal(temporary, data.ChannelsLiveData[0].Name);
                Assert.True(service.IsSecureSessionAuthorized);
                output.WriteLine("Temporary channel name saved and confirmed by controller static readback.");
            }
            finally
            {
                await SaveName(service, data, original);
                Assert.Equal(original, data.ChannelsLiveData[0].Name);
                output.WriteLine("Original channel name restored, saved and confirmed by static readback.");
            }
            Assert.True((await service.GetSecureStatusAsync())?.IsAuthorized);
            Assert.True(await service.SecureLockAsync());
        }
        finally
        {
            service.Close();
        }
    }

    private static async Task SaveName(SerialPortService service, DataStructures data, char[] name)
    {
        data.ChannelsStaticData[0].Name = name.ToArray();
        service.UpdateSettingsData(data);
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSaved(object? sender, ConfigurationSaveCompletedEventArgs args) => saved.TrySetResult(args.Succeeded);
        void OnData(DataStructures _) { if (!service.UpdateStaticData) snapshot.TrySetResult(); }
        service.ConfigurationSaveCompleted += OnSaved;
        service.DataUpdated += OnData;
        try
        {
            service.StartSendConfig();
            Assert.True(await saved.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Field<int>(service, "_configPacketsSent"));
        }
        finally
        {
            service.ConfigurationSaveCompleted -= OnSaved;
            service.DataUpdated -= OnData;
        }
    }

    private static async Task Snapshot(SerialPortService service)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnData(DataStructures _) { if (!service.UpdateStaticData) completion.TrySetResult(); }
        service.DataUpdated += OnData;
        try
        {
            service.RequestStaticSnapshot();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { service.DataUpdated -= OnData; }
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");
}
