using Cortex.Models;
using Xunit;

namespace Cortex.Tests;

public class CustomCanModelTests
{
    [Fact]
    public void CustomCanMessage_FlagWrappers_MapToFlagBits()
    {
        var message = new CustomCanMessage();

        message.IsEnabled = true;
        message.IsExtended = true;
        message.IsTx = true;
        message.IsPeriodic = true;

        Assert.Equal((byte)0x0F, message.Flags);

        message.IsPeriodic = false;
        Assert.Equal((byte)0x07, message.Flags);
    }

    [Fact]
    public void CustomCanMessage_DisplayLabels_ReflectDirectionAndTrigger()
    {
        var message = new CustomCanMessage { Flags = 0x01, TimeoutMs = 1000 };
        Assert.Equal("RX", message.DirectionLabel);
        Assert.Equal("-", message.TriggerLabel);
        Assert.Equal("1000 ms", message.TimeoutDisplay);
        Assert.Equal("-", message.PeriodDisplay);

        message.TimeoutMs = 0;
        Assert.Equal("None", message.TimeoutDisplay);

        message.Flags = 0x0D; // enabled | TX | periodic
        message.TimeoutMs = 100;
        message.TxTrigger = 2;
        Assert.Equal("TX periodic", message.DirectionLabel);
        Assert.Equal("CH3", message.TriggerLabel);
        Assert.Equal("-", message.TimeoutDisplay);
        Assert.Equal("100 ms", message.PeriodDisplay);

        message.TxTrigger = Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
        Assert.Equal("-", message.TriggerLabel);

        message.IsEnabled = false;
        Assert.Equal("TX periodic (off)", message.DirectionLabel);
        Assert.Equal("-", message.TriggerLabel);
    }

    [Fact]
    public void CustomCanSignal_OwnerMessage_ForwardsDirectionChanges()
    {
        var message = new CustomCanMessage { Flags = 0x01 };
        var signal = new CustomCanSignal { ValueSource = 2 };

        Assert.Equal("-", signal.ValueSourceDisplay);

        signal.OwnerMessage = message;
        Assert.False(signal.IsOnTxMessage);
        Assert.Equal("RX", signal.DirectionLabel);
        Assert.Equal("-", signal.ValueSourceDisplay);

        bool sourceDisplayChanged = false;
        signal.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CustomCanSignal.ValueSourceDisplay))
            {
                sourceDisplayChanged = true;
            }
        };

        message.IsTx = true;
        Assert.True(sourceDisplayChanged);
        Assert.True(signal.IsOnTxMessage);
        Assert.Equal("TX", signal.DirectionLabel);
        Assert.Equal("CH3", signal.ValueSourceDisplay);

        signal.ValueSource = Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT;
        Assert.True(signal.IsConstantSource);
        Assert.Equal("Constant", signal.ValueSourceDisplay);
    }
}
