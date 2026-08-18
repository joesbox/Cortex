using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Cortex.Tests;

public class SerialPortProtocolTests
{
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

    private static List<byte> GetSendBuffer(SerialPortService service)
    {
        FieldInfo sendBufferField = typeof(SerialPortService).GetField("_sendBuffer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Unable to find _sendBuffer field.");

        return (List<byte>)(sendBufferField.GetValue(service)
            ?? throw new InvalidOperationException("_sendBuffer was null."));
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
