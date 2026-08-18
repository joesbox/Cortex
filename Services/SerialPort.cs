using Cortex.Models;
using Cortex.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Cortex.Models.OutputChannel;


public sealed class ConfigurationSaveCompletedEventArgs : EventArgs
{
    public ConfigurationSaveCompletedEventArgs(bool succeeded)
    {
        Succeeded = succeeded;
    }

    public bool Succeeded { get; }
}

public class SerialPortService
{
    private static readonly bool EnableLogTransferDebug = false;
    private const int ExtendedLiveSystemPayloadBytes = 23;
    private const int ExtendedStaticSystemPayloadBytes = 61;
    private const int StaticTimeZoneRulePayloadBytes = Constants.TIME_ZONE_RULE_LENGTH;
    private const int StaticCellularPayloadBytes = Constants.CELLULAR_STATIC_PAYLOAD_BYTES;
    private const int StaticSnapshotRetryIntervalMs = 1500;
    private const int StandaloneCommandDrainDelayMs = 25;
    private const int OpenRemoteProvisioningMaxRequestBytes = 8191;

    public event Action<DataStructures>? DataUpdated;

    public event EventHandler<EventArgs>? ConfigurationSaved;

    public event EventHandler<ConfigurationSaveCompletedEventArgs>? ConfigurationSaveCompleted;

    private SerialPort _serialPort;

    private DataStructures dataStructures;

    private DataStructures settingsData;

    private List<byte> receivedDataBuffer;
    private bool foundTrailer1;
    private UInt32 pdmCheckSum;
    private char lastCommandSent;
    private bool sendingConfig;
    private bool saveToEEPROM;
    public bool foundECU;
    private int totalBytesSent;
    private int checkSumSend;

    private int _configRetryCount;
    private int _saveRetryCount;
    private readonly Stopwatch _configSendStopwatch = new Stopwatch();
    private int _configPacketsSent;
    private int _configSlotsSkippedLocally;

    /// <summary>
    /// Setting index: 0 = channel, 1 = analogue input, 2 = system, 3 = digital, 4 = cellular.
    /// Analogue inputs are sent before channels so channel type changes that depend
    /// on analogue-input digital mode are accepted by the controller in one save.
    /// </summary>
    private int settingIndex;

    /// <summary>
    /// Parameter index within setting: e.g. for channel data, 0 = type, 1 = current limit high, etc.
    /// </summary>
    private int parameterIndex;

    private int channelIndex;
    private int analogueIndex;
    private int digitalIndex;

    private TaskCompletionSource<IReadOnlyList<ControllerLogFileInfo>>? _logFileListTcs;
    private TaskCompletionSource<bool>? _logOpenTcs;
    private TaskCompletionSource<bool>? _logResetTcs;
    private TaskCompletionSource<LogChunkResponse>? _logChunkTcs;
    private TaskCompletionSource<string>? _firmwareVersionTcs;
    private TaskCompletionSource<string>? _buildDateTcs;
    private TaskCompletionSource<string>? _firmwareDiagnosticTcs;
    private TaskCompletionSource<string>? _cellularTestTcs;
    private TaskCompletionSource<string>? _openRemoteProvisioningTcs;
    private TaskCompletionSource<bool>? _standaloneCommandAckTcs;
    private char _standaloneCommandAckId;
    private readonly Queue<LogChunkResponse> _pendingLogChunks = new Queue<LogChunkResponse>();
    private readonly object _logChunkLock = new object();
    private readonly SemaphoreSlim _logChunkRequestLock = new SemaphoreSlim(1, 1);
    private volatile bool _suspendLiveRequestPolling;
    private volatile bool _logTransferSessionActive;
    private long _lastLiveRequestSentTick;
    private long _lastLiveStatusFrameTick;
    private long _lastStaticRequestSentTick;
    private long _lastHandshakeAttemptTick;
    private int _lastChunkProgressLogged = -1;
    private int _txLogChunkCounter = 0;
    private long _lastPendingFrameLogTick = 0;
    private int _lastPendingFrameLogBytes = -1;
    private bool _awaitingPostLogRefreshStatusFrame;
    private int _postLogRefreshRequestRetryCount;
    private bool _repeatAnalogueSettingsAfterChannelsPending;
    private bool _finalAnalogueResendActive;
    private ControllerProtocolCapabilities _controllerCapabilities = ControllerProtocolCapabilities.V09;


    public bool UpdateStaticData = false;

    private List<byte> _sendBuffer = new List<byte>();

    // Timer to process received packets off the serial thread at a 10ms interval
    private readonly System.Timers.Timer _processTimer;
    private readonly object _bufferLock = new object();
    private volatile bool _packetReady = false;
    private volatile bool _bulkLogTransferActive = false;

    private byte[] dataBytes = new byte[4096];
    private byte[] checkSumArray = new byte[4];
    private int dataLength;

    public string? LastError { get; private set; }

    private static void LogProtocolDebug(string message)
    {
        if (!EnableLogTransferDebug)
        {
            return;
        }

        string fullMessage = $"[LOGDBG] {message}";
        Debug.WriteLine(fullMessage);
    }

    private void SetBulkTransferReaderActive(bool active)
    {
        _bulkLogTransferActive = active;
        if (active)
        {
            _processTimer.Stop();
            _serialPort.DataReceived -= OnDataReceived;
            return;
        }

        _serialPort.DataReceived -= OnDataReceived;
        _serialPort.DataReceived += OnDataReceived;
        _processTimer.Start();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        while (count > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count), token);
            if (read <= 0)
            {
                throw new IOException("Serial stream closed during log transfer.");
            }

