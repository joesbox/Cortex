using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Services
{
    public static class ConfigFileSerializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string SerializeSettings(DataStructures settingsData, IoFlowGraphConfigSnapshot? ioFlowGraph = null, bool includeCustomCanMetadata = true)
        {
            var snapshot = BuildSnapshot(settingsData, ioFlowGraph);
            if (!includeCustomCanMetadata)
            {
                snapshot.CustomCanSignalMetadata = null;
            }

            return JsonSerializer.Serialize(snapshot, JsonOptions);
        }

        internal static ConfigSnapshot CreateSettingsSnapshot(DataStructures settingsData) => BuildSnapshot(settingsData, null);

        public static bool TryDeserialize(string json, out ConfigSnapshot? snapshot, out string? error)
        {
            try
            {
                snapshot = JsonSerializer.Deserialize<ConfigSnapshot>(json, JsonOptions);

                if (snapshot == null)
                {
                    error = "Configuration file is empty or invalid.";
                    return false;
                }

                // Older files kept CAN records only inside the graph's canonical signature.
                if ((snapshot.CanMessages == null || snapshot.CanSignals == null)
                    && !string.IsNullOrWhiteSpace(snapshot.IoFlowGraph?.ControllerSettingsSignature))
                {
                    using var signature = JsonDocument.Parse(snapshot.IoFlowGraph.ControllerSettingsSignature);
                    if (signature.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("The I/O Flow settings signature must be a JSON object.");
                    if (signature.RootElement.TryGetProperty("InputsAndOutputs", out _))
                    {
                        var legacy = JsonSerializer.Deserialize<ConfigSnapshot>(snapshot.IoFlowGraph.ControllerSettingsSignature);
                        snapshot.CanMessages ??= legacy?.CanMessages;
                        snapshot.CanSignals ??= legacy?.CanSignals;
                    }
                }

                if (snapshot.Channels == null && snapshot.DigitalInputs == null && snapshot.AnalogueInputs == null && snapshot.SystemParameters == null && snapshot.CellularParameters == null && snapshot.CustomCanSignalMetadata == null && snapshot.IoFlowGraph == null && snapshot.CanMessages == null && snapshot.CanSignals == null)
                {
                    error = "Configuration file does not contain any recognized configuration sections.";
                    return false;
                }

                ValidateSnapshot(snapshot);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                snapshot = null;
                error = $"Failed to parse configuration file: {ex.Message}";
                return false;
            }
        }

        public static void ApplySnapshot(DataStructures target, ConfigSnapshot snapshot)
        {
            ValidateSnapshot(snapshot);
            if (snapshot.CanMessages?.Any(message => message.Index >= target.CustomCanMessagesStaticData.Count) == true ||
                snapshot.CanSignals?.Any(signal => signal.Index >= target.CustomCanSignalsStaticData.Count) == true ||
                snapshot.Channels?.Count > target.ChannelsStaticData.Count ||
                snapshot.DigitalInputs?.Count > target.DigitalInputsStaticData.Count ||
                snapshot.AnalogueInputs?.Count > target.AnalogueInputsStaticData.Count)
                throw new ArgumentException("Configuration does not fit the target settings.");
            if (snapshot.CanMessages != null)
            {
                foreach (var source in snapshot.CanMessages)
                {
                    if (source.Index < 0 || source.Index >= target.CustomCanMessagesStaticData.Count)
                        throw new ArgumentException("Configuration contains an invalid CAN message slot.");
                    var message = target.CustomCanMessagesStaticData[source.Index];
                    message.Id = source.Id;
                    message.Dlc = source.Dlc;
                    message.Flags = source.Flags;
                    message.TimeoutMs = source.TimeoutMs;
                    message.TxTrigger = source.TxTrigger;
                }
            }
            if (snapshot.CanSignals != null)
            {
                foreach (var source in snapshot.CanSignals)
                {
                    if (source.Index < 0 || source.Index >= target.CustomCanSignalsStaticData.Count)
                        throw new ArgumentException("Configuration contains an invalid CAN signal slot.");
                    var signal = target.CustomCanSignalsStaticData[source.Index];
                    signal.MessageIndex = source.MessageIndex;
                    signal.StartBit = source.StartBit;
                    signal.BitLength = source.BitLength;
                    signal.Flags = source.Flags;
                    signal.ValueSource = source.ValueSource;
                    signal.Factor = source.Factor;
                    signal.Offset = source.Offset;
                }
            }
            if (snapshot.Channels != null)
            {
                int channelCount = Math.Min(target.ChannelsStaticData.Count, snapshot.Channels.Count);
                for (int i = 0; i < channelCount; i++)
                {
                    var targetChannel = target.ChannelsStaticData[i];
                    var sourceChannel = snapshot.Channels[i];

                    targetChannel.ChanType = sourceChannel.ChanType ?? targetChannel.ChanType;
                    targetChannel.Category = sourceChannel.Category ?? targetChannel.Category;
                    targetChannel.PWMSetDuty = sourceChannel.PWMSetDuty ?? targetChannel.PWMSetDuty;
                    if (sourceChannel.Name != null)
                    {
                        targetChannel.Name = ToFixedName(sourceChannel.Name);
                    }

                    targetChannel.CurrentThresholdHigh = sourceChannel.CurrentThresholdHigh ?? targetChannel.CurrentThresholdHigh;
                    targetChannel.CurrentThresholdLow = sourceChannel.CurrentThresholdLow ?? targetChannel.CurrentThresholdLow;
                    targetChannel.RetryCount = sourceChannel.RetryCount ?? targetChannel.RetryCount;
                    targetChannel.InrushDelay = sourceChannel.InrushDelay ?? targetChannel.InrushDelay;
                    targetChannel.InrushCurrentLimit = sourceChannel.InrushCurrentLimit ?? targetChannel.InrushCurrentLimit;
                    targetChannel.MultiChannel = sourceChannel.MultiChannel ?? targetChannel.MultiChannel;
                    targetChannel.GroupNumber = sourceChannel.GroupNumber ?? targetChannel.GroupNumber;
                    targetChannel.ControlPin = sourceChannel.ControlPin ?? targetChannel.ControlPin;
                    targetChannel.CurrentSensePin = sourceChannel.CurrentSensePin ?? targetChannel.CurrentSensePin;
                    if (sourceChannel.InputControlPin.HasValue)
                    {
                        targetChannel.InputControlPin = sourceChannel.InputControlPin.Value;
                    }
                    else if (!string.IsNullOrWhiteSpace(sourceChannel.InputControlLabel))
                    {
                        byte? parsedInputPin = InputPinCatalog.TryParseLabel(sourceChannel.InputControlLabel);
                        if (parsedInputPin.HasValue)
                        {
                            targetChannel.InputControlPin = parsedInputPin.Value;
                        }
                    }
                    targetChannel.SetImportedThresholds(sourceChannel.OnThreshold, sourceChannel.OffThreshold);
                    targetChannel.ScaleMin = sourceChannel.ScaleMin ?? targetChannel.ScaleMin;
                    targetChannel.ScaleMax = sourceChannel.ScaleMax ?? targetChannel.ScaleMax;
                    targetChannel.PWMMin = sourceChannel.PWMMin ?? targetChannel.PWMMin;
                    targetChannel.PWMMax = sourceChannel.PWMMax ?? targetChannel.PWMMax;
                    targetChannel.SoftStartEnabled = sourceChannel.SoftStartEnabled ?? targetChannel.SoftStartEnabled;
                    targetChannel.SoftStartTime = sourceChannel.SoftStartTime ?? targetChannel.SoftStartTime;
                    targetChannel.SoftStopEnabled = sourceChannel.SoftStopEnabled ?? targetChannel.SoftStopEnabled;
                    targetChannel.SoftStopTime = sourceChannel.SoftStopTime ?? targetChannel.SoftStopTime;
                    targetChannel.IntermittentOnTime = sourceChannel.IntermittentOnTime ?? targetChannel.IntermittentOnTime;
                    targetChannel.IntermittentOffTime = sourceChannel.IntermittentOffTime ?? targetChannel.IntermittentOffTime;
                    targetChannel.MathRuleCount = sourceChannel.MathRuleCount ?? targetChannel.MathRuleCount;
                    targetChannel.MathRootRule = sourceChannel.MathRootRule ?? targetChannel.MathRootRule;
                    targetChannel.MathFollowChannel = sourceChannel.MathFollowChannel ?? OutputChannel.MathFollowNone;
                    targetChannel.MathOutputMode = sourceChannel.MathOutputMode ?? targetChannel.MathOutputMode;
                    targetChannel.EnsureMathRuleSlots();

                    if (sourceChannel.MathRules != null)
                    {
                        int ruleCount = Math.Min(Constants.NUM_MATH_RULE_SLOTS, sourceChannel.MathRules.Count);
                        for (int ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
                        {
                            OutputChannel.MathRule rule = targetChannel.MathRules[ruleIndex];
                            MathRuleSnapshot sourceRule = sourceChannel.MathRules[ruleIndex];
                            rule.Operation = sourceRule.Operation ?? rule.Operation;
                            rule.HysteresisEnabled = sourceRule.HysteresisEnabled ?? rule.HysteresisEnabled;
                            rule.OnThreshold = sourceRule.OnThreshold ?? rule.OnThreshold;
                            rule.OffThreshold = sourceRule.OffThreshold ?? rule.OffThreshold;
                            rule.OperandAType = sourceRule.OperandAType ?? rule.OperandAType;
                            rule.OperandAIndex = sourceRule.OperandAIndex ?? rule.OperandAIndex;
                            rule.OperandAConstant = sourceRule.OperandAConstant ?? rule.OperandAConstant;
                            rule.OperandBType = sourceRule.OperandBType ?? rule.OperandBType;
                            rule.OperandBIndex = sourceRule.OperandBIndex ?? rule.OperandBIndex;
                            rule.OperandBConstant = sourceRule.OperandBConstant ?? rule.OperandBConstant;
                        }
                    }

                    targetChannel.DelayedOn = sourceChannel.DelayedOn ?? targetChannel.DelayedOn;
                    targetChannel.DelayedOnTime = sourceChannel.DelayedOnTime ?? targetChannel.DelayedOnTime;
                    bool hasExplicitDelayedOff = sourceChannel.DelayedOff.HasValue ||
                                                 sourceChannel.DelayedOffTime.HasValue ||
                                                 sourceChannel.DelayedOffTrigger.HasValue;

                    targetChannel.DelayedOff = sourceChannel.DelayedOff ?? targetChannel.DelayedOff;
                    targetChannel.DelayedOffTime = sourceChannel.DelayedOffTime ?? targetChannel.DelayedOffTime;
                    targetChannel.DelayedOffTrigger = sourceChannel.DelayedOffTrigger ?? targetChannel.DelayedOffTrigger;

                    if (!hasExplicitDelayedOff)
                    {
                        ApplyLegacyRunOnFallback(targetChannel, sourceChannel);
                    }

                    targetChannel.RefreshDelayUiUnitsFromStoredValues();
                }
            }

            if (snapshot.DigitalInputs != null)
            {
                int digitalCount = Math.Min(target.DigitalInputsStaticData.Count, snapshot.DigitalInputs.Count);
                for (int i = 0; i < digitalCount; i++)
                {
                    var targetDigital = target.DigitalInputsStaticData[i];
                    var sourceDigital = snapshot.DigitalInputs[i];

                    targetDigital.InputNumber = sourceDigital.InputNumber ?? targetDigital.InputNumber;
                    targetDigital.IsActiveHigh = sourceDigital.IsActiveHigh ?? targetDigital.IsActiveHigh;
                }
            }

            if (snapshot.AnalogueInputs != null)
            {
                int analogueCount = Math.Min(target.AnalogueInputsStaticData.Count, snapshot.AnalogueInputs.Count);
                for (int i = 0; i < analogueCount; i++)
                {
                    var targetAnalogue = target.AnalogueInputsStaticData[i];
                    var sourceAnalogue = snapshot.AnalogueInputs[i];

                    targetAnalogue.InputNumber = sourceAnalogue.InputNumber ?? targetAnalogue.InputNumber;
                    targetAnalogue.ChanType = sourceAnalogue.ChanType ?? targetAnalogue.ChanType;
                    targetAnalogue.Units = sourceAnalogue.Units ?? targetAnalogue.Units;
                    targetAnalogue.CalibrationPoints = sourceAnalogue.CalibrationPoints ?? targetAnalogue.CalibrationPoints;
                    targetAnalogue.PullUpEnable = sourceAnalogue.PullUpEnable ?? targetAnalogue.PullUpEnable;
                    targetAnalogue.PullDownEnable = sourceAnalogue.PullDownEnable ?? targetAnalogue.PullDownEnable;
                    targetAnalogue.CalibrationVolt1 = sourceAnalogue.CalibrationVolt1 ?? targetAnalogue.CalibrationVolt1;
                    targetAnalogue.CalibrationValue1 = sourceAnalogue.CalibrationValue1 ?? targetAnalogue.CalibrationValue1;
                    targetAnalogue.CalibrationVolt2 = sourceAnalogue.CalibrationVolt2 ?? targetAnalogue.CalibrationVolt2;
                    targetAnalogue.CalibrationValue2 = sourceAnalogue.CalibrationValue2 ?? targetAnalogue.CalibrationValue2;
                    targetAnalogue.CalibrationVolt3 = sourceAnalogue.CalibrationVolt3 ?? targetAnalogue.CalibrationVolt3;
                    targetAnalogue.CalibrationValue3 = sourceAnalogue.CalibrationValue3 ?? targetAnalogue.CalibrationValue3;
                    targetAnalogue.ConfigRangeMin = sourceAnalogue.ConfigRangeMin ?? targetAnalogue.ConfigRangeMin;
                    targetAnalogue.ConfigRangeMax = sourceAnalogue.ConfigRangeMax ?? targetAnalogue.ConfigRangeMax;
                    targetAnalogue.NtcBeta = sourceAnalogue.NTCBeta ?? targetAnalogue.NtcBeta;
                    targetAnalogue.NtcNominalResistance = sourceAnalogue.NTCNominalResistance ?? targetAnalogue.NtcNominalResistance;
                }
            }

            if (snapshot.SystemParameters != null)
            {
                target.SystemParamsStaticData.CANResEnabled = snapshot.SystemParameters.CANResEnabled ?? target.SystemParamsStaticData.CANResEnabled;
                target.SystemParamsStaticData.CANBusBitrate = snapshot.SystemParameters.CANBusBitrate ?? target.SystemParamsStaticData.CANBusBitrate;
                target.SystemParamsStaticData.SystemCurrentLimit = snapshot.SystemParameters.SystemCurrentLimit ?? target.SystemParamsStaticData.SystemCurrentLimit;
                target.SystemParamsStaticData.ChannelDataCANID = snapshot.SystemParameters.ChannelDataCANID ?? target.SystemParamsStaticData.ChannelDataCANID;
                target.SystemParamsStaticData.DigitalInputDataCANID = snapshot.SystemParameters.DigitalInputDataCANID ?? target.SystemParamsStaticData.DigitalInputDataCANID;
                target.SystemParamsStaticData.AnalogueInputDataCANID = snapshot.SystemParameters.AnalogueInputDataCANID ?? target.SystemParamsStaticData.AnalogueInputDataCANID;
                target.SystemParamsStaticData.SystemDataCANID = snapshot.SystemParameters.SystemDataCANID ?? target.SystemParamsStaticData.SystemDataCANID;
                target.SystemParamsStaticData.SystemConfigCANID = snapshot.SystemParameters.SystemConfigCANID ?? target.SystemParamsStaticData.SystemConfigCANID;
                target.SystemParamsStaticData.ConfigDataCANID = snapshot.SystemParameters.ConfigDataCANID ?? target.SystemParamsStaticData.ConfigDataCANID;
                target.SystemParamsStaticData.IMUWakeWindow = snapshot.SystemParameters.IMUWakeWindow ?? target.SystemParamsStaticData.IMUWakeWindow;
                target.SystemParamsStaticData.SpeedUnitPref = snapshot.SystemParameters.SpeedUnitPref ?? target.SystemParamsStaticData.SpeedUnitPref;
                target.SystemParamsStaticData.DistanceUnitPref = snapshot.SystemParameters.DistanceUnitPref ?? target.SystemParamsStaticData.DistanceUnitPref;
                target.SystemParamsStaticData.AllowData = snapshot.SystemParameters.AllowData ?? target.SystemParamsStaticData.AllowData;
                target.SystemParamsStaticData.AllowGPS = snapshot.SystemParameters.AllowGPS ?? target.SystemParamsStaticData.AllowGPS;
                target.SystemParamsStaticData.AllowMotionDetect = snapshot.SystemParameters.AllowMotionDetect ?? target.SystemParamsStaticData.AllowMotionDetect;
                target.SystemParamsStaticData.TimeZoneId = snapshot.SystemParameters.TimeZoneId ?? target.SystemParamsStaticData.TimeZoneId;
                target.SystemParamsStaticData.TimeZoneRule = snapshot.SystemParameters.TimeZoneRule ?? target.SystemParamsStaticData.TimeZoneRule;
            }

            if (snapshot.CellularParameters != null)
            {
                target.CellularParamsStaticData.ConfigVersion = snapshot.CellularParameters.ConfigVersion ?? target.CellularParamsStaticData.ConfigVersion;
                target.CellularParamsStaticData.Protocol = snapshot.CellularParameters.Protocol ?? target.CellularParamsStaticData.Protocol;
                target.CellularParamsStaticData.UseTLS = snapshot.CellularParameters.UseTLS ?? target.CellularParamsStaticData.UseTLS;
                target.CellularParamsStaticData.APN = snapshot.CellularParameters.APN ?? target.CellularParamsStaticData.APN;
                target.CellularParamsStaticData.APNUser = snapshot.CellularParameters.APNUser ?? target.CellularParamsStaticData.APNUser;
                target.CellularParamsStaticData.APNPassword = snapshot.CellularParameters.APNPassword ?? target.CellularParamsStaticData.APNPassword;
                target.CellularParamsStaticData.OpenRemoteHost = snapshot.CellularParameters.OpenRemoteHost ?? target.CellularParamsStaticData.OpenRemoteHost;
                target.CellularParamsStaticData.OpenRemotePort = snapshot.CellularParameters.OpenRemotePort ?? target.CellularParamsStaticData.OpenRemotePort;
                target.CellularParamsStaticData.OpenRemoteRealm = snapshot.CellularParameters.OpenRemoteRealm ?? target.CellularParamsStaticData.OpenRemoteRealm;
                target.CellularParamsStaticData.OpenRemoteAssetId = snapshot.CellularParameters.OpenRemoteAssetId ?? target.CellularParamsStaticData.OpenRemoteAssetId;
                target.CellularParamsStaticData.OpenRemoteAssetName = snapshot.CellularParameters.OpenRemoteAssetName ?? target.CellularParamsStaticData.OpenRemoteAssetName;
                target.CellularParamsStaticData.MQTTUsername = snapshot.CellularParameters.MQTTUsername ?? target.CellularParamsStaticData.MQTTUsername;
                target.CellularParamsStaticData.MQTTPassword = snapshot.CellularParameters.MQTTPassword ?? target.CellularParamsStaticData.MQTTPassword;
                target.CellularParamsStaticData.KeepAliveSeconds = snapshot.CellularParameters.KeepAliveSeconds ?? target.CellularParamsStaticData.KeepAliveSeconds;
                target.CellularParamsStaticData.PublishIntervalMs = snapshot.CellularParameters.PublishIntervalMs ?? target.CellularParamsStaticData.PublishIntervalMs;
                target.CellularParamsStaticData.TelemetryUploadMask = snapshot.CellularParameters.TelemetryUploadMask ?? target.CellularParamsStaticData.TelemetryUploadMask;
            }

            if (snapshot.CustomCanSignalMetadata != null)
            {
                // Metadata is a local name/limit store keyed by signal identity; merge rather than replace so
                // loading an older file cannot wipe names tracked for other signals.
                foreach (CustomCanSignalMetadataSnapshot metadata in snapshot.CustomCanSignalMetadata)
                {
                    var imported = new CustomCanSignalMetadata
                    {
                        SignalIndex = Math.Clamp(metadata.SignalIndex ?? 0, 0, Constants.MAX_CUSTOM_CAN_SIGNALS - 1),
                        MessageIndex = (byte)Math.Clamp(metadata.MessageIndex ?? 0, 0, Constants.MAX_CUSTOM_CAN_MESSAGES - 1),
                        MessageId = metadata.MessageId,
                        IsExtended = metadata.IsExtended ?? false,
                        StartBit = Math.Clamp(metadata.StartBit ?? 0, (byte)0, (byte)63),
                        BitLength = Math.Clamp(metadata.BitLength ?? 1, (byte)1, (byte)64),
                        IsMotorola = metadata.IsMotorola ?? false,
                        IsSigned = metadata.IsSigned ?? false,
                        Factor = metadata.Factor ?? 1.0f,
                        Offset = metadata.Offset ?? 0.0f,
                        Name = metadata.Name ?? string.Empty,
                        MessageName = metadata.MessageName ?? string.Empty,
                        MinValue = metadata.MinValue,
                        MaxValue = metadata.MaxValue,
                        Units = metadata.Units ?? string.Empty,
                        IdentityFingerprint = metadata.IdentityFingerprint ?? string.Empty,
                    };
                    int existing = target.CustomCanSignalsMetadata.ToList().FindIndex(item => CustomCanSignalMetadata.HasSameIdentity(item, imported));
                    if (existing >= 0)
                        target.CustomCanSignalsMetadata[existing] = imported;
                    else
                        target.CustomCanSignalsMetadata.Add(imported);
                }
            }
        }

        private static void ValidateSnapshot(ConfigSnapshot snapshot)
        {
            static void Require(bool condition, string message)
            {
                if (!condition) throw new ArgumentException(message);
            }
            static bool Finite(params float?[] values) => values.All(value => !value.HasValue || float.IsFinite(value.Value));

            Require(snapshot.FormatVersion == 1, $"Unsupported configuration format version {snapshot.FormatVersion}.");
            Require(snapshot.Channels == null || snapshot.Channels.Count <= Constants.NUM_OUTPUT_CHANNELS, "Too many output channels.");
            Require(snapshot.DigitalInputs == null || snapshot.DigitalInputs.Count <= Constants.NUM_DIGITAL_INPUTS, "Too many digital inputs.");
            Require(snapshot.AnalogueInputs == null || snapshot.AnalogueInputs.Count <= Constants.NUM_ANALOGUE_INPUTS, "Too many analogue inputs.");
            if (snapshot.Channels != null)
            {
                foreach (var channel in snapshot.Channels)
                {
                    Require(channel != null, "A channel entry is null.");
                    Require(!channel!.ChanType.HasValue || Enum.IsDefined(channel.ChanType.Value), "Invalid channel type.");
                    Require(!channel.Category.HasValue || Enum.IsDefined(channel.Category.Value), "Invalid channel category.");
                    Require(channel.PWMSetDuty is null or <= 100 && channel.PWMMin is null or <= 100 && channel.PWMMax is null or <= 100, "Invalid PWM percentage.");
                    Require(channel.CurrentThresholdHigh is null or >= 0 and <= 17 && channel.CurrentThresholdLow is null or >= 0 and <= 17 &&
                        channel.InrushDelay is null or >= 0 and <= 2 && channel.InrushCurrentLimit is null or >= 0 and <= 50, "Invalid current limit or inrush setting.");
                    Require(channel.InputControlPin is null or 0 || InputPinCatalog.AllInputPins.Contains(channel.InputControlPin.Value), "Invalid input control pin.");
                    if (!channel.InputControlPin.HasValue && !string.IsNullOrWhiteSpace(channel.InputControlLabel))
                    {
                        byte? pin = InputPinCatalog.TryParseLabel(channel.InputControlLabel);
                        Require(pin == 0 || (pin.HasValue && InputPinCatalog.AllInputPins.Contains(pin.Value)), "Invalid input control label.");
                    }
                    Require(Finite(channel.CurrentThresholdHigh, channel.CurrentThresholdLow, channel.InrushDelay, channel.InrushCurrentLimit,
                        channel.OnThreshold, channel.OffThreshold, channel.ScaleMin, channel.ScaleMax, channel.SoftStartTime,
                        channel.SoftStopTime, channel.IntermittentOnTime, channel.IntermittentOffTime), "A channel setting is not finite.");
                    Require(channel.MathRuleCount is null or <= Constants.NUM_MATH_RULE_SLOTS && channel.MathRootRule is null or < Constants.NUM_MATH_RULE_SLOTS, "Invalid math rule count or root.");
                    Require(!channel.MathRuleCount.HasValue || !channel.MathRootRule.HasValue || channel.MathRuleCount == 0 || channel.MathRootRule < channel.MathRuleCount, "Math root is outside the configured rules.");
                    Require(channel.MathFollowChannel is null or OutputChannel.MathFollowNone || channel.MathFollowChannel < Constants.NUM_OUTPUT_CHANNELS, "Invalid math follower channel.");
                    Require(channel.MathOutputMode is null or <= 1, "Invalid math output mode.");
                    Require(channel.DelayedOnTime is null or >= 0 and <= 3600000 && channel.DelayedOffTime is null or >= 0 and <= 3600000 &&
                        channel.RunOnTime is null or >= 0 and <= 3600000, "Invalid channel delay.");
                    Require(channel.SoftStartTime is null or >= 0 and <= 5 && channel.SoftStopTime is null or >= 0 and <= 5 &&
                        channel.IntermittentOnTime is null or >= 0 and <= 10 && channel.IntermittentOffTime is null or >= 0 and <= 10, "Invalid channel timing.");
                    Require(channel.DelayedOn is null or <= 1 && channel.DelayedOff is null or <= 1 && channel.DelayedOffTrigger is null or <= 1 && channel.SoftStartEnabled is null or <= 1 && channel.SoftStopEnabled is null or <= 1 && channel.MultiChannel is null or <= 1, "Invalid channel flag.");
                    if (channel.MathRules != null)
                    {
                        Require(channel.MathRules.Count <= Constants.NUM_MATH_RULE_SLOTS, "Too many math rules.");
                        foreach (var rule in channel.MathRules)
                        {
                            Require(rule != null, "A math rule is null.");
                            Require(rule!.Operation is null or <= (byte)OutputChannel.MathOperation.TOGGLE &&
                                rule.OperandAType is null or <= (byte)OutputChannel.MathOperandType.CanSignal &&
                                rule.OperandBType is null or <= (byte)OutputChannel.MathOperandType.CanSignal &&
                                rule.HysteresisEnabled is null or <= 1, "Invalid math rule.");
                            Require(Finite(rule.OnThreshold, rule.OffThreshold, rule.OperandAConstant, rule.OperandBConstant), "A math rule setting is not finite.");
                        }
                    }
                }
            }
            if (snapshot.DigitalInputs != null)
                foreach (var input in snapshot.DigitalInputs)
                    Require(input != null && (input.InputNumber is null or >= 1 and <= Constants.NUM_DIGITAL_INPUTS), "Invalid digital input.");
            if (snapshot.AnalogueInputs != null)
                foreach (var input in snapshot.AnalogueInputs)
                {
                    Require(input != null, "An analogue input is null.");
                    Require(input!.InputNumber is null or >= 1 and <= Constants.NUM_ANALOGUE_INPUTS, "Invalid analogue input number.");
                    Require(!input.ChanType.HasValue || Enum.IsDefined(input.ChanType.Value), "Invalid analogue input type.");
                    Require(!input.Units.HasValue || Enum.IsDefined(input.Units.Value), "Invalid analogue input units.");
                    Require(input.CalibrationPoints is null or >= 2 and <= 3, "Invalid calibration point count.");
                    Require(Finite(input.CalibrationVolt1, input.CalibrationVolt2, input.CalibrationVolt3,
                        input.CalibrationValue1, input.CalibrationValue2, input.CalibrationValue3, input.ConfigRangeMin,
                        input.ConfigRangeMax, input.NTCBeta, input.NTCNominalResistance), "An analogue setting is not finite.");
                }

            if (snapshot.CanMessages != null)
            {
                var indices = new HashSet<int>();
                foreach (var message in snapshot.CanMessages)
                {
                    Require(message != null, "A CAN message is null.");
                    Require(message!.Index >= 0 && message.Index < Constants.MAX_CUSTOM_CAN_MESSAGES && indices.Add(message.Index), "Invalid or duplicate CAN message slot.");
                    Require(message.Dlc <= 8 && message.Id <= ((message.Flags & 2) != 0 ? 0x1FFFFFFFu : 0x7FFu), "Invalid CAN message ID or DLC.");
                    Require((message.Flags & ~0x0F) == 0 && ((message.Flags & 8) == 0 || (message.Flags & 4) != 0) &&
                        (message.TxTrigger == Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS || message.TxTrigger < Constants.NUM_OUTPUT_CHANNELS),
                        "Invalid CAN message flags or trigger.");
                }
            }
            if (snapshot.CanSignals != null)
            {
                var indices = new HashSet<int>();
                foreach (var signal in snapshot.CanSignals)
                {
                    Require(signal != null, "A CAN signal is null.");
                    Require(signal!.Index >= 0 && signal.Index < Constants.MAX_CUSTOM_CAN_SIGNALS && indices.Add(signal.Index), "Invalid or duplicate CAN signal slot.");
                    Require(signal.MessageIndex < Constants.MAX_CUSTOM_CAN_MESSAGES && signal.StartBit < 64 && signal.BitLength <= 64 && ((signal.Flags & 1) == 0 || signal.BitLength > 0), "Invalid CAN signal reference or bit range.");
                    Require((signal.Flags & ~0x07) == 0 &&
                        (signal.ValueSource == Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT || signal.ValueSource < Constants.NUM_OUTPUT_CHANNELS) &&
                        ((signal.Flags & 1) == 0 || CustomCanBitLayout.GetOccupiedBits(signal.StartBit, signal.BitLength, (signal.Flags & 2) != 0).Count == signal.BitLength),
                        "Invalid CAN signal flags, source or bit layout.");
                    Require(Finite(signal.Factor, signal.Offset), "A CAN signal setting is not finite.");
                }
            }
            if (snapshot.CustomCanSignalMetadata != null)
                foreach (var metadata in snapshot.CustomCanSignalMetadata)
                {
                    Require(metadata != null, "A CAN metadata entry is null.");
                    Require(metadata!.SignalIndex is null or >= 0 and < Constants.MAX_CUSTOM_CAN_SIGNALS &&
                        metadata.MessageIndex is null or >= 0 and < Constants.MAX_CUSTOM_CAN_MESSAGES &&
                        metadata.StartBit is null or < 64 && metadata.BitLength is null or >= 1 and <= 64 &&
                        metadata.MessageId is null or <= 0x1FFFFFFF, "Invalid CAN metadata identity.");
                    Require(Finite(metadata.Factor, metadata.Offset, metadata.MinValue, metadata.MaxValue), "A CAN metadata setting is not finite.");
                }
            if (snapshot.SystemParameters is { } system)
            {
                Require(system.ChannelDataCANID is null or <= 0x7FF && system.DigitalInputDataCANID is null or <= 0x7FF &&
                    system.AnalogueInputDataCANID is null or <= 0x7FF && system.SystemDataCANID is null or <= 0x7FF &&
                    system.SystemConfigCANID is null or <= 0x7FF && system.ConfigDataCANID is null or <= 0x7FF, "Invalid system CAN ID.");
                Require(system.TimeZoneRule == null || system.TimeZoneRule.Length == 0 || system.TimeZoneRule.Length == Constants.TIME_ZONE_RULE_LENGTH, "Invalid time zone rule length.");
                Require(Finite(system.SystemCurrentLimit) && system.SystemCurrentLimit is null or >= 0 and <= 150, "Invalid system current limit.");
            }
            if (snapshot.IoFlowGraph is { } graph)
                Require(graph.Canvases != null && graph.Nodes != null && graph.StandaloneLogicRules != null && graph.OutputRootLogicNodeByChannel != null &&
                    graph.Canvases.All(canvas => canvas != null) && graph.Nodes.All(node => node != null) && graph.StandaloneLogicRules.All(rule => rule != null), "Invalid I/O Flow graph.");
        }

        private static ConfigSnapshot BuildSnapshot(DataStructures settingsData, IoFlowGraphConfigSnapshot? ioFlowGraph)
        {
            return new ConfigSnapshot
            {
                CanMessages = settingsData.CustomCanMessagesStaticData.Select(message => new CanMessageConfigSnapshot
                {
                    Index = message.Index, Id = message.Id, Dlc = message.Dlc, Flags = message.Flags,
                    TimeoutMs = message.TimeoutMs, TxTrigger = message.TxTrigger,
                }).ToList(),
                CanSignals = settingsData.CustomCanSignalsStaticData.Select(signal => new CanSignalConfigSnapshot
                {
                    Index = signal.Index, MessageIndex = signal.MessageIndex, StartBit = signal.StartBit,
                    BitLength = signal.BitLength, Flags = signal.Flags, ValueSource = signal.ValueSource,
                    Factor = signal.Factor, Offset = signal.Offset,
                }).ToList(),
                FormatVersion = 1,
                IoFlowGraph = ioFlowGraph,
                Channels = settingsData.ChannelsStaticData.Select(channel => new OutputChannelSnapshot
                {
                    ChanType = channel.ChanType,
                    Category = channel.Category,
                    PWMSetDuty = channel.PWMSetDuty,
                    Name = ToStringName(channel.Name),
                    CurrentThresholdHigh = channel.CurrentThresholdHigh,
                    CurrentThresholdLow = channel.CurrentThresholdLow,
                    RetryCount = channel.RetryCount,
                    InrushDelay = channel.InrushDelay,
                    InrushCurrentLimit = channel.InrushCurrentLimit,
                    MultiChannel = channel.MultiChannel,
                    GroupNumber = channel.GroupNumber,
                    ControlPin = channel.ControlPin,
                    CurrentSensePin = channel.CurrentSensePin,
                    InputControlPin = channel.InputControlPin,
                    InputControlLabel = InputPinCatalog.GetLabelForPin(channel.InputControlPin),
                    OnThreshold = channel.OnThreshold,
                    OffThreshold = channel.OffThreshold,
                    ScaleMin = channel.ScaleMin,
                    ScaleMax = channel.ScaleMax,
                    PWMMin = channel.PWMMin,
                    PWMMax = channel.PWMMax,
                    SoftStartEnabled = channel.SoftStartEnabled,
                    SoftStartTime = channel.SoftStartTime,
                    SoftStopEnabled = channel.SoftStopEnabled,
                    SoftStopTime = channel.SoftStopTime,
                    IntermittentOnTime = channel.IntermittentOnTime,
                    IntermittentOffTime = channel.IntermittentOffTime,
                    MathRuleCount = channel.MathRuleCount,
                    MathRootRule = channel.MathRootRule,
                    MathFollowChannel = channel.MathFollowChannel == OutputChannel.MathFollowNone ? null : channel.MathFollowChannel,
                    MathOutputMode = channel.MathOutputMode,
                    MathRules = channel.MathRules
                        .Take(Constants.NUM_MATH_RULE_SLOTS)
                        .Select(rule => new MathRuleSnapshot
                        {
                            Operation = rule.Operation,
                            HysteresisEnabled = rule.HysteresisEnabled,
                            OnThreshold = rule.OnThreshold,
                            OffThreshold = rule.OffThreshold,
                            OperandAType = rule.OperandAType,
                            OperandAIndex = rule.OperandAIndex,
                            OperandAConstant = rule.OperandAConstant,
                            OperandBType = rule.OperandBType,
                            OperandBIndex = rule.OperandBIndex,
                            OperandBConstant = rule.OperandBConstant,
                        }).ToList(),
                    DelayedOn = channel.DelayedOn,
                    DelayedOnTime = channel.DelayedOnTime,
                    DelayedOff = channel.DelayedOff,
                    DelayedOffTime = channel.DelayedOffTime,
                    DelayedOffTrigger = channel.DelayedOffTrigger,
                }).ToList(),
                DigitalInputs = settingsData.DigitalInputsStaticData.Select(input => new DigitalInputSnapshot
                {
                    InputNumber = input.InputNumber,
                    IsActiveHigh = input.IsActiveHigh,
                }).ToList(),
                AnalogueInputs = settingsData.AnalogueInputsStaticData.Select(input => new AnalogueInputSnapshot
                {
                    InputNumber = input.InputNumber,
                    ChanType = input.ChanType,
                    Units = input.Units,
                    CalibrationPoints = input.CalibrationPoints,
                    PullUpEnable = input.PullUpEnable,
                    PullDownEnable = input.PullDownEnable,
                    CalibrationVolt1 = input.CalibrationVolt1,
                    CalibrationValue1 = input.CalibrationValue1,
                    CalibrationVolt2 = input.CalibrationVolt2,
                    CalibrationValue2 = input.CalibrationValue2,
                    CalibrationVolt3 = input.CalibrationVolt3,
                    CalibrationValue3 = input.CalibrationValue3,
                    ConfigRangeMin = input.ConfigRangeMin,
                    ConfigRangeMax = input.ConfigRangeMax,
                    NTCBeta = input.NtcBeta,
                    NTCNominalResistance = input.NtcNominalResistance,
                }).ToList(),
                SystemParameters = new SystemParametersSnapshot
                {
                    CANResEnabled = settingsData.SystemParamsStaticData.CANResEnabled,
                    CANBusBitrate = settingsData.SystemParamsStaticData.CANBusBitrate,
                    SystemCurrentLimit = settingsData.SystemParamsStaticData.SystemCurrentLimit,
                    ChannelDataCANID = settingsData.SystemParamsStaticData.ChannelDataCANID,
                    DigitalInputDataCANID = settingsData.SystemParamsStaticData.DigitalInputDataCANID,
                    AnalogueInputDataCANID = settingsData.SystemParamsStaticData.AnalogueInputDataCANID,
                    SystemDataCANID = settingsData.SystemParamsStaticData.SystemDataCANID,
                    SystemConfigCANID = settingsData.SystemParamsStaticData.SystemConfigCANID,
                    ConfigDataCANID = settingsData.SystemParamsStaticData.ConfigDataCANID,
                    IMUWakeWindow = settingsData.SystemParamsStaticData.IMUWakeWindow,
                    SpeedUnitPref = settingsData.SystemParamsStaticData.SpeedUnitPref,
                    DistanceUnitPref = settingsData.SystemParamsStaticData.DistanceUnitPref,
                    AllowData = settingsData.SystemParamsStaticData.AllowData,
                    AllowGPS = settingsData.SystemParamsStaticData.AllowGPS,
                    AllowMotionDetect = settingsData.SystemParamsStaticData.AllowMotionDetect,
                    TimeZoneId = settingsData.SystemParamsStaticData.TimeZoneId,
                    TimeZoneRule = settingsData.SystemParamsStaticData.TimeZoneRule,
                },
                CellularParameters = new CellularParametersSnapshot
                {
                    ConfigVersion = settingsData.CellularParamsStaticData.ConfigVersion,
                    Protocol = settingsData.CellularParamsStaticData.Protocol,
                    UseTLS = settingsData.CellularParamsStaticData.UseTLS,
                    APN = settingsData.CellularParamsStaticData.APN,
                    APNUser = settingsData.CellularParamsStaticData.APNUser,
                    OpenRemoteHost = settingsData.CellularParamsStaticData.OpenRemoteHost,
                    OpenRemotePort = settingsData.CellularParamsStaticData.OpenRemotePort,
                    OpenRemoteRealm = settingsData.CellularParamsStaticData.OpenRemoteRealm,
                    OpenRemoteAssetId = settingsData.CellularParamsStaticData.OpenRemoteAssetId,
                    OpenRemoteAssetName = settingsData.CellularParamsStaticData.OpenRemoteAssetName,
                    MQTTUsername = settingsData.CellularParamsStaticData.MQTTUsername,
                    KeepAliveSeconds = settingsData.CellularParamsStaticData.KeepAliveSeconds,
                    PublishIntervalMs = settingsData.CellularParamsStaticData.PublishIntervalMs,
                    TelemetryUploadMask = settingsData.CellularParamsStaticData.TelemetryUploadMask,
                },
                CustomCanSignalMetadata = settingsData.CustomCanSignalsMetadata.Select(metadata => new CustomCanSignalMetadataSnapshot
                {
                    SignalIndex = metadata.SignalIndex,
                    MessageIndex = metadata.MessageIndex,
                    MessageId = metadata.MessageId,
                    IsExtended = metadata.IsExtended,
                    StartBit = metadata.StartBit,
                    BitLength = metadata.BitLength,
                    IsMotorola = metadata.IsMotorola,
                    IsSigned = metadata.IsSigned,
                    Factor = metadata.Factor,
                    Offset = metadata.Offset,
                    Name = metadata.Name,
                    MessageName = metadata.MessageName,
                    MinValue = metadata.MinValue,
                    MaxValue = metadata.MaxValue,
                    Units = metadata.Units,
                    IdentityFingerprint = metadata.IdentityFingerprint,
                }).ToList(),
            };
        }

        private static string ToStringName(char[]? name)
        {
            return name == null ? string.Empty : new string(name).TrimEnd('\0');
        }

        private static void ApplyLegacyRunOnFallback(OutputChannel targetChannel, OutputChannelSnapshot sourceChannel)
        {
            if (sourceChannel.RunOn is not { } legacyRunOn)
            {
                return;
            }

            if (legacyRunOn == 0)
            {
                return;
            }

            targetChannel.DelayedOff = 1;
            targetChannel.DelayedOffTrigger = (byte)OutputChannel.DelayedOffTriggerMode.IgnitionOff;

            if (sourceChannel.RunOnTime.HasValue)
            {
                targetChannel.DelayedOffTime = sourceChannel.RunOnTime.Value;
            }
        }

        private static char[] ToFixedName(string? value)
        {
            var result = new char[Constants.CHANNEL_NAME_LENGTH];
            if (string.IsNullOrEmpty(value))
            {
                return result;
            }

            value.CopyTo(0, result, 0, Math.Min(value.Length, result.Length));
            return result;
        }
    }

    public sealed class ConfigSnapshot
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CanMessageConfigSnapshot>? CanMessages { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CanSignalConfigSnapshot>? CanSignals { get; set; }
        public int FormatVersion { get; set; } = 1;

        public IoFlowGraphConfigSnapshot? IoFlowGraph { get; set; }

        public List<OutputChannelSnapshot>? Channels { get; set; }

        public List<DigitalInputSnapshot>? DigitalInputs { get; set; }

        public List<AnalogueInputSnapshot>? AnalogueInputs { get; set; }

        public SystemParametersSnapshot? SystemParameters { get; set; }

        public CellularParametersSnapshot? CellularParameters { get; set; }

        public List<CustomCanSignalMetadataSnapshot>? CustomCanSignalMetadata { get; set; }
    }

    public sealed class CanMessageConfigSnapshot
    {
        public int Index { get; set; }
        public uint Id { get; set; }
        public byte Dlc { get; set; }
        public byte Flags { get; set; }
        public ushort TimeoutMs { get; set; }
        public byte TxTrigger { get; set; }
    }

    public sealed class CanSignalConfigSnapshot
    {
        public int Index { get; set; }
        public byte MessageIndex { get; set; }
        public byte StartBit { get; set; }
        public byte BitLength { get; set; }
        public byte Flags { get; set; }
        public byte ValueSource { get; set; }
        public float Factor { get; set; }
        public float Offset { get; set; }
    }

    public sealed class OutputChannelSnapshot
    {
        public OutputChannel.ChannelType? ChanType { get; set; }

        public OutputChannel.ChannelCategory? Category { get; set; }

        public byte? PWMSetDuty { get; set; }

        // Legacy field: Enabled is derived by the controller; read for old files but ignored.
        public byte? Enabled { get; set; }

        public string? Name { get; set; }

        public int? AnalogRaw { get; set; }

        public float? CurrentValue { get; set; }

        public bool? Override { get; set; }

        public float? CurrentThresholdHigh { get; set; }

        public float? CurrentThresholdLow { get; set; }

        public byte? RetryCount { get; set; }

        public float? InrushDelay { get; set; }

        public float? InrushCurrentLimit { get; set; }

        public byte? MultiChannel { get; set; }

        public byte? GroupNumber { get; set; }

        public byte? ControlPin { get; set; }

        public byte? CurrentSensePin { get; set; }

        public byte? InputControlPin { get; set; }

        public string? InputControlLabel { get; set; }

        public float? OnThreshold { get; set; }

        public float? OffThreshold { get; set; }

        public float? ScaleMin { get; set; }

        public float? ScaleMax { get; set; }

        public byte? PWMMin { get; set; }

        public byte? PWMMax { get; set; }

        public byte? ErrorFlags { get; set; }

        public byte? SoftStartEnabled { get; set; }

        public float? SoftStartTime { get; set; }

        public byte? SoftStopEnabled { get; set; }

        public float? SoftStopTime { get; set; }

        public byte? RunOn { get; set; }

        public int? RunOnTime { get; set; }

        public byte? DelayedOn { get; set; }

        public int? DelayedOnTime { get; set; }

        public byte? DelayedOff { get; set; }

        public int? DelayedOffTime { get; set; }

        public byte? DelayedOffTrigger { get; set; }

        public float? IntermittentOnTime { get; set; }

        public float? IntermittentOffTime { get; set; }

        public byte? MathRuleCount { get; set; }

        public byte? MathRootRule { get; set; }

        /// <summary>Zero-based owner channel index when this channel shares another channel's rules.</summary>
        public byte? MathFollowChannel { get; set; }

        public byte? MathOutputMode { get; set; }

        public float? MathCalculatedRootValue { get; set; }

        public byte? MathError { get; set; }

        public List<MathRuleSnapshot>? MathRules { get; set; }
    }

    public sealed class MathRuleSnapshot
    {
        public byte? Operation { get; set; }

        public byte? HysteresisEnabled { get; set; }

        public float? OnThreshold { get; set; }

        public float? OffThreshold { get; set; }

        public byte? OperandAType { get; set; }

        public byte? OperandAIndex { get; set; }

        public float? OperandAConstant { get; set; }

        public byte? OperandBType { get; set; }

        public byte? OperandBIndex { get; set; }

        public float? OperandBConstant { get; set; }

        public float? RuntimeValue { get; set; }

        public byte? RuntimeError { get; set; }

        public byte? Evaluated { get; set; }
    }

    public sealed class DigitalInputSnapshot
    {
        public int? InputNumber { get; set; }

        public bool? IsActiveHigh { get; set; }
    }

    public sealed class AnalogueInputSnapshot
    {
        public int? InputNumber { get; set; }

        public AnalogueInput.AnalogueChannelType? ChanType { get; set; }

        public AnalogueInput.AnalogueUnits? Units { get; set; }

        public byte? CalibrationPoints { get; set; }

        public bool? PullUpEnable { get; set; }

        public bool? PullDownEnable { get; set; }

        public float? CalibrationVolt1 { get; set; }

        public float? CalibrationValue1 { get; set; }

        public float? CalibrationVolt2 { get; set; }

        public float? CalibrationValue2 { get; set; }

        public float? CalibrationVolt3 { get; set; }

        public float? CalibrationValue3 { get; set; }

        public float? ConfigRangeMin { get; set; }

        public float? ConfigRangeMax { get; set; }

        public float? NTCBeta { get; set; }

        public float? NTCNominalResistance { get; set; }
    }

    public sealed class CustomCanSignalMetadataSnapshot
    {
        public int? SignalIndex { get; set; }

        public int? MessageIndex { get; set; }

        public uint? MessageId { get; set; }

        public bool? IsExtended { get; set; }

        public byte? StartBit { get; set; }

        public byte? BitLength { get; set; }

        public bool? IsMotorola { get; set; }

        public bool? IsSigned { get; set; }

        public float? Factor { get; set; }

        public float? Offset { get; set; }

        public string? Name { get; set; }

        public string? MessageName { get; set; }

        public float? MinValue { get; set; }

        public float? MaxValue { get; set; }

        public string? Units { get; set; }

        public string? IdentityFingerprint { get; set; }
    }

    public sealed class SystemParametersSnapshot
    {
        public int? SystemTemperature { get; set; }

        public float? SIMModuleTemp { get; set; }

        public float? IMUTemp { get; set; }

        public bool? CANResEnabled { get; set; }

        public uint? CANBusBitrate { get; set; }

        public float? VBatt { get; set; }

        public float? SystemCurrent { get; set; }

        public float? SystemCurrentLimit { get; set; }

        public ushort? ErrorFlags { get; set; }

        public ushort? ChannelDataCANID { get; set; }

        public ushort? DigitalInputDataCANID { get; set; }

        public ushort? AnalogueInputDataCANID { get; set; }

        public ushort? SystemDataCANID { get; set; }

        public ushort? SystemConfigCANID { get; set; }

        public ushort? ConfigDataCANID { get; set; }

        public uint? IMUWakeWindow { get; set; }

        public bool? SpeedUnitPref { get; set; }

        public bool? DistanceUnitPref { get; set; }

        public bool? AllowData { get; set; }

        public bool? AllowGPS { get; set; }

        public bool? AllowMotionDetect { get; set; }

        public ushort? MobileSignalPercent { get; set; }

        public string? TimeZoneId { get; set; }

        public byte[]? TimeZoneRule { get; set; }
    }

    public sealed class CellularParametersSnapshot
    {
        public byte? ConfigVersion { get; set; }

        public byte? Protocol { get; set; }

        public bool? UseTLS { get; set; }

        public string? APN { get; set; }

        public string? APNUser { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? APNPassword { get; set; }

        public string? OpenRemoteHost { get; set; }

        public ushort? OpenRemotePort { get; set; }

        public string? ClientID { get; set; }

        public string? OpenRemoteRealm { get; set; }

        public string? OpenRemoteAssetId { get; set; }

        public string? OpenRemoteAssetName { get; set; }

        public string? MQTTUsername { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MQTTPassword { get; set; }

        public ushort? KeepAliveSeconds { get; set; }

        public uint? PublishIntervalMs { get; set; }

        public uint? TelemetryUploadMask { get; set; }
    }

    public sealed class IoFlowGraphConfigSnapshot
    {
        public List<IoFlowCanvasConfigSnapshot> Canvases { get; set; } = [];

        public int SelectedCanvasId { get; set; } = 1;

        public string ControllerSettingsSignature { get; set; } = string.Empty;

        public List<IoFlowNodeConfigSnapshot> Nodes { get; set; } = [];

        public List<IoFlowStandaloneLogicRuleConfigSnapshot> StandaloneLogicRules { get; set; } = [];

        public Dictionary<int, int> OutputRootLogicNodeByChannel { get; set; } = [];
    }

    public sealed class IoFlowNodeConfigSnapshot
    {
        public int CanvasId { get; set; } = 1;
        public string Kind { get; set; } = string.Empty;

        public int Id { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public byte Option { get; set; }

        public byte Option2 { get; set; }
    }

    public sealed class IoFlowCanvasConfigSnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double ViewportX { get; set; }
        public double ViewportY { get; set; }
        public double ViewportZoom { get; set; } = 1;
    }

    public sealed class IoFlowStandaloneLogicRuleConfigSnapshot
    {
        public int LogicNodeId { get; set; }

        public byte Operation { get; set; }

        public byte HysteresisEnabled { get; set; }

        public float OnThreshold { get; set; }

        public float OffThreshold { get; set; }

        public byte OperandAType { get; set; }

        public byte OperandAIndex { get; set; }

        public float OperandAConstant { get; set; }

        public byte OperandBType { get; set; }

        public byte OperandBIndex { get; set; }

        public float OperandBConstant { get; set; }
    }
}
