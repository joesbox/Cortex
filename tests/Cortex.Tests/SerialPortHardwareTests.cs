using Cortex.Models;
using Cortex.Services;
using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Cortex.Tests;

public sealed class PdmHardwareFactAttribute : FactAttribute
{
    public PdmHardwareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CORTEX_PDM_TEST_PORT")))
            Skip = "Requires explicit CORTEX_PDM_TEST_PORT and dedicated joined-load fixture.";
    }
}

public class SerialPortHardwareTests
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static T Field<T>(SerialPortService service, string name) =>
        (T)(typeof(SerialPortService).GetField(name, PrivateInstance)?.GetValue(service)
            ?? throw new InvalidOperationException($"Missing client field {name}"));

    private static void SetField(SerialPortService service, string name, object value) =>
        (typeof(SerialPortService).GetField(name, PrivateInstance)
            ?? throw new InvalidOperationException($"Missing client field {name}")).SetValue(service, value);

    private static DataStructures Snapshot(DataStructures data)
    {
        string serialized = ConfigFileSerializer.SerializeSettings(data);
        Assert.True(ConfigFileSerializer.TryDeserialize(serialized, out var snapshot, out var error), error);
        var result = new DataStructures();
        ConfigFileSerializer.ApplySnapshot(result, snapshot!);
        for (int channel = 0; channel < 14; channel++)
        {
            var source = data.ChannelsLiveData[channel];
            var target = result.ChannelsStaticData[channel];
            target.MathRuleCount = source.MathRuleCount;
            target.MathRootRule = source.MathRootRule;
            target.MathOutputMode = source.MathOutputMode;
            for (int index = 0; index < 8; index++)
                foreach (string name in new[] { "Operation", "HysteresisEnabled", "OnThreshold", "OffThreshold",
                             "OperandAType", "OperandAIndex", "OperandAConstant", "OperandBType", "OperandBIndex", "OperandBConstant" })
                {
                    var property = typeof(OutputChannel.MathRule).GetProperty(name)!;
                    property.SetValue(target.MathRules[index], property.GetValue(source.MathRules[index]));
                }
        }
        return result;
    }

    private static async Task Until(SerialPortService service, Func<bool> condition, int timeoutMs = 15000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.ElapsedMilliseconds < timeoutMs,
                $"Client wait timed out after {timeoutMs}ms; foundECU={service.foundECU}; staticPending={service.UpdateStaticData}; " +
                $"staticSent={Field<long>(service, "_lastStaticRequestSentTick")}; mathFrame={Field<long>(service, "_lastMathStatusFrameTick")}; " +
                $"buffer={Field<List<byte>>(service, "receivedDataBuffer").Count}; lastError={service.LastError}");
            service.EnsureLiveRequestPolling(250);
            await Task.Delay(25);
        }
    }

    private static async Task Save(SerialPortService service, DataStructures desired)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ConfigurationSaveCompletedEventArgs> handler = (_, args) => completion.TrySetResult(args.Succeeded);
        service.ConfigurationSaveCompleted += handler;
        try
        {
            service.UpdateSettingsData(desired);
            service.StartSendConfig();
            Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(60)), "Cortex configuration save failed");
        }
        finally
        {
            service.ConfigurationSaveCompleted -= handler;
        }
    }

    [PdmHardwareFact]
    public async Task RealClient_SaveReadbackPollingReconnectAndLogAccess()
    {
        string port = Environment.GetEnvironmentVariable("CORTEX_PDM_TEST_PORT")!;
        string reportPath = Environment.GetEnvironmentVariable("CORTEX_PDM_TEST_REPORT")
            ?? throw new InvalidOperationException("CORTEX_PDM_TEST_REPORT is required for the pre-write backup");
        string loadPort = Environment.GetEnvironmentVariable("CORTEX_TENMA_TEST_PORT")
            ?? throw new InvalidOperationException("CORTEX_TENMA_TEST_PORT is required");
        var report = new Dictionary<string, object?> { ["startedUtc"] = DateTimeOffset.UtcNow,
            ["scope"] = "actual Cortex SerialPortService on dedicated PDM; not GUI automation", ["passed"] = false };
        var checks = new List<string>();
        report["checks"] = checks;
        void Record() => File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        using var load = new SerialPort(loadPort, 9600) { ReadTimeout = 2000, WriteTimeout = 2000, NewLine = "\n" };
        load.Open();
        void LoadOff()
        {
            load.DiscardInBuffer();
            load.Write(":INP 0\r\n:INP?\r\n");
            Assert.Equal("OFF", load.ReadLine().Trim());
        }
        LoadOff();
        var service = new SerialPortService(port);
        DataStructures? backup = null;
        bool touched = false;
        int liveUpdates = 0;
        service.DataUpdated += _ => Interlocked.Increment(ref liveUpdates);
        try
        {
            Assert.True(service.Open(), service.LastError);
            service.InitComms();
            await Until(service, () => service.foundECU && !service.UpdateStaticData && Volatile.Read(ref liveUpdates) > 0);
            checks.Add("connection and static snapshot through real Cortex client");
            Assert.Equal("v0.11", await service.RequestFirmwareVersionAsync());
            service.SetMathStatusPollingChannel(0);
            await Until(service, () => Field<long>(service, "_lastMathStatusFrameTick") > 0);
            var data = Field<DataStructures>(service, "dataStructures");
            Assert.Equal(OutputChannel.ChannelType.MATH, data.ChannelsLiveData[0].ChanType);
            Assert.True(data.ChannelsLiveData[0].MathRuleCount < 8 && data.ChannelsLiveData[0].MathRootRule != 7,
                "Rule 7 must be unused for this harmless parameter round-trip");
            backup = Snapshot(data);
            report["backupConfiguration"] = ConfigFileSerializer.SerializeSettings(backup);
            Record();
            float original = backup.ChannelsStaticData[0].MathRules[7].OperandAConstant;
            float marker = original == 123.5f ? 124.5f : 123.5f;
            var desired = Snapshot(data);
            desired.ChannelsStaticData[0].MathRules[7].OperandAConstant = marker;
            touched = true;
            await Save(service, desired);
            SetField(service, "_lastMathStatusRequestSentTick", 0L);
            await Until(service, () => data.ChannelsLiveData[0].MathRules[7].OperandAConstant == marker);
            checks.Add("unused math-rule parameter saved and read back through real Cortex client");
            int initialUpdates = Volatile.Read(ref liveUpdates);
            long initialMath = Field<long>(service, "_lastMathStatusFrameTick");
            var interval = Stopwatch.StartNew();
            while (interval.Elapsed < TimeSpan.FromSeconds(10))
            {
                service.EnsureLiveRequestPolling(250);
                await Task.Delay(25);
            }
            Assert.True(Volatile.Read(ref liveUpdates) >= initialUpdates + 10);
            Assert.True(Field<long>(service, "_lastMathStatusFrameTick") > initialMath);
            checks.Add("live and math status updates continue during ten-second real-client polling");
            await Save(service, backup);
            SetField(service, "_lastMathStatusRequestSentTick", 0L);
            await Until(service, () => data.ChannelsLiveData[0].MathRules[7].OperandAConstant == original);
            touched = false;
            checks.Add("original parameter restored and saved through Cortex");
            service.Close();
            Assert.True(service.Open(), service.LastError);
            service.InitComms();
            await Until(service, () => service.foundECU && !service.UpdateStaticData);
            Assert.Equal("v0.11", await service.RequestFirmwareVersionAsync());
            checks.Add("close/reopen and controller reconnection");
            var files = await service.RequestLogFileListAsync();
            Assert.NotNull(files);
            report["logFiles"] = files.Select(file => new { file.FileName, file.FileSizeBytes }).ToArray();
            checks.Add("log-file listing through Cortex");
            if (files.Count > 0)
            {
                service.BeginLogTransferSession();
                try
                {
                    Assert.True(await service.OpenLogTransferAsync(0));
                    var chunk = await service.RequestLogChunkAsync();
                    Assert.NotNull(chunk);
                    report["logChunk"] = new { chunk!.Progress, chunk.Done, textCharacters = chunk.Text.Length };
                    checks.Add("log open and first chunk received through Cortex");
                }
                finally
                {
                    service.CancelLogTransfer();
                    service.EndLogTransferSession();
                }
                long previousFrame = Field<long>(service, "_lastLiveStatusFrameTick");
                await Until(service, () => Field<long>(service, "_lastLiveStatusFrameTick") > previousFrame);
                checks.Add("live updates resume after cancelling log transfer");
            }
            else
            {
                report["skipped"] = new[] { "Log payload transfer: controller has no log files" };
            }
            report["passed"] = true;
        }
        catch (Exception error)
        {
            report["error"] = error.ToString();
            throw;
        }
        finally
        {
            LoadOff();
            if (touched && backup != null)
            {
                await Save(service, backup);
                report["cleanupRestoreSaved"] = true;
            }
            service.Close();
            Field<System.Timers.Timer>(service, "_processTimer").Dispose();
            report["completedUtc"] = DateTimeOffset.UtcNow;
            Record();
        }
    }
}