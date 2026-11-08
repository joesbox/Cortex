using System;
using System.Globalization;
using System.Linq;
using Cortex.Models;

namespace Cortex.ViewModels.IoFlow;

internal static class MathRuntimeDisplayFormatter
{
    public static string FormatOutputEffect(float rootValue, byte outputMode)
    {
        string formattedRoot = FormatLiveValue(rootValue);
        if (!float.IsFinite(rootValue))
        {
            return $"Output effect unavailable (invalid result {formattedRoot})";
        }

        if (outputMode == (byte)OutputChannel.MathOutputModeType.PWM)
        {
            int outputPercent = (int)Math.Clamp(
                Math.Round(rootValue, MidpointRounding.AwayFromZero),
                0d,
                100d);
            return $"PWM output: {outputPercent}% (result {formattedRoot}; clamped to 0-100%)";
        }

        string state = rootValue == 0f ? "OFF" : "ON";
        return $"Digital output: {state} (result {formattedRoot}; zero = OFF, nonzero = ON)";
    }

    public static string FormatValue(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // Live readings use a fixed single decimal so the text width stays stable as values change.
    public static string FormatLiveValue(float value)
    {
        return value.ToString("F1", CultureInfo.InvariantCulture);
    }

    public static string FormatRuleEvaluation(
        OutputChannel.MathRule configuredRule,
        OutputChannel.MathRule liveRule,
        DataStructures? liveData,
        int channelNumber,
        Func<byte, int?>? resolveRuleIndex = null)
    {
        OutputChannel.MathOperation operation = (OutputChannel.MathOperation)configuredRule.Operation;
        string operandA = FormatOperand(
            configuredRule.OperandAType,
            configuredRule.OperandAIndex,
            configuredRule.OperandAConstant,
            liveData,
            channelNumber,
            resolveRuleIndex);
        string expression = operation switch
        {
            OutputChannel.MathOperation.NOT => $"NOT ({operandA})",
            OutputChannel.MathOperation.ABS => $"ABS ({operandA})",
            OutputChannel.MathOperation.TOGGLE => $"TOGGLE ({operandA})",
            _ => $"{operandA} {GetOperationSymbol(operation)} {FormatOperand(
                configuredRule.OperandBType,
                configuredRule.OperandBIndex,
                configuredRule.OperandBConstant,
                liveData,
                channelNumber,
                resolveRuleIndex)}",
        };
        return $"{expression} = {FormatRuleResult(configuredRule, liveRule, liveData)}";
    }

    public static string FormatRuleResult(
        OutputChannel.MathRule configuredRule,
        OutputChannel.MathRule liveRule,
        DataStructures? liveData = null)
    {
        OutputChannel.MathOperation operation = (OutputChannel.MathOperation)configuredRule.Operation;
        bool isBooleanResult = operation is OutputChannel.MathOperation.GREATER_THAN
            or OutputChannel.MathOperation.GREATER_EQUAL
            or OutputChannel.MathOperation.LESS_THAN
            or OutputChannel.MathOperation.LESS_EQUAL
            or OutputChannel.MathOperation.EQUAL
            or OutputChannel.MathOperation.NOT_EQUAL
            or OutputChannel.MathOperation.AND
            or OutputChannel.MathOperation.OR
            or OutputChannel.MathOperation.XOR
            or OutputChannel.MathOperation.NOT
            or OutputChannel.MathOperation.TOGGLE;
        return liveRule.RuntimeError != 0
            ? FormatRuleError(configuredRule, liveRule.RuntimeError, liveData)
            : !liveRule.IsEvaluated
                ? "unavailable (not evaluated)"
                : isBooleanResult
                    ? liveRule.RuntimeValue != 0 ? "True" : "False"
                    : FormatLiveValue(liveRule.RuntimeValue);
    }

    public static string FormatRuleError(
        OutputChannel.MathRule configuredRule,
        byte runtimeError,
        DataStructures? liveData)
    {
        if (runtimeError == 2)
        {
            return GetCanSignalStatus(configuredRule, liveData) switch
            {
                CustomCanStatus.NotReceived => "Waiting for first CAN frame",
                CustomCanStatus.Timeout => "CAN input timed out; waiting for next frame",
                CustomCanStatus.InvalidFrame => "Waiting for a valid CAN frame",
                CustomCanStatus.DecodeError => "CAN signal could not be decoded",
                CustomCanStatus.Disabled => "CAN signal is disabled",
                CustomCanStatus.InvalidConfig => "CAN signal configuration is invalid",
                _ => $"unavailable (rule error {runtimeError})",
            };
        }

        return $"unavailable (rule error {runtimeError})";
    }

    public static string FormatOperand(
        OutputChannel.MathRule rule,
        bool operandA,
        DataStructures? liveData,
        int channelNumber,
        Func<byte, int?>? resolveRuleIndex = null)
    {
        return FormatOperand(
            operandA ? rule.OperandAType : rule.OperandBType,
            operandA ? rule.OperandAIndex : rule.OperandBIndex,
            operandA ? rule.OperandAConstant : rule.OperandBConstant,
            liveData,
            channelNumber,
            resolveRuleIndex);
    }

    private static string FormatOperand(
        byte operandTypeValue,
        byte operandIndex,
        float constant,
        DataStructures? liveData,
        int channelNumber,
        Func<byte, int?>? resolveRuleIndex)
    {
        OutputChannel.MathOperandType operandType = (OutputChannel.MathOperandType)operandTypeValue;
        string label;
        string value;
        switch (operandType)
        {
            case OutputChannel.MathOperandType.Constant:
                label = "constant";
                value = FormatValue(constant);
                break;
            case OutputChannel.MathOperandType.AnalogueInput:
                AnalogueInput? analogueInput = liveData?.AnalogueInputsLiveData
                    .FirstOrDefault(input => input.InputNumber == operandIndex + 1);
                label = $"Analogue input {operandIndex + 1}";
                value = analogueInput?.InputValue is float inputValue
                    ? FormatWithUnit(inputValue, GetAnalogueUnit(analogueInput.Units))
                    : analogueInput?.InputVoltage is float inputVoltage
                        ? FormatWithUnit(inputVoltage, "V")
                        : "--";
                break;
            case OutputChannel.MathOperandType.DigitalInput:
                DigitalInput? digitalInput = liveData?.DigitalInputsLiveData
                    .FirstOrDefault(input => input.InputNumber == operandIndex + 1);
                label = $"Digital input {operandIndex + 1}";
                value = digitalInput?.LiveElectricalState is bool state
                    ? state ? "ON" : "OFF"
                    : "--";
                break;
            case OutputChannel.MathOperandType.Channel:
                OutputChannel? sourceChannel = liveData?.ChannelsLiveData.ElementAtOrDefault(operandIndex);
                label = $"CH{operandIndex + 1}";
                value = sourceChannel?.ChanType == OutputChannel.ChannelType.MATH
                    ? FormatLiveValue(sourceChannel.MathCalculatedRootValue)
                    : sourceChannel?.IsPWMChannel == true
                        ? $"{sourceChannel.PWMSetDuty}%"
                        : "--";
                break;
            case OutputChannel.MathOperandType.Rule:
                int? ruleIndex = resolveRuleIndex == null
                    ? operandIndex
                    : resolveRuleIndex(operandIndex);
                OutputChannel? ownerChannel = liveData?.ChannelsLiveData.ElementAtOrDefault(channelNumber - 1);
                OutputChannel.MathRule? sourceRule = ruleIndex is int index
                    ? ownerChannel?.MathRules.ElementAtOrDefault(index)
                    : null;
                label = ruleIndex is int resolvedRuleIndex
                    ? $"Rule {resolvedRuleIndex + 1}"
                    : $"Rule source {operandIndex} unavailable";
                value = sourceRule?.IsEvaluated == true && sourceRule.RuntimeError == 0
                    ? FormatLiveValue(sourceRule.RuntimeValue)
                    : "--";
                break;
            case OutputChannel.MathOperandType.SystemValue:
                label = FormatSystemValue((OutputChannel.MathSystemValue)operandIndex);
                value = liveData?.SystemParams is SystemParameters system
                    ? (OutputChannel.MathSystemValue)operandIndex switch
                    {
                        OutputChannel.MathSystemValue.SystemTemperature => FormatWithUnit(system.SystemTemperature, "°C"),
                        OutputChannel.MathSystemValue.SIMModuleTemperature => FormatWithUnit(system.SIMModuleTemp, "°C"),
                        OutputChannel.MathSystemValue.IMUTemperature => FormatWithUnit(system.IMUTemp, "°C"),
                        OutputChannel.MathSystemValue.BatteryVoltage => FormatWithUnit(system.VBatt, "V"),
                        OutputChannel.MathSystemValue.SystemCurrent => FormatWithUnit(system.SystemCurrent, "A"),
                        _ => "--",
                    }
                    : "--";
                break;
            case OutputChannel.MathOperandType.CanSignal:
                CustomCanSignal? signal = liveData?.CustomCanSignalsLiveData
                    .FirstOrDefault(item => item.Index == operandIndex);
                label = string.IsNullOrWhiteSpace(signal?.Name)
                    ? $"CAN signal {operandIndex + 1}"
                    : signal.Name.Trim();
                value = signal is null
                    ? "--"
                    : signal.Status switch
                    {
                        (byte)CustomCanStatus.NotReceived => "Waiting for first CAN frame",
                        (byte)CustomCanStatus.Timeout => "Timed out; waiting for CAN frame",
                        (byte)CustomCanStatus.InvalidFrame => "Invalid CAN frame",
                        (byte)CustomCanStatus.DecodeError => "CAN signal decode error",
                        (byte)CustomCanStatus.Disabled => "CAN signal disabled",
                        (byte)CustomCanStatus.InvalidConfig => "CAN signal configuration invalid",
                        (byte)CustomCanStatus.Valid => FormatWithUnit(signal.RuntimeValue, signal.Units),
                        _ => "CAN signal status unavailable",
                    };
                break;
            default:
                label = $"Operand {operandIndex}";
                value = "--";
                break;
        }

        return $"{label}: {value}";
    }

    private static string GetOperationSymbol(OutputChannel.MathOperation operation)
    {
        return operation switch
        {
            OutputChannel.MathOperation.ADD => "+",
            OutputChannel.MathOperation.SUBTRACT => "−",
            OutputChannel.MathOperation.MULTIPLY => "×",
            OutputChannel.MathOperation.DIVIDE => "÷",
            OutputChannel.MathOperation.GREATER_THAN => ">",
            OutputChannel.MathOperation.GREATER_EQUAL => "≥",
            OutputChannel.MathOperation.LESS_THAN => "<",
            OutputChannel.MathOperation.LESS_EQUAL => "≤",
            OutputChannel.MathOperation.EQUAL => "==",
            OutputChannel.MathOperation.NOT_EQUAL => "!=",
            OutputChannel.MathOperation.AND => "AND",
            OutputChannel.MathOperation.OR => "OR",
            OutputChannel.MathOperation.XOR => "XOR",
            OutputChannel.MathOperation.MIN => "MIN",
            OutputChannel.MathOperation.MAX => "MAX",
            _ => operation.ToString(),
        };
    }

    private static string FormatSystemValue(OutputChannel.MathSystemValue value)
    {
        return value switch
        {
            OutputChannel.MathSystemValue.SystemTemperature => "System temperature",
            OutputChannel.MathSystemValue.SIMModuleTemperature => "SIM temperature",
            OutputChannel.MathSystemValue.IMUTemperature => "IMU temperature",
            OutputChannel.MathSystemValue.BatteryVoltage => "Battery voltage",
            OutputChannel.MathSystemValue.SystemCurrent => "System current",
            _ => value.ToString(),
        };
    }

    private static string GetAnalogueUnit(AnalogueInput.AnalogueUnits units)
    {
        return units switch
        {
            AnalogueInput.AnalogueUnits.Volts => "V",
            AnalogueInput.AnalogueUnits.Amps => "A",
            AnalogueInput.AnalogueUnits.Celsius => "°C",
            AnalogueInput.AnalogueUnits.Fahrenheit => "°F",
            AnalogueInput.AnalogueUnits.Percent => "%",
            AnalogueInput.AnalogueUnits.RPM => "RPM",
            AnalogueInput.AnalogueUnits.KPH => "km/h",
            AnalogueInput.AnalogueUnits.MPH => "mph",
            AnalogueInput.AnalogueUnits.Bar => "bar",
            AnalogueInput.AnalogueUnits.PSI => "psi",
            _ => string.Empty,
        };
    }

    private static string FormatWithUnit(float value, string? unit)
    {
        string formatted = FormatLiveValue(value);
        return string.IsNullOrWhiteSpace(unit) ? formatted : $"{formatted} {unit}";
    }

    private static CustomCanStatus? GetCanSignalStatus(
        OutputChannel.MathRule rule,
        DataStructures? liveData)
    {
        CustomCanStatus? statusA = GetCanSignalStatus(rule.OperandAType, rule.OperandAIndex, liveData);
        OutputChannel.MathOperation operation = (OutputChannel.MathOperation)rule.Operation;
        bool usesOperandB = operation is not (OutputChannel.MathOperation.NOT
            or OutputChannel.MathOperation.ABS
            or OutputChannel.MathOperation.TOGGLE)
            && rule.HysteresisEnabled == 0;
        CustomCanStatus? statusB = usesOperandB
            ? GetCanSignalStatus(rule.OperandBType, rule.OperandBIndex, liveData)
            : null;

        if (statusA == CustomCanStatus.NotReceived || statusB == CustomCanStatus.NotReceived)
        {
            return CustomCanStatus.NotReceived;
        }

        if (statusA is not null && statusA != CustomCanStatus.Valid)
        {
            return statusA;
        }

        return statusB;
    }

    private static CustomCanStatus? GetCanSignalStatus(
        byte operandType,
        byte operandIndex,
        DataStructures? liveData)
    {
        if ((OutputChannel.MathOperandType)operandType != OutputChannel.MathOperandType.CanSignal)
        {
            return null;
        }

        CustomCanSignal? signal = liveData?.CustomCanSignalsLiveData
            .FirstOrDefault(item => item.Index == operandIndex);
        return signal is null ? null : (CustomCanStatus)signal.Status;
    }
}
