using Cortex.Models;
using Cortex.Services;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Cortex.Tests;

public class SerialPortProtocolTests
{
    [Fact]
    public void CustomCanDefinitionsLoadAfterStaticSnapshotWithoutOpeningCanTab()
    {
        using var service = new SerialPortService("COM1");
        service.SetCustomCanStatusPollingEnabled(false);
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10000L, 1500)!);

        SetProperty(service, "HasLoadedStaticSnapshot", true);
        service.UpdateStaticData = false;
        Assert.True((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10000L, 1500)!);

        SetField(service, "_lastCustomCanStatusRequestSentTick", 10000L);
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10200L, 1500)!);
        Assert.True((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10500L, 1500)!);

        SetField(service, "_lastCustomCanStatusFrameTick", 10500L);
        SetProperty(service, "HasLoadedCustomCanSnapshot", true);
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 20000L, 1500)!);
        SetProperty(service, "HasLoadedCustomCanSnapshot", false);
        Assert.True((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 20000L, 1500)!);
        SetProperty(service, "HasLoadedCustomCanSnapshot", true);
        service.SetCustomCanStatusPollingEnabled(true);
        Assert.True((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 20000L, 1500)!);

        InvokeInstanceMethod(service, "MarkControllerSessionLost", true);
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 20000L, 1500)!);
        service.SetCustomCanStatusPollingEnabled(false);
        SetProperty(service, "HasLoadedStaticSnapshot", true);
        service.UpdateStaticData = false;
        Assert.True((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 20000L, 1500)!);
    }

    [Fact]
    public void IoFlowPollingRefreshesLogicAndCanStatusFastWithoutCanTab()
    {
        using var service = new SerialPortService("COM1");
        SetProperty(service, "HasLoadedStaticSnapshot", true);
        SetProperty(service, "HasLoadedCustomCanSnapshot", true);
        service.UpdateStaticData = false;
        service.SetCustomCanStatusPollingEnabled(false);
        SetField(service, "_customCanStatusSupportState", Enum.Parse(
            typeof(SerialPortService).GetNestedType("CustomCanStatusSupportState", BindingFlags.NonPublic)!, "Supported"));
        DataStructures data = GetField<DataStructures>(service, "dataStructures");
        data.ChannelsLiveData[0].ChanType = OutputChannel.ChannelType.MATH;
        service.SetMathStatusPollingChannel(0);
        SetField(service, "_lastMathStatusRequestSentTick", 10000L);
        SetField(service, "_lastCustomCanStatusRequestSentTick", 10000L);

        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10250L, 1500)!);
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestMathStatus", 10250L, 1500)!);

        service.SetIoFlowStatusPollingEnabled(true);
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10150L, 1500)!);
        Assert.True((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10250L, 1500)!);
        Assert.True((bool)InvokeInstanceMethod(service, "ShouldRequestMathStatus", 10250L, 1500)!);

        service.SetIoFlowStatusPollingEnabled(false);
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestMathStatus", 10250L, 1500)!);
    }

    [Fact]
    public void CustomCanBootstrapStopsForUnsupportedFirmware()
    {
        using var service = new SerialPortService("COM1");
        SetProperty(service, "HasLoadedStaticSnapshot", true);
        service.UpdateStaticData = false;
        InvokeInstanceMethod(service, "MarkCustomCanStatusUnsupported");
        Assert.False((bool)InvokeInstanceMethod(service, "ShouldRequestCustomCanStatus", 10000L, 1500)!);
    }

    [Fact]
    public void CustomCanRecords_MatchFirmwarePackedStructLayout()
    {
        byte[] message = SerialPortService.BuildCustomCanMessageRecord(0x18FF1234, 1000, 8, 0x0B, 3);
        Assert.Equal(Constants.CUSTOM_CAN_MESSAGE_RECORD_SIZE, message.Length);
        Assert.Equal(new byte[] { 0x34, 0x12, 0xFF, 0x18, 0xE8, 0x03, 8, 0x0B, 3 }, message);

        byte[] signal = SerialPortService.BuildCustomCanSignalRecord(2, 7, 12, 0x05, 0.5f, -40f, 255);
        Assert.Equal(Constants.CUSTOM_CAN_SIGNAL_RECORD_SIZE, signal.Length);
        Assert.Equal(new byte[] { 2, 7, 12, 0x05 }, signal[..4]);
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(signal.AsSpan(4)));
        Assert.Equal(-40f, BinaryPrimitives.ReadSingleLittleEndian(signal.AsSpan(8)));
        Assert.Equal(255, signal[12]);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProvisioningAndCellularCommands_UseSessionTransportRegardlessOfPin(bool sessionOpen, bool pinEnabled)
    {
        using var service = new SerialPortService("COM1");
        object session = GetField<object>(service, "_secureSession");
        SetProperty(session, "IsSessionOpen", sessionOpen);
        SetProperty(session, "IsAuthorized", sessionOpen);
        SetProperty(session, "PinEnabled", pinEnabled);
        SetProperty(session, "TxSequence", 7u);
        var settings = new CellularParameters
        {
            APN = "test-apn",
            ClientID = "test-pdm",
            UseTLS = true,
            OpenRemotePort = 8883,
            TelemetryUploadMask = 0x12345678,
        };
        const string request = "{\"test\":true}";
        byte[] provisioning = (byte[])InvokeInstanceMethod(service, "BuildOpenRemoteProvisioningCommand", settings, request)!;
        byte[] cellular = (byte[])InvokeInstanceMethod(service, "BuildCellularTestCommand", settings)!;
        byte[] cellularWithoutSettings = (byte[])InvokeInstanceMethod(service, "BuildCellularTestCommand", new object[] { null! })!;

        if (sessionOpen)
        {
            provisioning = DecryptCommandFrame(provisioning, session, 7);
            cellular = DecryptCommandFrame(cellular, session, 8);
            cellularWithoutSettings = DecryptCommandFrame(cellularWithoutSettings, session, 9);
        }

        Assert.Equal((byte)Constants.COMMAND_ID_OPENREMOTE_PROVISION, provisioning[0]);
        Assert.Equal((byte)Constants.COMMAND_ID_CELLULAR_TEST, cellular[0]);
        int settingsLength = Constants.CELLULAR_APN_LENGTH + Constants.CELLULAR_HOST_LENGTH + sizeof(ushort) +
            Constants.CELLULAR_CLIENT_ID_LENGTH + Constants.CELLULAR_MQTT_USERNAME_LENGTH +
            Constants.CELLULAR_MQTT_PASSWORD_LENGTH + Constants.CELLULAR_TOPIC_LENGTH + sizeof(byte);
        Assert.Equal(1 + settingsLength + sizeof(ushort) + request.Length, provisioning.Length);
        Assert.Equal(1 + settingsLength + sizeof(uint), cellular.Length);
        Assert.Equal(provisioning.AsSpan(1, settingsLength).ToArray(), cellular.AsSpan(1, settingsLength).ToArray());
        Assert.Equal(request.Length, BinaryPrimitives.ReadUInt16LittleEndian(provisioning.AsSpan(1 + settingsLength, 2)));
        Assert.Equal(request, Encoding.ASCII.GetString(provisioning.AsSpan(1 + settingsLength + 2)));
        Assert.Equal(settings.TelemetryUploadMask, BinaryPrimitives.ReadUInt32LittleEndian(cellular.AsSpan(1 + settingsLength)));
        Assert.Equal(new byte[] { (byte)Constants.COMMAND_ID_CELLULAR_TEST }, cellularWithoutSettings);
        Assert.Equal(sessionOpen ? 10u : 7u, GetProperty<uint>(session, "TxSequence"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6144)]
    public void Provisioning_RejectsRequestOutsideFirmwareLimit(int requestLength)
    {
        using var service = new SerialPortService("COM1");
        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeInstanceMethod(service, "BuildOpenRemoteProvisioningCommand", new CellularParameters(), new string('x', requestLength)));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("6143", exception.InnerException.Message);
    }

    [Fact]
    public void Provisioning_MaximumRequestFitsSingleEncryptedFirmwarePacket()
    {
        using var service = new SerialPortService("COM1");
        object session = GetField<object>(service, "_secureSession");
        SetProperty(session, "IsSessionOpen", true);
        SetProperty(session, "IsAuthorized", true);
        string request = new('x', 6143);

        byte[] frame = (byte[])InvokeInstanceMethod(service, "BuildOpenRemoteProvisioningCommand", new CellularParameters(), request)!;
        byte[] plaintext = DecryptCommandFrame(frame, session, 0);

        Assert.True(plaintext.Length <= Constants.SECURE_ENVELOPE_MAX_PLAINTEXT_BYTES);
        Assert.Equal(request, Encoding.ASCII.GetString(plaintext.AsSpan(plaintext.Length - request.Length)));
    }

    [Theory]
    [InlineData(Constants.COMMAND_ID_OPENREMOTE_PROVISION)]
    [InlineData(Constants.COMMAND_ID_CELLULAR_TEST)]
    public void OperationalCommand_LockedSessionCannotFallBackToPlaintext(char command)
    {
        using var service = new SerialPortService("COM1");
        object session = GetField<object>(service, "_secureSession");
        SetProperty(session, "IsSessionOpen", true);
        SetProperty(session, "PinEnabled", true);

        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeInstanceMethod(service, "BuildCommandFrame", new byte[] { (byte)command }));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(0u, GetProperty<uint>(session, "TxSequence"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandaloneRequestReset_PreservesFragmentedEncryptedResponse(bool pinEnabled)
    {
        using var service = new SerialPortService("COM1");
        object session = GetField<object>(service, "_secureSession");
        SetProperty(session, "IsSessionOpen", true);
        SetProperty(session, "IsAuthorized", true);
        SetProperty(session, "PinEnabled", pinEnabled);
        SetProperty(session, "RxSequence", 4u);
        byte[] plaintext = Encoding.ASCII.GetBytes("Provisioning: OK");
        MethodInfo encrypt = typeof(SerialPortService).GetMethod("EncryptSecureEnvelope", BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] frame = (byte[])encrypt.Invoke(null, new object[]
        {
            plaintext, GetProperty<byte[]>(session, "DeviceToClientKey"), GetProperty<byte[]>(session, "SessionId"), 4u,
        })!;
        List<byte> incoming = GetField<List<byte>>(service, "_secureIncomingBuffer");
        incoming.AddRange(frame[..10]);

        InvokeInstanceMethod(service, "ResetStandaloneReceiveState", true);
        InvokeInstanceMethod(service, "ResetStandaloneReceiveState", false);
        Assert.Equal(frame[..10], incoming.ToArray());
        incoming.AddRange(frame[10..]);
        object[] args = { Array.Empty<byte>() };
        Assert.True((bool)typeof(SerialPortService)
            .GetMethod("TryReadNextSecureIncomingPayload", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args)!);
        Assert.Equal(plaintext, (byte[])args[0]);
        Assert.Equal(5u, GetProperty<uint>(session, "RxSequence"));
    }

    private static void SetProperty(object instance, string name, object value)
        => instance.GetType().GetProperty(name)!.SetValue(instance, value);

    private static T GetProperty<T>(object instance, string name)
        => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private static byte[] DecryptCommandFrame(byte[] frame, object session, uint sequence)
    {
        Assert.Equal((byte)Constants.SECURE_ENVELOPE_FRAME_PREFIX, frame[0]);
        Assert.Equal(sequence, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(3, 4)));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2));
        Assert.Equal(Constants.SECURE_ENVELOPE_HEADER_LENGTH + length + Constants.SECURE_TAG_LENGTH, frame.Length);
        byte[] nonce = new byte[12];
        GetProperty<byte[]>(session, "SessionId").CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), sequence);
        byte[] plaintext = new byte[length];
        using var cipher = new ChaCha20Poly1305(GetProperty<byte[]>(session, "ClientToDeviceKey"));
        cipher.Decrypt(nonce, frame.AsSpan(7, length), frame.AsSpan(7 + length), plaintext, frame.AsSpan(0, 7));
        return plaintext;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationSave_FinalAckUsesPendingCommandAndRestoresPolling(bool previouslySuspended)
    {
        using var service = new SerialPortService("COM1");
        SetField(service, "_configurationSaveActive", true);
        SetField(service, "_configurationPreviousPollingSuspended", previouslySuspended);
        SetField(service, "_configurationCommandSentTick", Environment.TickCount64);
        SetField(service, "_pendingConfigurationCommand", Constants.COMMAND_ID_SAVECHANGES);
        SetField(service, "_suspendLiveRequestPolling", true);
        SetField(service, "lastCommandSent", Constants.COMMAND_ID_REQUEST);
        bool? result = null;
        service.ConfigurationSaveCompleted += (_, args) => result = args.Succeeded;

        Assert.True((bool)InvokeInstanceMethod(service, "HandleConfigurationAcknowledgement",
            (byte)Constants.COMMAND_ID_CONFIM)!);
        Assert.Equal(true, result);
        Assert.False(GetField<bool>(service, "_configurationSaveActive"));
        Assert.Equal(previouslySuspended, GetField<bool>(service, "_suspendLiveRequestPolling"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationSave_MissingAckTimesOutAndNotifiesFailure(bool previouslySuspended)
    {
        using var service = new SerialPortService("COM1");
        SetField(service, "_configurationSaveActive", true);
        SetField(service, "_configurationPreviousPollingSuspended", previouslySuspended);
        SetField(service, "_configurationCommandSentTick", Environment.TickCount64 - 10001);
        SetField(service, "_suspendLiveRequestPolling", true);
        SetField(service, "sendingConfig", true);
        bool? result = null;
        service.ConfigurationSaveCompleted += (_, args) => result = args.Succeeded;

        InvokeInstanceMethod(service, "CheckConfigurationSaveTimeout");
        Assert.Equal(false, result);
        Assert.False(GetField<bool>(service, "sendingConfig"));
        Assert.False(GetField<bool>(service, "_configurationSaveActive"));
        Assert.Equal(previouslySuspended, GetField<bool>(service, "_suspendLiveRequestPolling"));
    }

    [Fact]
    public void ConfigurationSave_LivePollCannotReplacePendingCommand()
    {
        using var service = new SerialPortService("COM1");
        SetField(service, "_configurationSaveActive", true);
        SetField(service, "_configurationCommandSentTick", Environment.TickCount64);
        SetField(service, "_pendingConfigurationCommand", Constants.COMMAND_ID_SAVECHANGES);
        SetField(service, "lastCommandSent", Constants.COMMAND_ID_SAVECHANGES);
        InvokeInstanceMethod(service, "SendCommand", Constants.COMMAND_ID_REQUEST);
        Assert.Equal(Constants.COMMAND_ID_SAVECHANGES, GetField<char>(service, "lastCommandSent"));
        Assert.Equal(Constants.COMMAND_ID_SAVECHANGES, GetField<char>(service, "_pendingConfigurationCommand"));
    }

    [Theory]
    [InlineData(Constants.COMMAND_ID_NEWCONFIG)]
    [InlineData(Constants.COMMAND_ID_SAVECHANGES)]
    public void ConfigurationAck_SecondFailureReportsFailureAndStopsSave(char command)
    {
        using var service = new SerialPortService("COM1");
        SetField(service, "lastCommandSent", command);
        SetField(service, "sendingConfig", true);
        SetField(service, "saveToEEPROM", true);
        SetField(service, "_configRetryCount", 1);
        SetField(service, "_saveRetryCount", 1);
        bool? succeeded = null;
        service.ConfigurationSaveCompleted += (_, args) => succeeded = args.Succeeded;

        Assert.True((bool)InvokeInstanceMethod(service, "HandleConfigurationAcknowledgement",
            (byte)Constants.COMMAND_ID_CHECKSUM_FAIL)!);
        Assert.Equal(false, succeeded);
        Assert.False(GetField<bool>(service, "sendingConfig"));
        Assert.False(GetField<bool>(service, "saveToEEPROM"));
    }

    [Theory]
    [InlineData(Constants.COMMAND_ID_CONFIM, true)]
    [InlineData(Constants.COMMAND_ID_CHECKSUM_FAIL, false)]
    public async Task ConfigurationAck_DoesNotAdvanceBulkSaveForStandaloneWrite(char response, bool success)
    {
        using var service = new SerialPortService("COM1");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(service, "_standaloneCommandAckTcs", completion);
        SetField(service, "_standaloneCommandAckId", Constants.COMMAND_ID_NEWCONFIG);
        SetField(service, "lastCommandSent", Constants.COMMAND_ID_NEWCONFIG);
        Assert.True((bool)InvokeInstanceMethod(service, "HandleConfigurationAcknowledgement", (byte)response)!);
        Assert.Equal(success, await completion.Task);
        Assert.Equal(0, GetField<int>(service, "_configPacketsSent"));
    }

    [Theory]
    [InlineData(Constants.COMMAND_ID_CONFIM, Constants.COMMAND_ID_LOG_RESET, true)]
    [InlineData(Constants.COMMAND_ID_CHECKSUM_FAIL, Constants.COMMAND_ID_LOG_RESET, false)]
    [InlineData(Constants.COMMAND_ID_CONFIM, Constants.COMMAND_ID_REQUEST, true)]
    [InlineData(Constants.COMMAND_ID_CHECKSUM_FAIL, Constants.COMMAND_ID_REQUEST, false)]
    public async Task LogReset_SharedAckHandler_UsesPendingRequestDespiteLivePolling(char response, char lastCommand, bool expected)
    {
        using var service = new SerialPortService("COM1");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(service, "_standaloneCommandAckTcs", completion);
        SetField(service, "_standaloneCommandAckId", Constants.COMMAND_ID_LOG_RESET);
        SetField(service, "lastCommandSent", lastCommand);

        Assert.False((bool)InvokeInstanceMethod(service, "TryHandleStandaloneAckByte", (byte)'!')!);
        Assert.False(completion.Task.IsCompleted);
        Assert.True((bool)InvokeInstanceMethod(service, "TryHandleStandaloneAckByte", (byte)response)!);
        Assert.Equal(expected, await completion.Task);
    }

    [Fact]
    public async Task SecureManagementResponse_IsDemultiplexedFromStreamedPayloads()
    {
        using var service = new SerialPortService("COM1");
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(service, "_secureManagementResponseTcs", completion);
        SetField(service, "_secureManagementExpectedOperation", (byte)SecureControlOperation.Disable);

        byte[] liveData = new byte[Constants.SECURE_MANAGEMENT_RESPONSE_BASE_LENGTH];
        liveData[0] = (byte)Constants.COMMAND_ID_REQUEST;
        byte[] otherOperation = BuildManagementPayload(SecureControlOperation.Status);
        byte[] expected = BuildManagementPayload(SecureControlOperation.Disable);

        Assert.False((bool)InvokeInstanceMethod(service, "TryCompleteSecureManagementResponse", liveData)!);
        Assert.False((bool)InvokeInstanceMethod(service, "TryCompleteSecureManagementResponse", otherOperation)!);
        Assert.False(completion.Task.IsCompleted);
        Assert.True((bool)InvokeInstanceMethod(service, "TryCompleteSecureManagementResponse", expected)!);
        Assert.Same(expected, await completion.Task);
        Assert.False((bool)InvokeInstanceMethod(service, "TryCompleteSecureManagementResponse", expected)!);
    }

    [Fact]
    public async Task SecureSessionReset_FailsPendingManagementRequestImmediately()
    {
        using var service = new SerialPortService("COM1");
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(service, "_secureManagementResponseTcs", completion);
        SetField(service, "_secureManagementExpectedOperation", (byte)SecureControlOperation.SetPin);

        InvokeInstanceMethod(service, "ResetSecureSession");

        Assert.Null(await completion.Task);
        Assert.False(service.IsSecureSessionOpen);
    }

    private static byte[] BuildManagementPayload(SecureControlOperation operation)
    {
        byte[] payload = new byte[Constants.SECURE_MANAGEMENT_RESPONSE_BASE_LENGTH];
        payload[0] = (byte)Constants.SECURE_CONTROL_FRAME_PREFIX;
        payload[1] = (byte)operation;
        payload[2] = (byte)SecureControlResult.Ok;
        return payload;
    }

    [Theory]
    [InlineData(OutputChannel.MathOperandType.Constant, 0)]
    [InlineData(OutputChannel.MathOperandType.Channel, 1)]
    [InlineData(OutputChannel.MathOperandType.AnalogueInput, 2)]
    [InlineData(OutputChannel.MathOperandType.SystemValue, 3)]
    [InlineData(OutputChannel.MathOperandType.Rule, 4)]
    [InlineData(OutputChannel.MathOperandType.DigitalInput, 5)]
    [InlineData(OutputChannel.MathOperandType.CanSignal, 6)]
    public void MathOperandType_MatchesControllerProtocol(OutputChannel.MathOperandType operandType, int expected)
    {
        Assert.Equal((byte)expected, (byte)operandType);
    }

    [Fact]
    public void ExpectsFramedResponse_IsTrue_ForProvisioningAndLiveDataCommands()
    {
        Assert.True(InvokeExpectsFramedResponse(Constants.COMMAND_ID_OPENREMOTE_PROVISION));
        Assert.True(InvokeExpectsFramedResponse(Constants.COMMAND_ID_REQUEST));
    }

    [Fact]
    public void ExpectsFramedResponse_IsFalse_ForStandaloneAcknowledgeCommands()
    {
        Assert.False(InvokeExpectsFramedResponse(Constants.COMMAND_ID_BEGIN));
        Assert.False(InvokeExpectsFramedResponse(Constants.COMMAND_ID_SAVECHANGES));
    }

    [Fact]
    public void ExpectsFramedResponse_IsTrue_ForMathStatusCommand()
    {
        Assert.True(InvokeExpectsFramedResponse(Constants.COMMAND_ID_MATH_STATUS));
    }

    [Fact]
    public void ExpectsFramedResponse_IsTrue_ForDigitalInputStatusCommand()
    {
        Assert.True(InvokeExpectsFramedResponse(Constants.COMMAND_ID_DIGITAL_INPUT_STATUS));
    }

    [Fact]
    public void ExpectsFramedResponse_IsTrue_ForCustomCanStatusCommand()
    {
        Assert.True(InvokeExpectsFramedResponse(Constants.COMMAND_ID_CUSTOM_CAN_STATUS));
    }

    [Fact]
    public void ParseCustomCanStatusPacket_ClearsUnusedSlotsAfterShortSnapshot()
    {
        SerialPortService service = CreateService();

        try
        {
            DataStructures data = GetField<DataStructures>(service, "dataStructures");

            data.CustomCanMessagesLiveData[1].Flags = 0x01;
            data.CustomCanMessagesLiveData[1].Id = 0x555;
            data.CustomCanSignalsLiveData[1].Flags = 0x01;
            data.CustomCanSignalsLiveData[1].MessageIndex = 1;
            data.CustomCanSignalsStaticData[1].Flags = 0x01;

            byte[] frame = BuildCustomCanStatusFrame(
                crcValid: 1,
                messageRecords:
                [
                    (0x18FF50E5u, (ushort)1500, (byte)8, (byte)0x03, (byte)CustomCanStatus.Valid),
                ],
                signalRecords:
                [
                    ((byte)0, (byte)16, (byte)12, (byte)0x01, 0.5f, -10.0f, 24.5f, (byte)CustomCanStatus.Valid),
                ]);

            SetField(service, "dataBytes", frame);
            SetField(service, "dataLength", frame.Length);

            bool parsed = (bool)InvokeInstanceMethod(service, "ParseCustomCanStatusPacket")!;

            Assert.True(parsed);
            Assert.Equal((uint)0x18FF50E5, data.CustomCanMessagesStaticData[0].Id);
            Assert.Equal((byte)0x03, data.CustomCanMessagesStaticData[0].Flags);
            Assert.Equal((byte)CustomCanStatus.Valid, data.CustomCanSignalsStaticData[0].Status);

            Assert.Equal((byte)0, data.CustomCanMessagesStaticData[1].Flags);
            Assert.Equal((byte)CustomCanStatus.Disabled, data.CustomCanMessagesStaticData[1].Status);
            Assert.Equal((byte)0, data.CustomCanSignalsStaticData[1].Flags);
            Assert.Equal((byte)CustomCanStatus.Disabled, data.CustomCanSignalsStaticData[1].Status);
            Assert.Equal(string.Empty, data.CustomCanSignalsStaticData[1].MessageName);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void ParseCustomCanStatusPacket_DoesNotEnableSlotsWithDisabledStatus()
    {
        SerialPortService service = CreateService();

        try
        {
            DataStructures data = GetField<DataStructures>(service, "dataStructures");

            byte[] frame = BuildCustomCanStatusFrame(
                crcValid: 1,
                messageRecords:
                [
                    (0x321u, (ushort)500, (byte)8, (byte)0x03, (byte)CustomCanStatus.Disabled),
                ],
                signalRecords:
                [
                    ((byte)0, (byte)8, (byte)8, (byte)0x01, 1.0f, 0.0f, 0.0f, (byte)CustomCanStatus.Disabled),
                ]);

            SetField(service, "dataBytes", frame);
            SetField(service, "dataLength", frame.Length);

            bool parsed = (bool)InvokeInstanceMethod(service, "ParseCustomCanStatusPacket")!;

            Assert.True(parsed);
            Assert.False(data.CustomCanMessagesStaticData[0].IsEnabled);
            Assert.False(data.CustomCanSignalsStaticData[0].IsEnabled);
            Assert.Equal((byte)0x02, data.CustomCanMessagesStaticData[0].Flags);
            Assert.Equal((byte)0x00, data.CustomCanSignalsStaticData[0].Flags);
            Assert.Equal((byte)CustomCanStatus.Disabled, data.CustomCanMessagesStaticData[0].Status);
            Assert.Equal((byte)CustomCanStatus.Disabled, data.CustomCanSignalsStaticData[0].Status);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void ProcessData_ParsesDigitalInputStatusFrame()
    {
        SerialPortService service = CreateService();

        try
        {
            SetReceivedDataBuffer(service, BuildDigitalInputStatusFrame(0, 1, 0, 1, 0, 1, 0, 1));

            Assert.True((bool)InvokeInstanceMethod(service, "TryDequeueNextFramedPacket")!);
            Assert.True((bool)InvokeInstanceMethod(service, "ParseDigitalInputStatusPacket")!);

            DataStructures data = GetField<DataStructures>(service, "dataStructures");
            Assert.False(data.DigitalInputsLiveData[0].LiveElectricalState);
            Assert.True(data.DigitalInputsLiveData[1].LiveElectricalState);
            Assert.Equal("Supported", GetField<object>(service, "_digitalInputStatusSupportState").ToString());
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void ProcessData_RejectsDigitalInputStatusFrameWithBadChecksum()
    {
        SerialPortService service = CreateService();

        try
        {
            byte[] frame = BuildDigitalInputStatusFrame(1, 1, 1, 1, 1, 1, 1, 1);
            frame[^1]++;
            SetReceivedDataBuffer(service, frame);

            bool processed = (bool)InvokeInstanceMethod(service, "processData")!;
            DataStructures data = GetField<DataStructures>(service, "dataStructures");

            Assert.False(processed);
            Assert.All(data.DigitalInputsLiveData, input => Assert.Null(input.LiveElectricalState));
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void ProcessData_WaitsForCompleteDigitalInputStatusFrameAcrossFragments()
    {
        SerialPortService service = CreateService();

        try
        {
            byte[] frame = BuildDigitalInputStatusFrame(1, 0, 1, 0, 1, 0, 1, 0);
            AppendReceivedDataBuffer(service, frame[..7]);
            Assert.False((bool)InvokeInstanceMethod(service, "TryDequeueNextFramedPacket")!);

            AppendReceivedDataBuffer(service, frame[7..]);
            Assert.True((bool)InvokeInstanceMethod(service, "TryDequeueNextFramedPacket")!);
            Assert.True((bool)InvokeInstanceMethod(service, "ParseDigitalInputStatusPacket")!);

            DataStructures data = GetField<DataStructures>(service, "dataStructures");
            Assert.True(data.DigitalInputsLiveData[0].LiveElectricalState);
            Assert.False(data.DigitalInputsLiveData[1].LiveElectricalState);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void ParseDigitalInputStatusPacket_RejectsInvalidStateValues()
    {
        SerialPortService service = CreateService();

        try
        {
            byte[] frame = BuildDigitalInputStatusFrame(0, 1, 2, 1, 0, 1, 0, 1);
            SetField(service, "dataBytes", frame);
            SetField(service, "dataLength", frame.Length);

            bool parsed = (bool)InvokeInstanceMethod(service, "ParseDigitalInputStatusPacket")!;

            Assert.False(parsed);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void UpdateDigitalInputStatusSupport_MarksUnsupportedAfterProbeTimeout()
    {
        SerialPortService service = CreateService();

        try
        {
            DataStructures data = GetField<DataStructures>(service, "dataStructures");
            data.DigitalInputsLiveData[0].LiveElectricalState = true;
            SetField(service, "_lastDigitalInputStatusRequestSentTick", Environment.TickCount64 - 4000);

            InvokeInstanceMethod(service, "UpdateDigitalInputStatusSupport", Environment.TickCount64, 1500);

            Assert.Equal("Unsupported", GetField<object>(service, "_digitalInputStatusSupportState").ToString());
            Assert.Null(data.DigitalInputsLiveData[0].LiveElectricalState);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void MarkControllerSessionLost_ResetsDigitalInputStatusSupport()
    {
        SerialPortService service = CreateService();

        try
        {
            SetReceivedDataBuffer(service, BuildDigitalInputStatusFrame(1, 1, 1, 1, 1, 1, 1, 1));
            Assert.True((bool)InvokeInstanceMethod(service, "TryDequeueNextFramedPacket")!);
            Assert.True((bool)InvokeInstanceMethod(service, "ParseDigitalInputStatusPacket")!);

            InvokeInstanceMethod(service, "MarkControllerSessionLost", true);
            DataStructures data = GetField<DataStructures>(service, "dataStructures");

            Assert.Equal("Unknown", GetField<object>(service, "_digitalInputStatusSupportState").ToString());
            Assert.All(data.DigitalInputsLiveData, input => Assert.Null(input.LiveElectricalState));
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void AdvanceConfigCursor_ForMathRuleParameters_WalksRuleSlotsBeforeNextParameter()
    {
        SerialPortService service = CreateService();

        try
        {
            SetField(service, "settingIndex", 0);
            SetField(service, "parameterIndex", 36);
            SetField(service, "mathRuleIndex", 0);
            SetField(service, "channelIndex", 0);
            SetField(service, "sendingConfig", true);
            SetField(service, "_controllerCapabilities", ControllerProtocolCapabilities.V010);

            bool advanced = (bool)InvokeInstanceMethod(service, "AdvanceConfigCursor")!;

            Assert.True(advanced);
            Assert.Equal(36, GetField<int>(service, "parameterIndex"));
            Assert.Equal(1, GetField<int>(service, "mathRuleIndex"));

            for (int i = 1; i < Constants.NUM_MATH_RULE_SLOTS; i++)
            {
                advanced = (bool)InvokeInstanceMethod(service, "AdvanceConfigCursor")!;
            }

            Assert.True(advanced);
            Assert.Equal(37, GetField<int>(service, "parameterIndex"));
            Assert.Equal(0, GetField<int>(service, "mathRuleIndex"));
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void StandaloneReadbackPreservesLoadedControllerSnapshots()
    {
        using var service = new SerialPortService("COM1");
        SetProperty(service, "HasLoadedStaticSnapshot", true);
        SetProperty(service, "HasLoadedMathSnapshot", true);
        SetProperty(service, "HasLoadedCustomCanSnapshot", true);
        service.UpdateStaticData = false;

        InvokeInstanceMethod(service, "ResetStandaloneReceiveState", true);
        InvokeInstanceMethod(service, "ResetStandaloneReceiveState", false);

        Assert.True(service.HasLoadedStaticSnapshot);
        Assert.True(service.HasLoadedMathSnapshot);
        Assert.True(service.HasLoadedCustomCanSnapshot);
        Assert.False(service.UpdateStaticData);
        InvokeInstanceMethod(service, "MarkControllerSessionLost", true);
        Assert.False(service.HasLoadedCustomCanSnapshot);
    }

    [Fact]
    public void StandaloneReadbackPreservesPendingFreshMathRefresh()
    {
        using var service = new SerialPortService("COM1");
        SetField(service, "_awaitingStaticMathSnapshot", true);
        service.UpdateStaticData = true;

        InvokeInstanceMethod(service, "ResetStandaloneReceiveState", true);
        InvokeInstanceMethod(service, "ResetStandaloneReceiveState", false);

        Assert.True(GetField<bool>(service, "_awaitingStaticMathSnapshot"));
        Assert.True(service.UpdateStaticData);
        Assert.False(service.HasLoadedMathSnapshot);
    }

    [Fact]
    public void StaticMathRefreshWaitsForAFreshMathPacketInsteadOfReloadingDeletedRules()
    {
        using var service = new SerialPortService("COM1");
        DataStructures data = GetField<DataStructures>(service, "dataStructures");
        data.ChannelsLiveData[0].MathRuleCount = 3;
        data.ChannelsLiveData[0].MathRules[1].OperandAConstant = 22;
        byte[] staticPacket = new byte[ControllerProtocolCapabilities.V010StaticPacketMinimumLength];
        staticPacket[3] = Constants.NUM_OUTPUT_CHANNELS;
        staticPacket[4] = (byte)OutputChannel.ChannelType.MATH;
        SetField(service, "dataBytes", staticPacket);
        SetField(service, "dataLength", staticPacket.Length);

        Assert.True((bool)InvokeInstanceMethod(service, "ParseStaticStatusPacket")!);
        Assert.True(service.UpdateStaticData);
        Assert.False(service.HasLoadedStaticSnapshot);
        Assert.False(service.HasLoadedMathSnapshot);

        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(new byte[] { Constants.SERIAL_HEADER1, Constants.SERIAL_HEADER2, (byte)Constants.COMMAND_ID_MATH_STATUS, Constants.NUM_OUTPUT_CHANNELS });
        for (int channelIndex = 0; channelIndex < Constants.NUM_OUTPUT_CHANNELS; channelIndex++)
        {
            writer.Write((byte)(channelIndex == 0 ? OutputChannel.ChannelType.MATH : OutputChannel.ChannelType.Digital));
            writer.Write((byte)1);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(0f);
            writer.Write((byte)0);
            writer.Write((byte)1);
            for (int ruleIndex = 0; ruleIndex < Constants.NUM_MATH_RULE_SLOTS; ruleIndex++)
            {
                writer.Write((byte)OutputChannel.MathOperation.ADD);
                writer.Write((byte)0);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write((byte)OutputChannel.MathOperandType.Constant);
                writer.Write((byte)0);
                writer.Write(channelIndex == 0 && ruleIndex == 0 ? 19f : 0f);
                writer.Write((byte)OutputChannel.MathOperandType.Constant);
                writer.Write((byte)0);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write((byte)0);
                writer.Write((byte)0);
            }
        }
        writer.Write(new byte[4]);
        byte[] mathPacket = stream.ToArray();
        SetField(service, "dataBytes", mathPacket);
        SetField(service, "dataLength", mathPacket.Length);
        Assert.True((bool)InvokeInstanceMethod(service, "ParseMathStatusPacket")!);

        Assert.True(service.HasLoadedStaticSnapshot);
        Assert.True(service.HasLoadedMathSnapshot);
        Assert.False(service.UpdateStaticData);
        Assert.Equal((byte)1, data.ChannelsStaticData[0].MathRuleCount);
        Assert.Equal(19, data.ChannelsStaticData[0].MathRules[0].OperandAConstant);
        Assert.Equal(0, data.ChannelsStaticData[0].MathRules[1].OperandAConstant);
    }

    [Fact]
    public async System.Threading.Tasks.Task DeletedFlowRuleIsEncodedInTheControllerSavePacket()
    {
        using var service = new SerialPortService("COM1");
        var settings = new DataStructures();
        var channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 2;
        var graph = new Cortex.ViewModels.IoFlow.IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();
        var rule = graph.MathRuleNodes.Single(node => node.OwnerChannelNumber == channel.ChannelNumber && node.RuleIndex == 1);
        graph.SelectedNodes.Add(rule);
        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);
        service.UpdateSettingsData(settings);
        DataStructures data = GetField<DataStructures>(service, "dataStructures");
        data.ChannelsLiveData[0].MathRuleCount = 2;
        SetField(service, "_controllerCapabilities", ControllerProtocolCapabilities.V010);
        SetField(service, "sendingConfig", true);
        SetField(service, "settingIndex", 0);
        SetField(service, "parameterIndex", 33);
        SetField(service, "channelIndex", 0);

        InvokeInstanceMethod(service, "SendConfig");

        Assert.Equal(new byte[] { Constants.SERIAL_HEADER1, Constants.SERIAL_HEADER2, 0, 33, 0, 1 },
            GetSendBuffer(service).Take(6).ToArray());
    }

    [Fact]
    public void AddUInt16ConfigIfChanged_EncodesLittleEndianPayloadAfterConfigHeader()
    {
        SerialPortService service = CreateService();

        try
        {
            SetField(service, "settingIndex", 4);
            SetField(service, "parameterIndex", 7);

            bool changed = (bool)InvokeInstanceMethod(
                service,
                "AddUInt16ConfigIfChanged",
                (ushort)1883,
                (ushort)8883,
                (byte)0,
                false)!;

            List<byte> sendBuffer = GetSendBuffer(service);

            Assert.True(changed);
            Assert.Equal(new byte[] { 4, 7, 0, 0xB3, 0x22 }, sendBuffer.ToArray());
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void AddStringConfigIfChanged_WritesFixedLengthAsciiWithNullPadding()
    {
        SerialPortService service = CreateService();

        try
        {
            SetField(service, "settingIndex", 4);
            SetField(service, "parameterIndex", 6);

            bool changed = (bool)InvokeInstanceMethod(
                service,
                "AddStringConfigIfChanged",
                string.Empty,
                "ab",
                8,
                false)!;

            List<byte> sendBuffer = GetSendBuffer(service);

            Assert.True(changed);
            Assert.Equal(new byte[] { 4, 6, 0, (byte)'a', (byte)'b', 0, 0, 0, 0, 0, 0 }, sendBuffer.ToArray());
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void AddStringConfigIfChanged_TruncatesToFieldLengthMinusOneAndKeepsTerminatorSpace()
    {
        SerialPortService service = CreateService();

        try
        {
            SetField(service, "settingIndex", 4);
            SetField(service, "parameterIndex", 6);

            bool changed = (bool)InvokeInstanceMethod(
                service,
                "AddStringConfigIfChanged",
                string.Empty,
                "123456789",
                5,
                false)!;

            List<byte> sendBuffer = GetSendBuffer(service);

            Assert.True(changed);
            Assert.Equal(new byte[] { 4, 6, 0, (byte)'1', (byte)'2', (byte)'3', (byte)'4', 0 }, sendBuffer.ToArray());
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void AddByteConfigIfChanged_SendsCellularProtocol_WhenSavingNewProtocol()
    {
        SerialPortService service = CreateService();

        try
        {
            SetField(service, "settingIndex", 4);
            SetField(service, "parameterIndex", 1);

            bool changed = (bool)InvokeInstanceMethod(
                service,
                "AddByteConfigIfChanged",
                (byte)Constants.CELLULAR_PROTOCOL_MQTT,
                (byte)2,
                (byte)0,
                false)!;

            List<byte> sendBuffer = GetSendBuffer(service);

            Assert.True(changed);
            Assert.Equal(new byte[] { 4, 1, 0, 2, 0, 0 }, sendBuffer.ToArray());
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void AddByteConfigIfChanged_DoesNotSendCellularProtocol_WhenProtocolUnchanged()
    {
        SerialPortService service = CreateService();

        try
        {
            SetField(service, "settingIndex", 4);
            SetField(service, "parameterIndex", 1);

            bool changed = (bool)InvokeInstanceMethod(
                service,
                "AddByteConfigIfChanged",
                (byte)Constants.CELLULAR_PROTOCOL_MQTT,
                (byte)Constants.CELLULAR_PROTOCOL_MQTT,
                (byte)0,
                false)!;

            List<byte> sendBuffer = GetSendBuffer(service);

            Assert.False(changed);
            Assert.Empty(sendBuffer);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void AddByteConfigIfChanged_SendsCellularProtocol_WhenForcedDuringProvisioningFlow()
    {
        SerialPortService service = CreateService();

        try
        {
            SetField(service, "settingIndex", 4);
            SetField(service, "parameterIndex", 1);

            bool changed = (bool)InvokeInstanceMethod(
                service,
                "AddByteConfigIfChanged",
                (byte)Constants.CELLULAR_PROTOCOL_MQTT,
                (byte)Constants.CELLULAR_PROTOCOL_MQTT,
                (byte)0,
                true)!;

            List<byte> sendBuffer = GetSendBuffer(service);

            Assert.True(changed);
            Assert.Equal(new byte[]
            {
                4,
                1,
                0,
                Constants.CELLULAR_PROTOCOL_MQTT,
                0,
                0,
            }, sendBuffer.ToArray());
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void TryBuildCustomCanPayload_EncodesMessageTimeoutAsUInt32LittleEndian()
    {
        byte[] payload = InvokeBuildCustomCanPayload(Constants.CONFIG_DATA_CUSTOM_CAN_MESSAGE, parameterIndex: 1, value: 1500);

        Assert.Equal(new byte[] { 0xDC, 0x05, 0x00, 0x00 }, payload);
    }

    [Fact]
    public void TryBuildCustomCanPayload_EncodesSignalHeaderFieldsAsUInt32LittleEndian()
    {
        byte[] payload = InvokeBuildCustomCanPayload(Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL, parameterIndex: 2, value: 64);

        Assert.Equal(new byte[] { 64, 0x00, 0x00, 0x00 }, payload);
    }

    [Fact]
    public void ParseCustomCanStatusPacket_ParsesVersion2TxFields()
    {
        SerialPortService service = CreateService();

        try
        {
            DataStructures data = GetField<DataStructures>(service, "dataStructures");

            byte[] frame = BuildCustomCanStatusFrameV2(
                crcValid: 1,
                messageRecords:
                [
                    // Enabled periodic TX message triggered by channel 4 (index 3).
                    (0x801u, (ushort)100, (byte)7, (byte)0x0D, (byte)3, (byte)CustomCanStatus.Disabled),
                ],
                signalRecords:
                [
                    // Enabled constant-source signal on the TX message.
                    ((byte)0, (byte)48, (byte)4, (byte)0x01, 1.0f, 15.0f, (byte)255, 0.0f, (byte)CustomCanStatus.Disabled),
                ]);

            SetField(service, "dataBytes", frame);
            SetField(service, "dataLength", frame.Length);

            bool parsed = (bool)InvokeInstanceMethod(service, "ParseCustomCanStatusPacket")!;

            Assert.True(parsed);
            CustomCanMessage message = data.CustomCanMessagesStaticData[0];
            Assert.Equal((uint)0x801, message.Id);
            Assert.Equal((byte)0x0D, message.Flags);
            Assert.Equal((byte)3, message.TxTrigger);
            Assert.True(message.IsTx);
            Assert.True(message.IsPeriodic);
            Assert.True(message.IsEnabled);
            Assert.Equal("TX periodic", message.DirectionLabel);
            Assert.Equal("CH4", message.TriggerLabel);
            Assert.Equal("100 ms", message.PeriodDisplay);

            CustomCanSignal signal = data.CustomCanSignalsStaticData[0];
            // Signals on TX messages report Disabled status but keep their configured enable flag.
            Assert.True(signal.IsEnabled);
            Assert.Equal((byte)255, signal.ValueSource);
            Assert.True(signal.IsConstantSource);
            Assert.Same(message, signal.OwnerMessage);
            Assert.Equal("Constant", signal.ValueSourceDisplay);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void ParseCustomCanStatusPacket_RejectsUnknownVersion()
    {
        SerialPortService service = CreateService();

        try
        {
            byte[] frame = BuildCustomCanStatusFrameV2(
                crcValid: 1,
                messageRecords: [],
                signalRecords: [],
                version: 3);

            SetField(service, "dataBytes", frame);
            SetField(service, "dataLength", frame.Length);

            bool parsed = (bool)InvokeInstanceMethod(service, "ParseCustomCanStatusPacket")!;

            Assert.False(parsed);
        }
        finally
        {
            StopProcessTimer(service);
        }
    }

    [Fact]
    public void TryBuildCustomCanPayload_AcceptsTxTriggerAndValueSourceParameters()
    {
        byte[] triggerPayload = InvokeBuildCustomCanPayload(
            Constants.CONFIG_DATA_CUSTOM_CAN_MESSAGE, Constants.CUSTOM_CAN_MESSAGE_PARAM_TX_TRIGGER, value: 3);
        byte[] sourcePayload = InvokeBuildCustomCanPayload(
            Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL, Constants.CUSTOM_CAN_SIGNAL_PARAM_VALUE_SOURCE, value: 255);

        Assert.Equal(new byte[] { 3, 0x00, 0x00, 0x00 }, triggerPayload);
        Assert.Equal(new byte[] { 255, 0x00, 0x00, 0x00 }, sourcePayload);
    }

    [Theory]
    [InlineData(Constants.CONFIG_DATA_CUSTOM_CAN_MESSAGE, 4)]
    [InlineData(Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL, 6)]
    [InlineData(Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL, 7)]
    public void TryBuildCustomCanPayload_RejectsReadOnlyParameters(byte configType, int parameterIndex)
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("TryBuildCustomCanPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find TryBuildCustomCanPayload.");

        object?[] args = { configType, parameterIndex, (uint)1, null };

        bool accepted = (bool)(method.Invoke(null, args)
            ?? throw new InvalidOperationException("TryBuildCustomCanPayload returned null."));

        Assert.False(accepted);
    }

    [Fact]
    public void TryBuildCustomCanPayload_RejectsInvalidParameterIndex()
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("TryBuildCustomCanPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find TryBuildCustomCanPayload.");

        object?[] args =
        {
            Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL,
            9,
            (uint)300,
            null,
        };

        bool accepted = (bool)(method.Invoke(null, args)
            ?? throw new InvalidOperationException("TryBuildCustomCanPayload returned null."));

        Assert.False(accepted);
    }

    [Fact]
    public void BuildStandaloneConfigPacket_UsesConfigParameterDataIndexOrder_AndLittleEndianChecksum()
    {
        byte[] packet = InvokeBuildStandaloneConfigPacket(
            Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL,
            parameterIndex: 4,
            dataIndex: 2,
            payload: new byte[] { 0x11, 0x22, 0x33, 0x44 });

        Assert.Equal(new byte[]
        {
            Constants.SERIAL_HEADER1,
            Constants.SERIAL_HEADER2,
            Constants.CONFIG_DATA_CUSTOM_CAN_SIGNAL,
            4,
            2,
            0x11,
            0x22,
            0x33,
            0x44,
            Constants.SERIAL_TRAILER1,
            Constants.SERIAL_TRAILER2,
            0x97,
            0x01,
            0x00,
            0x00,
        }, packet);
    }

    [Fact]
    public void SecureHandshake_X25519KeyPairGeneration_IsAvailable()
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("TryGenerateX25519KeyPair", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find TryGenerateX25519KeyPair.");

        object?[] args =
        {
            null,
            null,
        };

        bool generated = (bool)(method.Invoke(null, args)
            ?? throw new InvalidOperationException("TryGenerateX25519KeyPair returned null."));

        Assert.True(generated);
        Assert.NotNull(args[0]);
        Assert.IsType<byte[]>(args[1]);
        Assert.Equal(Constants.SECURE_DEVICE_PUBLIC_KEY_BYTES, ((byte[])args[1]!).Length);

        ((IDisposable?)args[0])?.Dispose();
    }

    [Fact]
    public void SecureHandshake_X25519SharedSecretDerivation_SucceedsForValidPeerPublicKeys()
    {
        MethodInfo generateMethod = typeof(SerialPortService).GetMethod("TryGenerateX25519KeyPair", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find TryGenerateX25519KeyPair.");
        MethodInfo deriveMethod = typeof(SerialPortService).GetMethod("TryDeriveX25519SharedSecret", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find TryDeriveX25519SharedSecret.");

        object?[] leftArgs = { null, null };
        object?[] rightArgs = { null, null };

        bool leftGenerated = (bool)(generateMethod.Invoke(null, leftArgs)
            ?? throw new InvalidOperationException("TryGenerateX25519KeyPair returned null for left key."));
        bool rightGenerated = (bool)(generateMethod.Invoke(null, rightArgs)
            ?? throw new InvalidOperationException("TryGenerateX25519KeyPair returned null for right key."));

        try
        {
            Assert.True(leftGenerated);
            Assert.True(rightGenerated);

            IDisposable leftKey = (IDisposable)(leftArgs[0]
                ?? throw new InvalidOperationException("Left key was null."));
            IDisposable rightKey = (IDisposable)(rightArgs[0]
                ?? throw new InvalidOperationException("Right key was null."));
            byte[] leftPublicKey = (byte[])(leftArgs[1]
                ?? throw new InvalidOperationException("Left public key was null."));
            byte[] rightPublicKey = (byte[])(rightArgs[1]
                ?? throw new InvalidOperationException("Right public key was null."));

            object?[] deriveLeftArgs = { leftKey, rightPublicKey, null };
            object?[] deriveRightArgs = { rightKey, leftPublicKey, null };

            bool leftDerived = (bool)(deriveMethod.Invoke(null, deriveLeftArgs)
                ?? throw new InvalidOperationException("TryDeriveX25519SharedSecret returned null for left key."));
            bool rightDerived = (bool)(deriveMethod.Invoke(null, deriveRightArgs)
                ?? throw new InvalidOperationException("TryDeriveX25519SharedSecret returned null for right key."));

            Assert.True(leftDerived);
            Assert.True(rightDerived);
            Assert.IsType<byte[]>(deriveLeftArgs[2]);
            Assert.IsType<byte[]>(deriveRightArgs[2]);
            Assert.Equal((byte[])deriveLeftArgs[2]!, (byte[])deriveRightArgs[2]!);
        }
        finally
        {
            ((IDisposable?)leftArgs[0])?.Dispose();
            ((IDisposable?)rightArgs[0])?.Dispose();
        }
    }

    private static SerialPortService CreateService() => new("COM1");

    private static bool InvokeExpectsFramedResponse(char commandId)
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("ExpectsFramedResponse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find ExpectsFramedResponse.");

        return (bool)(method.Invoke(null, new object[] { commandId })
            ?? throw new InvalidOperationException("ExpectsFramedResponse returned null."));
    }

    private static byte[] InvokeBuildCustomCanPayload(byte configType, int parameterIndex, uint value)
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("TryBuildCustomCanPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find TryBuildCustomCanPayload.");

        object?[] args =
        {
            configType,
            parameterIndex,
            value,
            null,
        };

        bool accepted = (bool)(method.Invoke(null, args)
            ?? throw new InvalidOperationException("TryBuildCustomCanPayload returned null."));

        Assert.True(accepted);
        return (byte[])(args[3]
            ?? throw new InvalidOperationException("TryBuildCustomCanPayload did not output payload."));
    }

    private static byte[] InvokeBuildStandaloneConfigPacket(byte configType, byte parameterIndex, byte dataIndex, byte[] payload)
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("BuildStandaloneConfigPacket", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find BuildStandaloneConfigPacket.");

        return (byte[])(method.Invoke(null, new object[] { configType, parameterIndex, dataIndex, payload })
            ?? throw new InvalidOperationException("BuildStandaloneConfigPacket returned null."));
    }

    private static object? InvokeInstanceMethod(object instance, string methodName, params object[] args)
    {
        MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Unable to find method {methodName}.");

        return method.Invoke(instance, args);
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Unable to find field {fieldName}.");

        field.SetValue(instance, value);
    }

    private static T GetField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Unable to find field {fieldName}.");

        return (T)(field.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {fieldName} was null."));
    }

    private static List<byte> GetSendBuffer(SerialPortService service)
    {
        FieldInfo sendBufferField = typeof(SerialPortService).GetField("_sendBuffer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Unable to find _sendBuffer field.");

        return (List<byte>)(sendBufferField.GetValue(service)
            ?? throw new InvalidOperationException("_sendBuffer was null."));
    }

    private static void SetReceivedDataBuffer(SerialPortService service, params byte[] bytes)
    {
        FieldInfo bufferField = typeof(SerialPortService).GetField("receivedDataBuffer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Unable to find receivedDataBuffer field.");

        bufferField.SetValue(service, new List<byte>(bytes));
    }

    private static void AppendReceivedDataBuffer(SerialPortService service, params byte[] bytes)
    {
        FieldInfo bufferField = typeof(SerialPortService).GetField("receivedDataBuffer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Unable to find receivedDataBuffer field.");

        List<byte> buffer = (List<byte>)(bufferField.GetValue(service)
            ?? throw new InvalidOperationException("receivedDataBuffer was null."));
        buffer.AddRange(bytes);
    }

    private static byte[] BuildDigitalInputStatusFrame(params byte[] states)
    {
        List<byte> frame =
        [
            Constants.SERIAL_HEADER1,
            Constants.SERIAL_HEADER2,
            (byte)Constants.COMMAND_ID_DIGITAL_INPUT_STATUS,
            (byte)states.Length,
        ];

        frame.AddRange(states);
        frame.Add(Constants.SERIAL_TRAILER1);
        frame.Add(Constants.SERIAL_TRAILER2);

        uint checksum = 0;
        foreach (byte value in frame)
        {
            checksum += value;
        }

        frame.AddRange(BitConverter.GetBytes(checksum));
        return frame.ToArray();
    }

    private static byte[] BuildCustomCanStatusFrame(
        byte crcValid,
        (uint id, ushort timeoutMs, byte dlc, byte flags, byte status)[] messageRecords,
        (byte messageIndex, byte startBit, byte bitLength, byte flags, float factor, float offset, float runtimeValue, byte status)[] signalRecords)
    {
        List<byte> frame =
        [
            Constants.SERIAL_HEADER1,
            Constants.SERIAL_HEADER2,
            (byte)Constants.COMMAND_ID_CUSTOM_CAN_STATUS,
            1,
            crcValid,
            (byte)messageRecords.Length,
            (byte)signalRecords.Length,
        ];

        foreach ((uint id, ushort timeoutMs, byte dlc, byte flags, byte status) message in messageRecords)
        {
            frame.AddRange(BitConverter.GetBytes(message.id));
            frame.AddRange(BitConverter.GetBytes(message.timeoutMs));
            frame.Add(message.dlc);
            frame.Add(message.flags);
            frame.Add(message.status);
        }

        foreach ((byte messageIndex, byte startBit, byte bitLength, byte flags, float factor, float offset, float runtimeValue, byte status) signal in signalRecords)
        {
            frame.Add(signal.messageIndex);
            frame.Add(signal.startBit);
            frame.Add(signal.bitLength);
            frame.Add(signal.flags);
            frame.AddRange(BitConverter.GetBytes(signal.factor));
            frame.AddRange(BitConverter.GetBytes(signal.offset));
            frame.AddRange(BitConverter.GetBytes(signal.runtimeValue));
            frame.Add(signal.status);
        }

        frame.Add(Constants.SERIAL_TRAILER1);
        frame.Add(Constants.SERIAL_TRAILER2);

        uint checksum = 0;
        foreach (byte value in frame)
        {
            checksum += value;
        }

        frame.AddRange(BitConverter.GetBytes(checksum));
        return frame.ToArray();
    }

    private static byte[] BuildCustomCanStatusFrameV2(
        byte crcValid,
        (uint id, ushort timeoutMs, byte dlc, byte flags, byte txTrigger, byte status)[] messageRecords,
        (byte messageIndex, byte startBit, byte bitLength, byte flags, float factor, float offset, byte valueSource, float runtimeValue, byte status)[] signalRecords,
        byte version = 2)
    {
        List<byte> frame =
        [
            Constants.SERIAL_HEADER1,
            Constants.SERIAL_HEADER2,
            (byte)Constants.COMMAND_ID_CUSTOM_CAN_STATUS,
            version,
            crcValid,
            (byte)messageRecords.Length,
            (byte)signalRecords.Length,
        ];

        foreach ((uint id, ushort timeoutMs, byte dlc, byte flags, byte txTrigger, byte status) message in messageRecords)
        {
            frame.AddRange(BitConverter.GetBytes(message.id));
            frame.AddRange(BitConverter.GetBytes(message.timeoutMs));
            frame.Add(message.dlc);
            frame.Add(message.flags);
            frame.Add(message.txTrigger);
            frame.Add(message.status);
        }

        foreach ((byte messageIndex, byte startBit, byte bitLength, byte flags, float factor, float offset, byte valueSource, float runtimeValue, byte status) signal in signalRecords)
        {
            frame.Add(signal.messageIndex);
            frame.Add(signal.startBit);
            frame.Add(signal.bitLength);
            frame.Add(signal.flags);
            frame.AddRange(BitConverter.GetBytes(signal.factor));
            frame.AddRange(BitConverter.GetBytes(signal.offset));
            frame.Add(signal.valueSource);
            frame.AddRange(BitConverter.GetBytes(signal.runtimeValue));
            frame.Add(signal.status);
        }

        frame.Add(Constants.SERIAL_TRAILER1);
        frame.Add(Constants.SERIAL_TRAILER2);

        uint checksum = 0;
        foreach (byte value in frame)
        {
            checksum += value;
        }

        frame.AddRange(BitConverter.GetBytes(checksum));
        return frame.ToArray();
    }

    private static void StopProcessTimer(SerialPortService service)
    {
        FieldInfo timerField = typeof(SerialPortService).GetField("_processTimer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Unable to find _processTimer field.");

        if (timerField.GetValue(service) is System.Timers.Timer timer)
        {
            timer.Stop();
            timer.Dispose();
        }
    }
}
