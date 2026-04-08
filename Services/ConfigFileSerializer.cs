using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Cortex.Services
{
    public static class ConfigFileSerializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        public static string SerializeSettings(DataStructures settingsData)
        {
            var snapshot = BuildSnapshot(settingsData);
            return JsonSerializer.Serialize(snapshot, JsonOptions);
        }

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

                if (snapshot.Channels == null && snapshot.DigitalInputs == null && snapshot.AnalogueInputs == null && snapshot.SystemParameters == null)
                {
                    error = "Configuration file does not contain any recognized configuration sections.";
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                snapshot = null;
                error = $"Failed to parse configuration file: {ex.Message}";
                return false;
            }
        }

        public static void ApplySnapshot(DataStructures target, ConfigSnapshot snapshot)
        {
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
                    targetChannel.Enabled = sourceChannel.Enabled ?? targetChannel.Enabled;
                    if (sourceChannel.Name != null)
                    {
                        targetChannel.Name = ToFixedName(sourceChannel.Name);
                    }

                    targetChannel.AnalogRaw = sourceChannel.AnalogRaw ?? targetChannel.AnalogRaw;
                    targetChannel.CurrentValue = sourceChannel.CurrentValue ?? targetChannel.CurrentValue;
                    targetChannel.Override = sourceChannel.Override ?? targetChannel.Override;
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
                    targetChannel.OnThreshold = sourceChannel.OnThreshold ?? targetChannel.OnThreshold;
                    targetChannel.OffThreshold = sourceChannel.OffThreshold ?? targetChannel.OffThreshold;
                    targetChannel.ScaleMin = sourceChannel.ScaleMin ?? targetChannel.ScaleMin;
                    targetChannel.ScaleMax = sourceChannel.ScaleMax ?? targetChannel.ScaleMax;
                    targetChannel.PWMMin = sourceChannel.PWMMin ?? targetChannel.PWMMin;
                    targetChannel.PWMMax = sourceChannel.PWMMax ?? targetChannel.PWMMax;
                    targetChannel.RunOn = sourceChannel.RunOn ?? targetChannel.RunOn;
                    targetChannel.RunOnTime = sourceChannel.RunOnTime ?? targetChannel.RunOnTime;
                    targetChannel.ErrorFlags = sourceChannel.ErrorFlags ?? targetChannel.ErrorFlags;
                    targetChannel.SoftStartEnabled = sourceChannel.SoftStartEnabled ?? targetChannel.SoftStartEnabled;
                    targetChannel.SoftStartTime = sourceChannel.SoftStartTime ?? targetChannel.SoftStartTime;
                    targetChannel.SoftStopEnabled = sourceChannel.SoftStopEnabled ?? targetChannel.SoftStopEnabled;
                    targetChannel.SoftStopTime = sourceChannel.SoftStopTime ?? targetChannel.SoftStopTime;
                    targetChannel.IntermittentOnTime = sourceChannel.IntermittentOnTime ?? targetChannel.IntermittentOnTime;
                    targetChannel.IntermittentOffTime = sourceChannel.IntermittentOffTime ?? targetChannel.IntermittentOffTime;
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
                target.SystemParamsStaticData.SystemTemperature = snapshot.SystemParameters.SystemTemperature ?? target.SystemParamsStaticData.SystemTemperature;
                target.SystemParamsStaticData.SIMModuleTemp = snapshot.SystemParameters.SIMModuleTemp ?? target.SystemParamsStaticData.SIMModuleTemp;
                target.SystemParamsStaticData.IMUTemp = snapshot.SystemParameters.IMUTemp ?? target.SystemParamsStaticData.IMUTemp;
                target.SystemParamsStaticData.CANResEnabled = snapshot.SystemParameters.CANResEnabled ?? target.SystemParamsStaticData.CANResEnabled;
                target.SystemParamsStaticData.VBatt = snapshot.SystemParameters.VBatt ?? target.SystemParamsStaticData.VBatt;
                target.SystemParamsStaticData.SystemCurrent = snapshot.SystemParameters.SystemCurrent ?? target.SystemParamsStaticData.SystemCurrent;
                target.SystemParamsStaticData.SystemCurrentLimit = snapshot.SystemParameters.SystemCurrentLimit ?? target.SystemParamsStaticData.SystemCurrentLimit;
                target.SystemParamsStaticData.ErrorFlags = snapshot.SystemParameters.ErrorFlags ?? target.SystemParamsStaticData.ErrorFlags;
                target.SystemParamsStaticData.ChannelDataCANID = snapshot.SystemParameters.ChannelDataCANID ?? target.SystemParamsStaticData.ChannelDataCANID;
                target.SystemParamsStaticData.SystemDataCANID = snapshot.SystemParameters.SystemDataCANID ?? target.SystemParamsStaticData.SystemDataCANID;
                target.SystemParamsStaticData.SystemConfigCANID = snapshot.SystemParameters.SystemConfigCANID ?? target.SystemParamsStaticData.SystemConfigCANID;
                target.SystemParamsStaticData.ConfigDataCANID = snapshot.SystemParameters.ConfigDataCANID ?? target.SystemParamsStaticData.ConfigDataCANID;
                target.SystemParamsStaticData.IMUWakeWindow = snapshot.SystemParameters.IMUWakeWindow ?? target.SystemParamsStaticData.IMUWakeWindow;
                target.SystemParamsStaticData.SpeedUnitPref = snapshot.SystemParameters.SpeedUnitPref ?? target.SystemParamsStaticData.SpeedUnitPref;
                target.SystemParamsStaticData.DistanceUnitPref = snapshot.SystemParameters.DistanceUnitPref ?? target.SystemParamsStaticData.DistanceUnitPref;
                target.SystemParamsStaticData.AllowData = snapshot.SystemParameters.AllowData ?? target.SystemParamsStaticData.AllowData;
                target.SystemParamsStaticData.AllowGPS = snapshot.SystemParameters.AllowGPS ?? target.SystemParamsStaticData.AllowGPS;
                target.SystemParamsStaticData.AllowMotionDetect = snapshot.SystemParameters.AllowMotionDetect ?? target.SystemParamsStaticData.AllowMotionDetect;
                target.SystemParamsStaticData.MobileSignalPercent = snapshot.SystemParameters.MobileSignalPercent ?? target.SystemParamsStaticData.MobileSignalPercent;
                target.SystemParamsStaticData.TimeZoneId = snapshot.SystemParameters.TimeZoneId ?? target.SystemParamsStaticData.TimeZoneId;
                target.SystemParamsStaticData.TimeZoneRule = snapshot.SystemParameters.TimeZoneRule ?? target.SystemParamsStaticData.TimeZoneRule;
            }
        }

        private static ConfigSnapshot BuildSnapshot(DataStructures settingsData)
        {
            return new ConfigSnapshot
            {
                FormatVersion = 1,
                Channels = settingsData.ChannelsStaticData.Select(channel => new OutputChannelSnapshot
                {
                    ChanType = channel.ChanType,
                    Category = channel.Category,
                    PWMSetDuty = channel.PWMSetDuty,
                    Enabled = channel.Enabled,
                    Name = ToStringName(channel.Name),
                    AnalogRaw = channel.AnalogRaw,
                    CurrentValue = channel.CurrentValue,
                    Override = channel.Override,
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
                    RunOn = channel.RunOn,
                    RunOnTime = channel.RunOnTime,
                    ErrorFlags = channel.ErrorFlags,
                    SoftStartEnabled = channel.SoftStartEnabled,
                    SoftStartTime = channel.SoftStartTime,
                    SoftStopEnabled = channel.SoftStopEnabled,
                    SoftStopTime = channel.SoftStopTime,
                    IntermittentOnTime = channel.IntermittentOnTime,
                    IntermittentOffTime = channel.IntermittentOffTime,
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
                    SystemTemperature = settingsData.SystemParamsStaticData.SystemTemperature,
                    SIMModuleTemp = settingsData.SystemParamsStaticData.SIMModuleTemp,
                    IMUTemp = settingsData.SystemParamsStaticData.IMUTemp,
                    CANResEnabled = settingsData.SystemParamsStaticData.CANResEnabled,
                    VBatt = settingsData.SystemParamsStaticData.VBatt,
                    SystemCurrent = settingsData.SystemParamsStaticData.SystemCurrent,
                    SystemCurrentLimit = settingsData.SystemParamsStaticData.SystemCurrentLimit,
                    ErrorFlags = settingsData.SystemParamsStaticData.ErrorFlags,
                    ChannelDataCANID = settingsData.SystemParamsStaticData.ChannelDataCANID,
                    SystemDataCANID = settingsData.SystemParamsStaticData.SystemDataCANID,
                    SystemConfigCANID = settingsData.SystemParamsStaticData.SystemConfigCANID,
                    ConfigDataCANID = settingsData.SystemParamsStaticData.ConfigDataCANID,
                    IMUWakeWindow = settingsData.SystemParamsStaticData.IMUWakeWindow,
                    SpeedUnitPref = settingsData.SystemParamsStaticData.SpeedUnitPref,
                    DistanceUnitPref = settingsData.SystemParamsStaticData.DistanceUnitPref,
                    AllowData = settingsData.SystemParamsStaticData.AllowData,
                    AllowGPS = settingsData.SystemParamsStaticData.AllowGPS,
                    AllowMotionDetect = settingsData.SystemParamsStaticData.AllowMotionDetect,
                    MobileSignalPercent = settingsData.SystemParamsStaticData.MobileSignalPercent,
                    TimeZoneId = settingsData.SystemParamsStaticData.TimeZoneId,
                    TimeZoneRule = settingsData.SystemParamsStaticData.TimeZoneRule,
                },
            };
        }

        private static string ToStringName(char[]? name)
        {
            return name == null ? string.Empty : new string(name).TrimEnd('\0');
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
        public int FormatVersion { get; set; } = 1;

        public List<OutputChannelSnapshot>? Channels { get; set; }

        public List<DigitalInputSnapshot>? DigitalInputs { get; set; }

        public List<AnalogueInputSnapshot>? AnalogueInputs { get; set; }

        public SystemParametersSnapshot? SystemParameters { get; set; }
    }

    public sealed class OutputChannelSnapshot
    {
        public OutputChannel.ChannelType? ChanType { get; set; }

        public OutputChannel.ChannelCategory? Category { get; set; }

        public byte? PWMSetDuty { get; set; }

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

        public byte? RunOn { get; set; }

        public int? RunOnTime { get; set; }

        public byte? ErrorFlags { get; set; }

        public byte? SoftStartEnabled { get; set; }

        public float? SoftStartTime { get; set; }

        public byte? SoftStopEnabled { get; set; }

        public float? SoftStopTime { get; set; }

        public float? IntermittentOnTime { get; set; }

        public float? IntermittentOffTime { get; set; }
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

    public sealed class SystemParametersSnapshot
    {
        public int? SystemTemperature { get; set; }

        public float? SIMModuleTemp { get; set; }

        public float? IMUTemp { get; set; }

        public bool? CANResEnabled { get; set; }

        public float? VBatt { get; set; }

        public float? SystemCurrent { get; set; }

        public float? SystemCurrentLimit { get; set; }

        public ushort? ErrorFlags { get; set; }

        public ushort? ChannelDataCANID { get; set; }

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
}