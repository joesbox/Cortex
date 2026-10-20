using Cortex.Models;

namespace Cortex.ViewModels.IoFlow;

public sealed class MathRuleNodeViewModel : IoFlowNodeViewModel
{
    public MathRuleNodeViewModel(OutputChannel ownerChannel, int ruleIndex, OutputChannel.MathRule rule, Avalonia.Point location)
        : base($"CH{ownerChannel.ChannelNumber} Rule {ruleIndex + 1}", location, ConnectorPinPalette.OutputMath)
    {
        OwnerChannel = ownerChannel;
        RuleIndex = ruleIndex;
        Rule = rule;
    }

    public OutputChannel OwnerChannel { get; }

    public int OwnerChannelNumber => OwnerChannel.ChannelNumber;

    public int RuleIndex { get; }

    public OutputChannel.MathRule Rule { get; }
}
