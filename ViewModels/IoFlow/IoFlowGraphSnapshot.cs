using System.Collections.Generic;

namespace Cortex.ViewModels.IoFlow;

public sealed class IoFlowGraphSnapshot
{
    public string ControllerSettingsSignature { get; set; } = string.Empty;

    public List<IoFlowNodeSnapshot> Nodes { get; set; } = [];

    public List<IoFlowStandaloneLogicRuleSnapshot> StandaloneLogicRules { get; set; } = [];

    public Dictionary<int, int> OutputRootLogicNodeByChannel { get; set; } = [];
}

public sealed class IoFlowNodeSnapshot
{
    public string Kind { get; set; } = string.Empty;

    public int Id { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public byte Option { get; set; }

    public byte Option2 { get; set; }
}

public sealed class IoFlowStandaloneLogicRuleSnapshot
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
