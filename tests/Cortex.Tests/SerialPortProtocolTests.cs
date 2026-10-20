using Cortex.Models;
using Cortex.Services;
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Cortex.Tests;

public class SerialPortProtocolTests
{
    [Theory]
    [InlineData(OutputChannel.MathOperandType.Constant, 0)]
    [InlineData(OutputChannel.MathOperandType.Channel, 1)]
    [InlineData(OutputChannel.MathOperandType.AnalogueInput, 2)]
    [InlineData(OutputChannel.MathOperandType.SystemValue, 3)]
    [InlineData(OutputChannel.MathOperandType.Rule, 4)]
    [InlineData(OutputChannel.MathOperandType.DigitalInput, 5)]
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

    private static SerialPortService CreateService() => new("COM1");

    private static bool InvokeExpectsFramedResponse(char commandId)
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("ExpectsFramedResponse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to find ExpectsFramedResponse.");

        return (bool)(method.Invoke(null, new object[] { commandId })
            ?? throw new InvalidOperationException("ExpectsFramedResponse returned null."));
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
