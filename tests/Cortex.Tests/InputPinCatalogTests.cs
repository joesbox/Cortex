using Cortex.Models;
using Xunit;

namespace Cortex.Tests;

public class InputPinCatalogTests
{
    // GetLabelForPin

    [Fact]
    public void GetLabelForPin_ReturnsIgnition_ForIgnitionPin()
    {
        Assert.Equal("Ignition", InputPinCatalog.GetLabelForPin(InputPinCatalog.IgnitionInputPin));
    }

    [Theory]
    [InlineData(79, "Digital 1")]
    [InlineData(78, "Digital 2")]
    [InlineData(77, "Digital 3")]
    [InlineData(72, "Digital 8")]
    public void GetLabelForPin_ReturnsDigitalLabel_ForDigitalInputPins(byte pin, string expected)
    {
        Assert.Equal(expected, InputPinCatalog.GetLabelForPin(pin));
    }

    [Theory]
    [InlineData(208, "Ana/Dig 1")]
    [InlineData(209, "Ana/Dig 2")]
    [InlineData(215, "Ana/Dig 8")]
    public void GetLabelForPin_ReturnsAnalogueLabel_ForAnalogueInputPins(byte pin, string expected)
    {
        Assert.Equal(expected, InputPinCatalog.GetLabelForPin(pin));
    }

    [Fact]
    public void GetLabelForPin_ReturnsPinNumber_ForUnknownPin()
    {
        Assert.Equal("Pin 5", InputPinCatalog.GetLabelForPin(5));
    }

    // TryParseLabel

    [Theory]
    [InlineData("Ignition")]
    [InlineData("ignition")]
    [InlineData("IGNITION")]
    public void TryParseLabel_ReturnsIgnitionPin_ForIgnitionLabel(string label)
    {
        Assert.Equal(InputPinCatalog.IgnitionInputPin, InputPinCatalog.TryParseLabel(label));
    }

    [Theory]
    [InlineData("Digital 1", 79)]
    [InlineData("Digital 2", 78)]
    [InlineData("digital 8", 72)]
    public void TryParseLabel_ReturnsCorrectPin_ForDigitalLabel(string label, byte expected)
    {
        Assert.Equal(expected, InputPinCatalog.TryParseLabel(label));
    }

    [Theory]
    [InlineData("Ana/Dig 1", 208)]
    [InlineData("Ana/Dig 8", 215)]
    [InlineData("Analogue 1", 208)]
    [InlineData("analogue 2", 209)]
    public void TryParseLabel_ReturnsCorrectPin_ForAnalogueLabel(string label, byte expected)
    {
        Assert.Equal(expected, InputPinCatalog.TryParseLabel(label));
    }

    [Fact]
    public void TryParseLabel_ReturnsPinNumber_ForPinLabel()
    {
        Assert.Equal((byte)99, InputPinCatalog.TryParseLabel("Pin 99"));
    }

    [Theory]
    [InlineData("Digital 0")]
    [InlineData("Digital 9")]
    [InlineData("Analogue 0")]
    [InlineData("Analogue 9")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void TryParseLabel_ReturnsNull_ForUnrecognizedLabel(string label)
    {
        Assert.Null(InputPinCatalog.TryParseLabel(label));
    }

    [Fact]
    public void GetLabelForPin_ThenTryParseLabel_RoundTrips_ForAllKnownPins()
    {
        foreach (var pin in InputPinCatalog.AllInputPins)
        {
            var label = InputPinCatalog.GetLabelForPin(pin);
            var parsed = InputPinCatalog.TryParseLabel(label);
            Assert.Equal(pin, parsed);
        }
    }
}
