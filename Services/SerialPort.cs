using Cortex.Models;
using Cortex.Services;
using Microsoft.Win32;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Security;
using static Cortex.Models.OutputChannel;


public sealed class ConfigurationSaveCompletedEventArgs : EventArgs
{
    public ConfigurationSaveCompletedEventArgs(bool succeeded)
    {
        Succeeded = succeeded;
    }

    public bool Succeeded { get; }
}

public class SerialPortService : IDisposable
{
    private bool _isDisposed;
    private enum DigitalInputStatusSupportState
    {
        Unknown,
        Supported,
        Unsupported,
    }

    private enum CustomCanStatusSupportState
    {
        Unknown,
        Supported,
        Unsupported,
    }

    private sealed class SecureSessionState
    {
        public bool ProtocolSupported { get; set; }

        public bool PinEnabled { get; set; }

        public bool IsProvisioned { get; set; }

        public bool StorageFault { get; set; }

        public bool SupportsPlaintextLogBulk { get; set; }

        public bool IsSessionOpen { get; set; }

        public bool IsAuthorized { get; set; }

        public uint RetryAfterMs { get; set; }

        public uint Pbkdf2Iterations { get; set; }

        public byte[] DevicePublicKey { get; set; } = new byte[Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES];

        public byte[] SessionId { get; set; } = new byte[Constants.SECURE_SESSION_ID_BYTES];

        public byte[] ClientToDeviceKey { get; set; } = new byte[32];

        public byte[] DeviceToClientKey { get; set; } = new byte[32];

        public uint TxSequence { get; set; }

        public uint RxSequence { get; set; }

        public void ResetSession()
        {
            IsSessionOpen = false;
            IsAuthorized = false;
            RetryAfterMs = 0;
            TxSequence = 0;
            RxSequence = 0;
            SessionId = new byte[Constants.SECURE_SESSION_ID_BYTES];
            ClientToDeviceKey = new byte[32];
            DeviceToClientKey = new byte[32];
        }
    }

    private sealed class X25519KeyMaterial : IDisposable
    {
        public X25519KeyMaterial(byte[] privateKey)
        {
            PrivateKey = privateKey;
        }

