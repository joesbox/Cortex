using System.Collections.Generic;
using System.Linq;
using Cortex.Models;
using Cortex.Services;

namespace Cortex.ViewModels.IoFlow;

public sealed class IoFlowGraphSnapshot
{
    public List<IoFlowCanvasSnapshot> Canvases { get; set; } = [];

    public int SelectedCanvasId { get; set; } = 1;

    public string ControllerSettingsSignature { get; set; } = string.Empty;

    public List<IoFlowNodeSnapshot> Nodes { get; set; } = [];

    public List<IoFlowStandaloneLogicRuleSnapshot> StandaloneLogicRules { get; set; } = [];

    public Dictionary<int, int> OutputRootLogicNodeByChannel { get; set; } = [];

    public IoFlowGraphLayoutSnapshot ToLayoutSnapshot() => new()
    {
        Canvases = Canvases,
        SelectedCanvasId = SelectedCanvasId,
        NodePositions = Nodes.Select(node => new IoFlowNodePositionSnapshot
        {
            CanvasId = node.CanvasId,
            Kind = node.Kind,
            Id = node.Id,
            Option = node.Option,
            X = node.X,
            Y = node.Y,
        }).ToList(),
    };
}

public sealed class IoFlowGraphLayoutSnapshot
{
    public List<IoFlowCanvasSnapshot> Canvases { get; set; } = [];

    public int SelectedCanvasId { get; set; } = 1;

    public List<IoFlowNodePositionSnapshot> NodePositions { get; set; } = [];

    public IoFlowGraphSnapshot ToGraphSnapshot() => new()
    {
        Canvases = Canvases,
        SelectedCanvasId = SelectedCanvasId,
        Nodes = NodePositions.Select(position => new IoFlowNodeSnapshot
        {
            CanvasId = position.CanvasId,
            Kind = position.Kind,
            Id = position.Id,
            Option = position.Option,
            X = position.X,
            Y = position.Y,
        }).ToList(),
    };
}

public sealed class IoFlowNodePositionSnapshot
{
    public int CanvasId { get; set; } = 1;

    public string Kind { get; set; } = string.Empty;

    public int Id { get; set; }

    public byte Option { get; set; }

    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class IoFlowDraftSettings
{
    public ConfigSnapshot InputsAndOutputs { get; set; } = new();
    public List<CustomCanMessage> CanMessages { get; set; } = [];
    public List<CustomCanSignal> CanSignals { get; set; } = [];
}

public sealed class IoFlowNodeSnapshot
{
    public int CanvasId { get; set; } = 1;
    public string Kind { get; set; } = string.Empty;

    public int Id { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public byte Option { get; set; }

    public byte Option2 { get; set; }
}

public sealed class IoFlowCanvasSnapshot
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double ViewportX { get; set; }
    public double ViewportY { get; set; }
    public double ViewportZoom { get; set; } = 1;
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