            offset += read;
            count -= read;
        }
    }

    private void LogBufferedDataState(string context)
    {
        byte[] snapshot;
        lock (_bufferLock)
        {
            snapshot = receivedDataBuffer.ToArray();
        }

        if (snapshot.Length == 0)
        {
            LogProtocolDebug($"{context}: buffer empty");
            return;
        }

        int headerIndex = -1;
        int trailerIndex = -1;
        for (int i = 0; i <= snapshot.Length - 2; i++)
        {
            if (headerIndex < 0 && snapshot[i] == Constants.SERIAL_HEADER1 && snapshot[i + 1] == Constants.SERIAL_HEADER2)
            {
                headerIndex = i;
            }

            if (trailerIndex < 0 && snapshot[i] == Constants.SERIAL_TRAILER1 && snapshot[i + 1] == Constants.SERIAL_TRAILER2)
            {
                trailerIndex = i;
            }

            if (headerIndex >= 0 && trailerIndex >= 0)
            {
                break;
            }
        }

        string headHex = BitConverter.ToString(snapshot, 0, Math.Min(12, snapshot.Length));
        int tailLen = Math.Min(12, snapshot.Length);
        string tailHex = BitConverter.ToString(snapshot, snapshot.Length - tailLen, tailLen);
        LogProtocolDebug($"{context}: bytes={snapshot.Length} headerIdx={headerIndex} trailerIdx={trailerIndex} head={headHex} tail={tailHex}");
    }

    private static bool BufferContainsTrailer(IReadOnlyList<byte> buffer)
    {
        if (buffer.Count < 2)
        {
            return false;
        }

        for (int i = 0; i <= buffer.Count - 2; i++)
        {
            if (buffer[i] == Constants.SERIAL_TRAILER1 && buffer[i + 1] == Constants.SERIAL_TRAILER2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExpectsFramedResponse(char commandId)
    {
        return commandId == Constants.COMMAND_ID_REQUEST ||
               commandId == Constants.COMMAND_ID_REQUEST_STATIC ||
               commandId == Constants.COMMAND_ID_LOG_LIST ||
               commandId == Constants.COMMAND_ID_LOG_CHUNK ||
               commandId == Constants.COMMAND_ID_LOG_STREAM ||
               commandId == Constants.COMMAND_ID_FW_VER ||
               commandId == Constants.COMMAND_ID_BUILD_DATE ||
               commandId == Constants.COMMAND_ID_FW_DIAGNOSTIC ||
               commandId == Constants.COMMAND_ID_CELLULAR_TEST ||
               commandId == Constants.COMMAND_ID_OPENREMOTE_PROVISION;
    }

    private void TrackFramedResponseByte(byte readByte)
    {
        if (foundTrailer1)
        {
            if (readByte == Constants.SERIAL_TRAILER2)
            {
                _packetReady = true;
            }

            foundTrailer1 = (readByte == Constants.SERIAL_TRAILER1);
        }
        else if (readByte == Constants.SERIAL_TRAILER1)
        {
            foundTrailer1 = true;
        }
    }

    private void ResetLogReceiveState(bool discardSerialInput)
    {
        if (discardSerialInput && _serialPort.IsOpen)
        {
            try
            {
                _serialPort.DiscardInBuffer();
            }
            catch
            {
            }
        }

        if (_serialPort.IsOpen)
        {
            try
            {
                _serialPort.DiscardOutBuffer();
            }
            catch
            {
            }
        }

        lock (_bufferLock)
        {
            receivedDataBuffer.Clear();
        }

        foundTrailer1 = false;
        _packetReady = false;
        lastCommandSent = '\0';
        _lastLiveRequestSentTick = 0;
        _lastLiveStatusFrameTick = 0;
        _lastStaticRequestSentTick = 0;
        _lastHandshakeAttemptTick = 0;

        lock (_logChunkLock)
        {
            _pendingLogChunks.Clear();
        }
    }

    private void MarkControllerSessionLost(bool requestStaticSnapshot)
    {
        foundECU = false;
        _controllerCapabilities = ControllerProtocolCapabilities.V09;
        _lastLiveRequestSentTick = 0;
        _lastLiveStatusFrameTick = 0;
        _lastStaticRequestSentTick = 0;

        if (requestStaticSnapshot)
        {
            UpdateStaticData = true;
        }
    }

    private bool TryDequeuePendingLogChunk(out LogChunkResponse chunk)
    {
        lock (_logChunkLock)
        {
            if (_pendingLogChunks.Count > 0)
            {
                chunk = _pendingLogChunks.Dequeue();
                return true;
            }
        }

        chunk = null!;
        return false;
    }

    private void PublishOrQueueLogChunk(LogChunkResponse chunk)
    {
        TaskCompletionSource<LogChunkResponse>? pendingTcs;
        lock (_logChunkLock)
        {
            pendingTcs = _logChunkTcs;
            if (pendingTcs == null)
            {
                _pendingLogChunks.Enqueue(chunk);
                return;
            }
        }

        if (!pendingTcs.TrySetResult(chunk))
        {
            lock (_logChunkLock)
            {
                _pendingLogChunks.Enqueue(chunk);
            }
        }
    }

    private bool TryRecoverBufferedLogChunk(out LogChunkResponse chunk)
    {
        if (TryDequeuePendingLogChunk(out chunk))
        {
            return true;
        }

        int pendingBytes;
        lock (_bufferLock)
        {
            pendingBytes = receivedDataBuffer.Count;
        }

        if (pendingBytes <= 0)
        {
            chunk = null!;
            return false;
        }

        try
        {
            for (int i = 0; i < 8; i++)
            {
                if (!processData())
                {
                    break;
                }

                if (TryDequeuePendingLogChunk(out chunk))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return TryDequeuePendingLogChunk(out chunk);
    }

    private async Task<LogChunkResponse?> AwaitLogChunkAsync(TaskCompletionSource<LogChunkResponse> tcs, int timeoutMs, string operationName)
    {
        try
        {
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask == tcs.Task)
            {
                return await tcs.Task;
            }

            if (TryRecoverBufferedLogChunk(out var recoveredChunk))
            {
                LogProtocolDebug($"{operationName} recovered queued chunk after timeout");
                return recoveredChunk;
            }

            int pendingBytes;
            lock (_bufferLock)
            {
                pendingBytes = receivedDataBuffer.Count;
            }

            if (pendingBytes > 0)
            {
                LogProtocolDebug($"{operationName} timeout with pending={pendingBytes}; waiting for completion grace");
                LogBufferedDataState($"{operationName} timeout snapshot");
                int lastPendingBytes = pendingBytes;

                for (int graceAttempt = 0; graceAttempt < 4; graceAttempt++)
                {
                    if (TryRecoverBufferedLogChunk(out recoveredChunk))
                    {
                        LogProtocolDebug($"{operationName} recovered during grace attempt={graceAttempt + 1}");
                        return recoveredChunk;
                    }

                    if (tcs.Task.IsCompleted)
                    {
                        return await tcs.Task;
                    }

                    Task graceCompleted = await Task.WhenAny(tcs.Task, Task.Delay(80));
                    if (graceCompleted == tcs.Task)
                    {
                        return await tcs.Task;
                    }

                    lock (_bufferLock)
                    {
                        pendingBytes = receivedDataBuffer.Count;
                    }

                    if (pendingBytes <= 0 || pendingBytes == lastPendingBytes)
                    {
                        break;
                    }

                    lastPendingBytes = pendingBytes;
                }

                LogBufferedDataState($"{operationName} post-grace snapshot");
            }

            LogProtocolDebug($"{operationName} timeout/cancel");
            return null;
        }
        catch
        {
            LogProtocolDebug($"{operationName} timeout/cancel");
            return null;
        }
    }

    private async Task<LogChunkResponse?> WaitForQueuedLogChunkAsync(int timeoutMs, string operationName)
    {
        long deadlineTick = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadlineTick)
        {
            if (TryRecoverBufferedLogChunk(out var chunk))
            {
                return chunk;
            }

            int delayMs = (int)Math.Min(20, Math.Max(1, deadlineTick - Environment.TickCount64));
            await Task.Delay(delayMs);
        }

        if (TryRecoverBufferedLogChunk(out var recoveredChunk))
        {
            LogProtocolDebug($"{operationName} recovered queued chunk at timeout boundary");
            return recoveredChunk;
        }

        int pendingBytes;
        lock (_bufferLock)
        {
            pendingBytes = receivedDataBuffer.Count;
        }

        if (pendingBytes > 0)
        {
            LogProtocolDebug($"{operationName} timed out with pending={pendingBytes}");
            LogBufferedDataState($"{operationName} timeout snapshot");
        }

        return null;
    }

    public void BeginLogTransferSession()
    {
        if (_logTransferSessionActive)
        {
            return;
        }

        ResetLogReceiveState(discardSerialInput: true);

        _logTransferSessionActive = true;
        _suspendLiveRequestPolling = true;
        _txLogChunkCounter = 0;
        _lastChunkProgressLogged = -1;
        LogProtocolDebug("LOG session begin");
    }

    public void EndLogTransferSession()
    {
        if (!_logTransferSessionActive)
        {
            return;
        }

        _logTransferSessionActive = false;
        _suspendLiveRequestPolling = false;
        ResetLogReceiveState(discardSerialInput: false);

        if (_serialPort.IsOpen && !sendingConfig)
        {
            SendCommand(Constants.COMMAND_ID_REQUEST);
        }

        LogProtocolDebug("LOG session end");
    }

    public SerialPortService(string portName, int baudRate = 921600)
    {
        _serialPort = new SerialPort(portName, baudRate);
        _serialPort.DataBits = 8;
        _serialPort.RtsEnable = true;
        _serialPort.DtrEnable = true;
        _serialPort.DataReceived += OnDataReceived;
        receivedDataBuffer = new List<byte>();
        dataStructures = new DataStructures();
        settingsData = new DataStructures();
        dataBytes = Array.Empty<byte>();
        // create and start the processing timer
        _processTimer = new System.Timers.Timer(2);
        _processTimer.AutoReset = true;
        _processTimer.Elapsed += (s, e) =>
        {
            bool shouldProcess = _packetReady;

            // During log transfer, opportunistically parse buffered framed data
            // even if trailer-edge detection missed flipping _packetReady.
            if (!shouldProcess && _logTransferSessionActive)
            {
                lock (_bufferLock)
                {
                    shouldProcess = receivedDataBuffer.Count >= 8;
                }
            }

            if (!shouldProcess)
            {
                return;
            }

            // ensure only one thread processes the buffer
            lock (_bufferLock)
            {
                // clear flag before processing to avoid re-entrancy
                _packetReady = false;

                // Drain several packets per tick to avoid backlog on fast transfers.
                try
                {
                    for (int i = 0; i < 32; i++)
                    {
                        if (!processData())
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in processData: {ex.Message}");
                }
            }

        };
        _processTimer.Start();
    }

    public void UpdateSettingsData(DataStructures newSettingsData)
    {
        settingsData = newSettingsData;
    }

    private bool TryDequeueNextFramedPacket()
    {
        lock (_bufferLock)
        {
            if (receivedDataBuffer.Count < 8)
            {
                return false;
            }

            int headerIndex = -1;
            for (int i = 0; i <= receivedDataBuffer.Count - 2; i++)
            {
                if (receivedDataBuffer[i] == Constants.SERIAL_HEADER1 && receivedDataBuffer[i + 1] == Constants.SERIAL_HEADER2)
                {
                    headerIndex = i;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                bool keepTrailingHeaderByte =
                    receivedDataBuffer.Count > 0 &&
                    receivedDataBuffer[receivedDataBuffer.Count - 1] == Constants.SERIAL_HEADER1;

                if (keepTrailingHeaderByte)
                {
                    byte trailing = receivedDataBuffer[receivedDataBuffer.Count - 1];
                    receivedDataBuffer.Clear();
                    receivedDataBuffer.Add(trailing);
                }
                else
                {
                    receivedDataBuffer.Clear();
                }
                return false;
            }

            if (headerIndex > 0)
            {
                receivedDataBuffer.RemoveRange(0, headerIndex);
            }

            if (receivedDataBuffer.Count < 8)
            {
                return false;
            }

            // Fast path for streamed LOG_CHUNK frames:
            // frame = header(2) + cmd(1) + progress(1) + done(1) + chunkLen(2) + text(chunkLen) + trailer(2) + checksum(4)
            if (receivedDataBuffer[0] == Constants.SERIAL_HEADER1 &&
                receivedDataBuffer[1] == Constants.SERIAL_HEADER2 &&
                receivedDataBuffer.Count >= 7 &&
                receivedDataBuffer[2] == (byte)Constants.COMMAND_ID_LOG_CHUNK)
            {
                int chunkBytes = receivedDataBuffer[5] | (receivedDataBuffer[6] << 8);
                int packetLength = 13 + chunkBytes;

                if (packetLength > 0 && packetLength <= 65536)
                {
                    if (receivedDataBuffer.Count < packetLength)
                    {
                        return false;
                    }

                    int localCheckSum = 0;
                    for (int i = 0; i < packetLength - 4; i++)
                    {
                        localCheckSum += receivedDataBuffer[i];
                    }

                    uint receivedCheckSum =
                        (uint)(receivedDataBuffer[packetLength - 4]
                        | (receivedDataBuffer[packetLength - 3] << 8)
                        | (receivedDataBuffer[packetLength - 2] << 16)
                        | (receivedDataBuffer[packetLength - 1] << 24));

                    if (localCheckSum == receivedCheckSum)
                    {
                        dataLength = packetLength;
                        if (dataLength > dataBytes.Length)
                        {
                            dataBytes = new byte[dataLength * 2];
                        }

                        for (int i = 0; i < dataLength; i++)
                        {
                            dataBytes[i] = receivedDataBuffer[i];
                        }

                        receivedDataBuffer.RemoveRange(0, dataLength);
                        _packetReady = BufferContainsTrailer(receivedDataBuffer);
                        return true;
                    }
                }
            }

            for (int trailerIndex = 2; trailerIndex <= receivedDataBuffer.Count - 6; trailerIndex++)
            {
                if (receivedDataBuffer[trailerIndex] != Constants.SERIAL_TRAILER1 || receivedDataBuffer[trailerIndex + 1] != Constants.SERIAL_TRAILER2)
                {
                    continue;
                }

                int packetLength = trailerIndex + 2 + 4;

                int localCheckSum = 0;
                for (int i = 0; i < packetLength - 4; i++)
                {
                    localCheckSum += receivedDataBuffer[i];
                }

                uint receivedCheckSum =
                    (uint)(receivedDataBuffer[packetLength - 4]
                    | (receivedDataBuffer[packetLength - 3] << 8)
                    | (receivedDataBuffer[packetLength - 2] << 16)
                    | (receivedDataBuffer[packetLength - 1] << 24));

                if (localCheckSum != receivedCheckSum)
                {
                    continue;
                }

                dataLength = packetLength;
                if (dataLength > dataBytes.Length)
                {
                    dataBytes = new byte[dataLength * 2];
                }

                for (int i = 0; i < dataLength; i++)
                {
                    dataBytes[i] = receivedDataBuffer[i];
                }

                receivedDataBuffer.RemoveRange(0, dataLength);

                _packetReady = BufferContainsTrailer(receivedDataBuffer);

                return true;
            }

            if (_logTransferSessionActive && receivedDataBuffer.Count >= 8)
            {
                int candidateHeader = -1;
                int candidateTrailer = -1;
                for (int i = 0; i <= receivedDataBuffer.Count - 2; i++)
                {
                    if (candidateHeader < 0 && receivedDataBuffer[i] == Constants.SERIAL_HEADER1 && receivedDataBuffer[i + 1] == Constants.SERIAL_HEADER2)
                    {
                        candidateHeader = i;
                    }

                    if (candidateTrailer < 0 && receivedDataBuffer[i] == Constants.SERIAL_TRAILER1 && receivedDataBuffer[i + 1] == Constants.SERIAL_TRAILER2)
                    {
                        candidateTrailer = i;
                    }

                    if (candidateHeader >= 0 && candidateTrailer >= 0)
                    {
                        break;
                    }
                }

                long now = Environment.TickCount64;
                bool shouldLogPending =
                    (now - _lastPendingFrameLogTick) >= 500 ||
                    Math.Abs(receivedDataBuffer.Count - _lastPendingFrameLogBytes) >= 256 ||
                    candidateTrailer >= 0;

                if (shouldLogPending)
                {
                    _lastPendingFrameLogTick = now;
                    _lastPendingFrameLogBytes = receivedDataBuffer.Count;
                    int headLen = Math.Min(8, receivedDataBuffer.Count);
                    int tailLen = Math.Min(8, receivedDataBuffer.Count);
                    string headHex = BitConverter.ToString(receivedDataBuffer.Take(headLen).ToArray());
                    string tailHex = BitConverter.ToString(receivedDataBuffer.Skip(receivedDataBuffer.Count - tailLen).Take(tailLen).ToArray());
                    LogProtocolDebug($"RX frame pending bytes={receivedDataBuffer.Count} headerIdx={candidateHeader} trailerIdx={candidateTrailer} head={headHex} tail={tailHex}");
                }
            }

            return false;
        }
    }

    private void CopyLiveDataToStaticSnapshot()
    {
        int channelCount = Math.Min(dataStructures.ChannelsLiveData.Count, dataStructures.ChannelsStaticData.Count);
        for (int i = 0; i < channelCount; i++)
        {
            var source = dataStructures.ChannelsLiveData[i];
            var target = dataStructures.ChannelsStaticData[i];

            target.ChannelNumber = source.ChannelNumber;
            target.ChanType = source.ChanType;
            target.Category = source.Category;
            target.PWMSetDuty = source.PWMSetDuty;
            target.Enabled = source.Enabled;
            target.Name = source.Name is null ? null : (char[])source.Name.Clone();
            target.CurrentValue = source.CurrentValue;
            target.Override = source.Override;
            target.CurrentThresholdHigh = source.CurrentThresholdHigh;
            target.CurrentThresholdLow = source.CurrentThresholdLow;
            target.RetryCount = source.RetryCount;
            target.InrushDelay = source.InrushDelay;
            target.InrushCurrentLimit = source.InrushCurrentLimit;
            target.MultiChannel = source.MultiChannel;
            target.GroupNumber = source.GroupNumber;
            target.ControlPin = source.ControlPin;
            target.CurrentSensePin = source.CurrentSensePin;
            target.InputControlPin = source.InputControlPin;
            target.OnThreshold = source.OnThreshold;
            target.OffThreshold = source.OffThreshold;
            target.ScaleMin = source.ScaleMin;
            target.ScaleMax = source.ScaleMax;
            target.PWMMin = source.PWMMin;
            target.PWMMax = source.PWMMax;
            target.ErrorFlags = source.ErrorFlags;
            target.SoftStartEnabled = source.SoftStartEnabled;
            target.SoftStartTime = source.SoftStartTime;
            target.SoftStopEnabled = source.SoftStopEnabled;
            target.SoftStopTime = source.SoftStopTime;
            target.IntermittentOnTime = source.IntermittentOnTime;
            target.IntermittentOffTime = source.IntermittentOffTime;
            target.DelayedOn = source.DelayedOn;
            target.DelayedOnTime = source.DelayedOnTime;
            target.DelayedOff = source.DelayedOff;
            target.DelayedOffTime = source.DelayedOffTime;
            target.DelayedOffTrigger = source.DelayedOffTrigger;
            target.RefreshDelayUiUnitsFromStoredValues();
        }

        int analogueCount = Math.Min(dataStructures.AnalogueInputsLiveData.Count, dataStructures.AnalogueInputsStaticData.Count);
        for (int i = 0; i < analogueCount; i++)
        {
            var source = dataStructures.AnalogueInputsLiveData[i];
            var target = dataStructures.AnalogueInputsStaticData[i];

            target.InputNumber = source.InputNumber;
            target.ChanType = source.ChanType;
            target.Units = source.Units;
            target.CalibrationPoints = source.CalibrationPoints;
            target.PullUpEnable = source.PullUpEnable;
            target.PullDownEnable = source.PullDownEnable;
            target.InputVoltage = source.InputVoltage;
            target.InputValue = source.InputValue;
            target.CalibrationVolt1 = source.CalibrationVolt1;
            target.CalibrationValue1 = source.CalibrationValue1;
            target.CalibrationVolt2 = source.CalibrationVolt2;
            target.CalibrationValue2 = source.CalibrationValue2;
            target.CalibrationVolt3 = source.CalibrationVolt3;
            target.CalibrationValue3 = source.CalibrationValue3;
            target.ConfigRangeMin = source.ConfigRangeMin;
            target.ConfigRangeMax = source.ConfigRangeMax;
            target.NtcBeta = source.NtcBeta;
            target.NtcNominalResistance = source.NtcNominalResistance;
        }

        dataStructures.SystemParamsStaticData.SystemTemperature = dataStructures.SystemParams.SystemTemperature;
        dataStructures.SystemParamsStaticData.SIMModuleTemp = dataStructures.SystemParams.SIMModuleTemp;
        dataStructures.SystemParamsStaticData.IMUTemp = dataStructures.SystemParams.IMUTemp;
        dataStructures.SystemParamsStaticData.CANResEnabled = dataStructures.SystemParams.CANResEnabled;
        dataStructures.SystemParamsStaticData.CANBusBitrate = dataStructures.SystemParams.CANBusBitrate;
        dataStructures.SystemParamsStaticData.VBatt = dataStructures.SystemParams.VBatt;
        dataStructures.SystemParamsStaticData.SystemCurrent = dataStructures.SystemParams.SystemCurrent;
        dataStructures.SystemParamsStaticData.SystemCurrentLimit = dataStructures.SystemParams.SystemCurrentLimit;
        dataStructures.SystemParamsStaticData.ErrorFlags = dataStructures.SystemParams.ErrorFlags;
        dataStructures.SystemParamsStaticData.ChannelDataCANID = dataStructures.SystemParams.ChannelDataCANID;
        dataStructures.SystemParamsStaticData.DigitalInputDataCANID = dataStructures.SystemParams.DigitalInputDataCANID;
        dataStructures.SystemParamsStaticData.AnalogueInputDataCANID = dataStructures.SystemParams.AnalogueInputDataCANID;
        dataStructures.SystemParamsStaticData.SystemDataCANID = dataStructures.SystemParams.SystemDataCANID;
        dataStructures.SystemParamsStaticData.SystemConfigCANID = dataStructures.SystemParams.SystemConfigCANID;
        dataStructures.SystemParamsStaticData.ConfigDataCANID = dataStructures.SystemParams.ConfigDataCANID;
        dataStructures.SystemParamsStaticData.IMUWakeWindow = dataStructures.SystemParams.IMUWakeWindow;
        dataStructures.SystemParamsStaticData.SpeedUnitPref = dataStructures.SystemParams.SpeedUnitPref;
        dataStructures.SystemParamsStaticData.DistanceUnitPref = dataStructures.SystemParams.DistanceUnitPref;
        dataStructures.SystemParamsStaticData.AllowData = dataStructures.SystemParams.AllowData;
        dataStructures.SystemParamsStaticData.AllowGPS = dataStructures.SystemParams.AllowGPS;
        dataStructures.SystemParamsStaticData.AllowMotionDetect = dataStructures.SystemParams.AllowMotionDetect;
        dataStructures.SystemParamsStaticData.MobileSignalPercent = dataStructures.SystemParams.MobileSignalPercent;
        dataStructures.SystemParamsStaticData.TimeZoneRule = dataStructures.SystemParams.TimeZoneRule?.ToArray() ?? Array.Empty<byte>();

        dataStructures.CellularParamsStaticData.ConfigVersion = dataStructures.CellularParams.ConfigVersion;
        dataStructures.CellularParamsStaticData.Protocol = dataStructures.CellularParams.Protocol;
        dataStructures.CellularParamsStaticData.UseTLS = dataStructures.CellularParams.UseTLS;
        dataStructures.CellularParamsStaticData.APN = dataStructures.CellularParams.APN;
        dataStructures.CellularParamsStaticData.APNUser = dataStructures.CellularParams.APNUser;
        dataStructures.CellularParamsStaticData.APNPassword = dataStructures.CellularParams.APNPassword;
        dataStructures.CellularParamsStaticData.OpenRemoteHost = dataStructures.CellularParams.OpenRemoteHost;
        dataStructures.CellularParamsStaticData.OpenRemotePort = dataStructures.CellularParams.OpenRemotePort;
        dataStructures.CellularParamsStaticData.ClientID = dataStructures.CellularParams.ClientID;
        dataStructures.CellularParamsStaticData.PreserveOpenRemoteFieldsFrom(dataStructures.CellularParams);
        dataStructures.CellularParamsStaticData.MQTTUsername = dataStructures.CellularParams.MQTTUsername;
        dataStructures.CellularParamsStaticData.MQTTPassword = dataStructures.CellularParams.MQTTPassword;
        dataStructures.CellularParamsStaticData.PublishTopic = dataStructures.CellularParams.PublishTopic;
        dataStructures.CellularParamsStaticData.SubscribeTopic = dataStructures.CellularParams.SubscribeTopic;
        dataStructures.CellularParamsStaticData.KeepAliveSeconds = dataStructures.CellularParams.KeepAliveSeconds;
        dataStructures.CellularParamsStaticData.PublishIntervalMs = dataStructures.CellularParams.PublishIntervalMs;
        dataStructures.CellularParamsStaticData.TelemetryUploadMask = dataStructures.CellularParams.TelemetryUploadMask;
        dataStructures.CellularParamsStaticData.OpenRemoteAssetName = dataStructures.CellularParams.OpenRemoteAssetName;
    }

    private static bool ByteArraysEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedCanBusBitrate(uint bitrate)
    {
        return bitrate == Constants.CAN_BITRATE_125K
            || bitrate == Constants.CAN_BITRATE_250K
            || bitrate == Constants.CAN_BITRATE_500K
            || bitrate == Constants.CAN_BITRATE_1M;
    }

    private bool ParseLiveStatusPacket()
    {
        if (dataBytes[3] != Constants.NUM_OUTPUT_CHANNELS)
        {
            return false;
        }

        var reader = new ByteReader(dataBytes, 4);

        for (int i = 0; i < dataBytes[3] && i < dataStructures.ChannelsLiveData.Count; i++)
        {
            dataStructures.ChannelsLiveData[i].ChanType = (ChannelType)reader.ReadByte();
            dataStructures.ChannelsLiveData[i].Override = reader.ReadByte() != 0;
            dataStructures.ChannelsLiveData[i].CurrentValue = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].Enabled = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].ErrorFlags = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].PWMSetDuty = reader.ReadByte();
        }

        int numAnalogueChannels = reader.ReadByte();
        for (int i = 0; i < numAnalogueChannels && i < dataStructures.AnalogueInputsLiveData.Count; i++)
        {
            dataStructures.AnalogueInputsLiveData[i].InputVoltage = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].InputValue = reader.ReadSingle();
        }

        int packetEndIndex = dataLength - 4;
        int bytesRemaining = packetEndIndex - reader.Position;
        if (bytesRemaining < ExtendedLiveSystemPayloadBytes)
        {
            return false;
        }

        dataStructures.SystemParams.SystemTemperature = reader.ReadInt32();
        dataStructures.SystemParams.SIMModuleTemp = reader.ReadSingle();
        dataStructures.SystemParams.IMUTemp = reader.ReadSingle();

        dataStructures.SystemParams.VBatt = (float)Math.Round(reader.ReadSingle(), 1);
        dataStructures.SystemParams.SystemCurrent = reader.ReadSingle();
        dataStructures.SystemParams.ErrorFlags = reader.ReadUInt16();
        dataStructures.SystemParams.MobileSignalPercent = reader.ReadByte();
        return true;
    }

    private bool ParseStaticStatusPacket()
    {
        if (dataBytes[3] != Constants.NUM_OUTPUT_CHANNELS)
        {
            return false;
        }

        int dataIndex = 4;
        int packetEndIndex = dataLength - 4;
        var reader = new ByteReader(dataBytes, dataIndex);
        ControllerProtocolCapabilities packetCapabilities = ControllerProtocolCapabilities.FromStaticPacketLength(dataLength);

        for (int i = 0; i < dataBytes[3] && i < dataStructures.ChannelsLiveData.Count; i++)
        {
            dataStructures.ChannelsLiveData[i].ChanType = (ChannelType)reader.ReadByte();
            dataStructures.ChannelsLiveData[i].Override = reader.ReadByte() != 0;
            dataStructures.ChannelsLiveData[i].CurrentSensePin = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].CurrentThresholdHigh = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].CurrentThresholdLow = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].CurrentValue = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].Enabled = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].ErrorFlags = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].GroupNumber = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].InputControlPin = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].OnThreshold = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].OffThreshold = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].ScaleMin = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].ScaleMax = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].PWMMin = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].PWMMax = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].MultiChannel = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].RetryCount = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].InrushDelay = reader.ReadInt32() / 1000.0F;
            dataStructures.ChannelsLiveData[i].Name = reader.ReadChars(3);
            byte legacyRunOn = 0;
            int legacyRunOnTime = 0;
            if (!packetCapabilities.SupportsDelayedChannelSettings)
            {
                legacyRunOn = reader.ReadByte();
                legacyRunOnTime = reader.ReadInt32();
            }
            dataStructures.ChannelsLiveData[i].SoftStartEnabled = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].SoftStartTime = reader.ReadInt32() / 1000.0F;
            dataStructures.ChannelsLiveData[i].InrushCurrentLimit = reader.ReadSingle();
            dataStructures.ChannelsLiveData[i].PWMSetDuty = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].SoftStopEnabled = reader.ReadByte();
            dataStructures.ChannelsLiveData[i].SoftStopTime = reader.ReadInt32() / 1000.0F;
            dataStructures.ChannelsLiveData[i].Category = (OutputChannel.ChannelCategory)reader.ReadByte();
            dataStructures.ChannelsLiveData[i].IntermittentOnTime = reader.ReadInt32() / 1000.0F;
            dataStructures.ChannelsLiveData[i].IntermittentOffTime = reader.ReadInt32() / 1000.0F;
            if (packetCapabilities.SupportsDelayedChannelSettings)
            {
                dataStructures.ChannelsLiveData[i].DelayedOn = reader.ReadByte();
                dataStructures.ChannelsLiveData[i].DelayedOnTime = reader.ReadInt32();
                dataStructures.ChannelsLiveData[i].DelayedOff = reader.ReadByte();
                dataStructures.ChannelsLiveData[i].DelayedOffTime = reader.ReadInt32();
                dataStructures.ChannelsLiveData[i].DelayedOffTrigger = reader.ReadByte();
            }
            else
            {
                dataStructures.ChannelsLiveData[i].DelayedOn = 0;
                dataStructures.ChannelsLiveData[i].DelayedOnTime = 0;
                dataStructures.ChannelsLiveData[i].DelayedOff = legacyRunOn != 0 ? (byte)1 : (byte)0;
                dataStructures.ChannelsLiveData[i].DelayedOffTime = legacyRunOnTime;
                dataStructures.ChannelsLiveData[i].DelayedOffTrigger = (byte)OutputChannel.DelayedOffTriggerMode.IgnitionOff;
            }
            dataStructures.ChannelsLiveData[i].RefreshDelayUiUnitsFromStoredValues();
        }

        int numAnalogueChannels = reader.ReadByte();
        const int bytesPerAnalogueInputExtended = 45;

        int bytesRemainingAfterAnalogueCount = packetEndIndex - reader.Position;
        int minimumFullPayload = (numAnalogueChannels * bytesPerAnalogueInputExtended) + ExtendedStaticSystemPayloadBytes;

        if (bytesRemainingAfterAnalogueCount < minimumFullPayload)
        {
            return false;
        }

        for (int i = 0; i < numAnalogueChannels && i < dataStructures.AnalogueInputsLiveData.Count; i++)
        {
            dataStructures.AnalogueInputsLiveData[i].ChanType = (AnalogueInput.AnalogueChannelType)reader.ReadByte();
            dataStructures.AnalogueInputsLiveData[i].Units = (AnalogueInput.AnalogueUnits)reader.ReadByte();
            dataStructures.AnalogueInputsLiveData[i].CalibrationPoints = reader.ReadByte();
            dataStructures.AnalogueInputsLiveData[i].PullUpEnable = reader.ReadByte() != 0;
            dataStructures.AnalogueInputsLiveData[i].PullDownEnable = reader.ReadByte() != 0;
            dataStructures.AnalogueInputsLiveData[i].InputVoltage = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].InputValue = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].CalibrationVolt1 = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].CalibrationValue1 = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].CalibrationVolt2 = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].CalibrationValue2 = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].CalibrationVolt3 = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].CalibrationValue3 = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].NtcBeta = reader.ReadSingle();
            dataStructures.AnalogueInputsLiveData[i].NtcNominalResistance = reader.ReadSingle();

        }

        dataStructures.SystemParams.SystemTemperature = reader.ReadInt32();
        dataStructures.SystemParams.SIMModuleTemp = reader.ReadSingle();
        dataStructures.SystemParams.IMUTemp = reader.ReadSingle();

        dataStructures.SystemParams.CANResEnabled = reader.ReadByte() != 0;
        dataStructures.SystemParams.VBatt = (float)Math.Round(reader.ReadSingle(), 1);
        dataStructures.SystemParams.SystemCurrent = reader.ReadSingle();
        dataStructures.SystemParams.SystemCurrentLimit = reader.ReadByte();
        dataStructures.SystemParams.ErrorFlags = reader.ReadUInt16();
        dataStructures.SystemParams.ChannelDataCANID = reader.ReadUInt16();
        dataStructures.SystemParams.DigitalInputDataCANID = reader.ReadUInt16();
        dataStructures.SystemParams.AnalogueInputDataCANID = reader.ReadUInt16();
        dataStructures.SystemParams.SystemDataCANID = reader.ReadUInt16();
        dataStructures.SystemParams.ConfigDataCANID = reader.ReadUInt16();
        dataStructures.SystemParams.SystemConfigCANID = reader.ReadUInt16();
        dataStructures.SystemParams.IMUWakeWindow = reader.ReadUInt32();
        dataStructures.SystemParams.SpeedUnitPref = reader.ReadByte() != 0;
        dataStructures.SystemParams.DistanceUnitPref = reader.ReadByte() != 0;
        dataStructures.SystemParams.AllowData = reader.ReadByte() != 0;
        dataStructures.SystemParams.AllowGPS = reader.ReadByte() != 0;
        dataStructures.SystemParams.AllowMotionDetect = reader.ReadByte() != 0;
        dataStructures.SystemParams.MobileSignalPercent = reader.ReadByte();
        if ((packetEndIndex - reader.Position) >= StaticTimeZoneRulePayloadBytes)
        {
            dataStructures.SystemParams.TimeZoneRule = reader.ReadBytes(StaticTimeZoneRulePayloadBytes);
        }
        else
        {
            dataStructures.SystemParams.TimeZoneRule = Array.Empty<byte>();
        }

        if ((packetEndIndex - reader.Position) >= sizeof(uint))
        {
            uint canBusBitrate = reader.ReadUInt32();
            dataStructures.SystemParams.CANBusBitrate = IsSupportedCanBusBitrate(canBusBitrate)
                ? canBusBitrate
                : Constants.DEFAULT_CAN_BITRATE;
        }

        if ((packetEndIndex - reader.Position) >= Constants.CELLULAR_LEGACY_STATIC_PAYLOAD_BYTES)
        {
            dataStructures.CellularParams.ConfigVersion = reader.ReadByte();
            dataStructures.CellularParams.Protocol = reader.ReadByte();
            dataStructures.CellularParams.UseTLS = reader.ReadByte() != 0;
            dataStructures.CellularParams.APN = reader.ReadFixedAsciiString(Constants.CELLULAR_APN_LENGTH);
            dataStructures.CellularParams.APNUser = reader.ReadFixedAsciiString(Constants.CELLULAR_APN_USER_LENGTH);
            dataStructures.CellularParams.APNPassword = reader.ReadFixedAsciiString(Constants.CELLULAR_APN_PASSWORD_LENGTH);
            dataStructures.CellularParams.OpenRemoteHost = reader.ReadFixedAsciiString(Constants.CELLULAR_HOST_LENGTH);
            dataStructures.CellularParams.OpenRemotePort = reader.ReadUInt16();
            dataStructures.CellularParams.ClientID = reader.ReadFixedAsciiString(Constants.CELLULAR_CLIENT_ID_LENGTH);
            dataStructures.CellularParams.MQTTUsername = reader.ReadFixedAsciiString(Constants.CELLULAR_MQTT_USERNAME_LENGTH);
            dataStructures.CellularParams.MQTTPassword = reader.ReadFixedAsciiString(Constants.CELLULAR_MQTT_PASSWORD_LENGTH);
            dataStructures.CellularParams.PublishTopic = reader.ReadFixedAsciiString(Constants.CELLULAR_TOPIC_LENGTH);
            dataStructures.CellularParams.SubscribeTopic = reader.ReadFixedAsciiString(Constants.CELLULAR_TOPIC_LENGTH);
            dataStructures.CellularParams.KeepAliveSeconds = reader.ReadUInt16();
            dataStructures.CellularParams.PublishIntervalMs = reader.ReadUInt32();
            dataStructures.CellularParams.TelemetryUploadMask = reader.ReadUInt32();
            if (dataStructures.CellularParams.TelemetryUploadMask == 0)
            {
                dataStructures.CellularParams.TelemetryUploadMask = Constants.TELEMETRY_UPLOAD_DEFAULT_MASK;
            }
            if ((packetEndIndex - reader.Position) >= Constants.CELLULAR_OPENREMOTE_ASSET_NAME_LENGTH)
            {
                dataStructures.CellularParams.OpenRemoteAssetName = reader.ReadFixedAsciiString(Constants.CELLULAR_OPENREMOTE_ASSET_NAME_LENGTH);
            }
        }

        CopyLiveDataToStaticSnapshot();
        _controllerCapabilities = packetCapabilities;
        _lastStaticRequestSentTick = 0;
        UpdateStaticData = false;
        return true;
    }

    /// <summary>
    /// Process incoming data from the serial port
    /// </summary>
    /// <returns>True if a full data packet received and processed</returns>
    private bool processData()
    {
        bool retVal = false;
        if (!TryDequeueNextFramedPacket())
        {
            return false;
        }

        if (dataLength < 8)
        {
            return false;
        }

        int checkSum = 0;
        for (int i = 0; i < dataLength - 4; i++)
        {
            checkSum += dataBytes[i];
        }

        // Copy checksum bytes to reusable array
        Array.Copy(dataBytes, dataLength - 4, checkSumArray, 0, 4);
        pdmCheckSum = BitConverter.ToUInt32(checkSumArray, 0);

        // First two bytes are the header
        uint header = BitConverter.ToUInt16(dataBytes, 0);

        // Checksums match. Continue.
        if (checkSum == pdmCheckSum)
        {
            // Header check
            if (header == 0x1984)
            {
                switch (dataBytes[2])
                {
                    // Request response for channel and system data
                    case (byte)Constants.COMMAND_ID_REQUEST:
                        _lastLiveStatusFrameTick = Environment.TickCount64;
                        _awaitingPostLogRefreshStatusFrame = false;
                        _postLogRefreshRequestRetryCount = 0;
                        ParseLiveStatusPacket();
                        break;

                    case (byte)Constants.COMMAND_ID_REQUEST_STATIC:
                        _lastLiveStatusFrameTick = Environment.TickCount64;
                        _awaitingPostLogRefreshStatusFrame = false;
                        _postLogRefreshRequestRetryCount = 0;
                        ParseStaticStatusPacket();
                        break;

                    case (byte)Constants.COMMAND_ID_LOG_LIST:
                        {
                            if (dataLength < 4)
                            {
                                break;
                            }

                            int count = dataBytes[3];
                            int offset = 4;
                            int entrySize = Constants.LOG_FILE_NAME_LENGTH + 4;
                            var files = new List<ControllerLogFileInfo>(count);
                            for (int i = 0; i < count; i++)
                            {
                                if (offset + entrySize > dataLength - 6)
                                {
                                    break;
                                }

                                string fileName = Encoding.ASCII
                                    .GetString(dataBytes, offset, Constants.LOG_FILE_NAME_LENGTH)
                                    .TrimEnd('\0', ' ');

                                long fileSizeBytes =
                                    (uint)(dataBytes[offset + Constants.LOG_FILE_NAME_LENGTH + 0]
                                    | (dataBytes[offset + Constants.LOG_FILE_NAME_LENGTH + 1] << 8)
                                    | (dataBytes[offset + Constants.LOG_FILE_NAME_LENGTH + 2] << 16)
                                    | (dataBytes[offset + Constants.LOG_FILE_NAME_LENGTH + 3] << 24));

                                if (!string.IsNullOrWhiteSpace(fileName))
                                {
                                    files.Add(new ControllerLogFileInfo
                                    {
                                        FileName = fileName,
                                        FileSizeBytes = fileSizeBytes,
                                    });
                                }

                                offset += entrySize;
                            }

                            _logFileListTcs?.TrySetResult(files);
                            LogProtocolDebug($"RX LOG_LIST entries={files.Count} payloadLen={dataLength}");
                            if (files.Count > 0)
                            {
                                string preview = string.Join(", ", files.Take(3).Select(f => $"{f.FileName}:{f.FileSizeBytes}"));
                                LogProtocolDebug($"RX LOG_LIST preview={preview}");
                            }
                            break;
                        }

                    case (byte)Constants.COMMAND_ID_LOG_CHUNK:
                        {
                            if (dataLength < 7)
                            {
                                break;
                            }

                            byte progress = dataBytes[3];
                            bool done = dataBytes[4] != 0;
                            int chunkBytes = dataBytes[5] | (dataBytes[6] << 8);
                            int dataStart = 7;
                            int availableChunkBytes = Math.Max(0, (dataLength - 6) - dataStart);
                            int safeChunkBytes = Math.Min(chunkBytes, availableChunkBytes);

                            string text = safeChunkBytes > 0
                                ? Encoding.ASCII.GetString(dataBytes, dataStart, safeChunkBytes)
                                : string.Empty;

                            PublishOrQueueLogChunk(new LogChunkResponse
                            {
                                Progress = progress,
                                Done = done,
                                Text = text,
                            });

                            if (done || progress != _lastChunkProgressLogged)
                            {
                                _lastChunkProgressLogged = progress;
                                LogProtocolDebug($"RX LOG_CHUNK progress={progress}% bytes={safeChunkBytes} done={done}");
                            }
                            break;
                        }

                    case (byte)Constants.COMMAND_ID_FW_VER:
                        {
                            int payloadLength = Math.Max(0, dataLength - 9);
                            string firmwareVersion = payloadLength > 0
                                ? Encoding.ASCII.GetString(dataBytes, 3, payloadLength)
                                : string.Empty;
                            _firmwareVersionTcs?.TrySetResult(firmwareVersion);
                            break;
                        }

                    case (byte)Constants.COMMAND_ID_BUILD_DATE:
                        {
                            int payloadLength = Math.Max(0, dataLength - 9);
                            string buildDate = payloadLength > 0
                                ? Encoding.ASCII.GetString(dataBytes, 3, payloadLength)
                                : string.Empty;
                            _buildDateTcs?.TrySetResult(buildDate);
                            break;
                        }

                    case (byte)Constants.COMMAND_ID_FW_DIAGNOSTIC:
                        {
                            int payloadLength = Math.Max(0, dataLength - 9);
                            string diagnostic = payloadLength > 0
                                ? Encoding.ASCII.GetString(dataBytes, 3, payloadLength)
                                : string.Empty;
                            _firmwareDiagnosticTcs?.TrySetResult(diagnostic);
                            break;
                        }

                    case (byte)Constants.COMMAND_ID_CELLULAR_TEST:
                        {
                            int payloadLength = Math.Max(0, dataLength - 9);
                            string diagnostic = payloadLength > 0
                                ? Encoding.ASCII.GetString(dataBytes, 3, payloadLength)
                                : string.Empty;
                            _cellularTestTcs?.TrySetResult(diagnostic);
                            break;
                        }

                    case (byte)Constants.COMMAND_ID_OPENREMOTE_PROVISION:
                        {
                            int payloadLength = Math.Max(0, dataLength - 9);
                            string diagnostic = payloadLength > 0
                                ? Encoding.ASCII.GetString(dataBytes, 3, payloadLength)
                                : string.Empty;
                            _openRemoteProvisioningTcs?.TrySetResult(diagnostic);
                            break;
                        }
                }
            }

            DataUpdated?.Invoke(dataStructures);

        }
        else
        {
            Debug.WriteLine($"STATUS CHECKSUM FAIL len={dataLength} calc=0x{(uint)checkSum:X8} rx=0x{pdmCheckSum:X8} cmd={(dataLength > 2 ? dataBytes[2] : (byte)0):X2}");
        }
        if (!sendingConfig && !_suspendLiveRequestPolling)
        {
            SendCommand(Constants.COMMAND_ID_REQUEST);
        }


        return retVal;
    }

    private void AdvanceToNextSettingGroup()
    {
        settingIndex = settingIndex switch
        {
            1 => 0,
            0 => 2,
            2 => 3,
            3 => 4,
            _ => settingIndex,
        };
    }

    private bool AdvanceConfigCursor()
    {
        switch (settingIndex)
        {
            case 0: // Channel data
                parameterIndex++;
                if (parameterIndex > _controllerCapabilities.LastChannelParameterIndex)
                {
                    parameterIndex = 0;
                    channelIndex++;
                    if (channelIndex >= Constants.NUM_OUTPUT_CHANNELS)
                    {
                        channelIndex = 0;
                        AdvanceToNextSettingGroup();
                    }
                }
                break;
            case 1: // Analogue input data
                parameterIndex++;
                if (parameterIndex > Constants.LAST_ANALOGUE_PARAM_INDEX)
                {
                    parameterIndex = 0;
                    analogueIndex++;
                    if (analogueIndex >= Constants.NUM_ANALOGUE_INPUTS)
                    {
                        analogueIndex = 0;
                        if (_finalAnalogueResendActive)
                        {
                            _finalAnalogueResendActive = false;
                            sendingConfig = false;
                            saveToEEPROM = true;
                        }
                        else
                        {
                            AdvanceToNextSettingGroup();
                        }
                    }
                }
                break;
            case 2: // System data
                parameterIndex++;
                if (parameterIndex > _controllerCapabilities.LastSystemParameterIndex)
                {
                    parameterIndex = 0;
                    AdvanceToNextSettingGroup();
                }
                break;
            case 3: // Digital inputs
                parameterIndex++;
                if (parameterIndex > Constants.LAST_DIGITAL_PARAM_INDEX)
                {
                    parameterIndex = 0;
                    digitalIndex++;
                    if (digitalIndex >= Constants.NUM_DIGITAL_INPUTS)
                    {
                        if (_repeatAnalogueSettingsAfterChannelsPending)
                        {
                            _repeatAnalogueSettingsAfterChannelsPending = false;
                            _finalAnalogueResendActive = true;
                            settingIndex = 1;
                            analogueIndex = 0;
                            parameterIndex = 0;
                        }
                        else
                        {
                            if (_controllerCapabilities.SupportsCellularSettings)
                            {
                                AdvanceToNextSettingGroup();
                            }
                            else
                            {
                                sendingConfig = false;
                                saveToEEPROM = true;
                            }
                        }
                    }
                }
                break;
            case 4: // Cellular data
                parameterIndex++;
                if (parameterIndex > Constants.LAST_CELLULAR_PARAM_INDEX)
                {
                    sendingConfig = false;
                    saveToEEPROM = true;
                }
                break;
        }

        if (!sendingConfig)
        {
            parameterIndex = channelIndex = analogueIndex = settingIndex = digitalIndex = 0;
            return false;
        }

        return true;
    }

    private static ChannelType DefaultToAnalogueChannelType(ChannelType currentType)
    {
        return currentType switch
        {
            ChannelType.Digital => ChannelType.Analogue,
            ChannelType.PWM => ChannelType.AnalogueScaled,
            ChannelType.Intermittent => ChannelType.Analogue,
            _ => currentType,
        };
    }

    private static ChannelType DefaultToDigitalChannelType(ChannelType currentType)
    {
        return currentType switch
        {
            ChannelType.Analogue => ChannelType.Digital,
            ChannelType.AnalogueScaled => ChannelType.PWM,
            _ => currentType,
        };
    }

    private bool ShouldLetAnalogueInputAutoSyncChannelType(int channelIndex)
    {
        byte desiredInputPin = settingsData.ChannelsStaticData[channelIndex].InputControlPin;
        int analogueInputIndex = Array.IndexOf(InputPinCatalog.ANAChannelInputPins, desiredInputPin);
        if (analogueInputIndex < 0 || analogueInputIndex >= Constants.NUM_ANALOGUE_INPUTS)
        {
            return false;
        }

        var liveAnalogueInput = dataStructures.AnalogueInputsLiveData[analogueInputIndex];
        var desiredAnalogueInput = settingsData.AnalogueInputsStaticData[analogueInputIndex];
        if (liveAnalogueInput.ChanType == desiredAnalogueInput.ChanType)
        {
            return false;
        }

        ChannelType liveChannelType = dataStructures.ChannelsLiveData[channelIndex].ChanType;
        ChannelType expectedAutoSyncedType = desiredAnalogueInput.ChanType == AnalogueInput.AnalogueChannelType.Digital
            ? DefaultToDigitalChannelType(liveChannelType)
            : DefaultToAnalogueChannelType(liveChannelType);

        return settingsData.ChannelsStaticData[channelIndex].ChanType == expectedAutoSyncedType;
    }

    private bool SendConfig()
    {
        bool retVal = false;

        while (true)
        {
            totalBytesSent = 0;
            checkSumSend = 0;
            _sendBuffer.Clear();
            bool configChanged = false;

            AddData(Constants.SERIAL_HEADER1, true);
            AddData(Constants.SERIAL_HEADER2, true);

            switch (settingIndex)
            {
            case 0: // Channel data
                switch (parameterIndex)
                {
                    case 0: // Channel type
                        if (!ShouldLetAnalogueInputAutoSyncChannelType(channelIndex) &&
                            dataStructures.ChannelsLiveData[channelIndex].ChanType != settingsData.ChannelsStaticData[channelIndex].ChanType)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)settingsData.ChannelsStaticData[channelIndex].ChanType, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 1: // Override
                        if (dataStructures.ChannelsLiveData[channelIndex].Override != settingsData.ChannelsStaticData[channelIndex].Override)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].Override ? (byte)1 : (byte)0, true);
                        }
                        break;
                    case 2: // Current threshold high
                        if (dataStructures.ChannelsLiveData[channelIndex].CurrentThresholdHigh != settingsData.ChannelsStaticData[channelIndex].CurrentThresholdHigh)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].CurrentThresholdHigh);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 3: // Current threshold low
                        if (dataStructures.ChannelsLiveData[channelIndex].CurrentThresholdLow != settingsData.ChannelsStaticData[channelIndex].CurrentThresholdLow)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].CurrentThresholdLow);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 4: // Enabled
                        if (dataStructures.ChannelsLiveData[channelIndex].Enabled != settingsData.ChannelsStaticData[channelIndex].Enabled)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].Enabled, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 5: // Group number
                        if (dataStructures.ChannelsLiveData[channelIndex].GroupNumber != settingsData.ChannelsStaticData[channelIndex].GroupNumber)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].GroupNumber, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 6: // Input control pin
                        if (dataStructures.ChannelsLiveData[channelIndex].InputControlPin != settingsData.ChannelsStaticData[channelIndex].InputControlPin)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].InputControlPin, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 7: // Multi channel
                        if (dataStructures.ChannelsLiveData[channelIndex].MultiChannel != settingsData.ChannelsStaticData[channelIndex].MultiChannel)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MultiChannel, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 8: // Retry count
                        if (dataStructures.ChannelsLiveData[channelIndex].RetryCount != settingsData.ChannelsStaticData[channelIndex].RetryCount)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].RetryCount, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 9: // Inrush delay
                        if (dataStructures.ChannelsLiveData[channelIndex].InrushDelay != settingsData.ChannelsStaticData[channelIndex].InrushDelay)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes((int)settingsData.ChannelsStaticData[channelIndex].InrushDelay * 1000);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 10: // Name
                        if (!(dataStructures.ChannelsLiveData[channelIndex].Name ?? Array.Empty<char>())
                              .SequenceEqual(settingsData.ChannelsStaticData[channelIndex].Name ?? Array.Empty<char>()))
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            foreach (char c in settingsData.ChannelsStaticData[channelIndex].Name ?? Array.Empty<char>())
                            {
                                AddData((byte)c, true);
                            }
                            AddData(0, true); // Padding
                        }
                        break;
                    case 11: // Legacy run-on flag slot (unused)
                        break;

                    case 12: // Legacy run-on time slot (unused)
                        break;

                    case 13: // Soft start flag
                        if (dataStructures.ChannelsLiveData[channelIndex].SoftStartEnabled != settingsData.ChannelsStaticData[channelIndex].SoftStartEnabled)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);

                            AddData(settingsData.ChannelsStaticData[channelIndex].SoftStartEnabled, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;

                    case 14: // Soft start time
                        if (dataStructures.ChannelsLiveData[channelIndex].SoftStartTime != settingsData.ChannelsStaticData[channelIndex].SoftStartTime)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            int softStartTimeMs = (int)Math.Round(settingsData.ChannelsStaticData[channelIndex].SoftStartTime * 1000.0f);
                            byte[] floatBytes = BitConverter.GetBytes(softStartTimeMs);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;

                    case 15: // Inrush current limit
                        if (dataStructures.ChannelsLiveData[channelIndex].InrushCurrentLimit != settingsData.ChannelsStaticData[channelIndex].InrushCurrentLimit)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].InrushCurrentLimit);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 16: // PWM set duty
                        if (dataStructures.ChannelsLiveData[channelIndex].PWMSetDuty != settingsData.ChannelsStaticData[channelIndex].PWMSetDuty)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);

                            AddData(settingsData.ChannelsStaticData[channelIndex].PWMSetDuty, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;

                    case 17: // On threshold
                        if (dataStructures.ChannelsLiveData[channelIndex].OnThreshold != settingsData.ChannelsStaticData[channelIndex].OnThreshold)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].OnThreshold);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;

                    case 18: // Off threshold
                        if (dataStructures.ChannelsLiveData[channelIndex].OffThreshold != settingsData.ChannelsStaticData[channelIndex].OffThreshold)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].OffThreshold);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;

                    case 19: // Scale min
                        if (dataStructures.ChannelsLiveData[channelIndex].ScaleMin != settingsData.ChannelsStaticData[channelIndex].ScaleMin)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].ScaleMin);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;

                    case 20: // Scale max
                        if (dataStructures.ChannelsLiveData[channelIndex].ScaleMax != settingsData.ChannelsStaticData[channelIndex].ScaleMax)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].ScaleMax);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;

                    case 21: // PWM min
                        if (dataStructures.ChannelsLiveData[channelIndex].PWMMin != settingsData.ChannelsStaticData[channelIndex].PWMMin)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].PWMMin, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;

                    case 22: // PWM max
                        if (dataStructures.ChannelsLiveData[channelIndex].PWMMax != settingsData.ChannelsStaticData[channelIndex].PWMMax)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].PWMMax, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;

                    case 23: // Soft stop flag
                        if (dataStructures.ChannelsLiveData[channelIndex].SoftStopEnabled != settingsData.ChannelsStaticData[channelIndex].SoftStopEnabled)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);

                            AddData(settingsData.ChannelsStaticData[channelIndex].SoftStopEnabled, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;

                    case 24: // Soft stop time
                        if (dataStructures.ChannelsLiveData[channelIndex].SoftStopTime != settingsData.ChannelsStaticData[channelIndex].SoftStopTime)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            int softStopTimeMs = (int)Math.Round(settingsData.ChannelsStaticData[channelIndex].SoftStopTime * 1000.0f);
                            byte[] floatBytes = BitConverter.GetBytes(softStopTimeMs);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;

                    case 25: // Output category
                        if (dataStructures.ChannelsLiveData[channelIndex].Category != settingsData.ChannelsStaticData[channelIndex].Category)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)settingsData.ChannelsStaticData[channelIndex].Category, true);
                            AddData(0, true);
                            AddData(0, true);
                            AddData(0, true);
                        }
                        break;

                    case 26: // Intermittent on time
                        if (dataStructures.ChannelsLiveData[channelIndex].IntermittentOnTime != settingsData.ChannelsStaticData[channelIndex].IntermittentOnTime)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            int intermittentOnTimeMs = (int)Math.Round(settingsData.ChannelsStaticData[channelIndex].IntermittentOnTime * 1000.0f);
                            byte[] floatBytes = BitConverter.GetBytes(intermittentOnTimeMs);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;

                    case 27: // Intermittent off time
                        if (dataStructures.ChannelsLiveData[channelIndex].IntermittentOffTime != settingsData.ChannelsStaticData[channelIndex].IntermittentOffTime)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            int intermittentOffTimeMs = (int)Math.Round(settingsData.ChannelsStaticData[channelIndex].IntermittentOffTime * 1000.0f);
                            byte[] floatBytes = BitConverter.GetBytes(intermittentOffTimeMs);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 28: // Delayed on flag
                        if (dataStructures.ChannelsLiveData[channelIndex].DelayedOn != settingsData.ChannelsStaticData[channelIndex].DelayedOn)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].DelayedOn, true);
                            AddData(0, true);
                            AddData(0, true);
                            AddData(0, true);
                        }
                        break;
                    case 29: // Delayed on time
                        if (dataStructures.ChannelsLiveData[channelIndex].DelayedOnTime != settingsData.ChannelsStaticData[channelIndex].DelayedOnTime)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] delayOnBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].DelayedOnTime);
                            foreach (byte b in delayOnBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 30: // Delayed off flag
                        if (dataStructures.ChannelsLiveData[channelIndex].DelayedOff != settingsData.ChannelsStaticData[channelIndex].DelayedOff)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].DelayedOff, true);
                            AddData(0, true);
                            AddData(0, true);
                            AddData(0, true);
                        }
                        break;
                    case 31: // Delayed off time
                        if (dataStructures.ChannelsLiveData[channelIndex].DelayedOffTime != settingsData.ChannelsStaticData[channelIndex].DelayedOffTime)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            byte[] delayOffBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].DelayedOffTime);
                            foreach (byte b in delayOffBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 32: // Delayed off trigger
                        if (dataStructures.ChannelsLiveData[channelIndex].DelayedOffTrigger != settingsData.ChannelsStaticData[channelIndex].DelayedOffTrigger)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].DelayedOffTrigger, true);
                            AddData(0, true);
                            AddData(0, true);
                            AddData(0, true);
                        }
                        break;

                }

                break;
            case 1: // Analogue input data
                switch (parameterIndex)
                {
                    case 0: // Pull-up enable
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].PullUpEnable != settingsData.AnalogueInputsStaticData[analogueIndex].PullUpEnable)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].PullUpEnable ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 1: // Pull-down enable
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].PullDownEnable != settingsData.AnalogueInputsStaticData[analogueIndex].PullDownEnable)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].PullDownEnable ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 2: // Channel type
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].ChanType != settingsData.AnalogueInputsStaticData[analogueIndex].ChanType)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData((byte)settingsData.AnalogueInputsStaticData[analogueIndex].ChanType, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 3: // Units
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].Units != settingsData.AnalogueInputsStaticData[analogueIndex].Units)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData((byte)settingsData.AnalogueInputsStaticData[analogueIndex].Units, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 4: // Calibration points
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].CalibrationPoints != settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationPoints)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            AddData(settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationPoints, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 5: // Calibration point 1 voltage
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].CalibrationVolt1 != settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationVolt1)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationVolt1);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 6: // Calibration point 1 value
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].CalibrationValue1 != settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationValue1)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationValue1);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 7: // Calibration point 2 voltage
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].CalibrationVolt2 != settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationVolt2)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationVolt2);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 8: // Calibration point 2 value
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].CalibrationValue2 != settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationValue2)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationValue2);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 9: // Calibration point 3 voltage
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].CalibrationVolt3 != settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationVolt3)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationVolt3);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 10: // Calibration point 3 value
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].CalibrationValue3 != settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationValue3)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].CalibrationValue3);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 11: // NTC beta
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].NtcBeta != settingsData.AnalogueInputsStaticData[analogueIndex].NtcBeta)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].NtcBeta);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 12: // NTC nominal resistance
                        if (dataStructures.AnalogueInputsLiveData[analogueIndex].NtcNominalResistance != settingsData.AnalogueInputsStaticData[analogueIndex].NtcNominalResistance)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)analogueIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.AnalogueInputsStaticData[analogueIndex].NtcNominalResistance);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                }
                break;
            case 2: // System data
                switch (parameterIndex)
                {
                    case 0: // CAN Resistor enabled
                        if (dataStructures.SystemParamsStaticData.CANResEnabled != settingsData.SystemParamsStaticData.CANResEnabled)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            AddData(settingsData.SystemParamsStaticData.CANResEnabled ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 1: // Channel data CAN ID
                        if (dataStructures.SystemParamsStaticData.ChannelDataCANID != settingsData.SystemParamsStaticData.ChannelDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.ChannelDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding                            
                        }
                        break;
                    case 2: // Digital input data CAN ID
                        if (dataStructures.SystemParamsStaticData.DigitalInputDataCANID != settingsData.SystemParamsStaticData.DigitalInputDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.DigitalInputDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding                            
                        }
                        break;
                    case 3: // Analogue input data CAN ID
                        if (dataStructures.SystemParamsStaticData.AnalogueInputDataCANID != settingsData.SystemParamsStaticData.AnalogueInputDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.AnalogueInputDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding                            
                        }
                        break;
                    case 4: // System data CAN ID
                        if (dataStructures.SystemParamsStaticData.SystemDataCANID != settingsData.SystemParamsStaticData.SystemDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.SystemDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding                            
                        }
                        break;
                    case 5: // Config data CAN ID
                        if (dataStructures.SystemParamsStaticData.ConfigDataCANID != settingsData.SystemParamsStaticData.ConfigDataCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.ConfigDataCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding                            
                        }
                        break;
                    case 6: // IMU wake window
                        if (dataStructures.SystemParamsStaticData.IMUWakeWindow != settingsData.SystemParamsStaticData.IMUWakeWindow)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.IMUWakeWindow);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding                            
                        }
                        break;
                    case 7: // Speed unit preference
                        if (dataStructures.SystemParamsStaticData.SpeedUnitPref != settingsData.SystemParamsStaticData.SpeedUnitPref)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            AddData(settingsData.SystemParamsStaticData.SpeedUnitPref ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 8: // Distance unit preference
                        if (dataStructures.SystemParamsStaticData.DistanceUnitPref != settingsData.SystemParamsStaticData.DistanceUnitPref)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            AddData(settingsData.SystemParamsStaticData.DistanceUnitPref ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 9: // Allow GSM data
                        if (dataStructures.SystemParamsStaticData.AllowData != settingsData.SystemParamsStaticData.AllowData)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            AddData(settingsData.SystemParamsStaticData.AllowData ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding                            
                        }
                        break;

                    case 10: // Allow GPS data
                        if (dataStructures.SystemParamsStaticData.AllowGPS != settingsData.SystemParamsStaticData.AllowGPS)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            AddData(settingsData.SystemParamsStaticData.AllowGPS ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                    case 11: // Allow motion detect
                        if (dataStructures.SystemParamsStaticData.AllowMotionDetect != settingsData.SystemParamsStaticData.AllowMotionDetect)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            AddData(settingsData.SystemParamsStaticData.AllowMotionDetect ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding                            
                        }
                        break;

                    case 12: // System config CAN ID
                        if (dataStructures.SystemParamsStaticData.SystemConfigCANID != settingsData.SystemParamsStaticData.SystemConfigCANID)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.SystemConfigCANID);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                            AddData(0, true); // Padding                            
                        }
                        break;
                    case 13: // Time zone and DST rule blob
                        if (!ByteArraysEqual(dataStructures.SystemParamsStaticData.TimeZoneRule, settingsData.SystemParamsStaticData.TimeZoneRule))
                        {
                            byte[] timeZoneRule = settingsData.SystemParamsStaticData.TimeZoneRule ?? Array.Empty<byte>();
                            if (timeZoneRule.Length == Constants.TIME_ZONE_RULE_LENGTH)
                            {
                                configChanged = true;
                                AddData((byte)settingIndex, true);
                                AddData((byte)parameterIndex, true);
                                AddData(0, true); // Padding
                                foreach (byte b in timeZoneRule)
                                {
                                    AddData(b, true);
                                }
                            }
                        }
                        break;
                    case 14: // CAN bus bitrate
                        if (dataStructures.SystemParamsStaticData.CANBusBitrate != settingsData.SystemParamsStaticData.CANBusBitrate)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData(0, true); // Padding
                            byte[] uintBytes = BitConverter.GetBytes(settingsData.SystemParamsStaticData.CANBusBitrate);
                            foreach (byte b in uintBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                }
                break;

            case 3: // Digital inputs
                switch (parameterIndex)
                {
                    case 0: // Digital input active high
                        if (dataStructures.DigitalInputsLiveData[digitalIndex].IsActiveHigh != settingsData.DigitalInputsStaticData[digitalIndex].IsActiveHigh)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)digitalIndex, true);
                            AddData(settingsData.DigitalInputsStaticData[digitalIndex].IsActiveHigh ? (byte)1 : (byte)0, true);
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                            AddData(0, true); // Padding
                        }
                        break;
                }
                break;

            case 4: // Cellular data
                CellularParameters liveCellular = dataStructures.CellularParamsStaticData;
                CellularParameters settingsCellular = settingsData.CellularParamsStaticData;
                bool forceCellularConfigSend = liveCellular.ConfigVersion != Constants.CELLULAR_CONFIG_VERSION;
                switch (parameterIndex)
                {
                    case 0:
                        configChanged = AddByteConfigIfChanged(liveCellular.ConfigVersion, Constants.CELLULAR_CONFIG_VERSION, 0);
                        break;
                    case 1:
                        configChanged = AddByteConfigIfChanged(liveCellular.Protocol, settingsCellular.Protocol, 0, forceCellularConfigSend);
                        break;
                    case 2:
                        configChanged = AddBoolConfigIfChanged(liveCellular.UseTLS, settingsCellular.UseTLS, 0, forceCellularConfigSend);
                        break;
                    case 3:
                        configChanged = AddStringConfigIfChanged(
                            liveCellular.APN,
                            settingsCellular.APN,
                            Constants.CELLULAR_APN_LENGTH,
                            forceCellularConfigSend || !string.IsNullOrWhiteSpace(settingsCellular.APN));
                        break;
                    case 4:
                        configChanged = AddStringConfigIfChanged(liveCellular.APNUser, settingsCellular.APNUser, Constants.CELLULAR_APN_USER_LENGTH, forceCellularConfigSend);
                        break;
                    case 5:
                        configChanged = AddStringConfigIfChanged(liveCellular.APNPassword, settingsCellular.APNPassword, Constants.CELLULAR_APN_PASSWORD_LENGTH, forceCellularConfigSend);
                        break;
                    case 6:
                        configChanged = AddStringConfigIfChanged(liveCellular.OpenRemoteHost, settingsCellular.OpenRemoteHost, Constants.CELLULAR_HOST_LENGTH, forceCellularConfigSend);
                        break;
                    case 7:
                        configChanged = AddUInt16ConfigIfChanged(liveCellular.OpenRemotePort, settingsCellular.OpenRemotePort, 0, forceCellularConfigSend);
                        break;
                    case 8:
                        configChanged = false;
                        break;
                    case 9:
                        configChanged = AddStringConfigIfChanged(liveCellular.MQTTUsername, settingsCellular.MQTTUsername, Constants.CELLULAR_MQTT_USERNAME_LENGTH, forceCellularConfigSend);
                        break;
                    case 10:
                        configChanged = AddStringConfigIfChanged(liveCellular.MQTTPassword, settingsCellular.MQTTPassword, Constants.CELLULAR_MQTT_PASSWORD_LENGTH, forceCellularConfigSend);
                        break;
                    case 11:
                        configChanged = AddStringConfigIfChanged(liveCellular.PublishTopic, settingsCellular.PublishTopic, Constants.CELLULAR_TOPIC_LENGTH, forceCellularConfigSend);
                        break;
                    case 12:
                        configChanged = AddStringConfigIfChanged(liveCellular.SubscribeTopic, settingsCellular.SubscribeTopic, Constants.CELLULAR_TOPIC_LENGTH, forceCellularConfigSend);
                        break;
                    case 13:
                        if (forceCellularConfigSend || liveCellular.KeepAliveSeconds != settingsCellular.KeepAliveSeconds || liveCellular.PublishIntervalMs != settingsCellular.PublishIntervalMs)
                        {
                            configChanged = true;
                            AddConfigHeader(0);
                            foreach (byte b in BitConverter.GetBytes(settingsCellular.KeepAliveSeconds))
                            {
                                AddData(b, true);
                            }
                            foreach (byte b in BitConverter.GetBytes(settingsCellular.PublishIntervalMs))
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 14:
                        configChanged = AddUInt32ConfigIfChanged(liveCellular.TelemetryUploadMask, settingsCellular.TelemetryUploadMask, 0, forceCellularConfigSend);
                        break;
                    case 15:
                        configChanged = AddStringConfigIfChanged(liveCellular.OpenRemoteAssetName, settingsCellular.OpenRemoteAssetName, Constants.CELLULAR_OPENREMOTE_ASSET_NAME_LENGTH, forceCellularConfigSend);
                        break;
                }
                break;
            }

            AddData(Constants.SERIAL_TRAILER1, true);
            AddData(Constants.SERIAL_TRAILER2, true);
            AddData((byte)(checkSumSend & 0xFF), false);
            AddData((byte)((checkSumSend >> 8) & 0xFF), false);
            AddData((byte)((checkSumSend >> 16) & 0xFF), false);
            AddData((byte)((checkSumSend >> 24) & 0xFF), false);

            if (configChanged)
            {
                switch (settingIndex)
                {
                    case 0:
                        Debug.WriteLine("Sending channel " + channelIndex + " parameter " + parameterIndex);
                        break;
                    case 1:
                        Debug.WriteLine("Sending analogue input " + analogueIndex + " parameter " + parameterIndex);
                        break;
                    case 2:
                        Debug.WriteLine("Sending system parameter " + parameterIndex);
                        break;
                    case 3:
                        Debug.WriteLine("Sending digital input " + digitalIndex + " parameter " + parameterIndex);
                        break;
                    case 4:
                        Debug.WriteLine("Sending cellular parameter " + parameterIndex);
                        break;
                }

                Debug.WriteLine(channelIndex);
                if (_serialPort.IsOpen && _serialPort != null)
                {
                    byte[] packet = _sendBuffer.ToArray();
                    byte[] commandAndPacket = new byte[packet.Length + 1];
                    commandAndPacket[0] = (byte)Constants.COMMAND_ID_NEWCONFIG;
                    Array.Copy(packet, 0, commandAndPacket, 1, packet.Length);
                    Debug.WriteLine($"CFG TX s={settingIndex} p={parameterIndex} ch={channelIndex} a={analogueIndex} d={digitalIndex} len={commandAndPacket.Length} cksum=0x{(uint)checkSumSend:X8} bytes={BitConverter.ToString(commandAndPacket)}");
                    _serialPort.Write(commandAndPacket, 0, commandAndPacket.Length);
                    lastCommandSent = Constants.COMMAND_ID_NEWCONFIG;
                    _configPacketsSent++;

                    Debug.Write("Wrote ");
                    Debug.Write(commandAndPacket.Length);
                    Debug.WriteLine(" bytes.");
                    _sendBuffer.Clear();
                }

                return retVal;
            }

            _sendBuffer.Clear();
            _configSlotsSkippedLocally++;

            if (!AdvanceConfigCursor())
            {
                if (saveToEEPROM)
                {
                    saveToEEPROM = false;
                    SendCommand(Constants.COMMAND_ID_SAVECHANGES);
                }

                return retVal;
            }
        }
    }

    private void AddData(byte data, bool addToCheck)
    {
        _sendBuffer.Add(data);
        totalBytesSent++;
        if (addToCheck)
        {
            checkSumSend += data;
        }
    }

    private void AddConfigHeader(byte dataIndex)
    {
        AddData((byte)settingIndex, true);
        AddData((byte)parameterIndex, true);
        AddData(dataIndex, true);
    }

    private bool AddBoolConfigIfChanged(bool liveValue, bool settingsValue, byte dataIndex, bool forceSend = false)
    {
        if (!forceSend && liveValue == settingsValue)
        {
            return false;
        }

        AddConfigHeader(dataIndex);
        AddData(settingsValue ? (byte)1 : (byte)0, true);
        AddData(0, true);
        AddData(0, true);
        return true;
    }

    private bool AddByteConfigIfChanged(byte liveValue, byte settingsValue, byte dataIndex, bool forceSend = false)
    {
        if (!forceSend && liveValue == settingsValue)
        {
            return false;
        }

        AddConfigHeader(dataIndex);
        AddData(settingsValue, true);
        AddData(0, true);
        AddData(0, true);
        return true;
    }

    private bool AddUInt16ConfigIfChanged(ushort liveValue, ushort settingsValue, byte dataIndex, bool forceSend = false)
    {
        if (!forceSend && liveValue == settingsValue)
        {
            return false;
        }

        AddConfigHeader(dataIndex);
        foreach (byte b in BitConverter.GetBytes(settingsValue))
        {
            AddData(b, true);
        }
        return true;
    }

    private bool AddUInt32ConfigIfChanged(uint liveValue, uint settingsValue, byte dataIndex, bool forceSend = false)
    {
        if (!forceSend && liveValue == settingsValue)
        {
            return false;
        }

        AddConfigHeader(dataIndex);
        foreach (byte b in BitConverter.GetBytes(settingsValue))
        {
            AddData(b, true);
        }
        return true;
    }

    private bool AddStringConfigIfChanged(string? liveValue, string? settingsValue, int fieldLength, bool forceSend = false)
    {
        string liveText = liveValue ?? string.Empty;
        string settingsText = settingsValue ?? string.Empty;
        if (!forceSend && string.Equals(liveText, settingsText, StringComparison.Ordinal))
        {
            return false;
        }

        AddConfigHeader(0);
        byte[] ascii = Encoding.ASCII.GetBytes(settingsText);
        int copyLength = Math.Min(ascii.Length, fieldLength - 1);
        for (int i = 0; i < fieldLength; i++)
        {
            AddData(i < copyLength ? ascii[i] : (byte)0, true);
        }

        return true;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_bulkLogTransferActive)
        {
            return;
        }

        if (_serialPort.IsOpen)
        {
            try
            {
                while (_serialPort.BytesToRead > 0 && _serialPort.IsOpen)
                {
                    byte readByte = (byte)_serialPort.ReadByte();

                    lock (_bufferLock)
                    {
                        receivedDataBuffer.Add(readByte);

                        if (ExpectsFramedResponse(lastCommandSent))
                        {
                            TrackFramedResponseByte(readByte);
                        }
                    }

                    bool expectsStandaloneAck =
                        lastCommandSent == Constants.COMMAND_ID_BEGIN ||
                        lastCommandSent == Constants.COMMAND_ID_NEWCONFIG ||
                        lastCommandSent == Constants.COMMAND_ID_SKIP ||
                        lastCommandSent == Constants.COMMAND_ID_SAVECHANGES ||
                        lastCommandSent == Constants.COMMAND_ID_SET_RTC ||
                        lastCommandSent == Constants.COMMAND_ID_FACTORY_RESET ||
                        lastCommandSent == Constants.COMMAND_ID_LOG_OPEN ||
                        lastCommandSent == Constants.COMMAND_ID_LOG_RESET ||
                        lastCommandSent == Constants.COMMAND_ID_LOG_CANCEL ||
                        lastCommandSent == Constants.COMMAND_ID_FW_UPLOAD_BEGIN ||
                        lastCommandSent == Constants.COMMAND_ID_FW_UPLOAD_CHUNK ||
                        lastCommandSent == Constants.COMMAND_ID_FW_UPLOAD_END ||
                        lastCommandSent == Constants.COMMAND_ID_FW_UPLOAD_CANCEL ||
                        lastCommandSent == Constants.COMMAND_ID_FW_INSTALL;

                    if (expectsStandaloneAck &&
                        (readByte == Constants.COMMAND_ID_CONFIM || readByte == Constants.COMMAND_ID_CHECKSUM_FAIL) &&
                        _serialPort.BytesToRead == 0)
                    {
                        lock (_bufferLock)
                        {
                            if (receivedDataBuffer.Count == 1)
                            {
                                receivedDataBuffer.RemoveAt(receivedDataBuffer.Count - 1);
                            }
                        }
                    }

                    bool standaloneCommandResponse = expectsStandaloneAck && (_serialPort.BytesToRead == 0);

                    if (lastCommandSent == Constants.COMMAND_ID_LOG_CHUNK &&
                        readByte == Constants.COMMAND_ID_CHECKSUM_FAIL &&
                        _serialPort.BytesToRead == 0)
                    {
                        bool singleByteChunkFail = false;
                        lock (_bufferLock)
                        {
                            if (receivedDataBuffer.Count == 1 && receivedDataBuffer[0] == Constants.COMMAND_ID_CHECKSUM_FAIL)
                            {
                                receivedDataBuffer.Clear();
                                singleByteChunkFail = true;
                            }
                        }

                        if (singleByteChunkFail)
                        {
                            _logChunkTcs?.TrySetCanceled();
                            LogProtocolDebug("RX LOG_CHUNK standalone CHECKSUM_FAIL byte");
                            continue;
                        }
                    }

                    if (lastCommandSent == Constants.COMMAND_ID_LOG_STREAM &&
                        readByte == Constants.COMMAND_ID_CHECKSUM_FAIL &&
                        _serialPort.BytesToRead == 0)
                    {
                        bool singleByteStreamFail = false;
                        lock (_bufferLock)
                        {
                            if (receivedDataBuffer.Count == 1 && receivedDataBuffer[0] == Constants.COMMAND_ID_CHECKSUM_FAIL)
                            {
                                receivedDataBuffer.Clear();
                                singleByteStreamFail = true;
                            }
                        }

                        if (singleByteStreamFail)
                        {
                            _logChunkTcs?.TrySetCanceled();
                            continue;
                        }
                    }

                    if (readByte == Constants.COMMAND_ID_CONFIM && (lastCommandSent == Constants.COMMAND_ID_REQUEST || standaloneCommandResponse))
                    {
                        switch (lastCommandSent)
                        {
                            case Constants.COMMAND_ID_BEGIN:
                                if (!sendingConfig)
                                {
                                    foundECU = true;
                                    lock (_bufferLock)
                                    {
                                        receivedDataBuffer.Clear();
                                    }
                                    RequestStaticSnapshot();
                                }
                                break;

                            case Constants.COMMAND_ID_REQUEST:
                                // After LOG_LIST, some firmware states may ACK REQUEST before
                                // framed status traffic resumes. Retry a couple of times only in
                                // that narrow recovery window.
                                if (_awaitingPostLogRefreshStatusFrame &&
                                    !sendingConfig &&
                                    !_suspendLiveRequestPolling &&
                                    _postLogRefreshRequestRetryCount < 3)
                                {
                                    _postLogRefreshRequestRetryCount++;
                                    SendCommand(Constants.COMMAND_ID_REQUEST);
                                }
                                break;

                            case Constants.COMMAND_ID_NEWCONFIG:
                            case Constants.COMMAND_ID_SKIP:
                                if (lastCommandSent == Constants.COMMAND_ID_NEWCONFIG && !sendingConfig && _standaloneCommandAckId == lastCommandSent)
                                {
                                    _standaloneCommandAckTcs?.TrySetResult(true);
                                    break;
                                }

                                _configRetryCount = 0;
                                if (sendingConfig)
                                {
                                    if (AdvanceConfigCursor())
                                    {
                                        SendConfig();
                                    }

                                    if (saveToEEPROM)
                                    {
                                        saveToEEPROM = false;
                                        SendCommand(Constants.COMMAND_ID_SAVECHANGES);
                                    }
                                }
                                else
                                {
                                    SendCommand(Constants.COMMAND_ID_REQUEST);
                                }
                                break;

                            case Constants.COMMAND_ID_SAVECHANGES:
                                if (!sendingConfig && _standaloneCommandAckId == lastCommandSent)
                                {
                                    _standaloneCommandAckTcs?.TrySetResult(true);
                                }

                                _saveRetryCount = 0;
                                lock (_bufferLock)
                                {
                                    receivedDataBuffer.Clear();
                                }
                                RequestStaticSnapshot();
                                _configSendStopwatch.Stop();
                                Debug.WriteLine($"Configuration saved to EEPROM. packets={_configPacketsSent}, localSkips={_configSlotsSkippedLocally}, elapsedMs={_configSendStopwatch.ElapsedMilliseconds}");
                                LoggingService.AddLog("PDM settings saved.");
                                ConfigurationSaved?.Invoke(this, EventArgs.Empty);
                                ConfigurationSaveCompleted?.Invoke(this, new ConfigurationSaveCompletedEventArgs(true));
                                break;

                            case Constants.COMMAND_ID_LOG_OPEN:
                                _logOpenTcs?.TrySetResult(true);
                                LogProtocolDebug("RX LOG_OPEN ACK");
                                break;

                            case Constants.COMMAND_ID_LOG_CANCEL:
                                break;

                            case Constants.COMMAND_ID_LOG_RESET:
                                _logResetTcs?.TrySetResult(true);
                                break;

                            case Constants.COMMAND_ID_SET_RTC:
                            case Constants.COMMAND_ID_FACTORY_RESET:
                            case Constants.COMMAND_ID_FW_UPLOAD_BEGIN:
                            case Constants.COMMAND_ID_FW_UPLOAD_CHUNK:
                            case Constants.COMMAND_ID_FW_UPLOAD_END:
                            case Constants.COMMAND_ID_FW_UPLOAD_CANCEL:
                            case Constants.COMMAND_ID_FW_INSTALL:
                                if (_standaloneCommandAckId == lastCommandSent)
                                {
                                    _standaloneCommandAckTcs?.TrySetResult(true);
                                }
                                break;
                        }
                    }
                    else if (readByte == Constants.COMMAND_ID_CHECKSUM_FAIL && standaloneCommandResponse)
                    {
                        switch (lastCommandSent)
                        {
                            case Constants.COMMAND_ID_NEWCONFIG:
                                if (!sendingConfig && _standaloneCommandAckId == lastCommandSent)
                                {
                                    _standaloneCommandAckTcs?.TrySetResult(false);
                                    break;
                                }

                                Debug.WriteLine($"CFG FAIL s={settingIndex} p={parameterIndex} ch={channelIndex} a={analogueIndex} d={digitalIndex}");
                                if (_configRetryCount < 1)
                                {
                                    _configRetryCount++;
                                    Debug.WriteLine("PDM reported checksum failure for config data. Retrying...");
                                    LoggingService.AddLog("There was a problem saving settings. Trying again...");
                                    SendConfig();
                                }
                                else
                                {
                                    Debug.WriteLine("PDM reported checksum failure for config data. Aborting send.");
                                    LoggingService.AddLog("Settings could not be saved. Please try again.");
                                    sendingConfig = false;
                                    saveToEEPROM = false;
                                    parameterIndex = channelIndex = analogueIndex = settingIndex = digitalIndex = 0;
                                    _configRetryCount = 0;
                                    _configSendStopwatch.Stop();
                                    ConfigurationSaveCompleted?.Invoke(this, new ConfigurationSaveCompletedEventArgs(false));
                                }
                                break;
                            case Constants.COMMAND_ID_SAVECHANGES:
                                if (!sendingConfig && _standaloneCommandAckId == lastCommandSent)
                                {
                                    _standaloneCommandAckTcs?.TrySetResult(false);
                                    break;
                                }

                                Debug.WriteLine("SAVE FAIL");
                                if (_saveRetryCount < 1)
                                {
                                    _saveRetryCount++;
                                    Debug.WriteLine("PDM reported checksum failure when saving changes. Retrying...");
                                    LoggingService.AddLog("There was a problem saving settings. Trying again...");
                                    SendCommand(Constants.COMMAND_ID_SAVECHANGES);
                                }
                                else
                                {
                                    Debug.WriteLine("PDM reported checksum failure when saving changes. Try again.");
                                    LoggingService.AddLog("Settings could not be saved. Please try again.");
                                    parameterIndex = channelIndex = analogueIndex = settingIndex = digitalIndex = 0;
                                    _saveRetryCount = 0;
                                    _configSendStopwatch.Stop();
                                    ConfigurationSaveCompleted?.Invoke(this, new ConfigurationSaveCompletedEventArgs(false));
                                }

                                break;

                            case Constants.COMMAND_ID_LOG_OPEN:
                                _logOpenTcs?.TrySetResult(false);
                                LogProtocolDebug("RX LOG_OPEN CHECKSUM_FAIL");
                                break;

                            case Constants.COMMAND_ID_LOG_CHUNK:
                                _logChunkTcs?.TrySetCanceled();
                                LogProtocolDebug("RX LOG_CHUNK CHECKSUM_FAIL");
                                break;

                            case Constants.COMMAND_ID_LOG_RESET:
                                _logResetTcs?.TrySetResult(false);
                                break;

                            case Constants.COMMAND_ID_SET_RTC:
                            case Constants.COMMAND_ID_FACTORY_RESET:
                            case Constants.COMMAND_ID_FW_UPLOAD_BEGIN:
                            case Constants.COMMAND_ID_FW_UPLOAD_CHUNK:
                            case Constants.COMMAND_ID_FW_UPLOAD_END:
                            case Constants.COMMAND_ID_FW_UPLOAD_CANCEL:
                            case Constants.COMMAND_ID_FW_INSTALL:
                                if (_standaloneCommandAckId == lastCommandSent)
                                {
                                    _standaloneCommandAckTcs?.TrySetResult(false);
                                }
                                break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Do nothing. Disconnect was probably hit
            }
        }
    }

    public bool InitComms()
    {
        bool retVal = foundECU;
        if (_serialPort is not null)
        {
            if (_serialPort.IsOpen)
            {
                if (!foundECU)
                {
                    try
                    {
                        ResetLogReceiveState(discardSerialInput: true);
                        SendCommand(Constants.COMMAND_ID_BEGIN);

                        if (_serialPort.BytesToRead > 0)
                        {
                            Debug.WriteLine("Data in buffer");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error sending request: {ex.Message}");
                    }
                }
            }
        }
        return retVal;
    }

    public bool Open()
    {
        bool retVal = false;
        LastError = null;

        try
        {
            _serialPort.Open();

            if (_serialPort.IsOpen)
            {
                ResetLogReceiveState(discardSerialInput: true);
                retVal = true;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            LastError = OperatingSystem.IsLinux()
                ? $"Failed to open {_serialPort.PortName}: {ex.Message}. Check that the Ubuntu user can access serial devices, for example by joining the dialout group."
                : $"Failed to open {_serialPort.PortName}: {ex.Message}";
            Debug.WriteLine($"Error connecting: {LastError}");
        }
        catch (Exception ex)
        {
            LastError = $"Failed to open {_serialPort.PortName}: {ex.Message}";
            Debug.WriteLine($"Error connecting: {LastError}");
        }
        return retVal;
    }
    public void Close()
    {
        if (_serialPort.IsOpen)
        {
            ResetLogReceiveState(discardSerialInput: true);
            _serialPort.Close();
        }
    }
    public void Send(string text) => _serialPort.WriteLine(text);

    public void StartSendConfig()
    {
        sendingConfig = true;
        settingIndex = 1;
        channelIndex = 0;
        analogueIndex = 0;
        digitalIndex = 0;
        parameterIndex = 0;
        _configRetryCount = 0;
        _saveRetryCount = 0;
        _configPacketsSent = 0;
        _configSlotsSkippedLocally = 0;
        _repeatAnalogueSettingsAfterChannelsPending = true;
        _finalAnalogueResendActive = false;
        _configSendStopwatch.Restart();
        SendConfig();
        LoggingService.AddLog("Saving settings to PDM...");
    }

    private void SendCommand(char commandId)
    {
        if (_serialPort.IsOpen)
        {
            byte[] data = new byte[1];
            data[0] = (byte)commandId;
            _serialPort.Write(data, 0, data.Length);
            lastCommandSent = commandId;
            if (commandId == Constants.COMMAND_ID_BEGIN)
            {
                _lastHandshakeAttemptTick = Environment.TickCount64;
            }
            if (commandId == Constants.COMMAND_ID_REQUEST)
            {
                _lastLiveRequestSentTick = Environment.TickCount64;
            }
            if (commandId == Constants.COMMAND_ID_REQUEST_STATIC)
            {
                _lastStaticRequestSentTick = Environment.TickCount64;
            }
        }
    }

    public void EnsureLiveRequestPolling(int idleThresholdMs = 1500, int reconnectThresholdMs = 5000, int handshakeRetryMs = 1000)
    {
        if (!_serialPort.IsOpen ||
            sendingConfig ||
            _suspendLiveRequestPolling ||
            _logTransferSessionActive)
        {
            return;
        }

        long now = Environment.TickCount64;

        if (!foundECU)
        {
            if ((now - _lastHandshakeAttemptTick) >= handshakeRetryMs)
            {
                SendCommand(Constants.COMMAND_ID_BEGIN);
            }

            return;
        }

        if (UpdateStaticData)
        {
            if (_lastStaticRequestSentTick == 0 || (now - _lastStaticRequestSentTick) >= StaticSnapshotRetryIntervalMs)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST_STATIC);
            }

            return;
        }

        long lastActivityTick = Math.Max(_lastLiveRequestSentTick, _lastLiveStatusFrameTick);

        if (lastActivityTick == 0)
        {
            SendCommand(Constants.COMMAND_ID_REQUEST);
            return;
        }

        if ((now - lastActivityTick) >= reconnectThresholdMs)
        {
            LoggingService.AddLog("Controller comms lost. Reconnecting...");
            MarkControllerSessionLost(requestStaticSnapshot: true);
            SendCommand(Constants.COMMAND_ID_BEGIN);
            return;
        }

        if ((now - lastActivityTick) >= idleThresholdMs)
        {
            SendCommand(Constants.COMMAND_ID_REQUEST);
        }
    }

    public void RequestStaticSnapshot()
    {
        if (!_serialPort.IsOpen || sendingConfig)
        {
            return;
        }

        UpdateStaticData = true;
        SendCommand(Constants.COMMAND_ID_REQUEST_STATIC);
    }

    private void SendCommandWithByte(char commandId, byte value)
    {
        if (_serialPort.IsOpen)
        {
            byte[] data = new byte[2];
            data[0] = (byte)commandId;
            data[1] = value;
            _serialPort.Write(data, 0, data.Length);
            lastCommandSent = commandId;
        }
    }

    private void SendCommandWithUInt32(char commandId, byte firstValue, uint value)
    {
        if (_serialPort.IsOpen)
        {
            byte[] data = new byte[6];
            data[0] = (byte)commandId;
            data[1] = firstValue;
            byte[] valueBytes = BitConverter.GetBytes(value);
            Array.Copy(valueBytes, 0, data, 2, valueBytes.Length);
            _serialPort.Write(data, 0, data.Length);
            lastCommandSent = commandId;
        }
    }

    private void SendCommandWithPayload(char commandId, byte[] payload, int payloadLength)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        byte[] data = new byte[payloadLength + 3];
        data[0] = (byte)commandId;
        data[1] = (byte)(payloadLength & 0xFF);
        data[2] = (byte)((payloadLength >> 8) & 0xFF);
        Array.Copy(payload, 0, data, 3, payloadLength);
        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = commandId;
    }

    private void SendCommandWithRawPayload(char commandId, byte[] payload)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        byte[] data = new byte[payload.Length + 1];
        data[0] = (byte)commandId;
        Array.Copy(payload, 0, data, 1, payload.Length);
        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = commandId;
    }

    private void SendStandaloneSystemConfigCommand(byte parameterIndex, byte[] payload)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        int checksum = 0;
        var packet = new List<byte>(payload.Length + 12);

        void AddPacketByte(byte value, bool includeInChecksum)
        {
            packet.Add(value);
            if (includeInChecksum)
            {
                checksum += value;
            }
        }

        AddPacketByte(Constants.SERIAL_HEADER1, true);
        AddPacketByte(Constants.SERIAL_HEADER2, true);
        AddPacketByte(2, true);
        AddPacketByte(parameterIndex, true);
        AddPacketByte(0, true);

        foreach (byte value in payload)
        {
            AddPacketByte(value, true);
        }

        AddPacketByte(Constants.SERIAL_TRAILER1, true);
        AddPacketByte(Constants.SERIAL_TRAILER2, true);
        AddPacketByte((byte)(checksum & 0xFF), false);
        AddPacketByte((byte)((checksum >> 8) & 0xFF), false);
        AddPacketByte((byte)((checksum >> 16) & 0xFF), false);
        AddPacketByte((byte)((checksum >> 24) & 0xFF), false);

        SendCommandWithRawPayload(Constants.COMMAND_ID_NEWCONFIG, packet.ToArray());
    }

    private async Task<bool> AwaitStandaloneAckAsync(char commandId, Action sendAction, int timeoutMs, bool discardBeforeSend = true, int preCommandDelayMs = StandaloneCommandDrainDelayMs)
    {
        if (!_serialPort.IsOpen)
        {
            return false;
        }

        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        ResetLogReceiveState(discardSerialInput: discardBeforeSend);

        if (preCommandDelayMs > 0)
        {
            await Task.Delay(preCommandDelayMs);
        }

        if (!_serialPort.IsOpen)
        {
            return false;
        }

        if (discardBeforeSend)
        {
            ResetLogReceiveState(discardSerialInput: true);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _standaloneCommandAckTcs = tcs;
        _standaloneCommandAckId = commandId;

        try
        {
            sendAction();

            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                MarkControllerSessionLost(requestStaticSnapshot: true);
                return false;
            }

            return await tcs.Task;
        }
        finally
        {
            if (_standaloneCommandAckTcs == tcs)
            {
                _standaloneCommandAckTcs = null;
                _standaloneCommandAckId = '\0';
            }

            ResetLogReceiveState(discardSerialInput: false);
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
            {
                EnsureLiveRequestPolling();
            }
        }
    }

    public void BeginFirmwareUpdateSession()
    {
        ResetLogReceiveState(discardSerialInput: true);
        _suspendLiveRequestPolling = true;
    }

    public void EndFirmwareUpdateSession(bool expectControllerRestart = false)
    {
        ResetLogReceiveState(discardSerialInput: false);
        _suspendLiveRequestPolling = false;

        if (!_serialPort.IsOpen || sendingConfig)
        {
            return;
        }

        if (expectControllerRestart)
        {
            LoggingService.AddLog("Controller restarting after firmware update...");
            MarkControllerSessionLost(requestStaticSnapshot: true);
            return;
        }

        if (_serialPort.IsOpen && !sendingConfig)
        {
            SendCommand(Constants.COMMAND_ID_REQUEST);
        }
    }

    public async Task<string?> RequestFirmwareVersionAsync(int timeoutMs = 3000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        _suspendLiveRequestPolling = true;
        ResetLogReceiveState(discardSerialInput: true);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _firmwareVersionTcs = tcs;

        try
        {
            SendCommand(Constants.COMMAND_ID_FW_VER);
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                return null;
            }

            return await tcs.Task;
        }
        finally
        {
            if (_firmwareVersionTcs == tcs)
            {
                _firmwareVersionTcs = null;
            }

            ResetLogReceiveState(discardSerialInput: false);
            _suspendLiveRequestPolling = false;
            if (_serialPort.IsOpen && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }
        }
    }

    public async Task<string?> RequestBuildDateAsync(int timeoutMs = 3000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        _suspendLiveRequestPolling = true;
        ResetLogReceiveState(discardSerialInput: true);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _buildDateTcs = tcs;

        try
        {
            SendCommand(Constants.COMMAND_ID_BUILD_DATE);
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                return null;
            }

            return await tcs.Task;
        }
        finally
        {
            if (_buildDateTcs == tcs)
            {
                _buildDateTcs = null;
            }

            ResetLogReceiveState(discardSerialInput: false);
            _suspendLiveRequestPolling = false;
            if (_serialPort.IsOpen && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }
        }
    }

    public async Task<string?> RequestFirmwareDiagnosticAsync(int timeoutMs = 3000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        ResetLogReceiveState(discardSerialInput: true);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _firmwareDiagnosticTcs = tcs;

        try
        {
            SendCommand(Constants.COMMAND_ID_FW_DIAGNOSTIC);
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                return null;
            }

            return await tcs.Task;
        }
        finally
        {
            if (_firmwareDiagnosticTcs == tcs)
            {
                _firmwareDiagnosticTcs = null;
            }

            ResetLogReceiveState(discardSerialInput: false);
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }
        }
    }

    public async Task<string?> RequestCellularTestAsync(CellularParameters? cellularSettings = null, int timeoutMs = 3000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        ResetLogReceiveState(discardSerialInput: true);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cellularTestTcs = tcs;

        try
        {
            SendCellularTestCommand(cellularSettings);
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                return null;
            }

            return await tcs.Task;
        }
        finally
        {
            if (_cellularTestTcs == tcs)
            {
                _cellularTestTcs = null;
            }

            ResetLogReceiveState(discardSerialInput: false);
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }
        }
    }

    public async Task<string?> RequestOpenRemoteProvisioningAsync(CellularParameters cellularSettings, string provisioningRequestJson, int timeoutMs = 190000)
    {
        if (!_serialPort.IsOpen || cellularSettings == null || string.IsNullOrWhiteSpace(provisioningRequestJson))
        {
            return null;
        }

        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        ResetLogReceiveState(discardSerialInput: true);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _openRemoteProvisioningTcs = tcs;

        try
        {
            SendOpenRemoteProvisioningCommand(cellularSettings, provisioningRequestJson);
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                return null;
            }

            return await tcs.Task;
        }
        finally
        {
            if (_openRemoteProvisioningTcs == tcs)
            {
                _openRemoteProvisioningTcs = null;
            }

            ResetLogReceiveState(discardSerialInput: false);
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }
        }
    }

    private void SendOpenRemoteProvisioningCommand(CellularParameters cellularSettings, string provisioningRequestJson)
    {
        cellularSettings.EnsurePublishTopicFromOpenRemoteFields();
        byte[] requestBytes = Encoding.ASCII.GetBytes(provisioningRequestJson.Trim());
        if (requestBytes.Length == 0 || requestBytes.Length > OpenRemoteProvisioningMaxRequestBytes)
        {
            throw new InvalidOperationException($"OpenRemote provisioning request must be between 1 and {OpenRemoteProvisioningMaxRequestBytes} bytes.");
        }

        byte[] payload = new byte[
            Constants.CELLULAR_APN_LENGTH +
            Constants.CELLULAR_HOST_LENGTH +
            sizeof(ushort) +
            Constants.CELLULAR_CLIENT_ID_LENGTH +
            Constants.CELLULAR_MQTT_USERNAME_LENGTH +
            Constants.CELLULAR_MQTT_PASSWORD_LENGTH +
            Constants.CELLULAR_TOPIC_LENGTH +
            sizeof(byte) +
            sizeof(ushort) +
            requestBytes.Length];

        int offset = 0;
        WriteFixedAscii(payload, ref offset, cellularSettings.APN, Constants.CELLULAR_APN_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.OpenRemoteHost, Constants.CELLULAR_HOST_LENGTH);
        foreach (byte value in BitConverter.GetBytes(cellularSettings.OpenRemotePort))
        {
            payload[offset++] = value;
        }
        WriteFixedAscii(payload, ref offset, cellularSettings.ClientID, Constants.CELLULAR_CLIENT_ID_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.MQTTUsername, Constants.CELLULAR_MQTT_USERNAME_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.MQTTPassword, Constants.CELLULAR_MQTT_PASSWORD_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.PublishTopic, Constants.CELLULAR_TOPIC_LENGTH);
        payload[offset++] = cellularSettings.UseTLS ? (byte)1 : (byte)0;
        foreach (byte value in BitConverter.GetBytes((ushort)requestBytes.Length))
        {
            payload[offset++] = value;
        }
        Array.Copy(requestBytes, 0, payload, offset, requestBytes.Length);

        byte[] commandAndPayload = new byte[payload.Length + 1];
        commandAndPayload[0] = (byte)Constants.COMMAND_ID_OPENREMOTE_PROVISION;
        Array.Copy(payload, 0, commandAndPayload, 1, payload.Length);
        _serialPort.Write(commandAndPayload, 0, commandAndPayload.Length);
        lastCommandSent = Constants.COMMAND_ID_OPENREMOTE_PROVISION;
    }

    private void SendCellularTestCommand(CellularParameters? cellularSettings)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        if (cellularSettings is null)
        {
            _serialPort.Write([(byte)Constants.COMMAND_ID_CELLULAR_TEST], 0, 1);
            lastCommandSent = Constants.COMMAND_ID_CELLULAR_TEST;
            return;
        }

        cellularSettings.EnsurePublishTopicFromOpenRemoteFields();
        byte[] payload = new byte[
            Constants.CELLULAR_APN_LENGTH +
            Constants.CELLULAR_HOST_LENGTH +
            sizeof(ushort) +
            Constants.CELLULAR_CLIENT_ID_LENGTH +
            Constants.CELLULAR_MQTT_USERNAME_LENGTH +
            Constants.CELLULAR_MQTT_PASSWORD_LENGTH +
            Constants.CELLULAR_TOPIC_LENGTH +
            sizeof(byte) +
            sizeof(uint)];

        int offset = 0;
        WriteFixedAscii(payload, ref offset, cellularSettings.APN, Constants.CELLULAR_APN_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.OpenRemoteHost, Constants.CELLULAR_HOST_LENGTH);
        foreach (byte value in BitConverter.GetBytes(cellularSettings.OpenRemotePort))
        {
            payload[offset++] = value;
        }
        WriteFixedAscii(payload, ref offset, cellularSettings.ClientID, Constants.CELLULAR_CLIENT_ID_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.MQTTUsername, Constants.CELLULAR_MQTT_USERNAME_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.MQTTPassword, Constants.CELLULAR_MQTT_PASSWORD_LENGTH);
        WriteFixedAscii(payload, ref offset, cellularSettings.PublishTopic, Constants.CELLULAR_TOPIC_LENGTH);
        payload[offset++] = cellularSettings.UseTLS ? (byte)1 : (byte)0;
        foreach (byte value in BitConverter.GetBytes(cellularSettings.TelemetryUploadMask))
        {
            payload[offset++] = value;
        }

        byte[] commandAndPayload = new byte[payload.Length + 1];
        commandAndPayload[0] = (byte)Constants.COMMAND_ID_CELLULAR_TEST;
        Array.Copy(payload, 0, commandAndPayload, 1, payload.Length);
        _serialPort.Write(commandAndPayload, 0, commandAndPayload.Length);
        lastCommandSent = Constants.COMMAND_ID_CELLULAR_TEST;
    }

    private static void WriteFixedAscii(byte[] payload, ref int offset, string? value, int fieldLength)
    {
        string text = value?.Trim() ?? string.Empty;
        byte[] ascii = Encoding.ASCII.GetBytes(text);
        int copyLength = Math.Min(ascii.Length, fieldLength - 1);
        Array.Copy(ascii, 0, payload, offset, copyLength);
        offset += fieldLength;
    }

    public async Task<bool> WaitForControllerReconnectAsync(int timeoutMs = 20000, int pollIntervalMs = 250)
    {
        long deadlineTick = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadlineTick)
        {
            if (!_serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Open();
                    ResetLogReceiveState(discardSerialInput: true);
                    MarkControllerSessionLost(requestStaticSnapshot: true);
                }
                catch
                {
                    await Task.Delay(pollIntervalMs);
                    continue;
                }
            }

            EnsureLiveRequestPolling();

            if (foundECU && _lastLiveStatusFrameTick != 0)
            {
                return true;
            }

            await Task.Delay(pollIntervalMs);
        }

        return foundECU && _lastLiveStatusFrameTick != 0;
    }

    public Task<bool> BeginFirmwareUploadAsync(byte assetType, int sizeBytes, int timeoutMs)
    {
        return AwaitStandaloneAckAsync(
            Constants.COMMAND_ID_FW_UPLOAD_BEGIN,
            () => SendCommandWithUInt32(Constants.COMMAND_ID_FW_UPLOAD_BEGIN, assetType, (uint)sizeBytes),
            timeoutMs);
    }

    public Task<bool> SendFirmwareChunkAsync(byte[] chunk, int length, int timeoutMs)
    {
        return AwaitStandaloneAckAsync(
            Constants.COMMAND_ID_FW_UPLOAD_CHUNK,
            () => SendCommandWithPayload(Constants.COMMAND_ID_FW_UPLOAD_CHUNK, chunk, length),
            timeoutMs,
            discardBeforeSend: false,
            preCommandDelayMs: 0);
    }

    public Task<bool> FinishFirmwareUploadAsync(int timeoutMs)
    {
        return AwaitStandaloneAckAsync(Constants.COMMAND_ID_FW_UPLOAD_END, () => SendCommand(Constants.COMMAND_ID_FW_UPLOAD_END), timeoutMs);
    }

    public Task<bool> CancelFirmwareUploadAsync(int timeoutMs)
    {
        return AwaitStandaloneAckAsync(Constants.COMMAND_ID_FW_UPLOAD_CANCEL, () => SendCommand(Constants.COMMAND_ID_FW_UPLOAD_CANCEL), timeoutMs);
    }

    public Task<bool> InstallFirmwareAsync(int timeoutMs)
    {
        return AwaitStandaloneAckAsync(Constants.COMMAND_ID_FW_INSTALL, () => SendCommand(Constants.COMMAND_ID_FW_INSTALL), timeoutMs);
    }

    public Task<bool> SetControllerRtcAsync(DateTimeOffset controllerDateTime, int timeoutMs = 3000)
    {
        int year = controllerDateTime.Year;
        if (year < 2000 || year > 2099)
        {
            return Task.FromResult(false);
        }

        byte[] payload =
        {
            (byte)(year & 0xFF),
            (byte)((year >> 8) & 0xFF),
            (byte)controllerDateTime.Month,
            (byte)controllerDateTime.Day,
            (byte)controllerDateTime.Hour,
            (byte)controllerDateTime.Minute,
            (byte)controllerDateTime.Second,
        };

        return AwaitStandaloneAckAsync(
            Constants.COMMAND_ID_SET_RTC,
            () => SendCommandWithRawPayload(Constants.COMMAND_ID_SET_RTC, payload),
            timeoutMs);
    }

    public async Task<bool> SetControllerTimeZoneRuleAsync(byte[] timeZoneRule, int timeoutMs = 5000)
    {
        if (!_serialPort.IsOpen || timeZoneRule == null || timeZoneRule.Length != Constants.TIME_ZONE_RULE_LENGTH)
        {
            return false;
        }

        bool configAccepted = await AwaitStandaloneAckAsync(
            Constants.COMMAND_ID_NEWCONFIG,
            () => SendStandaloneSystemConfigCommand(Constants.SYSTEM_PARAM_TIME_ZONE_RULE, timeZoneRule),
            timeoutMs);

        if (!configAccepted)
        {
            return false;
        }

        return await AwaitStandaloneAckAsync(
            Constants.COMMAND_ID_SAVECHANGES,
            () => SendCommand(Constants.COMMAND_ID_SAVECHANGES),
            timeoutMs);
    }

    public Task<bool> FactoryResetAsync(int timeoutMs = 8000)
    {
        return AwaitStandaloneAckAsync(
            Constants.COMMAND_ID_FACTORY_RESET,
            () => SendCommand(Constants.COMMAND_ID_FACTORY_RESET),
            timeoutMs);
    }

    public async Task<IReadOnlyList<ControllerLogFileInfo>> RequestLogFileListAsync(int timeoutMs = 5000)
    {
        if (!_serialPort.IsOpen)
        {
            return Array.Empty<ControllerLogFileInfo>();
        }

        _suspendLiveRequestPolling = true;
        try
        {
            ResetLogReceiveState(discardSerialInput: true);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<ControllerLogFileInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _logFileListTcs = tcs;
                LogProtocolDebug($"TX LOG_LIST attempt={attempt + 1}");
                SendCommand(Constants.COMMAND_ID_LOG_LIST);

                try
                {
                    Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                    if (completedTask != tcs.Task)
                    {
                        int pendingBytes;
                        lock (_bufferLock)
                        {
                            pendingBytes = receivedDataBuffer.Count;
                        }

                        if (pendingBytes > 0)
                        {
                            LogProtocolDebug($"LOG_LIST timeout with pending={pendingBytes}; waiting for completion grace");
                            LogBufferedDataState("LOG_LIST timeout snapshot");
                            int lastPendingBytes = pendingBytes;
                            for (int graceAttempt = 0; graceAttempt < 3 && !tcs.Task.IsCompleted; graceAttempt++)
                            {
                                try
                                {
                                    processData();
                                }
                                catch
                                {
                                }

                                if (tcs.Task.IsCompleted)
                                {
                                    break;
                                }

                                Task graceCompleted = await Task.WhenAny(tcs.Task, Task.Delay(120));
                                if (graceCompleted == tcs.Task)
                                {
                                    break;
                                }

                                lock (_bufferLock)
                                {
                                    pendingBytes = receivedDataBuffer.Count;
                                }

                                // If no new bytes are arriving, avoid waiting the full grace budget.
                                if (pendingBytes <= 0 || pendingBytes == lastPendingBytes)
                                {
                                    break;
                                }

                                lastPendingBytes = pendingBytes;
                            }

                            if (tcs.Task.IsCompleted)
                            {
                                var forcedFiles = await tcs.Task;
                                LogProtocolDebug($"LOG_LIST recovered after grace entries={forcedFiles.Count}");
                                return forcedFiles;
                            }

                            LogBufferedDataState("LOG_LIST post-grace snapshot");
                        }

                        LogProtocolDebug($"LOG_LIST timeout/cancel on attempt={attempt + 1}");
                        if (attempt == 2)
                        {
                            return Array.Empty<ControllerLogFileInfo>();
                        }

                        await Task.Delay(100);

                        continue;
                    }

                    var files = await tcs.Task;
                    LogProtocolDebug($"LOG_LIST completed entries={files.Count}");
                    return files;
                }
                catch
                {
                    LogProtocolDebug($"LOG_LIST timeout/cancel on attempt={attempt + 1}");
                    if (attempt == 2)
                    {
                        return Array.Empty<ControllerLogFileInfo>();
                    }
                }
                finally
                {
                    if (_logFileListTcs == tcs)
                    {
                        _logFileListTcs = null;
                    }
                }
            }

            return Array.Empty<ControllerLogFileInfo>();
        }
        finally
        {
            // Align with log-transfer session teardown: clear parser state so
            // live status framing resumes cleanly after LOG_LIST traffic.
            ResetLogReceiveState(discardSerialInput: false);

            _suspendLiveRequestPolling = false;

            // Resume live polling immediately after a log-list refresh.
            if (_serialPort.IsOpen && !_logTransferSessionActive && !sendingConfig)
            {
                _awaitingPostLogRefreshStatusFrame = true;
                _postLogRefreshRequestRetryCount = 0;
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }
        }
    }

    public async Task<bool> OpenLogTransferAsync(byte logIndex, int timeoutMs = 2000)
    {
        if (!_serialPort.IsOpen)
        {
            return false;
        }

        _suspendLiveRequestPolling = true;
        try
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _logOpenTcs = tcs;
                LogProtocolDebug($"TX LOG_OPEN index={logIndex} attempt={attempt}");
                SendCommandWithByte(Constants.COMMAND_ID_LOG_OPEN, logIndex);

                try
                {
                    Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                    if (completedTask != tcs.Task)
                    {
                        LogProtocolDebug($"LOG_OPEN timeout on attempt={attempt}");
                        continue;
                    }

                    bool opened = await tcs.Task;
                    LogProtocolDebug($"LOG_OPEN completed opened={opened} attempt={attempt}");
                    if (opened)
                    {
                        return true;
                    }
                }
                catch
                {
                    LogProtocolDebug($"LOG_OPEN exception/timeout on attempt={attempt}");
                }
                finally
                {
                    if (_logOpenTcs == tcs)
                    {
                        _logOpenTcs = null;
                    }
                }

                await Task.Delay(75);
            }

            return false;
        }
        finally
        {
            if (!_logTransferSessionActive)
            {
                _suspendLiveRequestPolling = false;
            }
        }
    }

    public async Task<LogChunkResponse?> RequestLogChunkAsync(int timeoutMs = 5000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        await _logChunkRequestLock.WaitAsync();
        try
        {
            if (!_serialPort.IsOpen)
            {
                return null;
            }

            if (TryRecoverBufferedLogChunk(out var queuedChunk))
            {
                return queuedChunk;
            }

            if (!_logTransferSessionActive)
            {
                _suspendLiveRequestPolling = true;
            }
            _txLogChunkCounter++;
            if (_txLogChunkCounter <= 10 || (_txLogChunkCounter % 25) == 0)
            {
                int bufferedBytes;
                lock (_bufferLock)
                {
                    bufferedBytes = receivedDataBuffer.Count;
                }

                LogProtocolDebug($"TX LOG_CHUNK count={_txLogChunkCounter} bufferedBeforeTx={bufferedBytes}");
            }
            SendCommand(Constants.COMMAND_ID_LOG_CHUNK);

            var chunk = await WaitForQueuedLogChunkAsync(timeoutMs, "LOG_CHUNK");
            if (chunk == null)
            {
                LogProtocolDebug("LOG_CHUNK timeout/cancel");
            }

            return chunk;
        }
        finally
        {
            if (!_logTransferSessionActive)
            {
                _suspendLiveRequestPolling = false;
            }
            _logChunkRequestLock.Release();
        }
    }

    public bool StartLogTransferStream()
    {
        if (!_serialPort.IsOpen)
        {
            return false;
        }

        lock (_logChunkLock)
        {
            _pendingLogChunks.Clear();
        }

        _suspendLiveRequestPolling = true;
        SendCommand(Constants.COMMAND_ID_LOG_STREAM);
        return true;
    }

    public async Task<LogChunkResponse?> WaitForLogChunkAsync(int timeoutMs = 5000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        if (TryRecoverBufferedLogChunk(out var queuedChunk))
        {
            return queuedChunk;
        }

        var tcs = new TaskCompletionSource<LogChunkResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _logChunkTcs = tcs;
        if (!_logTransferSessionActive)
        {
            _suspendLiveRequestPolling = true;
        }

        try
        {
            return await AwaitLogChunkAsync(tcs, timeoutMs, "LOG_STREAM");
        }
        finally
        {
            if (!_logTransferSessionActive)
            {
                _suspendLiveRequestPolling = false;
            }

            if (_logChunkTcs == tcs)
            {
                _logChunkTcs = null;
            }
        }
    }

    public async Task<string> ReadLogBulkAsync(Action<byte>? progressCallback, CancellationToken token)
    {
        if (!_serialPort.IsOpen)
        {
            throw new IOException("Serial port is not open.");
        }

        const ushort bulkMagic = 0x4C42;
        byte[] headerBuffer = new byte[6];
        byte[] checksumBuffer = new byte[4];
        byte[] payloadBuffer = new byte[4096];
        using var output = new MemoryStream();

        ResetLogReceiveState(discardSerialInput: true);
        SetBulkTransferReaderActive(true);

        try
        {
            SendCommand(Constants.COMMAND_ID_LOG_BULK);

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await ReadExactlyAsync(_serialPort.BaseStream, headerBuffer, 0, headerBuffer.Length, token);

                ushort magic = (ushort)(headerBuffer[0] | (headerBuffer[1] << 8));
                if (magic != bulkMagic)
                {
                    throw new IOException("Invalid bulk transfer packet header.");
                }

                byte progress = headerBuffer[2];
                bool done = headerBuffer[3] != 0;
                int payloadLength = headerBuffer[4] | (headerBuffer[5] << 8);
                if (payloadLength < 0 || payloadLength > payloadBuffer.Length)
                {
                    throw new IOException("Invalid bulk transfer payload length.");
                }

                if (payloadLength > 0)
                {
                    await ReadExactlyAsync(_serialPort.BaseStream, payloadBuffer, 0, payloadLength, token);
                }

                await ReadExactlyAsync(_serialPort.BaseStream, checksumBuffer, 0, checksumBuffer.Length, token);
                uint receivedChecksum = (uint)(checksumBuffer[0]
                    | (checksumBuffer[1] << 8)
                    | (checksumBuffer[2] << 16)
                    | (checksumBuffer[3] << 24));

                uint calculatedChecksum = 0;
                for (int i = 0; i < headerBuffer.Length; i++)
                {
                    calculatedChecksum += headerBuffer[i];
                }
                for (int i = 0; i < payloadLength; i++)
                {
                    calculatedChecksum += payloadBuffer[i];
                }

                if (calculatedChecksum != receivedChecksum)
                {
                    throw new IOException("Bulk transfer checksum mismatch.");
                }

                if (payloadLength > 0)
                {
                    output.Write(payloadBuffer, 0, payloadLength);
                }

                progressCallback?.Invoke(progress);

                if (done)
                {
                    return Encoding.ASCII.GetString(output.GetBuffer(), 0, (int)output.Length);
                }
            }
        }
        finally
        {
            SetBulkTransferReaderActive(false);
            ResetLogReceiveState(discardSerialInput: false);
        }
    }

    public void CancelLogTransfer()
    {
        bool shouldResumePolling = !_logTransferSessionActive;
        _suspendLiveRequestPolling = true;
        SendCommand(Constants.COMMAND_ID_LOG_CANCEL);
        if (shouldResumePolling)
        {
            _suspendLiveRequestPolling = false;
        }
    }

    public async Task<bool> ResetLogStorageAsync(int timeoutMs = 5000)
    {
        if (!_serialPort.IsOpen)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _logResetTcs = tcs;
        _suspendLiveRequestPolling = true;
        SendCommand(Constants.COMMAND_ID_LOG_RESET);

        try
        {
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                return false;
            }

            bool resetOk = await tcs.Task;
            if (resetOk)
            {
                ResetLogReceiveState(discardSerialInput: true);
            }

            return resetOk;
        }
        catch
        {
            return false;
        }
        finally
        {
            _suspendLiveRequestPolling = false;

            // Kick the live stream loop back into motion after reset completes.
            if (_serialPort.IsOpen && !_logTransferSessionActive && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }

            if (_logResetTcs == tcs)
            {
                _logResetTcs = null;
            }
        }
    }

    /// <summary>
    /// Send override command immediately for a specific channel
    /// </summary>
    public void SendOverrideCommand(int channelIndex, bool overrideState)
    {
        _ = SendOverrideCommandAsync(channelIndex, overrideState);
    }

    private async Task SendOverrideCommandAsync(int channelIndex, bool overrideState)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        _sendBuffer.Clear();
        checkSumSend = 0;

        // Build packet
        AddData(Constants.SERIAL_HEADER1, true);
        AddData(Constants.SERIAL_HEADER2, true);
        AddData(0, true);  // settingIndex = 0 (Channel data)
        AddData(1, true);  // parameterIndex = 1 (Override)
        AddData((byte)channelIndex, true);
        AddData(overrideState ? (byte)1 : (byte)0, true);
        AddData(Constants.SERIAL_TRAILER1, true);
        AddData(Constants.SERIAL_TRAILER2, true);
        AddData((byte)(checkSumSend & 0xFF), false);
        AddData((byte)((checkSumSend >> 8) & 0xFF), false);
        AddData((byte)((checkSumSend >> 16) & 0xFF), false);
        AddData((byte)((checkSumSend >> 24) & 0xFF), false);

        byte[] overridePacket = _sendBuffer.ToArray();
        _sendBuffer.Clear();

        try
        {
            bool accepted = await AwaitStandaloneAckAsync(
                Constants.COMMAND_ID_NEWCONFIG,
                () => SendCommandWithRawPayload(Constants.COMMAND_ID_NEWCONFIG, overridePacket),
                timeoutMs: 3000);

            if (!accepted)
            {
                LoggingService.AddLog("Override command failed.");
            }
        }
        catch (Exception ex)
        {
            LoggingService.AddLog($"Override command failed: {ex.Message}");
        }
    }
}

public sealed class LogChunkResponse
{
    public byte Progress { get; set; }

    public bool Done { get; set; }

    public string Text { get; set; } = string.Empty;
}

public sealed class ControllerLogFileInfo
{
    public string FileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }
}