        public byte[] PrivateKey { get; }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(PrivateKey);
        }
    }

    private static readonly bool EnableLogTransferDebug = false;
    private const int ExtendedLiveSystemPayloadBytes = 23;
    private const int ExtendedStaticSystemPayloadBytes = 61;
    private const int StaticTimeZoneRulePayloadBytes = Constants.TIME_ZONE_RULE_LENGTH;
    private const int StaticCellularPayloadBytes = Constants.CELLULAR_STATIC_PAYLOAD_BYTES;
    private const int StaticSnapshotRetryIntervalMs = 1500;
    private const int DigitalInputStatusProbeTimeoutMs = 2500;
    private const int StandaloneCommandDrainDelayMs = 25;
    private const int OpenRemoteProvisioningMaxRequestBytes = 6143;
    private const int SecureControlResponseMaxPlaintextBytes = 128;

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
    private readonly object _configurationSaveLock = new();
    private bool _configurationSaveActive;
    private bool _configurationPreviousPollingSuspended;
    private char _pendingConfigurationCommand;
    private long _configurationCommandSentTick;
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
    private int mathRuleIndex;

    private TaskCompletionSource<IReadOnlyList<ControllerLogFileInfo>>? _logFileListTcs;
    private TaskCompletionSource<bool>? _logOpenTcs;
    private TaskCompletionSource<LogChunkResponse>? _logChunkTcs;
    private TaskCompletionSource<string>? _firmwareVersionTcs;
    private TaskCompletionSource<string>? _buildDateTcs;
    private TaskCompletionSource<string>? _firmwareDiagnosticTcs;
    private TaskCompletionSource<string>? _cellularTestTcs;
    private TaskCompletionSource<string>? _openRemoteProvisioningTcs;
    private TaskCompletionSource<bool>? _customCanSnapshotTcs;
    private TaskCompletionSource<bool>? _standaloneCommandAckTcs;
    private char _standaloneCommandAckId;
    private readonly Queue<LogChunkResponse> _pendingLogChunks = new Queue<LogChunkResponse>();
    private readonly object _logChunkLock = new object();
    private readonly SemaphoreSlim _logChunkRequestLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _standaloneRequestLock = new SemaphoreSlim(1, 1);
    private readonly List<byte> _secureIncomingBuffer = new List<byte>();
    private readonly object _serialReceiveLock = new();
    private volatile bool _suspendLiveRequestPolling;
    private volatile bool _logTransferSessionActive;
    private long _lastLiveRequestSentTick;
    private long _lastLiveStatusFrameTick;
    private long _lastDigitalInputStatusRequestSentTick;
    private long _lastDigitalInputStatusFrameTick;
    private long _lastMathStatusRequestSentTick;
    private long _lastMathStatusFrameTick;
    private long _lastCustomCanStatusRequestSentTick;
    private long _lastCustomCanStatusFrameTick;
    private long _lastStaticRequestSentTick;
    private long _lastHandshakeAttemptTick;
    private long _lastSecureStatusRequestTick;
    private long _lastSecureStatusResponseTick;
    private DigitalInputStatusSupportState _digitalInputStatusSupportState = DigitalInputStatusSupportState.Unknown;
    private CustomCanStatusSupportState _customCanStatusSupportState = CustomCanStatusSupportState.Unknown;
    private int _lastChunkProgressLogged = -1;
    private int _txLogChunkCounter = 0;
    private long _lastPendingFrameLogTick = 0;
    private int _lastPendingFrameLogBytes = -1;
    private bool _awaitingPostLogRefreshStatusFrame;
    private int _postLogRefreshRequestRetryCount;
    private bool _repeatAnalogueSettingsAfterChannelsPending;
    private bool _finalAnalogueResendActive;
    private int _mathStatusPollingChannelIndex = -1;
    private bool _customCanStatusPollingEnabled;
    private ControllerProtocolCapabilities _controllerCapabilities = ControllerProtocolCapabilities.V09;
    private readonly SecureSessionState _secureSession = new SecureSessionState();
    private readonly Dictionary<string, string> _pinnedDeviceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _secureDeviceIdentityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cortex",
        "secure-device-identities.json");
    private string? _pendingIdentityMismatchPairingKey;
    private string? _pendingIdentityMismatchExpectedIdentity;
    private string? _pendingIdentityMismatchPresentedIdentity;


    public bool UpdateStaticData = false;

    public bool HasLoadedStaticSnapshot { get; private set; }

    public bool IsSecureProtocolEnabled => _secureSession.ProtocolSupported;

    public bool IsPinLockEnabled => _secureSession.PinEnabled;

    public bool IsSecureSessionAuthorized => _secureSession.IsSessionOpen && _secureSession.IsAuthorized;

    public bool IsSecureSessionOpen => _secureSession.IsSessionOpen;

    public bool CanUsePlaintextLogBulk => IsSecureSessionAuthorized && _secureSession.SupportsPlaintextLogBulk;

    public uint PinRetryAfterMs => _secureSession.RetryAfterMs;

    private List<byte> _sendBuffer = new List<byte>();

    // Timer to process received packets off the serial thread at a 10ms interval
    private readonly System.Timers.Timer _processTimer;
    private readonly object _bufferLock = new object();
    private readonly object _serialLifecycleLock = new object();
    private readonly AutoResetEvent _parseSignal = new AutoResetEvent(false);
    private readonly CancellationTokenSource _parseLoopCts = new CancellationTokenSource();
    private Task _parseLoopTask;
    private int _parseLoopBusy;
    private volatile bool _packetReady = false;
    private volatile bool _bulkLogTransferActive = false;
    private volatile bool _bulkLogCancelRequested;
    private bool _bulkLogCancellationDrained;
    private bool _bulkLogTransferStopped;
    private volatile bool _isShuttingDown;
    private volatile bool _suspendDataReceivedReads;
    private readonly object _secureManagementResponseLock = new();
    private TaskCompletionSource<byte[]?>? _secureManagementResponseTcs;
    private byte _secureManagementExpectedOperation;
    private bool _dataReceivedSubscribed;

    private byte[] dataBytes = new byte[4096];
    private byte[] checkSumArray = new byte[4];
    private int dataLength;

    public string? LastError { get; private set; }

    public string? LastCustomCanWriteError { get; internal set; }

    public string? LastSecureSessionError { get; private set; }

    public bool HasPendingControllerIdentityMismatch =>
        !string.IsNullOrWhiteSpace(_pendingIdentityMismatchPairingKey) &&
        !string.IsNullOrWhiteSpace(_pendingIdentityMismatchPresentedIdentity);

    public string ControllerPairingPortName => _serialPort.PortName;

    private static void LogProtocolDebug(string message)
    {
        if (!EnableLogTransferDebug)
        {
            return;
        }

        string fullMessage = $"[LOGDBG] {message}";
        Debug.WriteLine(fullMessage);
    }

    private static string SecureResultToString(SecureControlResult result)
        => $"{result}({(byte)result})";

    private static byte[] GenerateSecureProvisionSeed()
    {
        byte[] seed = new byte[Constants.SECURE_PROVISION_SEED_BYTES];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    private static byte[] BuildSecureControlRequest(SecureControlOperation operation, byte[]? payload)
    {
        payload ??= Array.Empty<byte>();
        byte[] frame = new byte[4 + payload.Length];
        frame[0] = (byte)Constants.SECURE_CONTROL_FRAME_PREFIX;
        frame[1] = (byte)operation;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)payload.Length);

        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        }

        return frame;
    }

    private static byte[] BuildSecureEncryptedControlRequest(SecureControlOperation operation, byte[]? payload)
    {
        payload ??= Array.Empty<byte>();
        byte[] frame = new byte[2 + payload.Length];
        frame[0] = (byte)Constants.SECURE_CONTROL_FRAME_PREFIX;
        frame[1] = (byte)operation;
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
        }

        return frame;
    }

    private static byte[] BuildSecureEnvelopeHeader(ushort plaintextLength, uint sequence)
    {
        byte[] header = new byte[Constants.SECURE_ENVELOPE_HEADER_LENGTH];
        header[0] = (byte)Constants.SECURE_ENVELOPE_FRAME_PREFIX;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1, 2), plaintextLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(3, 4), sequence);
        return header;
    }

    private static byte[] BuildSecureNonce(byte[] sessionId, uint sequence)
    {
        byte[] nonce = new byte[12];
        Buffer.BlockCopy(sessionId, 0, nonce, 0, Math.Min(8, sessionId.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8, 4), sequence);
        return nonce;
    }

    private static byte[] HkdfSha256(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        byte[] prk;
        using (var hmac = new HMACSHA256(salt.ToArray()))
        {
            prk = hmac.ComputeHash(ikm.ToArray());
        }

        byte[] output = new byte[length];
        byte[] t = [];
        int offset = 0;
        byte counter = 1;

        while (offset < length)
        {
            using var hmac = new HMACSHA256(prk);
            byte[] input = new byte[t.Length + info.Length + 1];
            if (t.Length > 0)
            {
                t.CopyTo(input, 0);
            }

            info.CopyTo(input.AsSpan(t.Length, info.Length));
            input[^1] = counter;
            t = hmac.ComputeHash(input);
            int copy = Math.Min(t.Length, length - offset);
            Buffer.BlockCopy(t, 0, output, offset, copy);
            offset += copy;
            counter++;
        }

        CryptographicOperations.ZeroMemory(prk);
        CryptographicOperations.ZeroMemory(t);
        return output;
    }

    private static byte[] EncryptSecureEnvelope(byte[] plaintext, byte[] key, byte[] sessionId, uint sequence)
    {
        byte[] header = BuildSecureEnvelopeHeader((ushort)plaintext.Length, sequence);
        byte[] nonce = BuildSecureNonce(sessionId, sequence);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[Constants.SECURE_TAG_LENGTH];

        using (var cipher = new ChaCha20Poly1305(key))
        {
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, header);
        }

        byte[] frame = new byte[header.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        if (ciphertext.Length > 0)
        {
            Buffer.BlockCopy(ciphertext, 0, frame, header.Length, ciphertext.Length);
        }

        Buffer.BlockCopy(tag, 0, frame, header.Length + ciphertext.Length, tag.Length);
        return frame;
    }

    private static bool TryDecryptSecureEnvelope(byte[] frame, byte[] key, byte[] expectedSessionId, uint expectedSequence, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (frame.Length < Constants.SECURE_ENVELOPE_HEADER_LENGTH + Constants.SECURE_TAG_LENGTH)
        {
            return false;
        }

        if (frame[0] != (byte)Constants.SECURE_ENVELOPE_FRAME_PREFIX)
        {
            return false;
        }

        ushort plaintextLength = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2));
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(3, 4));
        if (sequence != expectedSequence)
        {
            return false;
        }

        int expectedLength = Constants.SECURE_ENVELOPE_HEADER_LENGTH + plaintextLength + Constants.SECURE_TAG_LENGTH;
        if (frame.Length != expectedLength)
        {
            return false;
        }

        byte[] header = new byte[Constants.SECURE_ENVELOPE_HEADER_LENGTH];
        byte[] ciphertext = new byte[plaintextLength];
        byte[] tag = new byte[Constants.SECURE_TAG_LENGTH];
        Buffer.BlockCopy(frame, 0, header, 0, header.Length);
        if (ciphertext.Length > 0)
        {
            Buffer.BlockCopy(frame, Constants.SECURE_ENVELOPE_HEADER_LENGTH, ciphertext, 0, ciphertext.Length);
        }

        Buffer.BlockCopy(frame, Constants.SECURE_ENVELOPE_HEADER_LENGTH + ciphertext.Length, tag, 0, tag.Length);
        byte[] nonce = BuildSecureNonce(expectedSessionId, sequence);
        plaintext = new byte[plaintextLength];

        try
        {
            using var cipher = new ChaCha20Poly1305(key);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, header);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = Array.Empty<byte>();
            return false;
        }
    }

    private static void DeriveSecureSessionKeys(
        byte[] sharedSecret,
        byte[] sessionId,
        byte[] clientPublicKey,
        byte[] devicePublicKey,
        out byte[] clientToDeviceKey,
        out byte[] deviceToClientKey)
    {
        byte[] clientLabel = Encoding.ASCII.GetBytes("SynapsePDM client v1");
        byte[] deviceLabel = Encoding.ASCII.GetBytes("SynapsePDM device v1");

        byte[] clientInfo = new byte[clientLabel.Length + clientPublicKey.Length + devicePublicKey.Length];
        Buffer.BlockCopy(clientLabel, 0, clientInfo, 0, clientLabel.Length);
        Buffer.BlockCopy(clientPublicKey, 0, clientInfo, clientLabel.Length, clientPublicKey.Length);
        Buffer.BlockCopy(devicePublicKey, 0, clientInfo, clientLabel.Length + clientPublicKey.Length, devicePublicKey.Length);

        byte[] deviceInfo = new byte[deviceLabel.Length + clientPublicKey.Length + devicePublicKey.Length];
        Buffer.BlockCopy(deviceLabel, 0, deviceInfo, 0, deviceLabel.Length);
        Buffer.BlockCopy(clientPublicKey, 0, deviceInfo, deviceLabel.Length, clientPublicKey.Length);
        Buffer.BlockCopy(devicePublicKey, 0, deviceInfo, deviceLabel.Length + clientPublicKey.Length, devicePublicKey.Length);

        clientToDeviceKey = HkdfSha256(sharedSecret, sessionId, clientInfo, 32);
        deviceToClientKey = HkdfSha256(sharedSecret, sessionId, deviceInfo, 32);
    }

    private static bool TryGenerateX25519KeyPair(out X25519KeyMaterial? keyAgreement, out byte[] publicKey)
    {
        keyAgreement = null;
        publicKey = Array.Empty<byte>();

        try
        {
            byte[] privateBytes = new byte[Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES];
            byte[] generatedPublicKey = new byte[Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES];
            X25519.GeneratePrivateKey(new SecureRandom(), privateBytes);
            X25519.GeneratePublicKey(privateBytes, 0, generatedPublicKey, 0);

            keyAgreement = new X25519KeyMaterial(privateBytes);
            publicKey = generatedPublicKey;
            return true;
        }
        catch
        {
            keyAgreement?.Dispose();
            keyAgreement = null;
            publicKey = Array.Empty<byte>();
            return false;
        }
    }

    private static bool TryDeriveX25519SharedSecret(X25519KeyMaterial privateKey, byte[] peerPublicKey, out byte[] sharedSecret)
    {
        sharedSecret = Array.Empty<byte>();
        if (peerPublicKey.Length != Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES)
        {
            return false;
        }

        try
        {
            byte[] secret = new byte[Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES];
            bool success = X25519.CalculateAgreement(privateKey.PrivateKey, 0, peerPublicKey, 0, secret, 0);
            if (!success)
            {
                CryptographicOperations.ZeroMemory(secret);
                return false;
            }

            sharedSecret = secret;
            return true;
        }
        catch
        {
            sharedSecret = Array.Empty<byte>();
            return false;
        }
    }

    private void ResetSecureSession()
    {
        _secureSession.ResetSession();
        lock (_secureManagementResponseLock)
        {
            _secureManagementResponseTcs?.TrySetResult(null);
            _secureManagementResponseTcs = null;
        }
    }

    private void ResetSecureSessionAfterDeviceReset()
    {
        ResetSecureSession();
        lock (_bufferLock)
        {
            _secureIncomingBuffer.Clear();
        }
    }

    private bool TryCompleteSecureManagementResponse(byte[] payload)
    {
        if (payload.Length != Constants.SECURE_MANAGEMENT_RESPONSE_BASE_LENGTH &&
            payload.Length != Constants.SECURE_MANAGEMENT_RESPONSE_WITH_RECOVERY_LENGTH)
        {
            return false;
        }

        if (payload[0] != (byte)Constants.SECURE_CONTROL_FRAME_PREFIX)
        {
            return false;
        }

        lock (_secureManagementResponseLock)
        {
            if (_secureManagementResponseTcs == null || payload[1] != _secureManagementExpectedOperation)
            {
                return false;
            }

            _secureManagementResponseTcs.TrySetResult(payload);
            _secureManagementResponseTcs = null;
            return true;
        }
    }

    // Management replies share the encrypted stream with live polling replies, so they must be
    // demultiplexed by the normal secure receiver rather than read directly from the port.
    private async Task<byte[]?> ExecuteStreamedSecureManagementRequestAsync(SecureControlOperation operation, byte[]? payload, int timeoutMs)
    {
        TaskCompletionSource<byte[]?> responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_secureManagementResponseLock)
        {
            _secureManagementResponseTcs?.TrySetResult(null);
            _secureManagementResponseTcs = responseTcs;
            _secureManagementExpectedOperation = (byte)operation;
        }

        try
        {
            lock (_configurationSaveLock)
            {
                if (_configurationSaveActive || !_secureSession.IsSessionOpen || !_serialPort.IsOpen)
                {
                    return null;
                }

                byte[] requestBytes = EncryptSecureEnvelope(
                    BuildSecureEncryptedControlRequest(operation, payload),
                    _secureSession.ClientToDeviceKey,
                    _secureSession.SessionId,
                    _secureSession.TxSequence++);
                _serialPort.Write(requestBytes, 0, requestBytes.Length);
            }

            Task completed = await Task.WhenAny(responseTcs.Task, Task.Delay(timeoutMs));
            return completed == responseTcs.Task ? await responseTcs.Task : null;
        }
        finally
        {
            lock (_secureManagementResponseLock)
            {
                if (ReferenceEquals(_secureManagementResponseTcs, responseTcs))
                {
                    _secureManagementResponseTcs = null;
                }
            }
        }
    }

    private void LoadPinnedDeviceIdentities()
    {
        _pinnedDeviceKeys.Clear();

        try
        {
            if (!File.Exists(_secureDeviceIdentityPath))
            {
                return;
            }

            string json = File.ReadAllText(_secureDeviceIdentityPath);
            Dictionary<string, string>? persisted = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (persisted == null)
            {
                return;
            }

            foreach ((string key, string value) in persisted)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    _pinnedDeviceKeys[key] = value;
                }
            }
        }
        catch
        {
        }
    }

    private void SavePinnedDeviceIdentities()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_secureDeviceIdentityPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(_pinnedDeviceKeys);
            File.WriteAllText(_secureDeviceIdentityPath, json);
        }
        catch
        {
        }
    }

    private static string NormalizeRegistryKeyName(string keyName)
    {
        const string localMachinePrefix = "HKEY_LOCAL_MACHINE\\";
        return keyName.StartsWith(localMachinePrefix, StringComparison.OrdinalIgnoreCase)
            ? keyName[localMachinePrefix.Length..]
            : keyName;
    }

    private static string? TryGetWindowsDeviceIdentifierForPort(string portName)
    {
        try
        {
            using RegistryKey? enumRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (enumRoot == null)
            {
                return null;
            }

            foreach (string busName in enumRoot.GetSubKeyNames())
            {
                using RegistryKey? busKey = enumRoot.OpenSubKey(busName);
                if (busKey == null)
                {
                    continue;
                }

                foreach (string deviceName in busKey.GetSubKeyNames())
                {
                    using RegistryKey? deviceKey = busKey.OpenSubKey(deviceName);
                    if (deviceKey == null)
                    {
                        continue;
                    }

                    foreach (string instanceName in deviceKey.GetSubKeyNames())
                    {
                        using RegistryKey? instanceKey = deviceKey.OpenSubKey(instanceName);
                        if (instanceKey == null)
                        {
                            continue;
                        }

                        using RegistryKey? deviceParameters = instanceKey.OpenSubKey("Device Parameters");
                        if (deviceParameters?.GetValue("PortName") is not string mappedPort ||
                            !string.Equals(mappedPort, portName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        return NormalizeRegistryKeyName(instanceKey.Name);
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private string GetDevicePairingKey()
    {
        string portName = _serialPort.PortName;
        if (!OperatingSystem.IsWindows())
        {
            return $"port:{portName}";
        }

        string? windowsDeviceIdentifier = TryGetWindowsDeviceIdentifierForPort(portName);
        return string.IsNullOrWhiteSpace(windowsDeviceIdentifier)
            ? $"port:{portName}"
            : $"win:{windowsDeviceIdentifier}";
    }

    private static bool IsSameIdentity(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private bool TryGetPinnedIdentity(string pairingKey, string legacyPortKey, out string matchedKey, out string pinnedIdentity)
    {
        if (_pinnedDeviceKeys.TryGetValue(pairingKey, out string? keyIdentity) && !string.IsNullOrWhiteSpace(keyIdentity))
        {
            matchedKey = pairingKey;
            pinnedIdentity = keyIdentity;
            return true;
        }

        if (_pinnedDeviceKeys.TryGetValue(legacyPortKey, out string? legacyIdentity) && !string.IsNullOrWhiteSpace(legacyIdentity))
        {
            matchedKey = legacyPortKey;
            pinnedIdentity = legacyIdentity;
            return true;
        }

        matchedKey = string.Empty;
        pinnedIdentity = string.Empty;
        return false;
    }

    private void ClearPendingIdentityMismatch()
    {
        _pendingIdentityMismatchPairingKey = null;
        _pendingIdentityMismatchExpectedIdentity = null;
        _pendingIdentityMismatchPresentedIdentity = null;
    }

    private bool FailSecureSession(string message)
    {
        LastSecureSessionError ??= message;
        return false;
    }

    private bool IsDeviceIdentityTrusted(byte[] devicePublicKey)
    {
        if (devicePublicKey.Length != Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES)
        {
            LastSecureSessionError = "Secure session could not be established.";
            return false;
        }

        string presentedIdentity = Convert.ToHexString(devicePublicKey);
        string pairingKey = GetDevicePairingKey();
        string legacyPortKey = _serialPort.PortName;

        if (!TryGetPinnedIdentity(pairingKey, legacyPortKey, out string matchedKey, out string pinnedIdentity))
        {
            _pinnedDeviceKeys[pairingKey] = presentedIdentity;
            SavePinnedDeviceIdentities();
            ClearPendingIdentityMismatch();
            return true;
        }

        if (IsSameIdentity(pinnedIdentity, presentedIdentity))
        {
            if (!IsSameIdentity(matchedKey, pairingKey))
            {
                _pinnedDeviceKeys[pairingKey] = presentedIdentity;
                SavePinnedDeviceIdentities();
            }

            ClearPendingIdentityMismatch();
            return true;
        }

        _pendingIdentityMismatchPairingKey = matchedKey;
        _pendingIdentityMismatchExpectedIdentity = pinnedIdentity;
        _pendingIdentityMismatchPresentedIdentity = presentedIdentity;
        LastSecureSessionError = "Controller identity has changed.";
        return false;
    }

    public bool ReplacePendingControllerPairing()
    {
        if (string.IsNullOrWhiteSpace(_pendingIdentityMismatchPairingKey) ||
            string.IsNullOrWhiteSpace(_pendingIdentityMismatchPresentedIdentity))
        {
            return false;
        }

        _pinnedDeviceKeys[_pendingIdentityMismatchPairingKey] = _pendingIdentityMismatchPresentedIdentity;
        SavePinnedDeviceIdentities();
        LastSecureSessionError = null;
        ClearPendingIdentityMismatch();
        return true;
    }

    private void RemoveCurrentControllerPairing()
    {
        if (_secureSession.DevicePublicKey.Length != Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES)
        {
            return;
        }

        string identity = Convert.ToHexString(_secureSession.DevicePublicKey);
        List<string> matchingKeys = _pinnedDeviceKeys
            .Where(entry => IsSameIdentity(entry.Value, identity))
            .Select(entry => entry.Key)
            .ToList();

        if (matchingKeys.Count == 0)
        {
            return;
        }

        foreach (string key in matchingKeys)
        {
            _pinnedDeviceKeys.Remove(key);
        }

        SavePinnedDeviceIdentities();
    }

    private static bool TryParseSecureControlResponse(byte[] responseBytes, SecureControlOperation expectedOperation, out SecureControlResult result, out byte[] payload)
    {
        result = SecureControlResult.Invalid;
        payload = Array.Empty<byte>();

        if (responseBytes.Length < 5 || responseBytes[0] != (byte)Constants.SECURE_CONTROL_FRAME_PREFIX)
        {
            return false;
        }

        if (responseBytes[1] != (byte)expectedOperation)
        {
            return false;
        }

        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(responseBytes.AsSpan(3, 2));
        int expectedLength = 5 + payloadLength;
        if (responseBytes.Length != expectedLength)
        {
            return false;
        }

        result = (SecureControlResult)responseBytes[2];
        payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            Buffer.BlockCopy(responseBytes, 5, payload, 0, payloadLength);
        }

        return true;
    }

    private static bool TryParseSecureInfoResponse(byte[] responseBytes, out SecureInfoResponse? info)
    {
        info = null;
        if (responseBytes.Length < 5 || responseBytes[0] != (byte)Constants.SECURE_CONTROL_FRAME_PREFIX)
        {
            return false;
        }

        if (!TryParseSecureControlResponse(responseBytes, SecureControlOperation.Info, out SecureControlResult result, out byte[] payload))
        {
            return false;
        }

        if (payload.Length != 42)
        {
            return false;
        }

        byte protocolVersion = payload[0];
        byte flags = payload[1];
        uint iterations = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2, 4));
        uint retryAfterMs = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6, 4));
        byte[] devicePublicKey = new byte[32];
        Buffer.BlockCopy(payload, 10, devicePublicKey, 0, devicePublicKey.Length);

        info = new SecureInfoResponse(
            protocolVersion,
            (flags & 0x01) != 0,
            (flags & 0x02) != 0,
            (flags & 0x04) != 0,
            iterations,
            retryAfterMs,
            devicePublicKey,
            result)
        {
            SupportsPlaintextLogBulk = (flags & 0x08) != 0,
        };

        return true;
    }

    private static bool TryParseSecureManagementResponse(byte[] plaintext, SecureControlOperation expectedOperation, out SecureManagementResponse? response)
    {
        response = null;
        if (plaintext.Length != Constants.SECURE_MANAGEMENT_RESPONSE_BASE_LENGTH &&
            plaintext.Length != Constants.SECURE_MANAGEMENT_RESPONSE_WITH_RECOVERY_LENGTH)
        {
            return false;
        }

        if (plaintext[0] != (byte)Constants.SECURE_CONTROL_FRAME_PREFIX || plaintext[1] != (byte)expectedOperation)
        {
            return false;
        }

        SecureControlResult result = (SecureControlResult)plaintext[2];
        uint retryAfterMs = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(3, 4));
        byte flags = plaintext[7];

        string? recoveryKey = null;
        if (plaintext.Length == Constants.SECURE_MANAGEMENT_RESPONSE_WITH_RECOVERY_LENGTH)
        {
            recoveryKey = Encoding.ASCII.GetString(plaintext, Constants.SECURE_MANAGEMENT_RESPONSE_BASE_LENGTH, Constants.SECURE_RECOVERY_KEY_LENGTH);
        }

        response = new SecureManagementResponse(
            expectedOperation,
            result,
            retryAfterMs,
            (flags & 0x01) != 0,
            (flags & 0x02) != 0,
            (flags & 0x04) != 0,
            recoveryKey);

        return true;
    }

    // Polling is already suspended, but a reply to an earlier request may still be arriving.
    // Drain it so its bytes cannot be mistaken for a plain control frame.
    private async Task WaitForQuietSerialInputAsync(int quietMs = 30, int maxWaitMs = 300)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (_serialPort.IsOpen && wait.ElapsedMilliseconds < maxWaitMs)
        {
            await Task.Delay(quietMs);
            if (_serialPort.BytesToRead == 0)
            {
                return;
            }

            try
            {
                _serialPort.DiscardInBuffer();
            }
            catch
            {
                return;
            }
        }
    }

    private byte[]? ReadSecureFrameBlocking(int timeoutMs, bool allowEnvelope, byte? expectedControlOperation = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (_serialPort.IsOpen && stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            if (_serialPort.BytesToRead <= 0)
            {
                Thread.Sleep(2);
                continue;
            }

            int first = _serialPort.ReadByte();
            if (first < 0)
            {
                continue;
            }

            if ((byte)first == (byte)Constants.SECURE_CONTROL_FRAME_PREFIX)
            {
                byte[] headerTail = new byte[4];
                if (!TryReadExactBlocking(headerTail, timeoutMs, stopwatch))
                {
                    return null;
                }

                if (expectedControlOperation.HasValue &&
                    headerTail[0] != expectedControlOperation.Value &&
                    headerTail[0] != 0xFF)
                {
                    continue;
                }

                ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(headerTail.AsSpan(2, 2));
                if (payloadLength > SecureControlResponseMaxPlaintextBytes)
                {
                    continue;
                }

                byte[] payload = new byte[payloadLength];
                if (payloadLength > 0 && !TryReadExactBlocking(payload, timeoutMs, stopwatch))
                {
                    return null;
                }

                byte[] controlFrame = new byte[5 + payloadLength];
                controlFrame[0] = (byte)first;
                Buffer.BlockCopy(headerTail, 0, controlFrame, 1, headerTail.Length);
                if (payloadLength > 0)
                {
                    Buffer.BlockCopy(payload, 0, controlFrame, 5, payload.Length);
                }

                return controlFrame;
            }

            if (!allowEnvelope || (byte)first != (byte)Constants.SECURE_ENVELOPE_FRAME_PREFIX)
            {
                continue;
            }

            byte[] envelopeHeaderTail = new byte[6];
            if (!TryReadExactBlocking(envelopeHeaderTail, timeoutMs, stopwatch))
            {
                return null;
            }

            ushort plaintextLength = BinaryPrimitives.ReadUInt16LittleEndian(envelopeHeaderTail.AsSpan(0, 2));
            if (plaintextLength < 5 || plaintextLength > SecureControlResponseMaxPlaintextBytes)
            {
                continue;
            }

            int remainingBytes = plaintextLength + Constants.SECURE_TAG_LENGTH;
            byte[] payloadAndTag = new byte[remainingBytes];
            if (!TryReadExactBlocking(payloadAndTag, timeoutMs, stopwatch))
            {
                return null;
            }

            byte[] envelopeFrame = new byte[1 + envelopeHeaderTail.Length + payloadAndTag.Length];
            envelopeFrame[0] = (byte)first;
            Buffer.BlockCopy(envelopeHeaderTail, 0, envelopeFrame, 1, envelopeHeaderTail.Length);
            Buffer.BlockCopy(payloadAndTag, 0, envelopeFrame, 1 + envelopeHeaderTail.Length, payloadAndTag.Length);
            return envelopeFrame;
        }

        return null;
    }

    private bool TryReadExactBlocking(byte[] buffer, int timeoutMs, Stopwatch stopwatch)
    {
        int offset = 0;
        while (offset < buffer.Length && _serialPort.IsOpen)
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                return false;
            }

            if (_serialPort.BytesToRead <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            int read = _serialPort.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            offset += read;
        }

        return offset == buffer.Length;
    }

    private async Task<byte[]?> ExecuteSecureControlRequestAsync(SecureControlOperation operation, byte[]? payload, bool encryptedRequest, int timeoutMs)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        await _standaloneRequestLock.WaitAsync();
        try
        {
            bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
            _suspendLiveRequestPolling = true;

            if (encryptedRequest && _secureSession.IsSessionOpen && _dataReceivedSubscribed && !_bulkLogTransferActive)
            {
                try
                {
                    return await ExecuteStreamedSecureManagementRequestAsync(operation, payload, timeoutMs);
                }
                finally
                {
                    _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
                }
            }

            _suspendDataReceivedReads = true;

            bool preserveSecureSequence = encryptedRequest && _secureSession.IsSessionOpen;
            if (!preserveSecureSequence)
            {
                ResetLogReceiveState(discardSerialInput: true);
                await WaitForQuietSerialInputAsync();
            }

            try
            {
                byte[] requestBytes = encryptedRequest
                    ? EncryptSecureEnvelope(
                        BuildSecureEncryptedControlRequest(operation, payload),
                        _secureSession.ClientToDeviceKey,
                        _secureSession.SessionId,
                        _secureSession.TxSequence++)
                    : BuildSecureControlRequest(operation, payload);

                await _serialPort.BaseStream.WriteAsync(requestBytes, 0, requestBytes.Length);
                await _serialPort.BaseStream.FlushAsync();

                byte[]? frame = ReadSecureFrameBlocking(timeoutMs, allowEnvelope: encryptedRequest, expectedControlOperation: (byte)operation);
                if (frame == null)
                {
                    return null;
                }

                if (frame.Length > 0 && frame[0] == (byte)Constants.SECURE_CONTROL_FRAME_PREFIX)
                {
                    return frame;
                }

                if (frame.Length > 0 && frame[0] == (byte)Constants.SECURE_ENVELOPE_FRAME_PREFIX)
                {
                    if (!TryDecryptSecureEnvelope(frame, _secureSession.DeviceToClientKey, _secureSession.SessionId, _secureSession.RxSequence, out byte[] plaintext))
                    {
                        ResetSecureSession();
                        return null;
                    }

                    _secureSession.RxSequence++;
                    return plaintext;
                }

                return null;
            }
            finally
            {
                _suspendDataReceivedReads = false;
                _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            }
        }
        finally
        {
            _standaloneRequestLock.Release();
        }
    }

    public async Task<SecureInfoResponse?> GetSecureInfoAsync(int timeoutMs = 2000)
    {
        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.Info, Array.Empty<byte>(), encryptedRequest: false, timeoutMs);
        if (response == null || !TryParseSecureInfoResponse(response, out SecureInfoResponse? info) || info == null)
        {
            return null;
        }

        _secureSession.ProtocolSupported = info.ProtocolVersion == Constants.SECURE_PROTOCOL_VERSION;
        _secureSession.PinEnabled = info.PinEnabled;
        _secureSession.IsProvisioned = info.IsProvisioned;
        _secureSession.StorageFault = info.StorageFault;
        _secureSession.SupportsPlaintextLogBulk = info.SupportsPlaintextLogBulk;
        _secureSession.RetryAfterMs = info.RetryAfterMs;
        _secureSession.Pbkdf2Iterations = info.Pbkdf2Iterations;
        _secureSession.DevicePublicKey = info.DevicePublicKey;

        return info;
    }

    public async Task<SecureManagementResponse?> GetSecureStatusAsync(int timeoutMs = 2000)
    {
        if (!_secureSession.IsSessionOpen)
        {
            return null;
        }

        _lastSecureStatusRequestTick = Environment.TickCount64;
        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.Status, Array.Empty<byte>(), encryptedRequest: true, timeoutMs);
        if (response == null || !TryParseSecureManagementResponse(response, SecureControlOperation.Status, out SecureManagementResponse? status) || status == null)
        {
            return null;
        }

        _lastSecureStatusResponseTick = Environment.TickCount64;
        _secureSession.PinEnabled = status.PinEnabled;
        _secureSession.IsAuthorized = status.IsAuthorized;
        _secureSession.StorageFault = status.StorageFault;
        _secureSession.RetryAfterMs = status.RetryAfterMs;
        return status;
    }

    public async Task<bool> EnsureSecureSessionAsync(int timeoutMs = 2500)
    {
        LastSecureSessionError = null;

        if (_secureSession.IsSessionOpen)
        {
            ClearPendingIdentityMismatch();
            return true;
        }

        SecureInfoResponse? info = await GetSecureInfoAsync(timeoutMs);
        if (info == null || info.ProtocolVersion != Constants.SECURE_PROTOCOL_VERSION)
        {
            LastSecureSessionError = "Secure session could not be established.";
            return false;
        }

        if (!info.IsProvisioned)
        {
            byte[] seed = GenerateSecureProvisionSeed();
            byte[]? provisionResponse = await ExecuteSecureControlRequestAsync(SecureControlOperation.Provision, seed, encryptedRequest: false, timeoutMs);
            if (provisionResponse == null || !TryParseSecureControlResponse(provisionResponse, SecureControlOperation.Provision, out SecureControlResult provisionResult, out byte[] provisionPayload))
            {
                return FailSecureSession("Secure session could not be established.");
            }

            if (provisionResult != SecureControlResult.Ok && provisionResult != SecureControlResult.Locked)
            {
                return FailSecureSession("Secure session could not be established.");
            }

            if (provisionPayload.Length == Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES)
            {
                _secureSession.DevicePublicKey = provisionPayload;
            }

            info = await GetSecureInfoAsync(timeoutMs);
            if (info == null || !info.IsProvisioned)
            {
                return FailSecureSession("Secure session could not be established.");
            }
        }

        if (!TryGenerateX25519KeyPair(out X25519KeyMaterial? clientKey, out byte[] clientPublicKey) || clientKey == null)
        {
            return FailSecureSession("Secure session could not be established.");
        }

        try
        {
            byte[]? helloResponse = await ExecuteSecureControlRequestAsync(SecureControlOperation.Hello, clientPublicKey, encryptedRequest: false, timeoutMs);
            if (helloResponse != null && helloResponse.Length == 5 + 4 &&
                (SecureControlResult)helloResponse[2] == SecureControlResult.Throttled)
            {
                uint retryAfterMs = BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(5, 4));
                await Task.Delay((int)Math.Min(retryAfterMs + 50, 1500));
                helloResponse = await ExecuteSecureControlRequestAsync(SecureControlOperation.Hello, clientPublicKey, encryptedRequest: false, timeoutMs);
            }

            if (helloResponse == null || helloResponse.Length != 5 + 40 || helloResponse[0] != (byte)Constants.SECURE_CONTROL_FRAME_PREFIX)
            {
                return FailSecureSession("Secure session could not be established.");
            }

            if ((SecureControlResult)helloResponse[2] != SecureControlResult.Ok)
            {
                return FailSecureSession("Secure session could not be established.");
            }

            byte[] devicePublicKey = new byte[32];
            byte[] sessionId = new byte[8];
            Buffer.BlockCopy(helloResponse, 5, devicePublicKey, 0, devicePublicKey.Length);
            Buffer.BlockCopy(helloResponse, 5 + devicePublicKey.Length, sessionId, 0, sessionId.Length);

            if (!IsDeviceIdentityTrusted(devicePublicKey))
            {
                return FailSecureSession("Secure session could not be established.");
            }

            if (!TryDeriveX25519SharedSecret(clientKey, devicePublicKey, out byte[] sharedSecret))
            {
                return FailSecureSession("Secure session could not be established.");
            }

            DeriveSecureSessionKeys(sharedSecret, sessionId, clientPublicKey, devicePublicKey, out byte[] clientToDeviceKey, out byte[] deviceToClientKey);
            _secureSession.SessionId = sessionId;
            _secureSession.DevicePublicKey = devicePublicKey;
            _secureSession.ClientToDeviceKey = clientToDeviceKey;
            _secureSession.DeviceToClientKey = deviceToClientKey;
            _secureSession.TxSequence = 0;
            _secureSession.RxSequence = 0;
            _secureSession.IsSessionOpen = true;
            _secureSession.IsAuthorized = !_secureSession.PinEnabled;
            _lastSecureStatusRequestTick = 0;
            _lastSecureStatusResponseTick = Environment.TickCount64;
            return true;
        }
        finally
        {
            clientKey.Dispose();
        }
    }

    private static bool IsOperationPending(SecureControlResult result)
        => result == SecureControlResult.Pending || result == SecureControlResult.Busy;

    private async Task<SecureManagementResponse?> AwaitSecureOperationCompletionAsync(
        SecureControlOperation operation,
        SecureManagementResponse initialResponse,
        int timeoutMs)
    {
        if (!IsOperationPending(initialResponse.Result))
        {
            return initialResponse;
        }

        Stopwatch wait = Stopwatch.StartNew();
        SecureManagementResponse latest = initialResponse;
        while (wait.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
            SecureManagementResponse? status = await GetSecureStatusAsync(timeoutMs: 2000);
            if (status == null)
            {
                continue;
            }

            latest = new SecureManagementResponse(
                operation,
                status.Result,
                status.RetryAfterMs,
                status.PinEnabled,
                status.IsAuthorized,
                status.StorageFault,
                status.NewRecoveryKey);

            if (!IsOperationPending(latest.Result))
            {
                return latest;
            }
        }

        return latest;
    }

    private static bool IsValidPin(string pin)
    {
        if (pin.Length != Constants.SECURE_PIN_LENGTH)
        {
            return false;
        }

        for (int i = 0; i < pin.Length; i++)
        {
            if (pin[i] < '0' || pin[i] > '9')
            {
                return false;
            }
        }

        return true;
    }

    public async Task<SecureManagementResponse?> SecureUnlockAsync(string pin, int timeoutMs = 12000)
    {
        if (!_secureSession.IsSessionOpen || !IsValidPin(pin))
        {
            return null;
        }

        byte[] payload = Encoding.ASCII.GetBytes(pin);
        LogProtocolDebug($"UNLOCK start timeoutMs={timeoutMs} txSeq={_secureSession.TxSequence} rxSeq={_secureSession.RxSequence} sessionOpen={_secureSession.IsSessionOpen} auth={_secureSession.IsAuthorized}");
        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.Unlock, payload, encryptedRequest: true, timeoutMs);
        if (response == null || !TryParseSecureManagementResponse(response, SecureControlOperation.Unlock, out SecureManagementResponse? managementResponse) || managementResponse == null)
        {
            LogProtocolDebug($"UNLOCK parse-fail responseLen={(response == null ? -1 : response.Length)}");
            return null;
        }

        LogProtocolDebug($"UNLOCK initial result={SecureResultToString(managementResponse.Result)} retryAfter={managementResponse.RetryAfterMs} authorized={managementResponse.IsAuthorized}");

        SecureManagementResponse? finalResponse = await AwaitSecureOperationCompletionAsync(SecureControlOperation.Unlock, managementResponse, timeoutMs);
        if (finalResponse == null)
        {
            return null;
        }

        _secureSession.IsAuthorized = finalResponse.IsAuthorized;
        _secureSession.PinEnabled = finalResponse.PinEnabled;
        _secureSession.RetryAfterMs = finalResponse.RetryAfterMs;
        _secureSession.StorageFault = finalResponse.StorageFault;
        LogProtocolDebug($"UNLOCK final result={SecureResultToString(finalResponse.Result)} retryAfter={finalResponse.RetryAfterMs} authorized={finalResponse.IsAuthorized} txSeq={_secureSession.TxSequence} rxSeq={_secureSession.RxSequence}");
        return finalResponse;
    }

    public async Task<PinSetupResult> SecureSetPinAsync(string pin, int timeoutMs = 12000)
    {
        if (!_secureSession.IsSessionOpen)
        {
            return new PinSetupResult(false, null, "Secure session is not open.", 0, SecureControlResult.Invalid);
        }

        if (!IsValidPin(pin))
        {
            return new PinSetupResult(false, null, "PIN must be exactly four digits.", 0, SecureControlResult.Invalid);
        }

        byte[] payload = Encoding.ASCII.GetBytes(pin);
        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.SetPin, payload, encryptedRequest: true, timeoutMs);
        if (response == null || !TryParseSecureManagementResponse(response, SecureControlOperation.SetPin, out SecureManagementResponse? managementResponse) || managementResponse == null)
        {
            return new PinSetupResult(false, null, "Controller did not return a valid PIN setup response.", 0, SecureControlResult.Invalid);
        }

        SecureManagementResponse? finalResponse = await AwaitSecureOperationCompletionAsync(SecureControlOperation.SetPin, managementResponse, timeoutMs);
        if (finalResponse == null)
        {
            return new PinSetupResult(false, null, "Controller did not return a valid PIN setup response.", 0, SecureControlResult.Invalid);
        }

        string? recoveryKey = finalResponse.NewRecoveryKey;
        if (finalResponse.Result == SecureControlResult.Ok && string.IsNullOrWhiteSpace(recoveryKey))
        {
            SecureManagementResponse? rotateRecovery = await SecureRotateRecoveryAsync(timeoutMs);
            if (rotateRecovery != null && rotateRecovery.Result == SecureControlResult.Ok)
            {
                recoveryKey = rotateRecovery.NewRecoveryKey;
            }
        }

        _secureSession.PinEnabled = finalResponse.PinEnabled;
        _secureSession.IsAuthorized = finalResponse.IsAuthorized;
        _secureSession.RetryAfterMs = finalResponse.RetryAfterMs;
        _secureSession.StorageFault = finalResponse.StorageFault;

        bool success = finalResponse.Result == SecureControlResult.Ok && !string.IsNullOrWhiteSpace(recoveryKey);
        string? error = success ? null : $"Controller returned {finalResponse.Result}.";
        return new PinSetupResult(success, recoveryKey, error, finalResponse.RetryAfterMs, finalResponse.Result);
    }

    public async Task<PinSetupResult> SecureRecoverAsync(string recoveryKey, string newPin, int timeoutMs = 12000)
    {
        if (!_secureSession.IsSessionOpen)
        {
            return new PinSetupResult(false, null, "Secure session is not open.", 0, SecureControlResult.Invalid);
        }

        string normalizedRecovery = (recoveryKey ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedRecovery.Length != Constants.SECURE_RECOVERY_KEY_LENGTH || !IsValidPin(newPin))
        {
            return new PinSetupResult(false, null, "Recovery key or PIN format is invalid.", 0, SecureControlResult.Invalid);
        }

        byte[] recoveryBytes = Encoding.ASCII.GetBytes(normalizedRecovery);
        byte[] pinBytes = Encoding.ASCII.GetBytes(newPin);
        byte[] payload = new byte[recoveryBytes.Length + pinBytes.Length];
        Buffer.BlockCopy(recoveryBytes, 0, payload, 0, recoveryBytes.Length);
        Buffer.BlockCopy(pinBytes, 0, payload, recoveryBytes.Length, pinBytes.Length);

        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.Recover, payload, encryptedRequest: true, timeoutMs);
        if (response == null || !TryParseSecureManagementResponse(response, SecureControlOperation.Recover, out SecureManagementResponse? managementResponse) || managementResponse == null)
        {
            return new PinSetupResult(false, null, "Controller did not return a valid recovery response.", 0, SecureControlResult.Invalid);
        }

        SecureManagementResponse? finalResponse = await AwaitSecureOperationCompletionAsync(SecureControlOperation.Recover, managementResponse, timeoutMs);
        if (finalResponse == null)
        {
            return new PinSetupResult(false, null, "Controller did not return a valid recovery response.", 0, SecureControlResult.Invalid);
        }

        _secureSession.PinEnabled = finalResponse.PinEnabled;
        _secureSession.IsAuthorized = finalResponse.IsAuthorized;
        _secureSession.RetryAfterMs = finalResponse.RetryAfterMs;
        _secureSession.StorageFault = finalResponse.StorageFault;

        bool success = finalResponse.Result == SecureControlResult.Ok && !string.IsNullOrWhiteSpace(finalResponse.NewRecoveryKey);
        string? error = success ? null : $"Controller returned {finalResponse.Result}.";
        return new PinSetupResult(success, finalResponse.NewRecoveryKey, error, finalResponse.RetryAfterMs, finalResponse.Result);
    }

    public async Task<SecureManagementResponse?> SecureDisablePinAsync(int timeoutMs = 12000)
    {
        if (!_secureSession.IsSessionOpen)
        {
            return null;
        }

        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.Disable, Array.Empty<byte>(), encryptedRequest: true, timeoutMs);
        if (response == null || !TryParseSecureManagementResponse(response, SecureControlOperation.Disable, out SecureManagementResponse? managementResponse) || managementResponse == null)
        {
            return null;
        }

        _secureSession.PinEnabled = managementResponse.PinEnabled;
        _secureSession.RetryAfterMs = managementResponse.RetryAfterMs;
        _secureSession.StorageFault = managementResponse.StorageFault;
        if (managementResponse.Result == SecureControlResult.Ok)
        {
            RemoveCurrentControllerPairing();
            // Firmware closes the session after a successful disable; failures keep it open.
            ResetSecureSessionAfterDeviceReset();
        }
        else
        {
            _secureSession.IsAuthorized = managementResponse.IsAuthorized;
        }

        return managementResponse;
    }

    public async Task<SecureManagementResponse?> SecureRotateRecoveryAsync(int timeoutMs = 12000)
    {
        if (!_secureSession.IsSessionOpen)
        {
            return null;
        }

        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.RotateRecovery, Array.Empty<byte>(), encryptedRequest: true, timeoutMs);
        if (response == null || !TryParseSecureManagementResponse(response, SecureControlOperation.RotateRecovery, out SecureManagementResponse? managementResponse) || managementResponse == null)
        {
            return null;
        }

        _secureSession.PinEnabled = managementResponse.PinEnabled;
        _secureSession.IsAuthorized = managementResponse.IsAuthorized;
        _secureSession.RetryAfterMs = managementResponse.RetryAfterMs;
        _secureSession.StorageFault = managementResponse.StorageFault;
        return managementResponse;
    }

    public async Task<bool> SecureLockAsync(int timeoutMs = 3000)
    {
        if (!_secureSession.IsSessionOpen)
        {
            return false;
        }

        byte[]? response = await ExecuteSecureControlRequestAsync(SecureControlOperation.Lock, Array.Empty<byte>(), encryptedRequest: true, timeoutMs);
        if (response == null || !TryParseSecureManagementResponse(response, SecureControlOperation.Lock, out SecureManagementResponse? managementResponse) || managementResponse == null)
        {
            return false;
        }

        if (managementResponse.Result != SecureControlResult.Ok)
        {
            return false;
        }

        ResetSecureSessionAfterDeviceReset();
        return true;
    }

    public bool ShouldSendSecureKeepalive(int intervalMs = 4000)
    {
        if (!_secureSession.IsSessionOpen)
        {
            return false;
        }

        // Once authorized, live/static polling is the primary transport for operational
        // data. Avoid periodic secure status requests that can contend with stream reads.
        if (!_secureSession.PinEnabled || _secureSession.IsAuthorized)
        {
            return false;
        }

        long now = Environment.TickCount64;
        if (_lastSecureStatusRequestTick == 0)
        {
            return true;
        }

        return (now - _lastSecureStatusRequestTick) >= intervalMs;
    }

    private void SubscribeDataReceived_NoLock()
    {
        if (_dataReceivedSubscribed || _isShuttingDown || _isDisposed)
        {
            return;
        }

        _serialPort.DataReceived += OnDataReceived;
        _dataReceivedSubscribed = true;
    }

    private void UnsubscribeDataReceived_NoLock()
    {
        if (!_dataReceivedSubscribed)
        {
            return;
        }

        // Keep the handler subscribed for the lifetime of the SerialPort instance.
        // Unsubscribing during close/dispose can race with framework receive-event dispatch
        // (SerialStream.EventLoopRunner.CallReceiveEvents), which may throw NullReferenceException.
        // Shutdown behavior is controlled by _isShuttingDown/_isDisposed guards in OnDataReceived.
    }

    private void SetBulkTransferReaderActive(bool active)
    {
        lock (_serialReceiveLock)
        {
            _bulkLogTransferActive = active;
        }
        if (!active)
        {
            _parseSignal.Set();
        }
    }

    private void RunParseLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _parseSignal.WaitOne(20);

            if (_isDisposed || _isShuttingDown || _bulkLogTransferActive)
            {
                continue;
            }

            if (Interlocked.Exchange(ref _parseLoopBusy, 1) == 1)
            {
                continue;
            }

            try
            {
                DrainPendingSerialInput();
                CheckConfigurationSaveTimeout();
                for (int i = 0; i < 256; i++)
                {
                    if (!processData())
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in parse loop: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _parseLoopBusy, 0);
            }
        }
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
               commandId == Constants.COMMAND_ID_DIGITAL_INPUT_STATUS ||
               commandId == Constants.COMMAND_ID_MATH_STATUS ||
               commandId == Constants.COMMAND_ID_LOG_LIST ||
               commandId == Constants.COMMAND_ID_LOG_CHUNK ||
               commandId == Constants.COMMAND_ID_LOG_STREAM ||
               commandId == Constants.COMMAND_ID_FW_VER ||
               commandId == Constants.COMMAND_ID_BUILD_DATE ||
               commandId == Constants.COMMAND_ID_FW_DIAGNOSTIC ||
               commandId == Constants.COMMAND_ID_CELLULAR_TEST ||
               commandId == Constants.COMMAND_ID_OPENREMOTE_PROVISION ||
               commandId == Constants.COMMAND_ID_CUSTOM_CAN_STATUS;
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
            _secureIncomingBuffer.Clear();
        }

        foundTrailer1 = false;
        _packetReady = false;
        lastCommandSent = '\0';
        _lastLiveRequestSentTick = 0;
        _lastLiveStatusFrameTick = 0;
        _lastMathStatusRequestSentTick = 0;
        _lastMathStatusFrameTick = 0;
        _lastCustomCanStatusRequestSentTick = 0;
        _lastCustomCanStatusFrameTick = 0;
        _lastStaticRequestSentTick = 0;
        _lastHandshakeAttemptTick = 0;
        _lastSecureStatusRequestTick = 0;
        _lastSecureStatusResponseTick = 0;
        HasLoadedStaticSnapshot = false;

        lock (_logChunkLock)
        {
            _pendingLogChunks.Clear();
        }
    }

    private void MarkControllerSessionLost(bool requestStaticSnapshot)
    {
        foundECU = false;
        _controllerCapabilities = ControllerProtocolCapabilities.V09;
        ResetSecureSession();
        _lastLiveRequestSentTick = 0;
        _lastLiveStatusFrameTick = 0;
        _lastDigitalInputStatusRequestSentTick = 0;
        _lastDigitalInputStatusFrameTick = 0;
        _lastMathStatusRequestSentTick = 0;
        _lastMathStatusFrameTick = 0;
        _lastCustomCanStatusRequestSentTick = 0;
        _lastCustomCanStatusFrameTick = 0;
        _lastStaticRequestSentTick = 0;
        _lastSecureStatusRequestTick = 0;
        HasLoadedStaticSnapshot = false;
        _lastSecureStatusResponseTick = 0;
        _digitalInputStatusSupportState = DigitalInputStatusSupportState.Unknown;
        _customCanStatusSupportState = CustomCanStatusSupportState.Unknown;
        ClearDigitalInputLiveStates();

        if (requestStaticSnapshot)
        {
            UpdateStaticData = true;
        }
    }

    private void ClearDigitalInputLiveStates()
    {
        foreach (DigitalInput input in dataStructures.DigitalInputsLiveData)
        {
            input.LiveElectricalState = null;
        }
    }

    private void MarkDigitalInputStatusUnsupported()
    {
        _digitalInputStatusSupportState = DigitalInputStatusSupportState.Unsupported;
        _lastDigitalInputStatusFrameTick = 0;
        ClearDigitalInputLiveStates();
    }

    private void UpdateDigitalInputStatusSupport(long now, int idleThresholdMs)
    {
        if (_digitalInputStatusSupportState != DigitalInputStatusSupportState.Unknown ||
            _lastDigitalInputStatusRequestSentTick == 0)
        {
            return;
        }

        if (_lastDigitalInputStatusFrameTick >= _lastDigitalInputStatusRequestSentTick)
        {
            _digitalInputStatusSupportState = DigitalInputStatusSupportState.Supported;
            return;
        }

        int timeoutMs = Math.Max(idleThresholdMs * 2, DigitalInputStatusProbeTimeoutMs);
        if ((now - _lastDigitalInputStatusRequestSentTick) >= timeoutMs)
        {
            MarkDigitalInputStatusUnsupported();
        }
    }

    private void ClearCustomCanLiveStates()
    {
        foreach (CustomCanMessage message in dataStructures.CustomCanMessagesLiveData)
        {
            message.Status = (byte)CustomCanStatus.InvalidConfig;
        }

        foreach (CustomCanSignal signal in dataStructures.CustomCanSignalsLiveData)
        {
            signal.RuntimeValue = 0;
            signal.Status = (byte)CustomCanStatus.InvalidConfig;
        }
    }

    private void MarkCustomCanStatusUnsupported()
    {
        _customCanStatusSupportState = CustomCanStatusSupportState.Unsupported;
        _lastCustomCanStatusFrameTick = 0;
        ClearCustomCanLiveStates();
    }

    private void UpdateCustomCanStatusSupport(long now, int idleThresholdMs)
    {
        if (_customCanStatusSupportState != CustomCanStatusSupportState.Unknown ||
            _lastCustomCanStatusRequestSentTick == 0)
        {
            return;
        }

        if (_lastCustomCanStatusFrameTick >= _lastCustomCanStatusRequestSentTick)
        {
            _customCanStatusSupportState = CustomCanStatusSupportState.Supported;
            return;
        }

        int timeoutMs = Math.Max(idleThresholdMs * 2, DigitalInputStatusProbeTimeoutMs);
        if ((now - _lastCustomCanStatusRequestSentTick) >= timeoutMs)
        {
            MarkCustomCanStatusUnsupported();
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

        bool preserveSecureSession = _secureSession.IsSessionOpen;
        if (!preserveSecureSession)
        {
            ResetLogReceiveState(discardSerialInput: true);
        }
        else
        {
            lock (_bufferLock)
            {
                receivedDataBuffer.Clear();
            }

            foundTrailer1 = false;
            _packetReady = false;
        }

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

        bool preserveSecureSession = _secureSession.IsSessionOpen;

        _logTransferSessionActive = false;
        _suspendLiveRequestPolling = false;
        if (!preserveSecureSession)
        {
            ResetLogReceiveState(discardSerialInput: false);
        }
        else
        {
            lock (_bufferLock)
            {
                receivedDataBuffer.Clear();
            }

            foundTrailer1 = false;
            _packetReady = false;
        }

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
        lock (_serialLifecycleLock)
        {
            SubscribeDataReceived_NoLock();
        }
        receivedDataBuffer = new List<byte>();
        dataStructures = new DataStructures();
        settingsData = new DataStructures();
        dataBytes = Array.Empty<byte>();
        // create and start the processing timer
        _processTimer = new System.Timers.Timer(2);
        _processTimer.AutoReset = true;
        _processTimer.Elapsed += (s, e) =>
        {
            // Legacy timer retained for compatibility; parsing is handled by RunParseLoop.
        };

        _parseLoopTask = Task.Run(() => RunParseLoop(_parseLoopCts.Token));
        LoadPinnedDeviceIdentities();
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

            if (receivedDataBuffer[0] == Constants.SERIAL_HEADER1 &&
                receivedDataBuffer[1] == Constants.SERIAL_HEADER2 &&
                receivedDataBuffer.Count >= 4 &&
                receivedDataBuffer[2] == (byte)Constants.COMMAND_ID_DIGITAL_INPUT_STATUS)
            {
                int stateCount = receivedDataBuffer[3];
                if (stateCount <= 0 || stateCount > Constants.NUM_DIGITAL_INPUTS)
                {
                    receivedDataBuffer.RemoveAt(0);
                    _packetReady = BufferContainsTrailer(receivedDataBuffer);
                    return false;
                }

                int packetLength = stateCount + 10;
                if (receivedDataBuffer.Count < packetLength)
                {
                    return false;
                }

                int trailerIndex = 4 + stateCount;
                bool invalidDigitalStatusFrame =
                    receivedDataBuffer[trailerIndex] != Constants.SERIAL_TRAILER1 ||
                    receivedDataBuffer[trailerIndex + 1] != Constants.SERIAL_TRAILER2;

                for (int i = 0; i < stateCount && !invalidDigitalStatusFrame; i++)
                {
                    byte state = receivedDataBuffer[4 + i];
                    if (state > 1)
                    {
                        invalidDigitalStatusFrame = true;
                    }
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

                if (invalidDigitalStatusFrame || localCheckSum != receivedCheckSum)
                {
                    receivedDataBuffer.RemoveAt(0);
                    _packetReady = BufferContainsTrailer(receivedDataBuffer);
                    return false;
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
            target.MathRuleCount = source.MathRuleCount;
            target.MathRootRule = source.MathRootRule;
            target.MathOutputMode = source.MathOutputMode;
            target.MathCalculatedRootValue = source.MathCalculatedRootValue;
            target.MathError = source.MathError;
            target.EnsureMathRuleSlots();
            source.EnsureMathRuleSlots();
            for (int ruleIndex = 0; ruleIndex < Constants.NUM_MATH_RULE_SLOTS; ruleIndex++)
            {
                OutputChannel.MathRule sourceRule = source.MathRules[ruleIndex];
                OutputChannel.MathRule targetRule = target.MathRules[ruleIndex];
                targetRule.Operation = sourceRule.Operation;
                targetRule.HysteresisEnabled = sourceRule.HysteresisEnabled;
                targetRule.OnThreshold = sourceRule.OnThreshold;
                targetRule.OffThreshold = sourceRule.OffThreshold;
                targetRule.OperandAType = sourceRule.OperandAType;
                targetRule.OperandAIndex = sourceRule.OperandAIndex;
                targetRule.OperandAConstant = sourceRule.OperandAConstant;
                targetRule.OperandBType = sourceRule.OperandBType;
                targetRule.OperandBIndex = sourceRule.OperandBIndex;
                targetRule.OperandBConstant = sourceRule.OperandBConstant;
                targetRule.RuntimeValue = sourceRule.RuntimeValue;
                targetRule.RuntimeError = sourceRule.RuntimeError;
                targetRule.Evaluated = sourceRule.Evaluated;
            }
            target.DelayedOn = source.DelayedOn;
            target.DelayedOnTime = source.DelayedOnTime;
            target.DelayedOff = source.DelayedOff;
            target.DelayedOffTime = source.DelayedOffTime;
            target.DelayedOffTrigger = source.DelayedOffTrigger;
            target.RefreshDelayUiUnitsFromStoredValues();
        }

        int customCanMessageCount = Math.Min(dataStructures.CustomCanMessagesLiveData.Count, dataStructures.CustomCanMessagesStaticData.Count);
        for (int i = 0; i < customCanMessageCount; i++)
        {
            CustomCanMessage sourceMessage = dataStructures.CustomCanMessagesLiveData[i];
            CustomCanMessage targetMessage = dataStructures.CustomCanMessagesStaticData[i];
            targetMessage.Index = sourceMessage.Index;
            targetMessage.Id = sourceMessage.Id;
            targetMessage.TimeoutMs = sourceMessage.TimeoutMs;
            targetMessage.Dlc = sourceMessage.Dlc;
            targetMessage.Flags = sourceMessage.Flags;
            targetMessage.TxTrigger = sourceMessage.TxTrigger;
            targetMessage.Status = sourceMessage.Status;
        }

        int customCanSignalCount = Math.Min(dataStructures.CustomCanSignalsLiveData.Count, dataStructures.CustomCanSignalsStaticData.Count);
        for (int i = 0; i < customCanSignalCount; i++)
        {
            CustomCanSignal sourceSignal = dataStructures.CustomCanSignalsLiveData[i];
            CustomCanSignal targetSignal = dataStructures.CustomCanSignalsStaticData[i];
            targetSignal.Index = sourceSignal.Index;
            targetSignal.Name = sourceSignal.Name;
            targetSignal.MessageName = sourceSignal.MessageName;
            targetSignal.MessageIndex = sourceSignal.MessageIndex;
            targetSignal.StartBit = sourceSignal.StartBit;
            targetSignal.BitLength = sourceSignal.BitLength;
            targetSignal.Flags = sourceSignal.Flags;
            targetSignal.ValueSource = sourceSignal.ValueSource;
            targetSignal.Factor = sourceSignal.Factor;
            targetSignal.Offset = sourceSignal.Offset;
            targetSignal.RuntimeValue = sourceSignal.RuntimeValue;
            targetSignal.Status = sourceSignal.Status;
            targetSignal.OwnerMessage = targetSignal.MessageIndex < dataStructures.CustomCanMessagesStaticData.Count
                ? dataStructures.CustomCanMessagesStaticData[targetSignal.MessageIndex]
                : null;
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

    private bool ParseDigitalInputStatusPacket()
    {
        int stateCount = dataBytes[3];
        if (stateCount <= 0 || stateCount > Constants.NUM_DIGITAL_INPUTS || dataLength != stateCount + 10)
        {
            return false;
        }

        int trailerIndex = 4 + stateCount;
        if (dataBytes[trailerIndex] != Constants.SERIAL_TRAILER1 ||
            dataBytes[trailerIndex + 1] != Constants.SERIAL_TRAILER2)
        {
            return false;
        }

        for (int i = 0; i < stateCount; i++)
        {
            byte state = dataBytes[4 + i];
            if (state > 1)
            {
                return false;
            }

            if (i < dataStructures.DigitalInputsLiveData.Count)
            {
                dataStructures.DigitalInputsLiveData[i].LiveElectricalState = state != 0;
            }
        }

        for (int i = stateCount; i < dataStructures.DigitalInputsLiveData.Count; i++)
        {
            dataStructures.DigitalInputsLiveData[i].LiveElectricalState = null;
        }

        _digitalInputStatusSupportState = DigitalInputStatusSupportState.Supported;
        return true;
    }

    private bool ParseMathStatusPacket()
    {
        if (dataBytes[3] != Constants.NUM_OUTPUT_CHANNELS)
        {
            return false;
        }

        int packetEndIndex = dataLength - 4;
        var reader = new ByteReader(dataBytes, 4);
        int channelCount = dataBytes[3];

        const int bytesPerMathRule = 28;
        const int bytesPerMathChannel = 10 + (Constants.NUM_MATH_RULE_SLOTS * bytesPerMathRule);
        if ((packetEndIndex - reader.Position) < (channelCount * bytesPerMathChannel))
        {
            return false;
        }

        for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            byte channelType = reader.ReadByte();
            byte ruleCount = reader.ReadByte();
            byte rootRule = reader.ReadByte();
            byte outputMode = reader.ReadByte();
            float rootValue = reader.ReadSingle();
            byte channelMathError = reader.ReadByte();
            byte enabled = reader.ReadByte();

            OutputChannel? targetChannel = channelIndex < dataStructures.ChannelsLiveData.Count
                ? dataStructures.ChannelsLiveData[channelIndex]
                : null;

            if (targetChannel != null)
            {
                targetChannel.ChanType = (ChannelType)channelType;
                targetChannel.MathRuleCount = ruleCount;
                targetChannel.MathRootRule = rootRule;
                targetChannel.MathOutputMode = outputMode;
                targetChannel.MathCalculatedRootValue = rootValue;
                targetChannel.MathError = channelMathError;
                targetChannel.Enabled = enabled;
                targetChannel.EnsureMathRuleSlots();
            }

            for (int ruleIndex = 0; ruleIndex < Constants.NUM_MATH_RULE_SLOTS; ruleIndex++)
            {
                byte operation = reader.ReadByte();
                byte hysteresis = reader.ReadByte();
                float onThreshold = reader.ReadSingle();
                float offThreshold = reader.ReadSingle();
                byte operandAType = reader.ReadByte();
                byte operandAIndex = reader.ReadByte();
                float operandAConstant = reader.ReadSingle();
                byte operandBType = reader.ReadByte();
                byte operandBIndex = reader.ReadByte();
                float operandBConstant = reader.ReadSingle();
                float runtimeValue = reader.ReadSingle();
                byte runtimeError = reader.ReadByte();
                byte evaluated = reader.ReadByte();

                if (targetChannel == null)
                {
                    continue;
                }

                OutputChannel.MathRule rule = targetChannel.MathRules[ruleIndex];
                rule.Operation = operation;
                rule.HysteresisEnabled = hysteresis;
                rule.OnThreshold = onThreshold;
                rule.OffThreshold = offThreshold;
                rule.OperandAType = operandAType;
                rule.OperandAIndex = operandAIndex;
                rule.OperandAConstant = operandAConstant;
                rule.OperandBType = operandBType;
                rule.OperandBIndex = operandBIndex;
                rule.OperandBConstant = operandBConstant;
                rule.RuntimeValue = runtimeValue;
                rule.RuntimeError = runtimeError;
                rule.Evaluated = evaluated;
            }
        }

        return true;
    }

    private void CopyCustomCanLiveToStaticSnapshot()
    {
        int customCanMessageCount = Math.Min(dataStructures.CustomCanMessagesLiveData.Count, dataStructures.CustomCanMessagesStaticData.Count);
        for (int i = 0; i < customCanMessageCount; i++)
        {
            CustomCanMessage sourceMessage = dataStructures.CustomCanMessagesLiveData[i];
            CustomCanMessage targetMessage = dataStructures.CustomCanMessagesStaticData[i];
            targetMessage.Index = sourceMessage.Index;
            targetMessage.Id = sourceMessage.Id;
            targetMessage.TimeoutMs = sourceMessage.TimeoutMs;
            targetMessage.Dlc = sourceMessage.Dlc;
            targetMessage.Flags = sourceMessage.Flags;
            targetMessage.TxTrigger = sourceMessage.TxTrigger;
            targetMessage.Status = sourceMessage.Status;
        }

        int customCanSignalCount = Math.Min(dataStructures.CustomCanSignalsLiveData.Count, dataStructures.CustomCanSignalsStaticData.Count);
        for (int i = 0; i < customCanSignalCount; i++)
        {
            CustomCanSignal sourceSignal = dataStructures.CustomCanSignalsLiveData[i];
            CustomCanSignal targetSignal = dataStructures.CustomCanSignalsStaticData[i];
            targetSignal.Index = sourceSignal.Index;
            targetSignal.Name = sourceSignal.Name;
            targetSignal.MessageName = sourceSignal.MessageName;
            targetSignal.MessageIndex = sourceSignal.MessageIndex;
            targetSignal.StartBit = sourceSignal.StartBit;
            targetSignal.BitLength = sourceSignal.BitLength;
            targetSignal.Flags = sourceSignal.Flags;
            targetSignal.ValueSource = sourceSignal.ValueSource;
            targetSignal.Factor = sourceSignal.Factor;
            targetSignal.Offset = sourceSignal.Offset;
            targetSignal.RuntimeValue = sourceSignal.RuntimeValue;
            targetSignal.Status = sourceSignal.Status;
            targetSignal.OwnerMessage = targetSignal.MessageIndex < dataStructures.CustomCanMessagesStaticData.Count
                ? dataStructures.CustomCanMessagesStaticData[targetSignal.MessageIndex]
                : null;
        }
    }

    private bool ParseCustomCanStatusPacket()
    {
        int packetEndIndex = dataLength - 4;
        var reader = new ByteReader(dataBytes, 3);
        if ((packetEndIndex - reader.Position) < 4)
        {
            return false;
        }

        byte version = reader.ReadByte();
        if (version != Constants.CUSTOM_CAN_STATUS_VERSION_LEGACY &&
            version != Constants.CUSTOM_CAN_STATUS_VERSION_TX)
        {
            return false;
        }

        bool hasTxFields = version >= Constants.CUSTOM_CAN_STATUS_VERSION_TX;
        byte crcValid = reader.ReadByte();
        byte messageCount = reader.ReadByte();
        byte signalCount = reader.ReadByte();

        if (messageCount > Constants.MAX_CUSTOM_CAN_MESSAGES || signalCount > Constants.MAX_CUSTOM_CAN_SIGNALS)
        {
            return false;
        }

        int bytesPerMessage = hasTxFields ? 10 : 9;
        int bytesPerSignal = hasTxFields ? 18 : 17;
        int expectedBytes = (messageCount * bytesPerMessage) + (signalCount * bytesPerSignal);
        if ((packetEndIndex - reader.Position) < expectedBytes)
        {
            return false;
        }

        for (int i = 0; i < messageCount; i++)
        {
            uint id = reader.ReadUInt32();
            ushort timeout = reader.ReadUInt16();
            byte dlc = reader.ReadByte();
            byte flags = reader.ReadByte();
            byte txTrigger = hasTxFields ? reader.ReadByte() : Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
            byte status = reader.ReadByte();
            bool enabled = status != (byte)CustomCanStatus.Disabled;
            // TX messages always report Disabled; keep their configured enabled flag.
            byte normalizedFlags = (flags & 0x04) != 0
                ? flags
                : enabled
                    ? (byte)(flags | 0x01)
                    : (byte)(flags & ~0x01);

            if (i < dataStructures.CustomCanMessagesLiveData.Count)
            {
                CustomCanMessage message = dataStructures.CustomCanMessagesLiveData[i];
                message.Index = i;
                message.Id = id;
                message.TimeoutMs = timeout;
                message.Dlc = dlc;
                message.Flags = normalizedFlags;
                message.TxTrigger = txTrigger;
                message.Status = status;
            }
        }

        for (int i = messageCount; i < dataStructures.CustomCanMessagesLiveData.Count; i++)
        {
            CustomCanMessage message = dataStructures.CustomCanMessagesLiveData[i];
            message.Index = i;
            message.Id = 0;
            message.TimeoutMs = 1000;
            message.Dlc = 8;
            message.Flags = 0;
            message.TxTrigger = Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
            message.Status = (byte)CustomCanStatus.Disabled;
        }

        for (int i = 0; i < signalCount; i++)
        {
            byte messageIndex = reader.ReadByte();
            byte startBit = reader.ReadByte();
            byte bitLength = reader.ReadByte();
            byte flags = reader.ReadByte();
            float factor = reader.ReadSingle();
            float offset = reader.ReadSingle();
            byte valueSource = hasTxFields ? reader.ReadByte() : (byte)0;
            float runtimeValue = reader.ReadSingle();
            byte status = reader.ReadByte();
            bool enabled = status != (byte)CustomCanStatus.Disabled;
            // Signals on TX messages always report Disabled; keep their configured enabled flag.
            bool onTxMessage = messageIndex < dataStructures.CustomCanMessagesLiveData.Count &&
                               dataStructures.CustomCanMessagesLiveData[messageIndex].IsTx;
            byte normalizedFlags = onTxMessage
                ? flags
                : enabled
                    ? (byte)(flags | 0x01)
                    : (byte)(flags & ~0x01);

            if (i < dataStructures.CustomCanSignalsLiveData.Count)
            {
                CustomCanSignal signal = dataStructures.CustomCanSignalsLiveData[i];
                signal.Index = i;
                signal.MessageIndex = messageIndex;
                signal.StartBit = startBit;
                signal.BitLength = bitLength;
                signal.Flags = normalizedFlags;
                signal.ValueSource = valueSource;
                signal.Factor = factor;
                signal.Offset = offset;
                signal.RuntimeValue = runtimeValue;
                signal.Status = status;
                signal.MessageName = $"Message {messageIndex + 1}";
            }
        }

        for (int i = signalCount; i < dataStructures.CustomCanSignalsLiveData.Count; i++)
        {
            CustomCanSignal signal = dataStructures.CustomCanSignalsLiveData[i];
            signal.Index = i;
            signal.Name = string.Empty;
            signal.MessageName = string.Empty;
            signal.MessageIndex = 0;
            signal.StartBit = 0;
            signal.BitLength = 1;
            signal.Flags = 0;
            signal.ValueSource = 0;
            signal.Factor = 1.0f;
            signal.Offset = 0.0f;
            signal.RuntimeValue = 0.0f;
            signal.Status = (byte)CustomCanStatus.Disabled;
            signal.IsMarkedForDeletion = false;
        }

        if (crcValid == 0)
        {
            foreach (CustomCanSignal signal in dataStructures.CustomCanSignalsLiveData)
            {
                signal.Status = (byte)CustomCanStatus.InvalidConfig;
            }
        }

        CopyCustomCanLiveToStaticSnapshot();
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
        HasLoadedStaticSnapshot = true;
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

                    case (byte)Constants.COMMAND_ID_DIGITAL_INPUT_STATUS:
                        _lastDigitalInputStatusFrameTick = Environment.TickCount64;
                        ParseDigitalInputStatusPacket();
                        break;

                    case (byte)Constants.COMMAND_ID_REQUEST_STATIC:
                        _lastLiveStatusFrameTick = Environment.TickCount64;
                        _awaitingPostLogRefreshStatusFrame = false;
                        _postLogRefreshRequestRetryCount = 0;
                        ParseStaticStatusPacket();
                        break;

                    case (byte)Constants.COMMAND_ID_MATH_STATUS:
                        _lastMathStatusFrameTick = Environment.TickCount64;
                        ParseMathStatusPacket();
                        break;

                    case (byte)Constants.COMMAND_ID_CUSTOM_CAN_STATUS:
                        _lastCustomCanStatusFrameTick = Environment.TickCount64;
                        if (ParseCustomCanStatusPacket())
                        {
                            _customCanStatusSupportState = CustomCanStatusSupportState.Supported;
                            _customCanSnapshotTcs?.TrySetResult(true);
                        }
                        else
                        {
                            _customCanSnapshotTcs?.TrySetResult(false);
                        }
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
                if (parameterIndex >= 36 && parameterIndex <= Constants.LAST_CHANNEL_PARAM_INDEX)
                {
                    mathRuleIndex++;
                    if (mathRuleIndex >= Constants.NUM_MATH_RULE_SLOTS)
                    {
                        mathRuleIndex = 0;
                        parameterIndex++;
                    }
                }
                else
                {
                    parameterIndex++;
                }

                if (parameterIndex > _controllerCapabilities.LastChannelParameterIndex)
                {
                    parameterIndex = 0;
                    mathRuleIndex = 0;
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
            parameterIndex = channelIndex = analogueIndex = settingIndex = digitalIndex = mathRuleIndex = 0;
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
                        // Live Enabled is the effective output state, not the persisted enable setting.
                        if (dataStructures.ChannelsStaticData[channelIndex].Enabled != settingsData.ChannelsStaticData[channelIndex].Enabled)
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
                    case 33: // MATH rule count
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRuleCount != settingsData.ChannelsStaticData[channelIndex].MathRuleCount)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRuleCount, true);
                        }
                        break;
                    case 34: // MATH root rule
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRootRule != settingsData.ChannelsStaticData[channelIndex].MathRootRule)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRootRule, true);
                        }
                        break;
                    case 35: // MATH output mode
                        if (dataStructures.ChannelsLiveData[channelIndex].MathOutputMode != settingsData.ChannelsStaticData[channelIndex].MathOutputMode)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathOutputMode, true);
                        }
                        break;
                    case 36: // MATH operation
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].Operation != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].Operation)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].Operation, true);
                        }
                        break;
                    case 37: // MATH hysteresis enabled
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].HysteresisEnabled != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].HysteresisEnabled)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].HysteresisEnabled, true);
                        }
                        break;
                    case 38: // MATH on threshold
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OnThreshold != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OnThreshold)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OnThreshold);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 39: // MATH off threshold
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OffThreshold != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OffThreshold)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OffThreshold);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 40: // MATH operand A type
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OperandAType != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandAType)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandAType, true);
                        }
                        break;
                    case 41: // MATH operand A index
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OperandAIndex != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandAIndex)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandAIndex, true);
                        }
                        break;
                    case 42: // MATH operand A constant
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OperandAConstant != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandAConstant)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandAConstant);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
                        }
                        break;
                    case 43: // MATH operand B type
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OperandBType != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandBType)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandBType, true);
                        }
                        break;
                    case 44: // MATH operand B index
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OperandBIndex != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandBIndex)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            AddData(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandBIndex, true);
                        }
                        break;
                    case 45: // MATH operand B constant
                        dataStructures.ChannelsLiveData[channelIndex].EnsureMathRuleSlots();
                        settingsData.ChannelsStaticData[channelIndex].EnsureMathRuleSlots();
                        if (dataStructures.ChannelsLiveData[channelIndex].MathRules[mathRuleIndex].OperandBConstant != settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandBConstant)
                        {
                            configChanged = true;
                            AddData((byte)settingIndex, true);
                            AddData((byte)parameterIndex, true);
                            AddData((byte)channelIndex, true);
                            AddData((byte)mathRuleIndex, true);
                            byte[] floatBytes = BitConverter.GetBytes(settingsData.ChannelsStaticData[channelIndex].MathRules[mathRuleIndex].OperandBConstant);
                            foreach (byte b in floatBytes)
                            {
                                AddData(b, true);
                            }
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
                    _pendingConfigurationCommand = Constants.COMMAND_ID_NEWCONFIG;
                    _configurationCommandSentTick = Environment.TickCount64;
                    SendCommandWithRawPayload(Constants.COMMAND_ID_NEWCONFIG, packet);
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

    private bool HandleConfigurationAcknowledgement(byte response)
    {
        lock (_configurationSaveLock)
        {
            return HandleConfigurationAcknowledgementCore(response);
        }
    }

    private bool HandleConfigurationAcknowledgementCore(byte response)
    {
        if (response != Constants.COMMAND_ID_CONFIM && response != Constants.COMMAND_ID_CHECKSUM_FAIL)
            return false;
        char command = _configurationSaveActive ? _pendingConfigurationCommand : lastCommandSent;
        if (command != Constants.COMMAND_ID_NEWCONFIG &&
            command != Constants.COMMAND_ID_SKIP &&
            command != Constants.COMMAND_ID_SAVECHANGES)
            return false;
        if (TryHandleStandaloneAckByte(response))
            return true;

        bool success = response == Constants.COMMAND_ID_CONFIM;
        if (command == Constants.COMMAND_ID_SAVECHANGES)
        {
            if (!success)
            {
                if (_saveRetryCount++ < 1)
                {
                    LoggingService.AddLog("There was a problem saving settings. Trying again...");
                    SendCommand(Constants.COMMAND_ID_SAVECHANGES);
                }
                else
                {
                    FailConfigurationSave();
                }
                return true;
            }

            _saveRetryCount = 0;
            CompleteConfigurationSave();
            lock (_bufferLock)
            {
                receivedDataBuffer.Clear();
            }
            RequestStaticSnapshot();
            _configSendStopwatch.Stop();
            LoggingService.AddLog("PDM settings saved.");
            ConfigurationSaved?.Invoke(this, EventArgs.Empty);
            ConfigurationSaveCompleted?.Invoke(this, new ConfigurationSaveCompletedEventArgs(true));
            return true;
        }

        if (!sendingConfig)
        {
            if (success && command == Constants.COMMAND_ID_SKIP)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
                return true;
            }
            return false;
        }
        if (!success)
        {
            if (_configRetryCount++ < 1)
            {
                LoggingService.AddLog("There was a problem saving settings. Trying again...");
                SendConfig();
            }
            else
            {
                FailConfigurationSave();
            }
            return true;
        }

        _configRetryCount = 0;
        if (AdvanceConfigCursor())
        {
            SendConfig();
        }
        if (saveToEEPROM)
        {
            saveToEEPROM = false;
            SendCommand(Constants.COMMAND_ID_SAVECHANGES);
        }
        return true;
    }

    private void FailConfigurationSave()
    {
        LoggingService.AddLog("Settings could not be saved. Please try again.");
        sendingConfig = false;
        saveToEEPROM = false;
        parameterIndex = channelIndex = analogueIndex = settingIndex = digitalIndex = mathRuleIndex = 0;
        _configRetryCount = _saveRetryCount = 0;
        _configSendStopwatch.Stop();
        CompleteConfigurationSave();
        ConfigurationSaveCompleted?.Invoke(this, new ConfigurationSaveCompletedEventArgs(false));
    }

    private void CompleteConfigurationSave()
    {
        if (!_configurationSaveActive) return;
        _configurationSaveActive = false;
        _pendingConfigurationCommand = '\0';
        _suspendLiveRequestPolling = _configurationPreviousPollingSuspended;
    }

    private void CheckConfigurationSaveTimeout()
    {
        lock (_configurationSaveLock)
        {
            if (_configurationSaveActive &&
                Environment.TickCount64 - _configurationCommandSentTick >= 10000)
            {
                LoggingService.AddLog("Controller settings acknowledgement timed out; settings may be partially applied. Read back the controller before retrying.");
                FailConfigurationSave();
            }
        }
    }

    private bool IsStandaloneAckExpectedForLastCommand()
    {
        return lastCommandSent == Constants.COMMAND_ID_BEGIN ||
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
    }

    private bool TryHandleStandaloneAckByte(byte readByte)
    {
        if (_standaloneCommandAckTcs == null || sendingConfig || _standaloneCommandAckId == '\0')
        {
            return false;
        }

        if (readByte != Constants.COMMAND_ID_CONFIM && readByte != Constants.COMMAND_ID_CHECKSUM_FAIL)
        {
            return false;
        }

        // Guard against stale/foreign ACK bytes being misattributed to the current
        // standalone command, while still tolerating live-poll commands that can
        // overwrite lastCommandSent during an active standalone request.
        bool lastCommandIsLivePoll = lastCommandSent == Constants.COMMAND_ID_REQUEST ||
                                     lastCommandSent == Constants.COMMAND_ID_DIGITAL_INPUT_STATUS ||
                                     lastCommandSent == Constants.COMMAND_ID_MATH_STATUS ||
                                     lastCommandSent == Constants.COMMAND_ID_CUSTOM_CAN_STATUS;
        bool allowLivePollAckOverride = _standaloneCommandAckId == Constants.COMMAND_ID_LOG_RESET ||
                                        _standaloneCommandAckId == Constants.COMMAND_ID_LOG_OPEN ||
                                        _standaloneCommandAckId == Constants.COMMAND_ID_LOG_CANCEL;
        if (lastCommandSent != _standaloneCommandAckId &&
            !(allowLivePollAckOverride && lastCommandIsLivePoll))
        {
            return false;
        }

        _standaloneCommandAckTcs.TrySetResult(readByte == Constants.COMMAND_ID_CONFIM);

        lock (_bufferLock)
        {
            if (receivedDataBuffer.Count > 0 && receivedDataBuffer[^1] == readByte)
            {
                receivedDataBuffer.RemoveAt(receivedDataBuffer.Count - 1);
            }
        }

        return true;
    }

    private bool UseSecureStreamingTransport()
        => _secureSession.IsSessionOpen;

    private bool TryReadNextSecureIncomingPayload(out byte[] payload)
    {
        payload = Array.Empty<byte>();
        byte[]? secureFrame = null;

        lock (_bufferLock)
        {
            if (_secureIncomingBuffer.Count == 0)
            {
                return false;
            }

            if (_secureIncomingBuffer[0] != (byte)Constants.SECURE_ENVELOPE_FRAME_PREFIX)
            {
                payload = [ _secureIncomingBuffer[0] ];
                _secureIncomingBuffer.RemoveAt(0);
                return true;
            }

            if (_secureIncomingBuffer.Count < Constants.SECURE_ENVELOPE_HEADER_LENGTH)
            {
                return false;
            }

            ushort plaintextLength = BinaryPrimitives.ReadUInt16LittleEndian(_secureIncomingBuffer.ToArray().AsSpan(1, 2));
            if (plaintextLength == 0 || plaintextLength > Constants.SECURE_ENVELOPE_MAX_PLAINTEXT_BYTES)
            {
                _secureIncomingBuffer.RemoveAt(0);
                return true;
            }

            int fullLength = Constants.SECURE_ENVELOPE_HEADER_LENGTH + plaintextLength + Constants.SECURE_TAG_LENGTH;
            if (_secureIncomingBuffer.Count < fullLength)
            {
                return false;
            }

            secureFrame = _secureIncomingBuffer.GetRange(0, fullLength).ToArray();
            _secureIncomingBuffer.RemoveRange(0, fullLength);
        }

        if (secureFrame == null)
        {
            return false;
        }

        if (!TryDecryptSecureEnvelope(secureFrame, _secureSession.DeviceToClientKey, _secureSession.SessionId, _secureSession.RxSequence, out payload))
        {
            LogProtocolDebug($"SEC RX decrypt-fail seq={_secureSession.RxSequence} frameLen={secureFrame.Length} sessionOpen={_secureSession.IsSessionOpen}");
            ResetSecureSession();
            payload = Array.Empty<byte>();
            return true;
        }

        LogProtocolDebug($"SEC RX decrypt-ok seq={_secureSession.RxSequence} payloadLen={payload.Length}");
        _secureSession.RxSequence++;
        return true;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            lock (_serialReceiveLock)
            {
                if (_isDisposed || _isShuttingDown || _suspendDataReceivedReads)
                {
                    return;
                }

                OnDataReceivedCore(sender, e);
            }
        }
        catch (ObjectDisposedException)
        {
            // Port is being torn down; ignore callback during shutdown.
        }
        catch (InvalidOperationException)
        {
            // Port state changed during teardown; ignore callback during shutdown.
        }
        catch (IOException)
        {
            // Transport closed while callback was in flight.
        }
        catch (NullReferenceException) when (_isShuttingDown || _isDisposed)
        {
            // Defensive guard for rare framework callback races during shutdown.
        }
    }

    private void OnDataReceivedCore(object sender, SerialDataReceivedEventArgs e)
    {
        // EOF callbacks can be raised during teardown and do not carry payload bytes.
        // Avoid spending time in the receive loop for EOF notifications.
        if (e.EventType == SerialData.Eof)
        {
            return;
        }

        DrainSerialInput();
    }

    // Reads are capped per pass; the parse loop calls back in while bytes remain so large frames
    // (e.g. the ~2.8 KB Custom CAN snapshot) are not stranded waiting for another DataReceived event.
    private void DrainPendingSerialInput()
    {
        if (_isDisposed || _isShuttingDown || _bulkLogTransferActive || _suspendDataReceivedReads)
        {
            return;
        }

        try
        {
            if (!_serialPort.IsOpen || _serialPort.BytesToRead == 0)
            {
                return;
            }

            lock (_serialReceiveLock)
            {
                DrainSerialInput();
            }
        }
        catch (Exception) when (_isDisposed || _isShuttingDown || !_serialPort.IsOpen)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void DrainSerialInput()
    {
        if (_isDisposed || _isShuttingDown || _bulkLogTransferActive || _suspendDataReceivedReads)
        {
            return;
        }

        bool sawFramedPacketBoundary = false;

        if (_serialPort.IsOpen)
        {
            try
            {
                bool secureStreamingTransport = UseSecureStreamingTransport();
                int processedBytes = 0;
                const int maxBytesPerCallback = 2048;
                while (_serialPort.BytesToRead > 0 && _serialPort.IsOpen && processedBytes < maxBytesPerCallback)
                {
                    byte readByte = (byte)_serialPort.ReadByte();
                    processedBytes++;

                    if (secureStreamingTransport)
                    {
                        lock (_bufferLock)
                        {
                            _secureIncomingBuffer.Add(readByte);
                        }

                        while (TryReadNextSecureIncomingPayload(out byte[] payload))
                        {
                            if (payload.Length == 0)
                            {
                                continue;
                            }

                            if (TryCompleteSecureManagementResponse(payload))
                            {
                                continue;
                            }

                            if (payload.Length == 1 && HandleConfigurationAcknowledgement(payload[0]))
                            {
                                continue;
                            }

                            for (int i = 0; i < payload.Length; i++)
                            {
                                byte decryptedByte = payload[i];
                                lock (_bufferLock)
                                {
                                    receivedDataBuffer.Add(decryptedByte);

                                    // In secure streaming mode, framed telemetry/status packets may
                                    // arrive interleaved with other secure control traffic. Detect
                                    // framed packet boundaries regardless of lastCommandSent.
                                    TrackFramedResponseByte(decryptedByte);
                                    if (_packetReady)
                                    {
                                        sawFramedPacketBoundary = true;
                                    }
                                }

                                if (payload.Length == 1 && TryHandleStandaloneAckByte(decryptedByte))
                                {
                                    continue;
                                }

                                // In secure streaming mode, standalone command responses can be
                                // delivered inside an encrypted payload. Handle LOG_OPEN ACK/NAK
                                // here because the raw-byte standalone response path is skipped.
                                if (lastCommandSent == Constants.COMMAND_ID_LOG_OPEN)
                                {
                                    if (decryptedByte == Constants.COMMAND_ID_CONFIM)
                                    {
                                        _logOpenTcs?.TrySetResult(true);
                                        continue;
                                    }

                                    if (decryptedByte == Constants.COMMAND_ID_CHECKSUM_FAIL)
                                    {
                                        _logOpenTcs?.TrySetResult(false);
                                        continue;
                                    }
                                }

                                if (lastCommandSent == Constants.COMMAND_ID_LOG_CHUNK)
                                {
                                    if (decryptedByte == Constants.COMMAND_ID_CHECKSUM_FAIL)
                                    {
                                        _logChunkTcs?.TrySetCanceled();
                                        continue;
                                    }
                                }
                            }
                        }

                        continue;
                    }

                    lock (_bufferLock)
                    {
                        receivedDataBuffer.Add(readByte);

                        if (ExpectsFramedResponse(lastCommandSent))
                        {
                            TrackFramedResponseByte(readByte);
                            if (_packetReady)
                            {
                                sawFramedPacketBoundary = true;
                            }
                        }
                    }

                    if (TryHandleStandaloneAckByte(readByte))
                    {
                        continue;
                    }

                    bool expectsStandaloneAck = IsStandaloneAckExpectedForLastCommand();
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

                    if (standaloneCommandResponse && HandleConfigurationAcknowledgement(readByte))
                    {
                        continue;
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

                            case Constants.COMMAND_ID_LOG_OPEN:
                                _logOpenTcs?.TrySetResult(true);
                                LogProtocolDebug("RX LOG_OPEN ACK");
                                break;

                            case Constants.COMMAND_ID_LOG_CANCEL:
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
                            case Constants.COMMAND_ID_LOG_OPEN:
                                _logOpenTcs?.TrySetResult(false);
                                LogProtocolDebug("RX LOG_OPEN CHECKSUM_FAIL");
                                break;

                            case Constants.COMMAND_ID_LOG_CHUNK:
                                _logChunkTcs?.TrySetCanceled();
                                LogProtocolDebug("RX LOG_CHUNK CHECKSUM_FAIL");
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

        if (sawFramedPacketBoundary || _packetReady || _serialPort.IsOpen && _serialPort.BytesToRead > 0)
        {
            _parseSignal.Set();
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
        if (_isDisposed)
        {
            return;
        }

        lock (_serialLifecycleLock)
        {
            _isShuttingDown = true;
            _suspendDataReceivedReads = true;

            lock (_serialReceiveLock)
            {
                if (_serialPort.IsOpen)
                {
                    ResetLogReceiveState(discardSerialInput: true);
                    _serialPort.Close();
                }

                UnsubscribeDataReceived_NoLock();
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        lock (_serialLifecycleLock)
        {
            _isShuttingDown = true;
            _suspendDataReceivedReads = true;

            try
            {
                _processTimer.Stop();
            }
            catch
            {
            }

            lock (_serialReceiveLock)
            {
                try
                {
                    if (_serialPort.IsOpen)
                    {
                        ResetLogReceiveState(discardSerialInput: true);
                        _serialPort.Close();
                    }
                }
                catch
                {
                }

                try
                {
                    UnsubscribeDataReceived_NoLock();
                }
                catch
                {
                }
            }
        }

        _parseLoopCts.Cancel();
        _parseSignal.Set();
        try
        {
            _parseLoopTask?.Wait(500);
        }
        catch
        {
        }

        _processTimer.Dispose();

        try
        {
            _serialPort.Dispose();
        }
        catch
        {
        }

        _parseSignal.Dispose();
        _parseLoopCts.Dispose();

        _logChunkRequestLock.Dispose();

        DataUpdated = null;
        ConfigurationSaved = null;
        ConfigurationSaveCompleted = null;
    }

    public void Send(string text) => _serialPort.WriteLine(text);

    public void StartSendConfig()
    {
        lock (_configurationSaveLock)
        {
            if (_configurationSaveActive)
                throw new InvalidOperationException("A controller settings save is already in progress.");
            if (!_serialPort.IsOpen)
                throw new IOException("The controller serial connection is closed.");
            if (_secureSession.PinEnabled && !_secureSession.IsAuthorized)
                throw new InvalidOperationException("Unlock the controller before saving settings.");
            _configurationPreviousPollingSuspended = _suspendLiveRequestPolling;
            _suspendLiveRequestPolling = true;
            _configurationSaveActive = true;
            _configurationCommandSentTick = Environment.TickCount64;
            try
            {
                StartSendConfigCore();
            }
            catch
            {
                FailConfigurationSave();
                throw;
            }
        }
    }

    private void StartSendConfigCore()
    {
        sendingConfig = true;
        settingIndex = 1;
        channelIndex = 0;
        analogueIndex = 0;
        digitalIndex = 0;
        mathRuleIndex = 0;
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

    private static bool IsPublicRawCommand(char commandId)
        => commandId == Constants.COMMAND_ID_BEGIN ||
           commandId == Constants.COMMAND_ID_SKIP ||
           commandId == Constants.COMMAND_ID_FW_VER ||
           commandId == Constants.COMMAND_ID_BUILD_DATE;

    private bool ShouldSendCommandOverSecureEnvelope(char commandId)
        => _secureSession.IsSessionOpen &&
           _secureSession.IsAuthorized &&
           !IsPublicRawCommand(commandId);

    private byte[] BuildCommandFrame(byte[] commandPayload)
    {
        char commandId = (char)commandPayload[0];
        if (_secureSession.IsSessionOpen && !IsPublicRawCommand(commandId) && !_secureSession.IsAuthorized)
        {
            throw new InvalidOperationException("Unlock the PDM before sending this command.");
        }

        if (!ShouldSendCommandOverSecureEnvelope(commandId))
        {
            return commandPayload;
        }

        if (commandPayload.Length > Constants.SECURE_ENVELOPE_MAX_PLAINTEXT_BYTES)
        {
            throw new InvalidOperationException("The command exceeds the PDM's encrypted packet size limit.");
        }

        return EncryptSecureEnvelope(
            commandPayload,
            _secureSession.ClientToDeviceKey,
            _secureSession.SessionId,
            _secureSession.TxSequence++);
    }

    private void SendCommand(char commandId)
    {
        lock (_configurationSaveLock)
        {
            if (_configurationSaveActive && commandId != Constants.COMMAND_ID_SAVECHANGES)
                return;
            if (_configurationSaveActive)
            {
                _pendingConfigurationCommand = commandId;
                _configurationCommandSentTick = Environment.TickCount64;
            }
            SendCommandCore(commandId);
        }
    }

    private void SendCommandCore(char commandId)
    {
        if (_serialPort.IsOpen)
        {
            bool commandRequiresAuthorization =
                _secureSession.IsSessionOpen &&
                _secureSession.PinEnabled &&
                !_secureSession.IsAuthorized &&
                !IsPublicRawCommand(commandId);

            if (commandRequiresAuthorization)
            {
                LogProtocolDebug($"TX block cmd={(byte)commandId}('{commandId}') sessionOpen={_secureSession.IsSessionOpen} pinEnabled={_secureSession.PinEnabled} auth={_secureSession.IsAuthorized}");
                return;
            }

            bool sendEncrypted = ShouldSendCommandOverSecureEnvelope(commandId);
            uint sequenceBefore = _secureSession.TxSequence;
            byte[] data = BuildCommandFrame([(byte)commandId]);

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
            if (commandId == Constants.COMMAND_ID_DIGITAL_INPUT_STATUS)
            {
                _lastDigitalInputStatusRequestSentTick = Environment.TickCount64;
            }
            if (commandId == Constants.COMMAND_ID_MATH_STATUS)
            {
                _lastMathStatusRequestSentTick = Environment.TickCount64;
            }
            if (commandId == Constants.COMMAND_ID_CUSTOM_CAN_STATUS)
            {
                _lastCustomCanStatusRequestSentTick = Environment.TickCount64;
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
            // Initial static snapshot hydrates the full settings UI. Keep retries
            // aligned with the comms timer cadence so a missed first request does
            // not add multi-second startup latency.
            int staticRetryIntervalMs = HasLoadedStaticSnapshot
                ? StaticSnapshotRetryIntervalMs
                : Math.Min(StaticSnapshotRetryIntervalMs, 250);

            if (_lastStaticRequestSentTick == 0 || (now - _lastStaticRequestSentTick) >= staticRetryIntervalMs)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST_STATIC);
            }

            return;
        }

        if (!HasLoadedStaticSnapshot)
        {
            return;
        }

        UpdateDigitalInputStatusSupport(now, idleThresholdMs);
        UpdateCustomCanStatusSupport(now, idleThresholdMs);

        long lastActivityTick = Math.Max(_lastLiveRequestSentTick, _lastLiveStatusFrameTick);

        if (lastActivityTick != 0 && (now - lastActivityTick) >= reconnectThresholdMs)
        {
            LoggingService.AddLog("Controller comms lost. Reconnecting...");
            MarkControllerSessionLost(requestStaticSnapshot: true);
            SendCommand(Constants.COMMAND_ID_BEGIN);
            return;
        }

        if (lastActivityTick == 0 || (now - lastActivityTick) >= idleThresholdMs)
        {
            SendCommand(Constants.COMMAND_ID_REQUEST);
        }

        if (_digitalInputStatusSupportState != DigitalInputStatusSupportState.Unsupported)
        {
            int digitalStatusPollIntervalMs = _digitalInputStatusSupportState == DigitalInputStatusSupportState.Supported
                ? idleThresholdMs
                : Math.Max(idleThresholdMs * 2, DigitalInputStatusProbeTimeoutMs);

            if (_lastDigitalInputStatusRequestSentTick == 0 ||
                (now - _lastDigitalInputStatusRequestSentTick) >= digitalStatusPollIntervalMs)
            {
                SendCommand(Constants.COMMAND_ID_DIGITAL_INPUT_STATUS);
            }
        }

        if (_mathStatusPollingChannelIndex >= 0 &&
            _mathStatusPollingChannelIndex < dataStructures.ChannelsLiveData.Count &&
            dataStructures.ChannelsLiveData[_mathStatusPollingChannelIndex].ChanType == ChannelType.MATH)
        {
            int mathIdleThresholdMs = Math.Max(idleThresholdMs * 3, idleThresholdMs);
            if (_lastMathStatusRequestSentTick == 0 || (now - _lastMathStatusRequestSentTick) >= mathIdleThresholdMs)
            {
                SendCommand(Constants.COMMAND_ID_MATH_STATUS);
            }
        }

        if (_customCanStatusPollingEnabled && _customCanStatusSupportState != CustomCanStatusSupportState.Unsupported)
        {
            int customCanPollIntervalMs = _customCanStatusSupportState == CustomCanStatusSupportState.Supported
                ? Math.Max(idleThresholdMs * 3, idleThresholdMs)
                : Math.Min(idleThresholdMs, 500);

            if (_lastCustomCanStatusRequestSentTick == 0 ||
                (now - _lastCustomCanStatusRequestSentTick) >= customCanPollIntervalMs)
            {
                SendCommand(Constants.COMMAND_ID_CUSTOM_CAN_STATUS);
            }
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

    public void RequestCustomCanSnapshot()
    {
        if (!_serialPort.IsOpen || sendingConfig)
        {
            return;
        }

        SendCommand(Constants.COMMAND_ID_CUSTOM_CAN_STATUS);
    }

    public async Task<bool> RequestCustomCanSnapshotAsync(int timeoutMs = 2500, int attempts = 2)
    {
        if (!_serialPort.IsOpen || sendingConfig)
        {
            return false;
        }

        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        try
        {
            for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                // Give any in-flight live frame time to finish. Replies are dispatched by command ID, so an
                // interleaved live frame is harmless; discarding input here would break secure-session sequencing.
                await Task.Delay(StandaloneCommandDrainDelayMs);
                if (!_serialPort.IsOpen || sendingConfig)
                {
                    return false;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _customCanSnapshotTcs = tcs;
                try
                {
                    SendCommand(Constants.COMMAND_ID_CUSTOM_CAN_STATUS);
                    Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                    if (completedTask == tcs.Task && await tcs.Task)
                    {
                        return true;
                    }
                }
                finally
                {
                    if (_customCanSnapshotTcs == tcs)
                    {
                        _customCanSnapshotTcs = null;
                    }
                }
            }

            return false;
        }
        finally
        {
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
            {
                EnsureLiveRequestPolling();
            }
        }
    }

    public void SetMathStatusPollingChannel(int channelIndex)
    {
        _mathStatusPollingChannelIndex = channelIndex >= 0 && channelIndex < Constants.NUM_OUTPUT_CHANNELS
            ? channelIndex
            : -1;
    }

    public void SetCustomCanStatusPollingEnabled(bool enabled)
    {
        _customCanStatusPollingEnabled = enabled;
    }

    public bool BeginCustomCanConfigSession()
    {
        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        return previousSuspendLiveRequestPolling;
    }

    public void EndCustomCanConfigSession(bool previousSuspendLiveRequestPolling)
    {
        _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
        if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
        {
            EnsureLiveRequestPolling();
        }
    }

    private void SendCommandWithByte(char commandId, byte value)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        bool commandRequiresAuthorization =
            _secureSession.IsSessionOpen &&
            _secureSession.PinEnabled &&
            !_secureSession.IsAuthorized &&
            !IsPublicRawCommand(commandId);

        if (commandRequiresAuthorization)
        {
            LogProtocolDebug($"TX block cmd={(byte)commandId}('{commandId}') sessionOpen={_secureSession.IsSessionOpen} pinEnabled={_secureSession.PinEnabled} auth={_secureSession.IsAuthorized}");
            return;
        }

        bool sendEncrypted = ShouldSendCommandOverSecureEnvelope(commandId);
        byte[] commandPayload = [ (byte)commandId, value ];
        byte[] data = sendEncrypted
            ? EncryptSecureEnvelope(
                commandPayload,
                _secureSession.ClientToDeviceKey,
                _secureSession.SessionId,
                _secureSession.TxSequence++)
            : commandPayload;

        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = commandId;
    }

    private void SendCommandWithUInt32(char commandId, byte firstValue, uint value)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        bool commandRequiresAuthorization =
            _secureSession.IsSessionOpen &&
            _secureSession.PinEnabled &&
            !_secureSession.IsAuthorized &&
            !IsPublicRawCommand(commandId);

        if (commandRequiresAuthorization)
        {
            LogProtocolDebug($"TX block cmd={(byte)commandId}('{commandId}') sessionOpen={_secureSession.IsSessionOpen} pinEnabled={_secureSession.PinEnabled} auth={_secureSession.IsAuthorized}");
            return;
        }

        byte[] commandPayload = new byte[6];
        commandPayload[0] = (byte)commandId;
        commandPayload[1] = firstValue;
        byte[] valueBytes = BitConverter.GetBytes(value);
        Array.Copy(valueBytes, 0, commandPayload, 2, valueBytes.Length);

        bool sendEncrypted = ShouldSendCommandOverSecureEnvelope(commandId);
        byte[] data = sendEncrypted
            ? EncryptSecureEnvelope(
                commandPayload,
                _secureSession.ClientToDeviceKey,
                _secureSession.SessionId,
                _secureSession.TxSequence++)
            : commandPayload;

        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = commandId;
    }

    private void SendCommandWithPayload(char commandId, byte[] payload, int payloadLength)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        bool commandRequiresAuthorization =
            _secureSession.IsSessionOpen &&
            _secureSession.PinEnabled &&
            !_secureSession.IsAuthorized &&
            !IsPublicRawCommand(commandId);

        if (commandRequiresAuthorization)
        {
            LogProtocolDebug($"TX block cmd={(byte)commandId}('{commandId}') sessionOpen={_secureSession.IsSessionOpen} pinEnabled={_secureSession.PinEnabled} auth={_secureSession.IsAuthorized}");
            return;
        }

        byte[] commandPayload = new byte[payloadLength + 3];
        commandPayload[0] = (byte)commandId;
        commandPayload[1] = (byte)(payloadLength & 0xFF);
        commandPayload[2] = (byte)((payloadLength >> 8) & 0xFF);
        Array.Copy(payload, 0, commandPayload, 3, payloadLength);

        bool sendEncrypted = ShouldSendCommandOverSecureEnvelope(commandId);
        byte[] data = sendEncrypted
            ? EncryptSecureEnvelope(
                commandPayload,
                _secureSession.ClientToDeviceKey,
                _secureSession.SessionId,
                _secureSession.TxSequence++)
            : commandPayload;

        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = commandId;
    }

    private void SendCommandWithRawPayload(char commandId, byte[] payload)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        bool commandRequiresAuthorization =
            _secureSession.IsSessionOpen &&
            _secureSession.PinEnabled &&
            !_secureSession.IsAuthorized &&
            !IsPublicRawCommand(commandId);

        if (commandRequiresAuthorization)
        {
            LogProtocolDebug($"TX block cmd={(byte)commandId}('{commandId}') sessionOpen={_secureSession.IsSessionOpen} pinEnabled={_secureSession.PinEnabled} auth={_secureSession.IsAuthorized}");
            return;
        }

        byte[] commandPayload = new byte[payload.Length + 1];
        commandPayload[0] = (byte)commandId;
        Array.Copy(payload, 0, commandPayload, 1, payload.Length);

        bool sendEncrypted = ShouldSendCommandOverSecureEnvelope(commandId);
        byte[] data = sendEncrypted
            ? EncryptSecureEnvelope(
                commandPayload,
                _secureSession.ClientToDeviceKey,
                _secureSession.SessionId,
                _secureSession.TxSequence++)
            : commandPayload;

        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = commandId;
    }

    private static byte[] BuildStandaloneConfigPacket(byte configType, byte parameterIndex, byte dataIndex, byte[] payload)
    {
        payload ??= [];

        int checksum = 0;
        var packet = new List<byte>(payload.Length + 11);

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
        AddPacketByte(configType, true);
        AddPacketByte(parameterIndex, true);
        AddPacketByte(dataIndex, true);

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
        return packet.ToArray();
    }

    private void SendStandaloneSystemConfigCommand(byte parameterIndex, byte[] payload)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        byte[] packet = BuildStandaloneConfigPacket(2, parameterIndex, 0, payload);
        SendCommandWithRawPayload(Constants.COMMAND_ID_NEWCONFIG, packet);
    }

    private void SendStandaloneCustomCanConfigCommand(byte configType, byte parameterIndex, byte dataIndex, byte[] payload)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        byte[] packet = BuildStandaloneConfigPacket(configType, parameterIndex, dataIndex, payload);
        SendCommandWithRawPayload(Constants.COMMAND_ID_NEWCONFIG, packet);
    }

    private static bool TryBuildCustomCanPayload(byte configType, int parameterIndex, uint value, out byte[] payload)
    {
        payload = [];

        // Firmware-writable parameters only: message param 4 (status) and signal
        // params 6/7 (live value/status) are read-only and rejected by the controller.
        bool validMessageParameter = configType == Constants.CONFIG_DATA_CUSTOM_CAN_MESSAGE &&
                                     parameterIndex is 0 or 1 or 2 or 3 or Constants.CUSTOM_CAN_MESSAGE_PARAM_TX_TRIGGER;
        bool validSignalParameter = configType == Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL &&
                                    parameterIndex is 0 or 1 or 2 or 3 or 4 or 5 or Constants.CUSTOM_CAN_SIGNAL_PARAM_VALUE_SOURCE;

        if (!validMessageParameter && !validSignalParameter)
        {
            return false;
        }

        payload = BitConverter.GetBytes(value);
        return true;
    }

    public async Task<bool> WriteCustomCanMessageParameterAsync(int messageIndex, int parameterIndex, uint value, int timeoutMs = 2500)
    {
        if (messageIndex < 0 || messageIndex >= Constants.MAX_CUSTOM_CAN_MESSAGES ||
            parameterIndex < 0 || parameterIndex > Constants.LAST_CUSTOM_CAN_MESSAGE_PARAM_INDEX)
        {
            LastCustomCanWriteError = $"Custom CAN message write input out of range (slot={messageIndex + 1}, parameter={parameterIndex}, value={value}).";
            return false;
        }

        if (!TryBuildCustomCanPayload(Constants.CONFIG_DATA_CUSTOM_CAN_MESSAGE, parameterIndex, value, out byte[] payload))
        {
            LastCustomCanWriteError = $"Custom CAN message payload encode failed (slot={messageIndex + 1}, parameter={parameterIndex}, value={value}).";
            return false;
        }

        bool accepted = false;
        for (int attempt = 0; attempt < 2 && !accepted; attempt++)
        {
            accepted = await AwaitStandaloneAckAsync(
                Constants.COMMAND_ID_NEWCONFIG,
                () => SendStandaloneCustomCanConfigCommand(Constants.CONFIG_DATA_CUSTOM_CAN_MESSAGE, (byte)parameterIndex, (byte)messageIndex, payload),
                timeoutMs,
                discardBeforeSend: true);

            if (!accepted && attempt == 0 && _serialPort.IsOpen)
            {
                await Task.Delay(StandaloneCommandDrainDelayMs);
            }
        }

        if (!accepted)
        {
            LastCustomCanWriteError = $"Controller rejected Custom CAN message write (slot={messageIndex + 1}, parameter={parameterIndex}, value={value}, payload={BitConverter.ToString(payload)}).";
            return false;
        }

        LastCustomCanWriteError = null;
        return true;
    }

    public async Task<bool> WriteCustomCanSignalParameterAsync(int signalIndex, int parameterIndex, uint value, int timeoutMs = 2500)
    {
        if (signalIndex < 0 || signalIndex >= Constants.MAX_CUSTOM_CAN_SIGNALS ||
            parameterIndex < 0 || parameterIndex > Constants.LAST_CUSTOM_CAN_SIGNAL_PARAM_INDEX)
        {
            LastCustomCanWriteError = $"Custom CAN signal write input out of range (slot={signalIndex + 1}, parameter={parameterIndex}, value={value}).";
            return false;
        }

        if (!TryBuildCustomCanPayload(Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL, parameterIndex, value, out byte[] payload))
        {
            LastCustomCanWriteError = $"Custom CAN signal payload encode failed (slot={signalIndex + 1}, parameter={parameterIndex}, value={value}).";
            return false;
        }

        bool accepted = false;
        for (int attempt = 0; attempt < 2 && !accepted; attempt++)
        {
            accepted = await AwaitStandaloneAckAsync(
                Constants.COMMAND_ID_NEWCONFIG,
                () => SendStandaloneCustomCanConfigCommand(Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL, (byte)parameterIndex, (byte)signalIndex, payload),
                timeoutMs,
                discardBeforeSend: true);

            if (!accepted && attempt == 0 && _serialPort.IsOpen)
            {
                await Task.Delay(StandaloneCommandDrainDelayMs);
            }
        }

        if (!accepted)
        {
            LastCustomCanWriteError = $"Controller rejected Custom CAN signal write (slot={signalIndex + 1}, parameter={parameterIndex}, value={value}, payload={BitConverter.ToString(payload)}).";
            return false;
        }

        LastCustomCanWriteError = null;
        return true;
    }

    /// <summary>Packs a message exactly like firmware CustomCANMessageConfig (packed, little-endian).</summary>
    public static byte[] BuildCustomCanMessageRecord(uint id, ushort timeoutMs, byte dlc, byte flags, byte txTrigger)
    {
        byte[] record = new byte[Constants.CUSTOM_CAN_MESSAGE_RECORD_SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0, 4), id);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), timeoutMs);
        record[6] = dlc;
        record[7] = flags;
        record[8] = txTrigger;
        return record;
    }

    /// <summary>Packs a signal exactly like firmware CustomCANSignalConfig (packed, little-endian).</summary>
    public static byte[] BuildCustomCanSignalRecord(byte messageIndex, byte startBit, byte bitLength, byte flags, float factor, float offset, byte valueSource)
    {
        byte[] record = new byte[Constants.CUSTOM_CAN_SIGNAL_RECORD_SIZE];
        record[0] = messageIndex;
        record[1] = startBit;
        record[2] = bitLength;
        record[3] = flags;
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(4, 4), factor);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(8, 4), offset);
        record[12] = valueSource;
        return record;
    }

    /// <summary>
    /// Writes a whole message slot in one packet. The controller validates the complete record at once,
    /// so no transient half-updated state can be rejected or left behind.
    /// </summary>
    public Task<bool> WriteCustomCanMessageRecordAsync(int messageIndex, uint id, ushort timeoutMs, byte dlc, byte flags, byte txTrigger, int timeoutMsAck = 2500)
    {
        if (messageIndex < 0 || messageIndex >= Constants.MAX_CUSTOM_CAN_MESSAGES)
        {
            LastCustomCanWriteError = $"Custom CAN message record slot out of range (slot={messageIndex + 1}).";
            return Task.FromResult(false);
        }

        byte[] record = BuildCustomCanMessageRecord(id, timeoutMs, dlc, flags, txTrigger);
        return WriteCustomCanRecordAsync(Constants.CONFIG_DATA_CUSTOM_CAN_MESSAGE, messageIndex, record, "message", timeoutMsAck);
    }

    /// <summary>Writes a whole signal slot in one packet (see <see cref="WriteCustomCanMessageRecordAsync"/>).</summary>
    public Task<bool> WriteCustomCanSignalRecordAsync(int signalIndex, byte messageIndex, byte startBit, byte bitLength, byte flags, float factor, float offset, byte valueSource, int timeoutMsAck = 2500)
    {
        if (signalIndex < 0 || signalIndex >= Constants.MAX_CUSTOM_CAN_SIGNALS)
        {
            LastCustomCanWriteError = $"Custom CAN signal record slot out of range (slot={signalIndex + 1}).";
            return Task.FromResult(false);
        }

        byte[] record = BuildCustomCanSignalRecord(messageIndex, startBit, bitLength, flags, factor, offset, valueSource);
        return WriteCustomCanRecordAsync(Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL, signalIndex, record, "signal", timeoutMsAck);
    }

    private async Task<bool> WriteCustomCanRecordAsync(byte configType, int slotIndex, byte[] record, string kind, int timeoutMs)
    {
        bool accepted = false;
        for (int attempt = 0; attempt < 2 && !accepted; attempt++)
        {
            accepted = await AwaitStandaloneAckAsync(
                Constants.COMMAND_ID_NEWCONFIG,
                () => SendStandaloneCustomCanConfigCommand(configType, Constants.CUSTOM_CAN_PARAM_RECORD, (byte)slotIndex, record),
                timeoutMs,
                discardBeforeSend: true);

            if (!accepted && attempt == 0 && _serialPort.IsOpen)
            {
                await Task.Delay(StandaloneCommandDrainDelayMs);
            }
        }

        if (!accepted)
        {
            LastCustomCanWriteError = $"Controller rejected Custom CAN {kind} record (slot={slotIndex + 1}, payload={BitConverter.ToString(record)}).";
            return false;
        }

        LastCustomCanWriteError = null;
        return true;
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

        await _standaloneRequestLock.WaitAsync();
        try
        {
            bool preserveSecureSession = _secureSession.IsSessionOpen;
            _suspendLiveRequestPolling = true;
            if (!preserveSecureSession)
            {
                ResetLogReceiveState(discardSerialInput: true);
            }
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

                if (!preserveSecureSession)
                {
                    ResetLogReceiveState(discardSerialInput: false);
                }

                _suspendLiveRequestPolling = false;
                if (_serialPort.IsOpen && !sendingConfig)
                {
                    EnsureLiveRequestPolling();
                }
            }
        }
        finally
        {
            _standaloneRequestLock.Release();
        }
    }

    public async Task<string?> RequestBuildDateAsync(int timeoutMs = 3000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        bool preserveSecureSession = _secureSession.IsSessionOpen;
        _suspendLiveRequestPolling = true;
        if (!preserveSecureSession)
        {
            ResetLogReceiveState(discardSerialInput: true);
        }
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

            if (!preserveSecureSession)
            {
                ResetLogReceiveState(discardSerialInput: false);
            }

            _suspendLiveRequestPolling = false;
            if (_serialPort.IsOpen && !sendingConfig)
            {
                EnsureLiveRequestPolling();
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
        bool preserveSecureSession = _secureSession.IsSessionOpen;
        _suspendLiveRequestPolling = true;
        if (!preserveSecureSession)
        {
            ResetLogReceiveState(discardSerialInput: true);
        }
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

    private void ResetStandaloneReceiveState(bool discardSerialInput)
    {
        if (!_secureSession.IsSessionOpen)
        {
            ResetLogReceiveState(discardSerialInput);
            return;
        }

        // Keep partial encrypted frames and their sequence numbers intact.
        lock (_bufferLock)
        {
            receivedDataBuffer.Clear();
        }

        foundTrailer1 = false;
        _packetReady = false;
    }

    public async Task<string?> RequestCellularTestAsync(CellularParameters? cellularSettings = null, int timeoutMs = 3000)
    {
        if (!_serialPort.IsOpen)
        {
            return null;
        }

        await _standaloneRequestLock.WaitAsync();
        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        ResetStandaloneReceiveState(discardSerialInput: true);
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

            ResetStandaloneReceiveState(discardSerialInput: false);

            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }

            _standaloneRequestLock.Release();
        }
    }

    public async Task<string?> RequestOpenRemoteProvisioningAsync(CellularParameters cellularSettings, string provisioningRequestJson, int timeoutMs = 190000)
    {
        if (!_serialPort.IsOpen || cellularSettings == null || string.IsNullOrWhiteSpace(provisioningRequestJson))
        {
            return null;
        }

        await _standaloneRequestLock.WaitAsync();
        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        ResetStandaloneReceiveState(discardSerialInput: true);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _openRemoteProvisioningTcs = tcs;

        try
        {
            SendOpenRemoteProvisioningCommand(cellularSettings, provisioningRequestJson);
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                throw new TimeoutException("The PDM did not reply to the registration request within the timeout. Check the controller connection and cellular diagnostics before retrying.");
            }

            return await tcs.Task;
        }
        finally
        {
            if (_openRemoteProvisioningTcs == tcs)
            {
                _openRemoteProvisioningTcs = null;
            }

            ResetStandaloneReceiveState(discardSerialInput: false);
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }

            _standaloneRequestLock.Release();
        }
    }

    private void SendOpenRemoteProvisioningCommand(CellularParameters cellularSettings, string provisioningRequestJson)
    {
        byte[] data = BuildOpenRemoteProvisioningCommand(cellularSettings, provisioningRequestJson);
        LoggingService.AddLog("Sending PDM registration request.",
            details: $"transport={(ShouldSendCommandOverSecureEnvelope(Constants.COMMAND_ID_OPENREMOTE_PROVISION) ? "encrypted" : "legacy")}; pinEnabled={_secureSession.PinEnabled}; frameBytes={data.Length}");
        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = Constants.COMMAND_ID_OPENREMOTE_PROVISION;
    }

    private byte[] BuildOpenRemoteProvisioningCommand(CellularParameters cellularSettings, string provisioningRequestJson)
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
        return BuildCommandFrame(commandAndPayload);
    }

    private void SendCellularTestCommand(CellularParameters? cellularSettings)
    {
        if (!_serialPort.IsOpen)
        {
            return;
        }

        byte[] data = BuildCellularTestCommand(cellularSettings);
        _serialPort.Write(data, 0, data.Length);
        lastCommandSent = Constants.COMMAND_ID_CELLULAR_TEST;
    }

    private byte[] BuildCellularTestCommand(CellularParameters? cellularSettings)
    {
        if (cellularSettings is null)
        {
            return BuildCommandFrame([(byte)Constants.COMMAND_ID_CELLULAR_TEST]);
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
        return BuildCommandFrame(commandAndPayload);
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

    public async Task<bool> SaveStandaloneChangesAsync(int timeoutMs = 3000)
    {
        if (!_serialPort.IsOpen)
        {
            LastCustomCanWriteError = "Controller connection is closed before SAVECHANGES.";
            return false;
        }

        bool accepted = await AwaitStandaloneAckAsync(
            Constants.COMMAND_ID_SAVECHANGES,
            () => SendCommand(Constants.COMMAND_ID_SAVECHANGES),
            timeoutMs);

        if (!accepted)
        {
            LastCustomCanWriteError = "Controller rejected SAVECHANGES after Custom CAN writes.";
            return false;
        }

        LastCustomCanWriteError = null;
        return true;
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

        bool preserveSecureSession = _secureSession.IsSessionOpen;

        await _standaloneRequestLock.WaitAsync();
        _suspendLiveRequestPolling = true;
        try
        {
            if (!preserveSecureSession)
            {
                ResetLogReceiveState(discardSerialInput: true);
            }
            else
            {
                lock (_bufferLock)
                {
                    receivedDataBuffer.Clear();
                }

                foundTrailer1 = false;
                _packetReady = false;
            }

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
            // Align with log-transfer session teardown. In secure mode, avoid
            // clearing secure stream state to keep envelope sequence continuity.
            if (!preserveSecureSession)
            {
                ResetLogReceiveState(discardSerialInput: false);
            }
            else
            {
                lock (_bufferLock)
                {
                    receivedDataBuffer.Clear();
                }

                foundTrailer1 = false;
                _packetReady = false;
            }

            _suspendLiveRequestPolling = false;

            // Resume live polling immediately after a log-list refresh.
            if (_serialPort.IsOpen && !_logTransferSessionActive && !sendingConfig)
            {
                _awaitingPostLogRefreshStatusFrame = true;
                _postLogRefreshRequestRetryCount = 0;
                SendCommand(Constants.COMMAND_ID_REQUEST);
            }

            _standaloneRequestLock.Release();
        }
    }

    public async Task<bool> OpenLogTransferAsync(byte logIndex, int timeoutMs = 2000)
    {
        if (!_serialPort.IsOpen)
        {
            return false;
        }

        _bulkLogTransferStopped = false;
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

        await _standaloneRequestLock.WaitAsync();
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
            _standaloneRequestLock.Release();
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

        bool secure = _secureSession.IsSessionOpen;
        if (secure && !CanUsePlaintextLogBulk)
        {
            throw new InvalidOperationException("The controller does not support authorized plaintext log downloads.");
        }

        await _standaloneRequestLock.WaitAsync(token);
        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;
        _bulkLogCancelRequested = false;
        _bulkLogCancellationDrained = false;
        _bulkLogTransferStopped = false;
        SetBulkTransferReaderActive(true);
        try
        {
            if (!secure)
            {
                ResetLogReceiveState(discardSerialInput: true);
                SendCommand(Constants.COMMAND_ID_LOG_BULK);
            }
            else
            {
                SendCommandWithByte(Constants.COMMAND_ID_LOG_BULK, 1);
            }

            return await ReadLogBulkPacketsAsync(_serialPort.BaseStream, secure, progressCallback, token);
        }
        catch (OperationCanceledException) when (_bulkLogCancellationDrained)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (secure)
            {
                Close();
                ResetSecureSession();
            }
            throw new IOException("Log download stalled. Reconnect to the PDM before retrying.", ex);
        }
        catch
        {
            // A partial plaintext packet cannot be handed back to the encrypted parser.
            if (secure)
            {
                Close();
                ResetSecureSession();
            }
            throw;
        }
        finally
        {
            if (!secure)
            {
                ResetLogReceiveState(discardSerialInput: false);
            }
            SetBulkTransferReaderActive(false);
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;
            _standaloneRequestLock.Release();
        }
    }

    private async Task<string> ReadLogBulkPacketsAsync(Stream stream, bool secure, Action<byte>? progressCallback, CancellationToken token)
    {
        const ushort bulkMagic = 0x4C42;
        byte[] headerBuffer = new byte[6];
        byte[] checksumBuffer = new byte[4];
        byte[] payloadBuffer = new byte[4096];
        using var output = new MemoryStream();
        long lastKeepalive = Environment.TickCount64;
        bool keepalivePending = false;
        bool downloadDone = false;
        bool cancelSent = false;
        // User cancellation must not interrupt a packet; drain through the cancel ACK.
        using var readTimeout = new CancellationTokenSource();
        while (!downloadDone || keepalivePending || cancelSent || token.IsCancellationRequested || _bulkLogCancelRequested)
        {
            if (!cancelSent)
            {
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            }
            CancellationToken readToken = readTimeout.Token;
            if (!cancelSent && (token.IsCancellationRequested || _bulkLogCancelRequested))
            {
                byte[] command = [(byte)Constants.COMMAND_ID_LOG_CANCEL];
                byte[] request = secure
                    ? EncryptSecureEnvelope(command, _secureSession.ClientToDeviceKey,
                        _secureSession.SessionId, _secureSession.TxSequence++)
                    : command;
                await stream.WriteAsync(request, 0, request.Length, readToken);
                cancelSent = true;
            }
            if (secure && !cancelSent && !downloadDone && !keepalivePending && Environment.TickCount64 - lastKeepalive >= 1000)
            {
                byte[] request = EncryptSecureEnvelope(
                    BuildSecureEncryptedControlRequest(SecureControlOperation.Status, Array.Empty<byte>()),
                    _secureSession.ClientToDeviceKey, _secureSession.SessionId, _secureSession.TxSequence++);
                await stream.WriteAsync(request, 0, request.Length, readToken);
                lastKeepalive = Environment.TickCount64;
                keepalivePending = true;
            }

            await ReadExactlyAsync(stream, headerBuffer, 0, 1, readToken);
            if (secure && headerBuffer[0] == (byte)Constants.SECURE_ENVELOPE_FRAME_PREFIX)
            {
                byte[] envelopeHeader = new byte[Constants.SECURE_ENVELOPE_HEADER_LENGTH];
                envelopeHeader[0] = headerBuffer[0];
                await ReadExactlyAsync(stream, envelopeHeader, 1, envelopeHeader.Length - 1, readToken);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(envelopeHeader.AsSpan(1, 2));
                if (length != Constants.SECURE_MANAGEMENT_RESPONSE_BASE_LENGTH && !(cancelSent && length == 1))
                {
                    throw new IOException("Unexpected encrypted response during plaintext log download.");
                }
                byte[] frame = new byte[envelopeHeader.Length + length + Constants.SECURE_TAG_LENGTH];
                envelopeHeader.CopyTo(frame, 0);
                await ReadExactlyAsync(stream, frame, envelopeHeader.Length, length + Constants.SECURE_TAG_LENGTH, readToken);
                if (!TryDecryptSecureEnvelope(frame, _secureSession.DeviceToClientKey, _secureSession.SessionId,
                    _secureSession.RxSequence, out byte[] plaintext))
                {
                    ResetSecureSession();
                    throw new IOException("Log download keepalive authentication failed.");
                }
                _secureSession.RxSequence++;
                if (cancelSent && plaintext.Length == 1)
                {
                    if (keepalivePending || plaintext[0] != (byte)Constants.COMMAND_ID_CONFIM)
                    {
                        throw new IOException("Log download cancellation was not acknowledged.");
                    }
                    CompleteBulkCancellation(token);
                }
                if (!keepalivePending ||
                    !TryParseSecureManagementResponse(plaintext, SecureControlOperation.Status, out SecureManagementResponse? status) ||
                    status == null || status.Result != SecureControlResult.Ok || !status.IsAuthorized || status.StorageFault)
                {
                    throw new IOException("Log download session is no longer authorized.");
                }
                keepalivePending = false;
                continue;
            }
            if (!secure && cancelSent && headerBuffer[0] == (byte)Constants.COMMAND_ID_CONFIM)
            {
                CompleteBulkCancellation(token);
            }
            await ReadExactlyAsync(stream, headerBuffer, 1, headerBuffer.Length - 1, readToken);

            ushort magic = (ushort)(headerBuffer[0] | (headerBuffer[1] << 8));
            if (magic != bulkMagic || downloadDone || headerBuffer[2] > 100 || headerBuffer[3] > 1)
            {
                throw new IOException("Invalid bulk transfer packet header.");
            }

            byte progress = headerBuffer[2];
            bool done = headerBuffer[3] != 0;
            int payloadLength = headerBuffer[4] | (headerBuffer[5] << 8);
            if (payloadLength > payloadBuffer.Length)
            {
                throw new IOException("Invalid bulk transfer payload length.");
            }

            if (payloadLength > 0)
            {
                await ReadExactlyAsync(stream, payloadBuffer, 0, payloadLength, readToken);
            }

            await ReadExactlyAsync(stream, checksumBuffer, 0, checksumBuffer.Length, readToken);
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

            if (!cancelSent && payloadLength > 0)
            {
                output.Write(payloadBuffer, 0, payloadLength);
            }

            if (!cancelSent) progressCallback?.Invoke(progress);
            downloadDone = done;
        }
        _bulkLogTransferStopped = true;
        return Encoding.ASCII.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private void CompleteBulkCancellation(CancellationToken token)
    {
        _bulkLogTransferStopped = true;
        _bulkLogCancellationDrained = true;
        throw new OperationCanceledException("Log download cancelled; controller session preserved.", token);
    }

    public void CancelLogTransfer()
    {
        if (_bulkLogTransferActive)
        {
            _bulkLogCancelRequested = true;
            return;
        }
        if (_bulkLogTransferStopped) return;
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

        bool previousSuspendLiveRequestPolling = _suspendLiveRequestPolling;
        _suspendLiveRequestPolling = true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _standaloneCommandAckTcs = tcs;
        _standaloneCommandAckId = Constants.COMMAND_ID_LOG_RESET;
        try
        {
            SendCommand(Constants.COMMAND_ID_LOG_RESET);
            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask != tcs.Task)
            {
                LoggingService.AddLog("Controller log reset acknowledgement timed out.");
                return false;
            }

            bool resetOk = await tcs.Task;
            if (resetOk && !UseSecureStreamingTransport())
            {
                ResetLogReceiveState(discardSerialInput: true);
            }
            if (!resetOk)
            {
                LoggingService.AddLog("Controller rejected the log reset request.");
            }
            return resetOk;
        }
        finally
        {
            if (_standaloneCommandAckTcs == tcs)
            {
                _standaloneCommandAckTcs = null;
                _standaloneCommandAckId = '\0';
            }
            _suspendLiveRequestPolling = previousSuspendLiveRequestPolling;

            // Kick the live stream loop back into motion after reset completes.
            if (!previousSuspendLiveRequestPolling && _serialPort.IsOpen && !_logTransferSessionActive && !sendingConfig)
            {
                SendCommand(Constants.COMMAND_ID_REQUEST);
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
