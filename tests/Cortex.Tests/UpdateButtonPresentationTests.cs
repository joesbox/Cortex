using Cortex.Services;
using Xunit;

namespace Cortex.Tests;

public class UpdateButtonPresentationTests
{
    [Fact]
    public void ForFirmware_ReturnsNoFirmware_WhenNotConnected()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
            isConnected: false,
            commsEstablished: true,
            isChecking: false,
            controllerVersion: "0.1.0",
            availableVersion: "0.1.1");

        Assert.Equal("No firmware available", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForFirmware_ReturnsChecking_WhenChecking()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
            isConnected: true,
            commsEstablished: true,
            isChecking: true,
            controllerVersion: "0.1.0",
            availableVersion: null);

        Assert.Equal("Checking firmware...", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForFirmware_ReturnsAvailable_WhenAvailableVersionExists()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
            isConnected: true,
            commsEstablished: true,
            isChecking: false,
            controllerVersion: "0.1.0",
            availableVersion: "0.1.1");

        Assert.Equal("Update 0.1.1", state.Text);
        Assert.True(state.IsHighlighted);
    }

    [Fact]
    public void ForFirmware_ReturnsUpToDate_WhenNoAvailableVersionAndControllerVersionKnown()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
            isConnected: true,
            commsEstablished: true,
            isChecking: false,
            controllerVersion: "0.1.1",
            availableVersion: null);

        Assert.Equal("Firmware up to date", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForApplication_ReturnsChecking_WhenChecking()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForApplication(
            isChecking: true,
            availableVersion: "0.2.0");

        Assert.Equal("Checking...", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForApplication_ReturnsAvailable_WhenNewVersionExists()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForApplication(
            isChecking: false,
            availableVersion: "0.2.0");

        Assert.Equal("Update 0.2.0 available", state.Text);
        Assert.True(state.IsHighlighted);
    }

    [Fact]
    public void ForApplication_ReturnsDefault_WhenNoVersionExists()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForApplication(
            isChecking: false,
            availableVersion: null);

        Assert.Equal("Check for updates", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForFirmware_ReturnsNoFirmware_WhenCommsNotEstablished()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
            isConnected: true,
            commsEstablished: false,
            isChecking: true,
            controllerVersion: "0.1.0",
            availableVersion: "0.1.1");

        Assert.Equal("No firmware available", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForFirmware_TreatsWhitespaceAvailableVersionAsUnavailable()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
            isConnected: true,
            commsEstablished: true,
            isChecking: false,
            controllerVersion: "0.1.1",
            availableVersion: "   ");

        Assert.Equal("Firmware up to date", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForFirmware_ReturnsNoFirmware_WhenControllerVersionBlankAndNoUpdate()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
            isConnected: true,
            commsEstablished: true,
            isChecking: false,
            controllerVersion: "   ",
            availableVersion: null);

        Assert.Equal("No firmware available", state.Text);
        Assert.False(state.IsHighlighted);
    }

    [Fact]
    public void ForApplication_TreatsWhitespaceAvailableVersionAsUnavailable()
    {
        UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForApplication(
            isChecking: false,
            availableVersion: "   ");

        Assert.Equal("Check for updates", state.Text);
        Assert.False(state.IsHighlighted);
    }
}
