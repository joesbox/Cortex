using Cortex.Models;
using Xunit;

namespace Cortex.Tests;

public class CellularParametersTests
{
    [Fact]
    public void UploadDigitalInputs_SelectsAllDigitalTelemetryFlags()
    {
        var parameters = new CellularParameters();

        parameters.UploadDigitalInputs = true;

        Assert.True(parameters.UploadDigital1Value);
        Assert.True(parameters.UploadDigital2Value);
        Assert.True(parameters.UploadDigital3Value);
        Assert.True(parameters.UploadDigital4Value);
        Assert.True(parameters.UploadDigital5Value);
        Assert.True(parameters.UploadDigital6Value);
        Assert.True(parameters.UploadDigital7Value);
        Assert.True(parameters.UploadDigital8Value);
    }

    [Fact]
    public void UploadAnalogueInputs_ClearsEveryAnalogueFlag_WhenDisabled()
    {
        var parameters = new CellularParameters();
        parameters.TelemetryUploadMask = Constants.TELEMETRY_UPLOAD_DEFAULT_MASK
            | Constants.TELEMETRY_UPLOAD_ANALOGUE1_VALUE
            | Constants.TELEMETRY_UPLOAD_ANALOGUE2_VALUE
            | Constants.TELEMETRY_UPLOAD_ANALOGUE3_VALUE
            | Constants.TELEMETRY_UPLOAD_ANALOGUE4_VALUE
            | Constants.TELEMETRY_UPLOAD_ANALOGUE5_VALUE
            | Constants.TELEMETRY_UPLOAD_ANALOGUE6_VALUE
            | Constants.TELEMETRY_UPLOAD_ANALOGUE7_VALUE
            | Constants.TELEMETRY_UPLOAD_ANALOGUE8_VALUE;

        parameters.UploadAnalogueInputs = false;

        Assert.False(parameters.UploadAnalogue1Value);
        Assert.False(parameters.UploadAnalogue2Value);
        Assert.False(parameters.UploadAnalogue3Value);
        Assert.False(parameters.UploadAnalogue4Value);
        Assert.False(parameters.UploadAnalogue5Value);
        Assert.False(parameters.UploadAnalogue6Value);
        Assert.False(parameters.UploadAnalogue7Value);
        Assert.False(parameters.UploadAnalogue8Value);
    }

    [Fact]
    public void PublishIntervalMs_ClampsToMinimum()
    {
        var parameters = new CellularParameters();

        parameters.PublishIntervalMs = 1;

        Assert.Equal(Constants.CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS, parameters.PublishIntervalMs);
    }

    [Fact]
    public void PublishIntervalSeconds_UsesSecondsWithMinimumEnforcement()
    {
        var parameters = new CellularParameters();

        parameters.PublishIntervalSeconds = 1;
        Assert.Equal(Constants.CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS, parameters.PublishIntervalMs);
        Assert.Equal(Constants.CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS / 1000, parameters.PublishIntervalSeconds);

        parameters.PublishIntervalSeconds = 120;
        Assert.Equal(120000u, parameters.PublishIntervalMs);
        Assert.Equal(120u, parameters.PublishIntervalSeconds);
    }

    [Fact]
    public void UseTls_TogglesDefaultPorts_WithoutOverwritingCustomPort()
    {
        var parameters = new CellularParameters();

        parameters.OpenRemotePort = Constants.CELLULAR_DEFAULT_MQTT_PORT;
        parameters.UseTLS = true;
        Assert.Equal(Constants.CELLULAR_DEFAULT_MQTT_TLS_PORT, parameters.OpenRemotePort);

        parameters.OpenRemotePort = 1234;
        parameters.UseTLS = false;
        Assert.Equal((ushort)1234, parameters.OpenRemotePort);
    }

    [Fact]
    public void EnsurePublishTopicFromOpenRemoteFields_BuildsTopicsAndUsername()
    {
        var parameters = new CellularParameters
        {
            OpenRemoteRealm = "team",
            ClientID = "client1",
            OpenRemoteAssetId = "asset99",
        };

        parameters.EnsurePublishTopicFromOpenRemoteFields();

        Assert.Equal($"team/client1/writeattributevalue/{Constants.OPENREMOTE_COMPATIBILITY_ATTRIBUTE}/asset99", parameters.PublishTopic);
        Assert.Equal($"team/client1/attributevalue/{Constants.OPENREMOTE_COMPATIBILITY_ATTRIBUTE}/asset99", parameters.SubscribeTopic);
        Assert.Equal("team:ps-client1", parameters.MQTTUsername);
    }

    [Fact]
    public void PublishTopic_ParsesRealmAndAssetId_WhenFormatIsValid()
    {
        var parameters = new CellularParameters();

        parameters.PublishTopic = "realm-a/client-b/writeattributevalue/compat/asset-c";

        Assert.Equal("realm-a", parameters.OpenRemoteRealm);
        Assert.Equal("asset-c", parameters.OpenRemoteAssetId);
    }

    [Fact]
    public void PublishTopic_DoesNotPopulateFields_WhenFormatIsInvalid()
    {
        var parameters = new CellularParameters();

        parameters.PublishTopic = "realm-a/client-b/attributevalue/compat/asset-c";

        Assert.Null(parameters.OpenRemoteRealm);
        Assert.Null(parameters.OpenRemoteAssetId);
    }

    [Fact]
    public void PreserveOpenRemoteFieldsFrom_CopiesOnlyMissingValues()
    {
        var source = new CellularParameters
        {
            OpenRemoteRealm = "source-realm",
            OpenRemoteAssetId = "source-asset",
        };

        var target = new CellularParameters
        {
            OpenRemoteRealm = "target-realm",
            OpenRemoteAssetId = null,
        };

        target.PreserveOpenRemoteFieldsFrom(source);

        Assert.Equal("target-realm", target.OpenRemoteRealm);
        Assert.Equal("source-asset", target.OpenRemoteAssetId);
    }
}
