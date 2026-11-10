using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Nodify.Avalonia.Connections;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowGraphViewModel : ObservableObject
{
    private DataStructures? _settings;
    private DataStructures? _live;
    private int _nextLogicNodeId = 1;
    private readonly Dictionary<int, int> _outputRootLogicNodeByChannel = [];
    private bool _isApplyingMathRulesFromGraph;
    private Point? _nextNodeSpawnLocation;
    private Point _spawnViewportOrigin;
    private Size _spawnViewportSize;
    private string _addNodeWarning = string.Empty;

    private readonly record struct MathRuleValues(
        byte Operation,
        byte HysteresisEnabled,
        float OnThreshold,
        float OffThreshold,
        byte OperandAType,
        byte OperandAIndex,
        float OperandAConstant,
        byte OperandBType,
        byte OperandBIndex,
        float OperandBConstant);

    public bool UseControllerConfigurationValues { get; set; }

    public bool IsApplyingMathRulesFromGraph => _isApplyingMathRulesFromGraph;

    public Func<string, string, string, string, Task<bool>>? ConfirmAction { get; set; }

    /// <summary>
    /// Stages a Custom CAN TX message's trigger channel and enabled state:
    /// (messageIndex, triggerChannelZeroBased, enabled) → success.
    /// </summary>
    public Func<int, byte, bool, Task<bool>>? SetCustomCanTxStateAction { get; set; }

    /// <summary>
    /// Stages a Custom CAN TX message's timing mode and period:
    /// (messageIndex, isPeriodic, timeoutMs) → success.
    /// </summary>
    public Func<int, bool, ushort, bool>? SetCustomCanTxModeAction { get; set; }

    public Action<bool>? GraphChangedAction { get; set; }

    public ObservableCollection<IoFlowNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<IoFlowConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<object> SelectedNodes { get; } = [];

    public IoFlowNodeViewModel? SelectedNode => SelectedNodes.Count == 1
        ? SelectedNodes[0] as IoFlowNodeViewModel
        : null;

    public bool HasSelectedNode => SelectedNode != null;

    public IoFlowConnectionViewModel? SelectedConnection { get; private set; }

    public IRelayCommand<object?> SelectConnectionCommand { get; }

    public IAsyncRelayCommand RemoveSelectedConnectionCommand { get; }

    public IAsyncRelayCommand<object?> RemoveConnectionCommand { get; }

    public IReadOnlyList<DigitalInputNodeViewModel> DigitalNodes => Nodes.OfType<DigitalInputNodeViewModel>().ToList();

    public IReadOnlyList<AnalogueInputNodeViewModel> AnalogueNodes => Nodes.OfType<AnalogueInputNodeViewModel>().ToList();

    public IReadOnlyList<OutputNodeViewModel> OutputNodes => Nodes.OfType<OutputNodeViewModel>().ToList();

    public IReadOnlyList<CanInputNodeViewModel> CanNodes => Nodes.OfType<CanInputNodeViewModel>().ToList();

    public IReadOnlyList<CanTxNodeViewModel> CanTxNodes => Nodes.OfType<CanTxNodeViewModel>().ToList();

    public IReadOnlyList<IgnitionOnNodeViewModel> IgnitionNodes => Nodes.OfType<IgnitionOnNodeViewModel>().ToList();

    public IReadOnlyList<MathRuleNodeViewModel> MathRuleNodes => Nodes.OfType<MathRuleNodeViewModel>().ToList();

    public bool CanAddIgnitionNode => _settings != null && IgnitionNodes.Count == 0;

    public bool CanAddCanTxNode => GetRemainingCanTxNodeCount() > 0;

    public bool CanAddOutputNode => GetRemainingOutputNodeCount() > 0;

    public string IgnitionNodeAddStatus => _settings == null
        ? "Unavailable: load controller settings first."
        : CanAddIgnitionNode
            ? "Available: 1 ignition node can be added."
            : "Unavailable: ignition node is already on the canvas.";

    public string CanTxNodeAddStatus
    {
        get
        {
            if (_settings == null)
            {
                return "Unavailable: load controller settings first.";
            }

            int totalTx = _settings.CustomCanMessagesStaticData.Count(IsConfiguredCanTxMessage);
            if (totalTx == 0)
            {
                return "Unavailable: no configured TX messages with a CAN ID are available.";
            }

            int remaining = GetRemainingCanTxNodeCount();
            return remaining > 0
                ? $"Available: {remaining} CAN TX node{(remaining == 1 ? string.Empty : "s")} remaining."
                : "Unavailable: all configured CAN TX nodes are already on the canvas.";
        }
    }

    private bool LogicTreeUsesNode(int rootLogicNodeId, int targetLogicNodeId)
    {
        var visited = new HashSet<int>();

        bool Visit(int logicNodeId)
        {
            if (logicNodeId == targetLogicNodeId)
            {
                return true;
            }

            if (!visited.Add(logicNodeId))
            {
                return false;
            }

            MathRuleNodeViewModel? node = MathRuleNodes.FirstOrDefault(item => item.IsStandalone && item.LogicNodeId == logicNodeId);
            return node != null && GetRuleDependencies(node).Any(dependencyId => Visit(dependencyId));
        }

        return Visit(rootLogicNodeId);
    }

    public string OutputNodeAddStatus
    {
        get
        {
            if (_settings == null)
            {
                return "Unavailable: load controller settings first.";
            }

            int totalOutputs = _settings.ChannelsStaticData.Count;
            int remaining = GetRemainingOutputNodeCount();
            return remaining > 0
                ? $"Available: {remaining} of {totalOutputs} output channel nodes remaining."
                : $"Unavailable: all {totalOutputs} output channel nodes are already on the canvas.";
        }
    }

    public string AddNodeWarning
    {
        get => _addNodeWarning;
        private set
        {
            if (SetProperty(ref _addNodeWarning, value))
            {
                OnPropertyChanged(nameof(HasAddNodeWarning));
            }
        }
    }

    public bool HasAddNodeWarning => !string.IsNullOrWhiteSpace(AddNodeWarning);

    public IoFlowGraphViewModel()
    {
        InitializeCanvases();
        SelectConnectionCommand = new RelayCommand<object?>(SelectConnection);
        RemoveSelectedConnectionCommand = new AsyncRelayCommand(RemoveSelectedConnection);
        RemoveConnectionCommand = new AsyncRelayCommand<object?>(RemoveConnection);
        SelectedNodes.CollectionChanged += (_, _) => SynchronizeNodeSelectionState();
        Nodes.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (IoFlowNodeViewModel node in e.NewItems)
                    if (node.CanvasId == 0)
                        node.CanvasId = SelectedCanvas.Id;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset || e.OldItems?.Count > 0)
            {
                ClearUndoHistory();
            }

            RefreshAddNodeAvailability();
            RefreshVisibleGraph();
        };
    }

    public void SetNextNodeSpawnLocation(Point location, Point viewportOrigin, Size viewportSize)
    {
        _nextNodeSpawnLocation = location;
        _spawnViewportOrigin = viewportOrigin;
        _spawnViewportSize = viewportSize;
    }

    private Point ConsumeNextNodeSpawnLocation(Point fallback, double nodeWidth = 340, double nodeHeight = 220)
    {
        Point preferred = _nextNodeSpawnLocation ?? fallback;
        _nextNodeSpawnLocation = null;

        Point clampedPreferred = ClampToSpawnViewport(preferred, nodeWidth, nodeHeight);
        return FindNearestAvailableSpawnLocation(clampedPreferred, nodeWidth, nodeHeight);
    }

    private Point ClampToSpawnViewport(Point location, double nodeWidth, double nodeHeight)
    {
        if (_spawnViewportSize.Width <= 0 || _spawnViewportSize.Height <= 0)
        {
            return location;
        }

        const double margin = 12;
        double minX = _spawnViewportOrigin.X + margin;
        double minY = _spawnViewportOrigin.Y + margin;
        double maxX = Math.Max(minX, (_spawnViewportOrigin.X + _spawnViewportSize.Width) - nodeWidth - margin);
        double maxY = Math.Max(minY, (_spawnViewportOrigin.Y + _spawnViewportSize.Height) - nodeHeight - margin);

        return new Point(
            Math.Clamp(location.X, minX, maxX),
            Math.Clamp(location.Y, minY, maxY));
    }

    private Point FindNearestAvailableSpawnLocation(Point preferred, double nodeWidth, double nodeHeight,
        IReadOnlyList<IoFlowNodeViewModel>? existingNodes = null,
        IReadOnlyList<IoFlowConnectionViewModel>? existingConnections = null, bool clampToViewport = true)
    {
        existingNodes ??= VisibleNodes;
        existingConnections ??= VisibleConnections;
        if (IsSpawnCandidateAvailable(preferred, nodeWidth, nodeHeight, existingNodes, existingConnections))
        {
            return preferred;
        }

        const double gridStep = 30;
        const int maxRings = 24;

        for (int ring = 1; ring <= maxRings; ring++)
        {
            for (int offset = -ring; offset <= ring; offset++)
            {
                var candidates = new[]
                {
                    new Point(preferred.X + (offset * gridStep), preferred.Y - (ring * gridStep)),
                    new Point(preferred.X + (offset * gridStep), preferred.Y + (ring * gridStep)),
                    new Point(preferred.X - (ring * gridStep), preferred.Y + (offset * gridStep)),
                    new Point(preferred.X + (ring * gridStep), preferred.Y + (offset * gridStep))
                };

                foreach (Point rawCandidate in candidates)
                {
                    Point candidate = clampToViewport ? ClampToSpawnViewport(rawCandidate, nodeWidth, nodeHeight) : rawCandidate;
                    if (IsSpawnCandidateAvailable(candidate, nodeWidth, nodeHeight, existingNodes, existingConnections))
                    {
                        return candidate;
                    }
                }
            }
        }

        // The viewport can be full. Expand the canvas rather than reuse an occupied position.
        double bottom = existingNodes.Select(node => node.Location.Y + EstimateNodeSize(node).Height).DefaultIfEmpty(0).Max();
        return new Point(preferred.X, Math.Max(preferred.Y, bottom + 40));
    }

    public void SynchronizeMathChannelFromSettings(OutputChannel channel, bool notifyGraphChanged = true)
    {
        List<MathRuleNodeViewModel> previousLogicRules = [];
        if (_outputRootLogicNodeByChannel.TryGetValue(channel.ChannelNumber, out int previousRootId))
        {
            MathRuleNodeViewModel? previousRoot = MathRuleNodes.FirstOrDefault(node => node.IsStandalone && node.LogicNodeId == previousRootId);
            if (previousRoot != null)
            {
                TryBuildOrderedLogicTree(previousRoot, out previousLogicRules, out _);
            }
        }

        _outputRootLogicNodeByChannel.Remove(channel.ChannelNumber);
        HashSet<int> retainedLogicIds = [];
        foreach (int rootId in _outputRootLogicNodeByChannel.Values)
        {
            MathRuleNodeViewModel? root = MathRuleNodes.FirstOrDefault(node => node.IsStandalone && node.LogicNodeId == rootId);
            if (root != null && TryBuildOrderedLogicTree(root, out List<MathRuleNodeViewModel> rules, out _))
            {
                retainedLogicIds.UnionWith(rules.Select(rule => rule.LogicNodeId));
            }
        }

        HashSet<int> previousLogicIds = previousLogicRules.Select(rule => rule.LogicNodeId).ToHashSet();
        foreach (MathRuleNodeViewModel otherRule in MathRuleNodes.Where(node => node.IsStandalone && !previousLogicIds.Contains(node.LogicNodeId)))
        {
            if (TryBuildOrderedLogicTree(otherRule, out List<MathRuleNodeViewModel> rules, out _))
            {
                retainedLogicIds.UnionWith(rules.Select(rule => rule.LogicNodeId));
            }
        }

        foreach (MathRuleNodeViewModel previousRule in previousLogicRules.Where(rule => !retainedLogicIds.Contains(rule.LogicNodeId)))
        {
            SelectedNodes.Remove(previousRule);
            Nodes.Remove(previousRule);
        }

        OutputNodeViewModel? outputNode = OutputNodes.FirstOrDefault(node => node.ChannelNumber == channel.ChannelNumber);
        if (outputNode == null)
        {
            return;
        }

        List<MathRuleNodeViewModel> existingNodes = MathRuleNodes
            .Where(node => node.OwnerChannelNumber == channel.ChannelNumber)
            .ToList();

        int activeRuleCount = channel.ChanType == OutputChannel.ChannelType.MATH
            ? Math.Clamp((int)channel.MathRuleCount, 1, Constants.NUM_MATH_RULE_SLOTS)
            : 0;

        foreach (MathRuleNodeViewModel existingNode in existingNodes.Where(node => node.RuleIndex >= activeRuleCount))
        {
            SelectedNodes.Remove(existingNode);
            Nodes.Remove(existingNode);
        }

        if (activeRuleCount > 0)
        {
            channel.EnsureMathRuleSlots();
            for (int ruleIndex = 0; ruleIndex < activeRuleCount; ruleIndex++)
            {
                if (MathRuleNodes.Any(node => node.OwnerChannelNumber == channel.ChannelNumber && node.RuleIndex == ruleIndex))
                {
                    continue;
                }

                MathRuleNodeViewModel ruleNode = CreateMathRuleNode(
                    channel,
                    ruleIndex,
                    channel.MathRules[ruleIndex],
                    default);
                ruleNode.CanvasId = outputNode.CanvasId;
                NodeSize ruleSize = EstimateNodeSize(ruleNode);
                if (ruleIndex < previousLogicRules.Count && !Nodes.Contains(previousLogicRules[ruleIndex]))
                {
                    ruleNode.Location = previousLogicRules[ruleIndex].Location;
                }
                else
                {
                    ruleNode.Location = FindNearestAvailableSpawnLocation(
                        new Point(outputNode.Location.X - ruleSize.Width - 120, outputNode.Location.Y),
                        ruleSize.Width, ruleSize.Height);
                }
                Nodes.Add(ruleNode);
            }
        }

        if (_settings != null)
        {
            RebuildConnections(_settings);
            if (notifyGraphChanged)
            {
                NotifyGraphChanged(controllerSettingsChanged: false);
            }
        }
    }

    public void RemoveChannelMathRules(OutputChannel channel, IReadOnlySet<int> removedIndices)
    {
        int count = Math.Min(channel.MathRuleCount, channel.MathRules.Count);
        int[] remainingIndices = Enumerable.Range(0, count).Where(index => !removedIndices.Contains(index)).ToArray();
        if (remainingIndices.Length == count)
        {
            return;
        }

        MathRuleValues[] remainingRules = remainingIndices.Select(index => CaptureMathRuleValues(channel.MathRules[index])).ToArray();
        int oldRoot = channel.MathRootRule;
        bool wasApplying = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        try
        {
            if (remainingRules.Length == 0)
            {
                ResetOutputChannelToIoFlowDefaults(channel);
            }
            else
            {
                channel.MathRuleCount = (byte)remainingRules.Length;
                int rootIndex = Array.IndexOf(remainingIndices, oldRoot);
                channel.MathRootRule = (byte)(rootIndex >= 0 ? rootIndex : remainingRules.Length - 1);
                for (int index = 0; index < remainingRules.Length; index++)
                {
                    RestoreMathRuleValues(channel.MathRules[index], remainingRules[index]);
                    RemapOperand(channel.MathRules[index], true);
                    RemapOperand(channel.MathRules[index], false);
                }
            }

            for (int index = remainingRules.Length; index < channel.MathRules.Count; index++)
            {
                RestoreMathRuleValues(channel.MathRules[index], default);
            }
        }
        finally
        {
            _isApplyingMathRulesFromGraph = wasApplying;
        }

        SynchronizeMathChannelFromSettings(channel, notifyGraphChanged: false);
        NotifyGraphChanged();

        void RemapOperand(OutputChannel.MathRule rule, bool operandA)
        {
            byte type = operandA ? rule.OperandAType : rule.OperandBType;
            byte oldIndex = operandA ? rule.OperandAIndex : rule.OperandBIndex;
            if (type != (byte)OutputChannel.MathOperandType.Rule)
            {
                return;
            }

            int newIndex = Array.IndexOf(remainingIndices, oldIndex);
            if (operandA)
            {
                rule.OperandAType = newIndex >= 0 ? type : (byte)OutputChannel.MathOperandType.Constant;
                rule.OperandAIndex = (byte)Math.Max(0, newIndex);
                if (newIndex < 0)
                {
                    rule.OperandAConstant = 0;
                }
            }
            else
            {
                rule.OperandBType = newIndex >= 0 ? type : (byte)OutputChannel.MathOperandType.Constant;
                rule.OperandBIndex = (byte)Math.Max(0, newIndex);
                if (newIndex < 0)
                {
                    rule.OperandBConstant = 0;
                }
            }
        }
    }

    private MathRuleNodeViewModel CreateMathRuleNode(
        OutputChannel ownerChannel,
        int ruleIndex,
        OutputChannel.MathRule rule,
        Point location)
    {
        MathRuleNodeViewModel node = new(ownerChannel, ruleIndex, rule, location, GetOperandIndexOptions)
        {
            RuleChangedAction = OnMathRuleNodeChanged,
            OperandSourceChangedAction = SelectMathOperandSource,
        };
        return node;
    }

    private MathRuleNodeViewModel CreateMathRuleNode(int logicNodeId, Point location)
    {
        MathRuleNodeViewModel node = new(logicNodeId, location, GetOperandIndexOptions)
        {
            RuleChangedAction = OnMathRuleNodeChanged,
            OperandSourceChangedAction = SelectMathOperandSource,
        };
        return node;
    }

    private void OnMathRuleNodeChanged(MathRuleNodeViewModel node)
    {
        if (!Nodes.Contains(node) || !node.IsStandalone || _isApplyingMathRulesFromGraph || _isEditingConnection || _isUndoing)
        {
            return;
        }

        if (!TryBuildOrderedLogicTree(node, out _, out string validationError)
            || !TrySynchronizeMappedMathOutputs(out validationError))
        {
            node.RestoreLastValidRuleState();
            ShowAddNodeWarning(validationError);
            if (_settings != null)
            {
                RebuildConnections(_settings);
            }

            NotifyGraphChanged(controllerSettingsChanged: false);
            return;
        }

        foreach (MathRuleNodeViewModel ruleNode in MathRuleNodes.Where(rule => rule.IsStandalone))
        {
            ruleNode.AcceptCurrentRuleState();
        }

        ClearAddNodeWarning();
        if (_settings != null)
        {
            RebuildConnections(_settings);
        }

        NotifyGraphChanged(controllerSettingsChanged: _outputRootLogicNodeByChannel.Count > 0);
    }

    private static bool IsSpawnCandidateAvailable(Point location, double nodeWidth, double nodeHeight,
        IReadOnlyList<IoFlowNodeViewModel> existingNodes, IReadOnlyList<IoFlowConnectionViewModel> existingConnections)
    {
        Rect candidateRect = new(location, new Size(nodeWidth, nodeHeight));

        foreach (IoFlowNodeViewModel existingNode in existingNodes)
        {
            NodeSize existingSize = EstimateNodeSize(existingNode);
            Rect existingRect = ExpandRect(new Rect(existingNode.Location, new Size(existingSize.Width, existingSize.Height)), 20);
            if (existingRect.Intersects(candidateRect))
            {
                return false;
            }
        }

        if (IsOverlappingConnectionSegment(candidateRect, existingConnections))
        {
            return false;
        }

        return true;
    }

    private static bool IsOverlappingConnectionSegment(Rect candidateRect, IReadOnlyList<IoFlowConnectionViewModel> connections)
    {
        Rect expandedCandidate = ExpandRect(candidateRect, 10);

        foreach (IoFlowConnectionViewModel connection in connections)
        {
            Point source = GetOutputAnchor(connection.Source.Node);
            Point target = GetInputAnchor(connection.Target.Node);

            if (LineIntersectsRect(source, target, expandedCandidate))
            {
                return true;
            }
        }

        return false;
    }

    private static Point GetOutputAnchor(IoFlowNodeViewModel node)
    {
        NodeSize size = EstimateNodeSize(node);
        return new Point(node.Location.X + size.Width, node.Location.Y + (size.Height * 0.5));
    }

    private static Point GetInputAnchor(IoFlowNodeViewModel node)
    {
        NodeSize size = EstimateNodeSize(node);
        return new Point(node.Location.X, node.Location.Y + (size.Height * 0.5));
    }

    private static Rect ExpandRect(Rect rect, double padding)
    {
        return new Rect(rect.X - padding, rect.Y - padding, rect.Width + (padding * 2), rect.Height + (padding * 2));
    }

    private static bool LineIntersectsRect(Point a, Point b, Rect rect)
    {
        if (rect.Contains(a) || rect.Contains(b))
        {
            return true;
        }

        Point topLeft = rect.TopLeft;
        Point topRight = rect.TopRight;
        Point bottomRight = rect.BottomRight;
        Point bottomLeft = rect.BottomLeft;

        return SegmentsIntersect(a, b, topLeft, topRight)
            || SegmentsIntersect(a, b, topRight, bottomRight)
            || SegmentsIntersect(a, b, bottomRight, bottomLeft)
            || SegmentsIntersect(a, b, bottomLeft, topLeft);
    }

    private static bool SegmentsIntersect(Point a1, Point a2, Point b1, Point b2)
    {
        static double Cross(Point p1, Point p2, Point p3)
        {
            return (p2.X - p1.X) * (p3.Y - p1.Y) - (p2.Y - p1.Y) * (p3.X - p1.X);
        }

        static bool OnSegment(Point p1, Point p2, Point p)
        {
            return p.X >= Math.Min(p1.X, p2.X) - 0.001
                && p.X <= Math.Max(p1.X, p2.X) + 0.001
                && p.Y >= Math.Min(p1.Y, p2.Y) - 0.001
                && p.Y <= Math.Max(p1.Y, p2.Y) + 0.001;
        }

        double d1 = Cross(a1, a2, b1);
        double d2 = Cross(a1, a2, b2);
        double d3 = Cross(b1, b2, a1);
        double d4 = Cross(b1, b2, a2);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        if (Math.Abs(d1) <= 0.001 && OnSegment(a1, a2, b1)) return true;
        if (Math.Abs(d2) <= 0.001 && OnSegment(a1, a2, b2)) return true;
        if (Math.Abs(d3) <= 0.001 && OnSegment(b1, b2, a1)) return true;
        if (Math.Abs(d4) <= 0.001 && OnSegment(b1, b2, a2)) return true;

        return false;
    }

    private int GetRemainingOutputNodeCount()
    {
        if (_settings == null)
        {
            return 0;
        }

        HashSet<int> presentChannels = OutputNodes.Select(node => node.ChannelNumber).ToHashSet();
        return _settings.ChannelsStaticData.Count(channel => !presentChannels.Contains(channel.ChannelNumber));
    }

    private int GetRemainingCanTxNodeCount()
    {
        if (_settings == null)
        {
            return 0;
        }

        HashSet<int> presentMessages = CanTxNodes.Select(node => node.MessageIndex).ToHashSet();
        return _settings.CustomCanMessagesStaticData.Count(message => IsConfiguredCanTxMessage(message) && !presentMessages.Contains(message.Index));
    }

    private void RefreshAddNodeAvailability()
    {
        OnPropertyChanged(nameof(CanAddIgnitionNode));
        OnPropertyChanged(nameof(CanAddCanTxNode));
        OnPropertyChanged(nameof(CanAddOutputNode));
        OnPropertyChanged(nameof(CanAddCanRxNode));
        OnPropertyChanged(nameof(CanAddSystemValueNode));
        OnPropertyChanged(nameof(IgnitionNodeAddStatus));
        OnPropertyChanged(nameof(CanTxNodeAddStatus));
        OnPropertyChanged(nameof(OutputNodeAddStatus));
    }

    private void ShowAddNodeWarning(string message)
    {
        AddNodeWarning = message;
    }

    private void ClearAddNodeWarning()
    {
        AddNodeWarning = string.Empty;
    }

    public void Build(DataStructures settings, DataStructures live)
    {
        ClearUndoHistory();
        _settings = settings;
        _live = live;
        _nextLogicNodeId = 1;
        _outputRootLogicNodeByChannel.Clear();
        UseControllerConfigurationValues = false;
        Nodes.Clear();
        Connections.Clear();
        SelectedNodes.Clear();
        SetSelectedConnection(null);
        ResetCanvases();
        ClearAddNodeWarning();
        RefreshAddNodeAvailability();
    }

    public void PopulateFromCurrentSettings()
    {
        if (_settings == null)
        {
            return;
        }

        UseControllerConfigurationValues = true;
        _outputRootLogicNodeByChannel.Clear();
        Nodes.Clear();
        Connections.Clear();
        SelectedNodes.Clear();
        SetSelectedConnection(null);
        ResetCanvases();

        HashSet<int> usedDigitalInputs = GetUsedDigitalInputNumbers();
        HashSet<int> usedAnalogueInputs = GetUsedAnalogueInputNumbers();

        foreach (DigitalInput input in _settings.DigitalInputsStaticData.Where(item => usedDigitalInputs.Contains(item.InputNumber)))
        {
            input.IsActiveHigh = true;
            Nodes.Add(new DigitalInputNodeViewModel(input, new Point(50, 50)));
        }

        foreach (AnalogueInput input in _settings.AnalogueInputsStaticData.Where(item => usedAnalogueInputs.Contains(item.InputNumber)))
        {
            Nodes.Add(new AnalogueInputNodeViewModel(input, new Point(430, 50)));
        }

        foreach (OutputChannel channel in _settings.ChannelsStaticData)
        {
            AddOutputNodeWithOptionalMath(channel);
        }

        if (ShouldIncludeCanNodeFromSettings())
        {
            Nodes.Add(new CanInputNodeViewModel(new Point(430, 50)));
        }

        foreach (CustomCanMessage message in GetCanTxEligibleMessages()
                     .Where(message => message.TxTrigger < Constants.NUM_OUTPUT_CHANNELS))
        {
            Nodes.Add(CreateCanTxNode(message, new Point(1240, 300)));
        }

        if (ShouldIncludeIgnitionNodeFromSettings())
        {
            Nodes.Add(new IgnitionOnNodeViewModel(new Point(50, 50)));
        }

        _nextLogicNodeId = MathRuleNodes.Where(node => node.IsStandalone).Select(node => node.LogicNodeId).DefaultIfEmpty(0).Max() + 1;

        RebuildConnections(_settings);
        AutoArrangeNodesCore();
        ClearAddNodeWarning();
        RefreshAddNodeAvailability();
    }

    private bool ShouldIncludeCanNodeFromSettings()
    {
        if (_settings == null)
        {
            return false;
        }

        if (_settings.ChannelsStaticData.Any(channel => channel.ChanType is OutputChannel.ChannelType.CAN or OutputChannel.ChannelType.CAN_PWM))
        {
            return true;
        }

        return false;
    }

    private bool ShouldIncludeIgnitionNodeFromSettings()
    {
        if (_settings == null)
        {
            return false;
        }

        return _settings.ChannelsStaticData.Any(channel => channel.InputControlPin == InputPinCatalog.IgnitionInputPin);
    }

    private HashSet<int> GetUsedDigitalInputNumbers()
    {
        var used = new HashSet<int>();

        if (_settings == null)
        {
            return used;
        }

        foreach (OutputChannel channel in _settings.ChannelsStaticData)
        {
            if (channel.InputControlPin != 0)
            {
                int digitalIndex = Array.IndexOf(InputPinCatalog.DIChannelInputPins, channel.InputControlPin);
                if (digitalIndex >= 0)
                {
                    used.Add(digitalIndex + 1);
                }
            }

            if (channel.ChanType != OutputChannel.ChannelType.MATH)
            {
                continue;
            }

            channel.EnsureMathRuleSlots();
            int activeRuleCount = Math.Min(channel.MathRuleCount, channel.MathRules.Count);
            for (int ruleIndex = 0; ruleIndex < activeRuleCount; ruleIndex++)
            {
                OutputChannel.MathRule rule = channel.MathRules[ruleIndex];
                CollectUsedInputNumbersFromOperand(rule.OperandAType, rule.OperandAIndex, used, null);
                if (!MathRuleNodeViewModel.IsUnary(rule.Operation))
                {
                    CollectUsedInputNumbersFromOperand(rule.OperandBType, rule.OperandBIndex, used, null);
                }
            }
        }

        return used;
    }

    private HashSet<int> GetUsedAnalogueInputNumbers()
    {
        var used = new HashSet<int>();

        if (_settings == null)
        {
            return used;
        }

        foreach (OutputChannel channel in _settings.ChannelsStaticData)
        {
            if (channel.InputControlPin != 0)
            {
                int analogueIndex = Array.IndexOf(InputPinCatalog.ANAChannelInputPins, channel.InputControlPin);
                if (analogueIndex >= 0)
                {
                    used.Add(analogueIndex + 1);
                }
            }

            if (channel.ChanType != OutputChannel.ChannelType.MATH)
            {
                continue;
            }

            channel.EnsureMathRuleSlots();
            int activeRuleCount = Math.Min(channel.MathRuleCount, channel.MathRules.Count);
            for (int ruleIndex = 0; ruleIndex < activeRuleCount; ruleIndex++)
            {
                OutputChannel.MathRule rule = channel.MathRules[ruleIndex];
                CollectUsedInputNumbersFromOperand(rule.OperandAType, rule.OperandAIndex, null, used);
                if (!MathRuleNodeViewModel.IsUnary(rule.Operation))
                {
                    CollectUsedInputNumbersFromOperand(rule.OperandBType, rule.OperandBIndex, null, used);
                }
            }
        }

        return used;
    }

    private static void CollectUsedInputNumbersFromOperand(byte operandType, byte operandIndex, HashSet<int>? digitalInputs, HashSet<int>? analogueInputs)
    {
        if (operandType == (byte)OutputChannel.MathOperandType.DigitalInput)
        {
            digitalInputs?.Add(operandIndex + 1);
            return;
        }

        if (operandType == (byte)OutputChannel.MathOperandType.AnalogueInput)
        {
            analogueInputs?.Add(operandIndex + 1);
        }
    }

    public IoFlowGraphLayoutSnapshot CreateLayoutSnapshot()
    {
        IoFlowGraphLayoutSnapshot layout = CreateSnapshot().ToLayoutSnapshot();
        foreach (var (channelNumber, rootId) in _outputRootLogicNodeByChannel)
        {
            MathRuleNodeViewModel? root = MathRuleNodes.FirstOrDefault(node => node.IsStandalone && node.LogicNodeId == rootId);
            if (root == null || !TryBuildOrderedLogicTree(root, out var ordered, out _))
            {
                throw new InvalidOperationException($"Cannot save logic node positions for output {channelNumber}.");
            }
            layout.NodePositions.RemoveAll(node => node.Kind == "OutputMath" && node.Id == channelNumber);
            // Standalone IDs are local; controller reloads identify the same rules by channel and slot.
            for (int index = 0; index < ordered.Count; index++)
            {
                var rule = ordered[index];
                layout.NodePositions.Add(new IoFlowNodePositionSnapshot
                {
                    Kind = "OutputMath",
                    Id = channelNumber,
                    Option = (byte)index,
                    CanvasId = rule.CanvasId,
                    X = rule.Location.X,
                    Y = rule.Location.Y,
                });
            }
        }
        return layout;
    }

    public IoFlowGraphSnapshot CreateSnapshot()
    {
        var snapshot = new IoFlowGraphSnapshot
        {
            Canvases = Canvases.Select(canvas => new IoFlowCanvasSnapshot
            {
                Id = canvas.Id, Name = canvas.Name,
                ViewportX = canvas.ViewportLocation.X, ViewportY = canvas.ViewportLocation.Y,
                ViewportZoom = canvas.ViewportZoom,
            }).ToList(),
            SelectedCanvasId = SelectedCanvas.Id,
        };

        foreach (IoFlowNodeViewModel node in Nodes)
        {
            int snapshotIndex = snapshot.Nodes.Count;
            switch (node)
            {
                case DigitalInputNodeViewModel digitalNode:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "Digital",
                        Id = digitalNode.InputNumber,
                        X = digitalNode.Location.X,
                        Y = digitalNode.Location.Y,
                    });
                    break;
                case AnalogueInputNodeViewModel analogueNode:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "Analogue",
                        Id = analogueNode.InputNumber,
                        X = analogueNode.Location.X,
                        Y = analogueNode.Location.Y,
                    });
                    break;
                case OutputNodeViewModel outputNode:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "Output",
                        Id = outputNode.ChannelNumber,
                        X = outputNode.Location.X,
                        Y = outputNode.Location.Y,
                    });
                    break;
                case CanInputNodeViewModel canNode:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "Can",
                        Id = 1,
                        X = canNode.Location.X,
                        Y = canNode.Location.Y,
                    });
                    break;
                case CanTxNodeViewModel canTxNode:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "CanTx",
                        Id = canTxNode.MessageIndex + 1,
                        X = canTxNode.Location.X,
                        Y = canTxNode.Location.Y,
                    });
                    break;
                case MathSourceNodeViewModel sourceNode:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = sourceNode.OperandType == OutputChannel.MathOperandType.CanSignal ? "CanRx" : "SystemValue",
                        Id = sourceNode.SourceIndex,
                        X = sourceNode.Location.X,
                        Y = sourceNode.Location.Y,
                    });
                    break;
                case IgnitionOnNodeViewModel ignitionNode:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "Ignition",
                        Id = 1,
                        X = ignitionNode.Location.X,
                        Y = ignitionNode.Location.Y,
                    });
                    break;
                case MathRuleNodeViewModel logicNode when logicNode.IsStandalone:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "Logic",
                        Id = logicNode.LogicNodeId,
                        X = logicNode.Location.X,
                        Y = logicNode.Location.Y,
                    });

                    snapshot.StandaloneLogicRules.Add(new IoFlowStandaloneLogicRuleSnapshot
                    {
                        LogicNodeId = logicNode.LogicNodeId,
                        Operation = logicNode.Rule.Operation,
                        HysteresisEnabled = logicNode.Rule.HysteresisEnabled,
                        OnThreshold = logicNode.Rule.OnThreshold,
                        OffThreshold = logicNode.Rule.OffThreshold,
                        OperandAType = logicNode.Rule.OperandAType,
                        OperandAIndex = logicNode.Rule.OperandAIndex,
                        OperandAConstant = logicNode.Rule.OperandAConstant,
                        OperandBType = logicNode.Rule.OperandBType,
                        OperandBIndex = logicNode.Rule.OperandBIndex,
                        OperandBConstant = logicNode.Rule.OperandBConstant,
                    });
                    break;
                case MathRuleNodeViewModel mathNode when !mathNode.IsStandalone && mathNode.OwnerChannelNumber is int ownerChannelNumber:
                    snapshot.Nodes.Add(new IoFlowNodeSnapshot
                    {
                        Kind = "OutputMath",
                        Id = ownerChannelNumber,
                        Option = (byte)mathNode.RuleIndex,
                        X = mathNode.Location.X,
                        Y = mathNode.Location.Y,
                    });
                    break;
            }
            if (snapshot.Nodes.Count > snapshotIndex)
                snapshot.Nodes[snapshotIndex].CanvasId = node.CanvasId;
        }

        foreach ((int channelNumber, int logicNodeId) in _outputRootLogicNodeByChannel)
        {
            snapshot.OutputRootLogicNodeByChannel[channelNumber] = logicNodeId;
        }

        return snapshot;
    }

    public void ApplySnapshot(IoFlowGraphSnapshot snapshot)
    {
        if (_settings == null)
        {
            return;
        }

        if (!IsCanvasLayoutValid(snapshot))
            throw new ArgumentException("Invalid I/O Flow canvas layout.", nameof(snapshot));

        Nodes.Clear();
        Connections.Clear();
        SelectedNodes.Clear();
        SetSelectedConnection(null);
        _outputRootLogicNodeByChannel.Clear();

        foreach (IoFlowNodeSnapshot node in snapshot.Nodes)
        {
            switch (node.Kind)
            {
                case "Digital":
                    DigitalInput? digitalInput = _settings.DigitalInputsStaticData.ElementAtOrDefault(node.Id - 1);
                    if (digitalInput != null)
                    {
                        digitalInput.IsActiveHigh = true;
                        Nodes.Add(new DigitalInputNodeViewModel(digitalInput, new Point(node.X, node.Y)));
                    }
                    break;
                case "Analogue":
                    AnalogueInput? analogueInput = _settings.AnalogueInputsStaticData.ElementAtOrDefault(node.Id - 1);
                    if (analogueInput != null)
                    {
                        Nodes.Add(new AnalogueInputNodeViewModel(analogueInput, new Point(node.X, node.Y)));
                    }
                    break;
                case "Output":
                    OutputChannel? outputChannel = _settings.ChannelsStaticData.ElementAtOrDefault(node.Id - 1);
                    if (outputChannel != null)
                    {
                        AddOutputNodeWithOptionalMath(outputChannel);
                        OutputNodeViewModel? outputNode = OutputNodes.FirstOrDefault(item => item.ChannelNumber == node.Id);
                        if (outputNode != null)
                        {
                            outputNode.Location = new Point(node.X, node.Y);
                        }
                    }
                    break;
                case "Can":
                    if (CanNodes.Count == 0)
                    {
                        Nodes.Add(new CanInputNodeViewModel(new Point(node.X, node.Y)));
                    }
                    break;
                case "CanTx":
                    CustomCanMessage? txMessage = _settings.CustomCanMessagesStaticData.ElementAtOrDefault(node.Id - 1);
                    if (txMessage != null
                        && IsConfiguredCanTxMessage(txMessage)
                        && CanTxNodes.All(existing => existing.MessageIndex != txMessage.Index))
                    {
                        Nodes.Add(CreateCanTxNode(txMessage, new Point(node.X, node.Y)));
                    }
                    break;
                case "Ignition":
                    if (IgnitionNodes.Count == 0)
                    {
                        Nodes.Add(new IgnitionOnNodeViewModel(new Point(node.X, node.Y)));
                    }
                    break;
                case "Logic":
                    Nodes.Add(CreateMathRuleNode(node.Id, new Point(node.X, node.Y)));
                    break;
                case "CanRx":
                    if (_settings.CustomCanSignalsStaticData.Any(signal => signal.Index == node.Id && IsCanRxSignal(signal)))
                    {
                        AddMathSourceNode(OutputChannel.MathOperandType.CanSignal, (byte)node.Id, new Point(node.X, node.Y));
                    }
                    break;
                case "SystemValue":
                    if (Enum.IsDefined((OutputChannel.MathSystemValue)node.Id))
                    {
                        AddMathSourceNode(OutputChannel.MathOperandType.SystemValue, (byte)node.Id, new Point(node.X, node.Y));
                    }
                    break;
            }
        }

        // Nodes created indirectly (e.g. math rule nodes added alongside their output node)
        // get default locations above; restore the stored locations for all of them.
        ApplyNodeLocations(snapshot);

        bool wasApplyingRules = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        try
        {
            foreach (IoFlowStandaloneLogicRuleSnapshot storedRule in snapshot.StandaloneLogicRules)
            {
                MathRuleNodeViewModel? logicNode = MathRuleNodes.FirstOrDefault(node => node.IsStandalone && node.LogicNodeId == storedRule.LogicNodeId);
                if (logicNode == null)
                {
                    continue;
                }

                logicNode.Rule.Operation = storedRule.Operation;
                logicNode.Rule.HysteresisEnabled = storedRule.HysteresisEnabled;
                logicNode.Rule.OnThreshold = storedRule.OnThreshold;
                logicNode.Rule.OffThreshold = storedRule.OffThreshold;
                logicNode.Rule.OperandAType = storedRule.OperandAType;
                logicNode.Rule.OperandAIndex = storedRule.OperandAIndex;
                logicNode.Rule.OperandAConstant = storedRule.OperandAConstant;
                logicNode.Rule.OperandBType = storedRule.OperandBType;
                logicNode.Rule.OperandBIndex = storedRule.OperandBIndex;
                logicNode.Rule.OperandBConstant = storedRule.OperandBConstant;
                logicNode.AcceptCurrentRuleState();
            }
        }
        finally
        {
            _isApplyingMathRulesFromGraph = wasApplyingRules;
        }

        foreach ((int channelNumber, int logicNodeId) in snapshot.OutputRootLogicNodeByChannel)
        {
            if (OutputNodes.Any(node => node.ChannelNumber == channelNumber)
                && MathRuleNodes.Any(node => node.IsStandalone && node.LogicNodeId == logicNodeId))
            {
                _outputRootLogicNodeByChannel[channelNumber] = logicNodeId;
            }
        }

        _nextLogicNodeId = MathRuleNodes.Where(node => node.IsStandalone).Select(node => node.LogicNodeId).DefaultIfEmpty(0).Max() + 1;
        RebuildConnections(_settings);
    }

    /// <summary>
    /// Restores node locations from a snapshot onto the matching nodes currently in the graph.
    /// Locations are cosmetic, so this is safe to apply even when the snapshot structure could
    /// not be applied (e.g. controller configuration signature mismatch).
    /// </summary>
    public void ApplyNodeLocations(IoFlowGraphSnapshot snapshot)
    {
        if (!IsCanvasLayoutValid(snapshot))
            throw new ArgumentException("Invalid I/O Flow canvas layout.", nameof(snapshot));
        RestoreCanvasLayout(snapshot);
        foreach (IoFlowNodeSnapshot node in snapshot.Nodes)
        {
            IoFlowNodeViewModel? target = node.Kind switch
            {
                "Digital" => DigitalNodes.FirstOrDefault(item => item.InputNumber == node.Id),
                "Analogue" => AnalogueNodes.FirstOrDefault(item => item.InputNumber == node.Id),
                "Output" => OutputNodes.FirstOrDefault(item => item.ChannelNumber == node.Id),
                "Can" => CanNodes.FirstOrDefault(),
                "CanTx" => CanTxNodes.FirstOrDefault(item => item.MessageIndex == node.Id - 1),
                "Ignition" => IgnitionNodes.FirstOrDefault(),
                "Logic" => MathRuleNodes.FirstOrDefault(item => item.IsStandalone && item.LogicNodeId == node.Id),
                "OutputMath" => MathRuleNodes.FirstOrDefault(item => !item.IsStandalone && item.OwnerChannelNumber == node.Id && item.RuleIndex == node.Option),
                "CanRx" => MathSourceNodes.FirstOrDefault(item => item.OperandType == OutputChannel.MathOperandType.CanSignal && item.SourceIndex == node.Id),
                "SystemValue" => MathSourceNodes.FirstOrDefault(item => item.OperandType == OutputChannel.MathOperandType.SystemValue && item.SourceIndex == node.Id),
                _ => null,
            };

            if (target != null)
            {
                target.CanvasId = node.CanvasId;
                target.Location = new Point(node.X, node.Y);
            }
        }
        KeepConnectedNodesOnSameCanvas();
    }

    public void ApplyNodePositions(IoFlowGraphLayoutSnapshot snapshot)
    {
        if (!IsNodePositionsValid(snapshot))
            throw new ArgumentException("Invalid I/O Flow canvas layout or node positions.", nameof(snapshot));

        ApplyNodeLocations(snapshot.ToGraphSnapshot());
    }

    [RelayCommand]
    private void AddNextDigitalNode()
    {
        if (_settings == null)
        {
            return;
        }

        DigitalInput? next = _settings.DigitalInputsStaticData
            .FirstOrDefault(input => Nodes.OfType<DigitalInputNodeViewModel>().All(node => node.InputNumber != input.InputNumber));

        if (next == null)
        {
            return;
        }

        next.IsActiveHigh = true;
        Nodes.Add(new DigitalInputNodeViewModel(next, ConsumeNextNodeSpawnLocation(new Point(50, 50), nodeWidth: 310, nodeHeight: 340)));
        RebuildConnections(_settings);
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void AddNextAnalogueNode()
    {
        if (_settings == null)
        {
            return;
        }

        AnalogueInput? next = _settings.AnalogueInputsStaticData
            .FirstOrDefault(input => Nodes.OfType<AnalogueInputNodeViewModel>().All(node => node.InputNumber != input.InputNumber));

        if (next == null)
        {
            return;
        }

        Nodes.Add(new AnalogueInputNodeViewModel(next, ConsumeNextNodeSpawnLocation(new Point(430, 50), nodeWidth: 310, nodeHeight: 440)));
        RebuildConnections(_settings);
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void AddNextOutputNode()
    {
        if (_settings == null)
        {
            return;
        }

        OutputChannel? next = _settings.ChannelsStaticData
            .FirstOrDefault(channel => Nodes.OfType<OutputNodeViewModel>().All(node => node.ChannelNumber != channel.ChannelNumber));

        if (next == null)
        {
            ShowAddNodeWarning(OutputNodeAddStatus);
            return;
        }

        AddOutputNodeWithOptionalMath(next, useSpawnLocation: true);
        RebuildConnections(_settings);
        ClearAddNodeWarning();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void AddCanInputNode()
    {
        if (_settings == null || CanNodes.Count > 0)
        {
            return;
        }

        Nodes.Add(new CanInputNodeViewModel(ConsumeNextNodeSpawnLocation(new Point(430, 50), nodeWidth: 310, nodeHeight: 220)));
        RebuildConnections(_settings);
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    /// <summary>
    /// TX messages eligible for I/O Flow: configured TX entries from Custom CAN
    /// settings (must have a non-zero CAN ID).
    /// </summary>
    private IEnumerable<CustomCanMessage> GetCanTxEligibleMessages() =>
        _settings == null
            ? []
            : _settings.CustomCanMessagesStaticData
                .Where(IsConfiguredCanTxMessage)
                .OrderBy(message => message.Index);

    private static bool IsConfiguredCanTxMessage(CustomCanMessage message) =>
        message.IsTx && message.Id != 0;

    private CanTxNodeViewModel CreateCanTxNode(CustomCanMessage message, Point location)
    {
        IReadOnlyList<CustomCanMessage> options = GetCanTxEligibleMessages().ToList();
        return new CanTxNodeViewModel(message, location, options, OnCanTxNodeMessageSelectionChanged)
        {
            SettingsEdited = ApplyCanTxNodeSettings,
        };
    }

    private void RefreshCanTxNodeMessageOptions()
    {
        IReadOnlyList<CustomCanMessage> options = GetCanTxEligibleMessages().ToList();
        foreach (CanTxNodeViewModel node in CanTxNodes)
        {
            node.SetAvailableMessages(options);
        }
    }

    private void OnCanTxNodeMessageSelectionChanged(CanTxNodeViewModel node, int previousMessageIndex, int selectedMessageIndex)
    {
        if (_settings == null)
        {
            return;
        }

        CustomCanMessage? selectedMessage = _settings.CustomCanMessagesStaticData.ElementAtOrDefault(selectedMessageIndex);
        if (selectedMessage == null || !IsConfiguredCanTxMessage(selectedMessage))
        {
            node.SetValidationMessage("Select a configured TX message from CAN Messages.");
            node.RevertSelectedMessageToCurrent();
            return;
        }

        if (CanTxNodes.Any(existing => !ReferenceEquals(existing, node) && existing.MessageIndex == selectedMessage.Index))
        {
            node.SetValidationMessage("That TX message is already assigned to another CAN TX node.");
            node.RevertSelectedMessageToCurrent();
            return;
        }

        ClearUndoHistory();
        node.RebindMessage(selectedMessage);
        node.ClearValidationMessage();
        RebuildConnections(_settings);
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void AddCanTxNode()
    {
        if (_settings == null)
        {
            return;
        }

        CustomCanMessage? next = GetCanTxEligibleMessages()
            .FirstOrDefault(message => CanTxNodes.All(node => node.MessageIndex != message.Index));

        if (next == null)
        {
            ShowAddNodeWarning(CanTxNodeAddStatus);
            return;
        }

        CanTxNodeViewModel node = CreateCanTxNode(next, default);
        NodeSize size = EstimateNodeSize(node);
        node.Location = ConsumeNextNodeSpawnLocation(new Point(1240, 300), size.Width, size.Height);
        Nodes.Add(node);
        RebuildConnections(_settings);
        ClearAddNodeWarning();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    private void ApplyCanTxNodeSettings(CanTxNodeViewModel? node)
    {
        if (node == null || SetCustomCanTxModeAction == null)
        {
            return;
        }

        int timeoutCandidate = node.EditTimeoutMs;
        if (node.EditIsPeriodic &&
            (timeoutCandidate < Constants.CUSTOM_CAN_TX_PERIOD_MIN_MS ||
             timeoutCandidate > Constants.CUSTOM_CAN_TX_PERIOD_MAX_MS ||
             timeoutCandidate % Constants.CUSTOM_CAN_TX_PERIOD_STEP_MS != 0))
        {
            node.SetValidationMessage("Periodic TX period must be between 100 and 30,000 ms in 100 ms steps.");
            return;
        }

        ushort period = node.EditIsPeriodic ? (ushort)timeoutCandidate : node.Message.TimeoutMs;
        bool applied = SetCustomCanTxModeAction(node.MessageIndex, node.EditIsPeriodic, period);
        if (!applied)
        {
            node.SetValidationMessage("Unable to apply CAN TX settings.");
            node.RefreshEditFieldsFromMessage();
            return;
        }

        node.ClearValidationMessage();
        NotifyGraphChanged();
    }

    [RelayCommand]
    private void AddIgnitionNode()
    {
        if (_settings == null)
        {
            return;
        }

        if (IgnitionNodes.Count > 0)
        {
            ShowAddNodeWarning(IgnitionNodeAddStatus);
            return;
        }

        Nodes.Add(new IgnitionOnNodeViewModel(ConsumeNextNodeSpawnLocation(new Point(50, 50), nodeWidth: 310, nodeHeight: 220)));
        RebuildConnections(_settings);
        ClearAddNodeWarning();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void AddLogicNode()
    {
        MathRuleNodeViewModel ruleNode = CreateMathRuleNode(_nextLogicNodeId++, default);
        NodeSize ruleSize = EstimateNodeSize(ruleNode);
        Point fallback = OutputNodes.FirstOrDefault() is OutputNodeViewModel outputNode
            ? new Point(outputNode.Location.X - ruleSize.Width - 120, outputNode.Location.Y)
            : new Point(430, 50);
        ruleNode.Location = ConsumeNextNodeSpawnLocation(fallback, ruleSize.Width, ruleSize.Height);
        Nodes.Add(ruleNode);

        if (_settings != null)
        {
            RebuildConnections(_settings);
        }

        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private async Task ConnectSelectedNodes()
    {
        var selected = SelectedNodes.OfType<IoFlowNodeViewModel>().Take(2).ToList();
        if (selected.Count < 2)
        {
            return;
        }

        await ConnectNodes(selected[0], selected[1]);
    }

    [RelayCommand]
    private async Task ConnectionCompleted(object? parameter)
    {
        IoFlowConnectorEndpoint? source = null;
        IoFlowConnectorEndpoint? target = null;

        if (parameter is not null)
        {
            TryExtractEndpointsFromParameter(parameter, out source, out target);
        }

        if (source == null || target == null || ReferenceEquals(source.Node, target.Node))
        {
            return;
        }

        if (source.IsOutput == target.IsOutput)
        {
            ShowAddNodeWarning("Connect an output connector to an input or operand connector.");
            return;
        }
        IoFlowConnectorEndpoint output = source.IsOutput ? source : target;
        IoFlowConnectorEndpoint input = source.IsOutput ? target : source;
        await ConnectNodes(output.Node, input.Node, input.OperandA);
    }

    private void ShowConnectionValidationWarning(IoFlowNodeViewModel first, IoFlowNodeViewModel second)
    {
        OutputNodeViewModel? mathOutput = new[] { first, second }
            .OfType<OutputNodeViewModel>()
            .FirstOrDefault(output => output.Channel.ChanType == OutputChannel.ChannelType.MATH);

        bool mathRuleTarget = mathOutput == first
            ? second is MathRuleNodeViewModel
            : first is MathRuleNodeViewModel;
        ShowAddNodeWarning(mathOutput != null && !mathRuleTarget
            ? "Logic outputs can only connect to a logic rule."
            : "That connection is not supported by the selected rule or channel.");
    }

    [RelayCommand]
    private void ItemsDragCompleted()
    {
        Dictionary<IoFlowNodeViewModel, Point>? before = _dragStartLocations;
        _dragStartLocations = null;
        RecordPositionUndo(before);
        UpdateUndoAvailability();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    private void SelectConnection(object? parameter)
    {
        IoFlowConnectionViewModel? connection = ExtractConnection(parameter);
        if (connection != null)
            SelectedNodes.Clear();
        SetSelectedConnection(connection);
    }

    public void ClearSelection()
    {
        SelectedNodes.Clear();
        SetSelectedConnection(null);
    }

    private async Task RemoveSelectedConnection()
    {
        if (SelectedConnection == null)
        {
            return;
        }

        IoFlowConnectionViewModel connection = SelectedConnection;
        await TrackConnectionEditAsync(() => RemoveConnectionCoreAsync(connection), connection.Target.Node as CanTxNodeViewModel);
    }

    private async Task RemoveConnection(object? parameter)
    {
        IoFlowConnectionViewModel? connection = ExtractConnection(parameter);
        if (connection == null)
        {
            return;
        }

        await TrackConnectionEditAsync(() => RemoveConnectionCoreAsync(connection), connection.Target.Node as CanTxNodeViewModel);
    }

    private static void TryExtractEndpointsFromParameter(object parameter, out IoFlowConnectorEndpoint? source, out IoFlowConnectorEndpoint? target)
    {
        source = null;
        target = null;

        if (parameter is ITuple tuple && tuple.Length >= 2)
        {
            source = ExtractEndpoint(tuple[0]);
            target = ExtractEndpoint(tuple[1]);
        }

        if ((source != null || target != null) || parameter is not PendingConnection pendingConnection)
        {
            return;
        }

        source = ExtractEndpoint(pendingConnection.Source);
        target = ExtractEndpoint(pendingConnection.Target)
            ?? ExtractEndpoint(pendingConnection.PreviewTarget);
    }

    private bool CanConnect(IoFlowNodeViewModel source, IoFlowNodeViewModel target, bool? operandA = null)
    {
        if (ReferenceEquals(source, target))
        {
            return false;
        }

        if (target is OutputNodeViewModel outputTarget)
        {
            if (outputTarget.Channel.ChanType == OutputChannel.ChannelType.MATH)
            {
                return source is MathRuleNodeViewModel mathRule
                    && (mathRule.IsStandalone || mathRule.OwnerChannelNumber == outputTarget.ChannelNumber)
                    && !WouldCreateOutputRootCycle(mathRule, outputTarget.ChannelNumber);
            }

            if (source is MathRuleNodeViewModel sourceRule)
            {
                return (sourceRule.IsStandalone || sourceRule.OwnerChannelNumber == outputTarget.ChannelNumber)
                    && !WouldCreateOutputRootCycle(sourceRule, outputTarget.ChannelNumber);
            }

            if (source is CanInputNodeViewModel)
            {
                return true;
            }

            return TryGetInputPin(source, out _);
        }

        if (target is MathRuleNodeViewModel targetRule)
        {
            if (operandA == false && targetRule.IsUnaryOperation)
            {
                return false;
            }
            if (operandA == null && targetRule.Rule.OperandAType != (byte)OutputChannel.MathOperandType.Constant
                && (targetRule.IsUnaryOperation
                    || targetRule.Rule.OperandBType != (byte)OutputChannel.MathOperandType.Constant))
            {
                return false;
            }

            return source switch
            {
                DigitalInputNodeViewModel or AnalogueInputNodeViewModel => true,
                OutputNodeViewModel outputSource => outputSource.ChannelNumber != targetRule.OwnerChannelNumber
                    && !IsOutputSourceUsedByTargetLogicTree(outputSource.ChannelNumber, targetRule)
                    && !WouldCreateOutputDependencyCycle(targetRule, outputSource.ChannelNumber),
                MathRuleNodeViewModel sourceRule => CanConnectMathRules(sourceRule, targetRule),
                MathSourceNodeViewModel node => node.OperandType == OutputChannel.MathOperandType.SystemValue
                    || _settings?.CustomCanSignalsStaticData.Any(signal => signal.Index == node.SourceIndex && IsCanRxSignal(signal)) == true,
                _ => false,
            };
        }

        if (target is CanTxNodeViewModel)
        {
            // A CAN TX message can only be triggered by an output channel's state.
            return source is OutputNodeViewModel;
        }

        return false;
    }

    private bool CanConnectMathRules(MathRuleNodeViewModel sourceRule, MathRuleNodeViewModel targetRule)
    {
        if (sourceRule.IsStandalone || targetRule.IsStandalone)
        {
            return sourceRule.IsStandalone && targetRule.IsStandalone;
        }

        return sourceRule.OwnerChannelNumber == targetRule.OwnerChannelNumber;
    }

    private static bool HasOwnedMathRuleCycle(OutputChannel channel)
    {
        int ruleCount = Math.Clamp((int)channel.MathRuleCount, 0, Math.Min(channel.MathRules.Count, Constants.NUM_MATH_RULE_SLOTS));
        HashSet<int> visiting = [];
        HashSet<int> visited = [];

        bool Visit(int ruleIndex)
        {
            if (visiting.Contains(ruleIndex))
            {
                return true;
            }

            if (!visited.Add(ruleIndex))
            {
                return false;
            }

            visiting.Add(ruleIndex);
            OutputChannel.MathRule rule = channel.MathRules[ruleIndex];
            if (rule.OperandAType == (byte)OutputChannel.MathOperandType.Rule
                && rule.OperandAIndex < ruleCount
                && Visit(rule.OperandAIndex))
            {
                return true;
            }

            if (rule.OperandBType == (byte)OutputChannel.MathOperandType.Rule
                && rule.OperandBIndex < ruleCount
                && Visit(rule.OperandBIndex))
            {
                return true;
            }

            visiting.Remove(ruleIndex);
            return false;
        }

        for (int ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            if (Visit(ruleIndex))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsOutputSourceUsedByTargetLogicTree(int channelNumber, MathRuleNodeViewModel targetRule)
    {
        return _outputRootLogicNodeByChannel.TryGetValue(channelNumber, out int rootLogicNodeId)
            && targetRule.IsStandalone
            && LogicTreeUsesNode(rootLogicNodeId, targetRule.LogicNodeId);
    }

    private bool WouldCreateOutputDependencyCycle(MathRuleNodeViewModel targetRule, int sourceChannelNumber)
    {
        IEnumerable<int> affectedChannels = targetRule.OwnerChannelNumber is int ownerChannelNumber
            ? [ownerChannelNumber]
            : _outputRootLogicNodeByChannel
                .Where(pair => LogicTreeUsesNode(pair.Value, targetRule.LogicNodeId))
                .Select(pair => pair.Key);

        return affectedChannels.Any(channelNumber =>
            HasOutputDependencyPath(sourceChannelNumber, channelNumber, new HashSet<int>()));
    }

    private bool WouldCreateOutputRootCycle(MathRuleNodeViewModel rootRule, int outputChannelNumber)
    {
        HashSet<MathRuleNodeViewModel> visitedRules = [];

        bool Visit(MathRuleNodeViewModel ruleNode)
        {
            if (!visitedRules.Add(ruleNode))
            {
                return false;
            }

            foreach ((byte operandType, byte operandIndex) in new[]
            {
                (ruleNode.Rule.OperandAType, ruleNode.Rule.OperandAIndex),
                (ruleNode.Rule.OperandBType, ruleNode.Rule.OperandBIndex),
            })
            {
                if (operandType == (byte)OutputChannel.MathOperandType.Channel
                    && HasOutputDependencyPath(operandIndex + 1, outputChannelNumber, new HashSet<int>()))
                {
                    return true;
                }

                if (operandType != (byte)OutputChannel.MathOperandType.Rule)
                {
                    continue;
                }

                MathRuleNodeViewModel? dependency = ruleNode.IsStandalone
                    ? MathRuleNodes.FirstOrDefault(candidate => candidate.IsStandalone && candidate.LogicNodeId == operandIndex)
                    : MathRuleNodes.FirstOrDefault(candidate =>
                        candidate.OwnerChannelNumber == ruleNode.OwnerChannelNumber && candidate.RuleIndex == operandIndex);
                if (dependency != null && Visit(dependency))
                {
                    return true;
                }
            }

            return false;
        }

        return Visit(rootRule);
    }

    private bool HasOutputDependencyPath(int sourceChannelNumber, int targetChannelNumber, HashSet<int> visitedChannels)
    {
        if (sourceChannelNumber == targetChannelNumber)
        {
            return true;
        }

        if (_settings == null || !visitedChannels.Add(sourceChannelNumber))
        {
            return false;
        }

        OutputChannel? channel = _settings.ChannelsStaticData
            .FirstOrDefault(candidate => candidate.ChannelNumber == sourceChannelNumber);
        if (channel?.ChanType != OutputChannel.ChannelType.MATH)
        {
            return false;
        }

        int ruleCount = Math.Clamp((int)channel.MathRuleCount, 0, Math.Min(channel.MathRules.Count, Constants.NUM_MATH_RULE_SLOTS));
        if (channel.MathRootRule >= ruleCount)
        {
            return false;
        }

        HashSet<int> visitedRules = [];
        bool VisitRule(int ruleIndex)
        {
            if (ruleIndex >= ruleCount || !visitedRules.Add(ruleIndex))
            {
                return false;
            }

            OutputChannel.MathRule rule = channel.MathRules[ruleIndex];
            foreach ((byte operandType, byte operandIndex) in new[]
            {
                (rule.OperandAType, rule.OperandAIndex),
                (rule.OperandBType, rule.OperandBIndex),
            })
            {
                if (operandType == (byte)OutputChannel.MathOperandType.Channel
                    && HasOutputDependencyPath(operandIndex + 1, targetChannelNumber, visitedChannels))
                {
                    return true;
                }

                if (operandType == (byte)OutputChannel.MathOperandType.Rule && VisitRule(operandIndex))
                {
                    return true;
                }
            }

            return false;
        }

        return VisitRule(channel.MathRootRule);
    }

    private static IoFlowConnectorEndpoint? ExtractEndpoint(object? value)
    {
        if (value is IoFlowConnectorEndpoint endpoint)
        {
            return endpoint;
        }

        if (value is Connector connector)
        {
            return connector.DataContext as IoFlowConnectorEndpoint;
        }

        if (value == null)
        {
            return null;
        }

        var dataContextProperty = value.GetType().GetProperty("DataContext");
        return dataContextProperty?.GetValue(value) as IoFlowConnectorEndpoint;
    }

    private static IoFlowConnectionViewModel? ExtractConnection(object? value)
    {
        if (value is IoFlowConnectionViewModel connection)
        {
            return connection;
        }

        if (value is Connection wire && wire.DataContext is IoFlowConnectionViewModel wireConnection)
        {
            return wireConnection;
        }

        if (value == null)
        {
            return null;
        }

        var dataContextProperty = value.GetType().GetProperty("DataContext");
        return dataContextProperty?.GetValue(value) as IoFlowConnectionViewModel;
    }

    public async Task ConnectNodes(IoFlowNodeViewModel source, IoFlowNodeViewModel target, bool? operandA = null)
    {
        await TrackConnectionEditAsync(() => ConnectNodesCoreAsync(source, target, operandA), target as CanTxNodeViewModel);
    }

    private async Task ConnectNodesCoreAsync(IoFlowNodeViewModel source, IoFlowNodeViewModel target, bool? operandA)
    {
        if (_settings == null || source == target)
        {
            return;
        }

        if (!CanConnect(source, target, operandA))
        {
            ShowAddNodeWarning(target is OutputNodeViewModel { Channel.ChanType: OutputChannel.ChannelType.MATH }
                ? "Logic outputs can only connect to a logic rule."
                : "That connection is not supported by the selected rule or channel.");
            return;
        }

        if (target is OutputNodeViewModel outputTarget)
        {
            if (source is MathRuleNodeViewModel sourceRule)
            {
                if (outputTarget.Channel.InputControlPin != 0)
                {
                    bool replace = ConfirmAction == null
                        ? false
                        : await ConfirmAction(
                            "Replace Output Input",
                            $"Output {outputTarget.ChannelNumber} already has an input connection. Remove it and connect logic instead?",
                            "REPLACE",
                            "CANCEL");

                    if (!replace)
                    {
                        return;
                    }
                }

                if (!ApplyLogicTreeToOutput(sourceRule, outputTarget))
                {
                    return;
                }

                outputTarget.Channel.InputControlPin = 0;
                if (sourceRule.IsStandalone)
                {
                    _outputRootLogicNodeByChannel[outputTarget.ChannelNumber] = sourceRule.LogicNodeId;
                }
                else
                {
                    _outputRootLogicNodeByChannel.Remove(outputTarget.ChannelNumber);
                }
            }
            else if (source is CanInputNodeViewModel)
            {
                _outputRootLogicNodeByChannel.Remove(outputTarget.ChannelNumber);
                outputTarget.Channel.InputControlPin = 0;

                if (outputTarget.Channel.ChanType is not (OutputChannel.ChannelType.CAN or OutputChannel.ChannelType.CAN_PWM))
                {
                    outputTarget.Channel.ChanType = OutputChannel.ChannelType.CAN;
                }
            }
            else if (TryGetInputPin(source, out byte pin))
            {
                _outputRootLogicNodeByChannel.Remove(outputTarget.ChannelNumber);
                if (!outputTarget.ConfigureInputPin(pin))
                {
                    ShowAddNodeWarning("The selected input is not compatible with this output channel.");
                    return;
                }
            }
            else
            {
                return;
            }
        }
        else if (target is MathRuleNodeViewModel targetRule)
        {
            MathConnectedSourceOption? option = GetSourceOption(targetRule, source);
            bool useA = operandA ?? (targetRule.Rule.OperandAType == (byte)OutputChannel.MathOperandType.Constant);
            if (option == null || !ApplyMathOperand(targetRule, useA, option))
            {
                return;
            }
        }
        else if (target is CanTxNodeViewModel canTxTarget && source is OutputNodeViewModel outputSource)
        {
            byte trigger = (byte)(outputSource.ChannelNumber - 1);
            if (SetCustomCanTxStateAction != null)
            {
                if (!await SetCustomCanTxStateAction(canTxTarget.MessageIndex, trigger, true))
                {
                    return;
                }
            }
            else
            {
                canTxTarget.Message.TxTrigger = trigger;
                canTxTarget.Message.IsEnabled = true;
            }
        }
        else
        {
            return;
        }

        RebuildConnections(_settings);
        NotifyGraphChanged();
    }

    private async Task RemoveConnectionCoreAsync(IoFlowConnectionViewModel connection)
    {
        if (_settings == null)
        {
            return;
        }

        IoFlowNodeViewModel source = connection.Source.Node;
        IoFlowNodeViewModel target = connection.Target.Node;

        if (target is OutputNodeViewModel outputTarget)
        {
            if (TryGetInputPin(source, out byte pin) && outputTarget.Channel.InputControlPin == pin)
            {
                _outputRootLogicNodeByChannel.Remove(outputTarget.ChannelNumber);
                ResetOutputChannelToIoFlowDefaults(outputTarget.Channel);
            }
            else if (source is CanInputNodeViewModel
                && (outputTarget.Channel.ChanType == OutputChannel.ChannelType.CAN || outputTarget.Channel.ChanType == OutputChannel.ChannelType.CAN_PWM))
            {
                _outputRootLogicNodeByChannel.Remove(outputTarget.ChannelNumber);
                ResetOutputChannelToIoFlowDefaults(outputTarget.Channel);
            }
            else if (source is MathRuleNodeViewModel sourceRule
                && ((sourceRule.OwnerChannelNumber == outputTarget.ChannelNumber
                        && sourceRule.RuleIndex == outputTarget.Channel.MathRootRule)
                    || (_outputRootLogicNodeByChannel.TryGetValue(outputTarget.ChannelNumber, out int mappedRoot)
                        && mappedRoot == sourceRule.LogicNodeId)))
            {
                _outputRootLogicNodeByChannel.Remove(outputTarget.ChannelNumber);
                ResetOutputChannelToIoFlowDefaults(outputTarget.Channel);
                SynchronizeMathChannelFromSettings(outputTarget.Channel, notifyGraphChanged: false);
            }
        }
        else if (target is MathRuleNodeViewModel targetRule)
        {
            if (!ApplyMathOperand(targetRule, connection.Target.OperandA ?? true,
                new MathConnectedSourceOption((byte)OutputChannel.MathOperandType.Constant, 0, "Constant")))
            {
                return;
            }
        }
        else if (target is CanTxNodeViewModel canTxTarget && source is OutputNodeViewModel outputSource
            && canTxTarget.Message.TxTrigger == outputSource.ChannelNumber - 1)
        {
            // Keep the trigger but stop transmitting: an unwired TX message is disabled
            // rather than switched to "Always", which would change its semantics.
            if (SetCustomCanTxStateAction != null)
            {
                if (!await SetCustomCanTxStateAction(canTxTarget.MessageIndex, canTxTarget.Message.TxTrigger, false))
                {
                    return;
                }
            }
            else
            {
                canTxTarget.Message.IsEnabled = false;
            }
        }

        if (ReferenceEquals(SelectedConnection, connection))
        {
            SetSelectedConnection(null);
        }

        RebuildConnections(_settings);
        NotifyGraphChanged();
    }

    private void RemoveSourceFromRuleOperand(MathRuleNodeViewModel targetRule, IoFlowNodeViewModel source)
    {
        ClearSourceFromMathRule(targetRule.Rule, targetRule.IsStandalone, source);
    }

    private bool ClearSourceFromMathRule(OutputChannel.MathRule rule, bool standalone, IoFlowNodeViewModel source)
    {
        MathRuleValues previous = CaptureMathRuleValues(rule);
        bool MatchesOperand(byte operandType, byte operandIndex)
        {
            return source switch
            {
                DigitalInputNodeViewModel digitalNode => operandType == (byte)OutputChannel.MathOperandType.DigitalInput
                    && operandIndex == (byte)(digitalNode.InputNumber - 1),
                AnalogueInputNodeViewModel analogueNode => operandType == (byte)OutputChannel.MathOperandType.AnalogueInput
                    && operandIndex == (byte)(analogueNode.InputNumber - 1),
                OutputNodeViewModel outputNode => operandType == (byte)OutputChannel.MathOperandType.Channel
                    && operandIndex == (byte)(outputNode.ChannelNumber - 1),
                MathRuleNodeViewModel ruleNode => operandType == (byte)OutputChannel.MathOperandType.Rule
                    && operandIndex == (byte)(standalone || ruleNode.IsStandalone ? ruleNode.LogicNodeId : ruleNode.RuleIndex),
                CanInputNodeViewModel => false,
                MathSourceNodeViewModel node => operandType == (byte)node.OperandType && operandIndex == node.SourceIndex,
                _ => false,
            };
        }

        if (MatchesOperand(rule.OperandAType, rule.OperandAIndex))
        {
            rule.OperandAType = (byte)OutputChannel.MathOperandType.Constant;
            rule.OperandAIndex = 0;
            rule.OperandAConstant = 0;
        }

        if (MatchesOperand(rule.OperandBType, rule.OperandBIndex))
        {
            rule.OperandBType = (byte)OutputChannel.MathOperandType.Constant;
            rule.OperandBIndex = 0;
            rule.OperandBConstant = 0;
        }
        return previous != CaptureMathRuleValues(rule);
    }

    private static bool TryGetInputPin(IoFlowNodeViewModel node, out byte pin)
    {
        pin = 0;

        if (node is DigitalInputNodeViewModel digitalSource
            && digitalSource.InputNumber >= 1
            && digitalSource.InputNumber <= InputPinCatalog.DIChannelInputPins.Length)
        {
            pin = InputPinCatalog.DIChannelInputPins[digitalSource.InputNumber - 1];
            return true;
        }

        if (node is AnalogueInputNodeViewModel analogueSource
            && analogueSource.InputNumber >= 1
            && analogueSource.InputNumber <= InputPinCatalog.ANAChannelInputPins.Length)
        {
            pin = InputPinCatalog.ANAChannelInputPins[analogueSource.InputNumber - 1];
            return true;
        }

        if (node is IgnitionOnNodeViewModel)
        {
            pin = InputPinCatalog.IgnitionInputPin;
            return true;
        }

        return false;
    }

    private bool ApplyLogicTreeToOutput(MathRuleNodeViewModel rootNode, OutputNodeViewModel outputTarget)
    {
        bool wasApplyingMathRulesFromGraph = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        try
        {
            if (rootNode.OwnerChannel != null)
            {
                outputTarget.Channel.ChanType = OutputChannel.ChannelType.MATH;
                outputTarget.Channel.MathRootRule = (byte)Math.Max(0, rootNode.RuleIndex);
                if (outputTarget.Channel.MathRuleCount <= rootNode.RuleIndex)
                {
                    outputTarget.Channel.MathRuleCount = (byte)(rootNode.RuleIndex + 1);
                }

                return true;
            }

            if (!TryBuildOrderedLogicTree(rootNode, out List<MathRuleNodeViewModel> ordered, out string validationError))
            {
                ShowAddNodeWarning(validationError);
                return false;
            }

            ApplyOrderedLogicTreeToOutput(rootNode, outputTarget, ordered);
            return true;
        }
        finally
        {
            _isApplyingMathRulesFromGraph = wasApplyingMathRulesFromGraph;
        }
    }

    private bool TrySynchronizeMappedMathOutputs(out string validationError)
    {
        var updates = new List<(MathRuleNodeViewModel Root, OutputNodeViewModel Output, List<MathRuleNodeViewModel> Rules)>();
        foreach ((int channelNumber, int logicNodeId) in _outputRootLogicNodeByChannel)
        {
            MathRuleNodeViewModel? rootNode = MathRuleNodes.FirstOrDefault(node => node.IsStandalone && node.LogicNodeId == logicNodeId);
            OutputNodeViewModel? outputNode = OutputNodes.FirstOrDefault(node => node.ChannelNumber == channelNumber);
            if (rootNode == null || outputNode == null)
            {
                validationError = $"Unable to synchronise the logic rules for output channel {channelNumber}: the connected logic or output node is missing.";
                return false;
            }

            if (!TryBuildOrderedLogicTree(rootNode, out List<MathRuleNodeViewModel> ordered, out validationError))
            {
                validationError = $"Unable to synchronise the logic rules for output channel {channelNumber}: {validationError}";
                return false;
            }

            updates.Add((rootNode, outputNode, ordered));
        }

        foreach ((MathRuleNodeViewModel rootNode, OutputNodeViewModel outputNode, List<MathRuleNodeViewModel> ordered) in updates)
        {
            ApplyOrderedLogicTreeToOutput(rootNode, outputNode, ordered);
        }

        foreach (MathRuleNodeViewModel ruleNode in MathRuleNodes.Where(node => node.IsStandalone))
        {
            ruleNode.AcceptCurrentRuleState();
        }

        validationError = string.Empty;
        return true;
    }

    public bool ReconcileMathConfiguration()
    {
        bool wasApplying = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        try
        {
            if (TrySynchronizeMappedMathOutputs(out string error)) return true;
            ShowAddNodeWarning(error);
            return false;
        }
        finally { _isApplyingMathRulesFromGraph = wasApplying; }
    }

    private static bool MathConfigurationMatches(OutputChannel configured, OutputChannel live)
    {
        if (live.ChanType != OutputChannel.ChannelType.MATH
            || configured.MathRuleCount != live.MathRuleCount
            || configured.MathRootRule != live.MathRootRule
            || configured.MathOutputMode != live.MathOutputMode) return false;
        for (int i = 0; i < configured.MathRuleCount; i++)
        {
            if (i >= configured.MathRules.Count || i >= live.MathRules.Count
                || CaptureMathRuleValues(configured.MathRules[i]) != CaptureMathRuleValues(live.MathRules[i])) return false;
        }
        return true;
    }

    private bool TryBuildOrderedLogicTree(
        MathRuleNodeViewModel rootNode,
        out List<MathRuleNodeViewModel> ordered,
        out string validationError)
    {
        var allLogicNodes = MathRuleNodes
            .Where(node => node.IsStandalone)
            .ToDictionary(node => node.LogicNodeId);
        var visited = new HashSet<int>();
        var visiting = new HashSet<int>();
        var ruleOrder = new List<MathRuleNodeViewModel>();
        string visitError = string.Empty;

        bool Visit(MathRuleNodeViewModel node)
        {
            if (visited.Contains(node.LogicNodeId))
            {
                return true;
            }

            if (!visiting.Add(node.LogicNodeId))
            {
                visitError = $"The logic chain contains a cycle at {node.Title}.";
                return false;
            }

            foreach (byte dependencyId in GetRuleDependencies(node))
            {
                if (!allLogicNodes.TryGetValue(dependencyId, out MathRuleNodeViewModel? dependencyNode))
                {
                    visitError = $"{node.Title} references a logic rule that is not present in the I/O Flow graph.";
                    return false;
                }

                if (!Visit(dependencyNode))
                {
                    return false;
                }
            }

            visiting.Remove(node.LogicNodeId);
            visited.Add(node.LogicNodeId);
            ruleOrder.Add(node);
            return true;
        }

        bool isValid = Visit(rootNode);
        ordered = ruleOrder;
        validationError = visitError;
        if (!isValid || ordered.Count == 0 || ordered.Count > Constants.NUM_MATH_RULE_SLOTS)
        {
            if (string.IsNullOrEmpty(validationError))
            {
                validationError = $"The logic chain must contain between 1 and {Constants.NUM_MATH_RULE_SLOTS} rules.";
            }

            return false;
        }

        return true;
    }

    private void ApplyOrderedLogicTreeToOutput(
        MathRuleNodeViewModel rootNode,
        OutputNodeViewModel outputTarget,
        IReadOnlyList<MathRuleNodeViewModel> ordered)
    {
        bool wasApplyingMathRulesFromGraph = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        try
        {
            outputTarget.Channel.EnsureMathRuleSlots();
            outputTarget.Channel.ChanType = OutputChannel.ChannelType.MATH;
            outputTarget.Channel.MathRuleCount = (byte)ordered.Count;

            var ruleIndexByLogicNode = new Dictionary<int, int>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                ruleIndexByLogicNode[ordered[i].LogicNodeId] = i;
            }

            outputTarget.Channel.MathRootRule = (byte)ruleIndexByLogicNode[rootNode.LogicNodeId];

            for (int i = 0; i < ordered.Count; i++)
            {
                OutputChannel.MathRule sourceRule = ordered[i].Rule;
                OutputChannel.MathRule targetRule = outputTarget.Channel.MathRules[i];

                targetRule.Operation = sourceRule.Operation;
                targetRule.HysteresisEnabled = sourceRule.HysteresisEnabled;
                targetRule.OnThreshold = sourceRule.OnThreshold;
                targetRule.OffThreshold = sourceRule.OffThreshold;
                targetRule.OperandAType = sourceRule.OperandAType;
                targetRule.OperandAIndex = sourceRule.OperandAIndex;
                targetRule.OperandAConstant = sourceRule.OperandAConstant;
                targetRule.OperandBType = sourceRule.OperandBType;
                targetRule.OperandBIndex = sourceRule.OperandBIndex;
                targetRule.OperandBConstant = sourceRule.OperandBConstant;

                if (targetRule.OperandAType == (byte)OutputChannel.MathOperandType.Rule
                    && ruleIndexByLogicNode.TryGetValue(sourceRule.OperandAIndex, out int operandAIndex))
                {
                    targetRule.OperandAIndex = (byte)operandAIndex;
                }

                if (targetRule.OperandBType == (byte)OutputChannel.MathOperandType.Rule
                    && ruleIndexByLogicNode.TryGetValue(sourceRule.OperandBIndex, out int operandBIndex))
                {
                    targetRule.OperandBIndex = (byte)operandBIndex;
                }
            }

            for (int index = ordered.Count; index < outputTarget.Channel.MathRules.Count; index++)
            {
                RestoreMathRuleValues(outputTarget.Channel.MathRules[index], default);
            }
        }
        finally
        {
            _isApplyingMathRulesFromGraph = wasApplyingMathRulesFromGraph;
        }
    }

    private static MathRuleValues CaptureMathRuleValues(OutputChannel.MathRule rule) =>
        new(rule.Operation, rule.HysteresisEnabled, rule.OnThreshold, rule.OffThreshold,
            rule.OperandAType, rule.OperandAIndex, rule.OperandAConstant,
            rule.OperandBType, rule.OperandBIndex, rule.OperandBConstant);

    private static void RestoreMathRuleValues(OutputChannel.MathRule rule, MathRuleValues values)
    {
        rule.Operation = values.Operation;
        rule.HysteresisEnabled = values.HysteresisEnabled;
        rule.OnThreshold = values.OnThreshold;
        rule.OffThreshold = values.OffThreshold;
        rule.OperandAType = values.OperandAType;
        rule.OperandAIndex = values.OperandAIndex;
        rule.OperandAConstant = values.OperandAConstant;
        rule.OperandBType = values.OperandBType;
        rule.OperandBIndex = values.OperandBIndex;
        rule.OperandBConstant = values.OperandBConstant;
    }

    private static IEnumerable<byte> GetRuleDependencies(MathRuleNodeViewModel node)
    {
        if (node.Rule.OperandAType == (byte)OutputChannel.MathOperandType.Rule)
        {
            yield return node.Rule.OperandAIndex;
        }

        if (node.Rule.OperandBType == (byte)OutputChannel.MathOperandType.Rule)
        {
            yield return node.Rule.OperandBIndex;
        }
    }

    private static bool UsesRuleIndex(OutputChannel.MathRule rule, int ruleIndex)
    {
        return (rule.OperandAType == (byte)OutputChannel.MathOperandType.Rule && rule.OperandAIndex == ruleIndex)
            || (rule.OperandBType == (byte)OutputChannel.MathOperandType.Rule && rule.OperandBIndex == ruleIndex);
    }

    [RelayCommand]
    private void RestoreAllNodes()
    {
        if (_settings == null)
        {
            return;
        }

        IoFlowGraphSnapshot layout = CreateSnapshot();
        _outputRootLogicNodeByChannel.Clear();
        Nodes.Clear();

        HashSet<int> usedDigitalInputs = GetUsedDigitalInputNumbers();
        HashSet<int> usedAnalogueInputs = GetUsedAnalogueInputNumbers();

        foreach (DigitalInput input in _settings.DigitalInputsStaticData.Where(item => usedDigitalInputs.Contains(item.InputNumber)))
        {
            input.IsActiveHigh = true;
            Nodes.Add(new DigitalInputNodeViewModel(input, new Point(50, 50)));
        }

        foreach (AnalogueInput input in _settings.AnalogueInputsStaticData.Where(item => usedAnalogueInputs.Contains(item.InputNumber)))
        {
            Nodes.Add(new AnalogueInputNodeViewModel(input, new Point(430, 50)));
        }

        foreach (OutputChannel channel in _settings.ChannelsStaticData)
        {
            AddOutputNodeWithOptionalMath(channel);
        }

        if (ShouldIncludeCanNodeFromSettings() && CanNodes.Count == 0)
        {
            Nodes.Add(new CanInputNodeViewModel(new Point(430, 50)));
        }

        if (ShouldIncludeIgnitionNodeFromSettings() && IgnitionNodes.Count == 0)
        {
            Nodes.Add(new IgnitionOnNodeViewModel(new Point(50, 50)));
        }

        RebuildConnections(_settings);
        ApplyNodeLocations(layout);
        foreach (var canvas in Canvases)
            AutoArrangeNodesOnCanvas(Nodes.Where(node => node.CanvasId == canvas.Id).ToList(),
                Connections.Where(connection => connection.Source.Node.CanvasId == canvas.Id).ToList());
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private async Task RemoveSelectedNodes()
    {
        if (SelectedNodes.Count == 0)
        {
            return;
        }

        var toRemove = SelectedNodes.OfType<IoFlowNodeViewModel>().ToList();
        bool controllerSettingsChanged = false;
        bool mathInputsChanged = false;
        bool previouslyApplying = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        try
        {
            foreach (IoFlowNodeViewModel source in toRemove.Where(node => node is not MathRuleNodeViewModel))
            {
                if (_settings != null)
                {
                    foreach (OutputChannel channel in _settings.ChannelsStaticData.Where(channel => channel.IsMathChannel))
                    {
                        foreach (OutputChannel.MathRule rule in channel.MathRules.Take(channel.MathRuleCount))
                        {
                            if (ClearSourceFromMathRule(rule, false, source))
                            {
                                mathInputsChanged = true;
                                controllerSettingsChanged = true;
                            }
                        }
                    }
                }
                foreach (MathRuleNodeViewModel target in MathRuleNodes.Where(node => node.IsStandalone && !toRemove.Contains(node)))
                {
                    MathRuleValues before = CaptureMathRuleValues(target.Rule);
                    RemoveSourceFromRuleOperand(target, source);
                    if (before != CaptureMathRuleValues(target.Rule))
                    {
                        mathInputsChanged = true;
                        controllerSettingsChanged |= !target.IsStandalone;
                    }
                }
            }
        }
        finally
        {
            _isApplyingMathRulesFromGraph = previouslyApplying;
        }
        foreach (var ownedRules in toRemove.OfType<MathRuleNodeViewModel>().Where(rule => !rule.IsStandalone)
            .GroupBy(rule => rule.OwnerChannel!))
        {
            RemoveChannelMathRules(ownedRules.Key, ownedRules.Select(rule => rule.RuleIndex).ToHashSet());
            controllerSettingsChanged = true;
        }

        MathRuleNodeViewModel[] removedLogicRules = toRemove.OfType<MathRuleNodeViewModel>().Where(rule => rule.IsStandalone).ToArray();
        bool wasApplying = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        try
        {
            foreach (MathRuleNodeViewModel removedRule in removedLogicRules)
            {
                foreach (int channelNumber in _outputRootLogicNodeByChannel
                    .Where(pair => pair.Value == removedRule.LogicNodeId).Select(pair => pair.Key).ToArray())
                {
                    _outputRootLogicNodeByChannel.Remove(channelNumber);
                    OutputChannel? channel = _settings?.ChannelsStaticData.FirstOrDefault(item => item.ChannelNumber == channelNumber);
                    if (channel != null)
                    {
                        ResetOutputChannelToIoFlowDefaults(channel);
                        foreach (OutputChannel.MathRule rule in channel.MathRules)
                        {
                            RestoreMathRuleValues(rule, default);
                        }
                        controllerSettingsChanged = true;
                    }
                }

                foreach (MathRuleNodeViewModel remainingRule in MathRuleNodes.Where(rule =>
                    rule.IsStandalone && !removedLogicRules.Contains(rule)))
                {
                    RemoveSourceFromRuleOperand(remainingRule, removedRule);
                }
            }
        }
        finally
        {
            _isApplyingMathRulesFromGraph = wasApplying;
        }

        foreach (var node in toRemove)
        {
            if (node is MathRuleNodeViewModel { IsStandalone: false })
            {
                continue;
            }

            if (node is CanTxNodeViewModel txNode &&
                (txNode.Message.IsEnabled || txNode.Message.TxTrigger != Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS))
            {
                if (SetCustomCanTxStateAction != null)
                {
                    if (!await SetCustomCanTxStateAction(txNode.MessageIndex, Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, false))
                    {
                        ShowAddNodeWarning("Unable to remove the CAN TX assignment. The node has been retained.");
                        continue;
                    }
                }
                else
                {
                    txNode.Message.IsEnabled = false;
                    txNode.Message.TxTrigger = Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
                }

                controllerSettingsChanged = true;
            }

            if (node is OutputNodeViewModel outputNode)
            {
                _outputRootLogicNodeByChannel.Remove(outputNode.ChannelNumber);
                var relatedRules = MathRuleNodes.Where(rule => rule.OwnerChannelNumber == outputNode.ChannelNumber).ToList();
                foreach (var ruleNode in relatedRules)
                {
                    Nodes.Remove(ruleNode);
                }
            }

            byte removedInputPin = node switch
            {
                DigitalInputNodeViewModel digital => InputPinCatalog.DIChannelInputPins[digital.InputNumber - 1],
                AnalogueInputNodeViewModel analogue => InputPinCatalog.ANAChannelInputPins[analogue.InputNumber - 1],
                IgnitionOnNodeViewModel => InputPinCatalog.IgnitionInputPin,
                _ => 0,
            };
            if (removedInputPin != 0 && _settings != null)
            {
                foreach (OutputChannel channel in _settings.ChannelsStaticData.Where(channel => channel.InputControlPin == removedInputPin))
                {
                    channel.InputControlPin = 0;
                    controllerSettingsChanged = true;
                }
            }

            Nodes.Remove(node);
        }

        if (removedLogicRules.Length > 0 || mathInputsChanged)
        {
            if (!TrySynchronizeMappedMathOutputs(out string validationError))
            {
                ShowAddNodeWarning(validationError);
            }
            controllerSettingsChanged |= _outputRootLogicNodeByChannel.Count > 0;
        }

        SelectedNodes.Clear();
        SetSelectedConnection(null);

        if (_settings != null)
        {
            RebuildConnections(_settings);
        }

        NotifyGraphChanged(controllerSettingsChanged);
    }

    [RelayCommand]
    private void AutoArrangeNodes()
    {
        Dictionary<IoFlowNodeViewModel, Point> before = CaptureNodeLocations();
        if (_settings != null)
        {
            RebuildConnections(_settings);
        }

        AutoArrangeNodesCore();
        RecordPositionUndo(before);
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    private void AutoArrangeNodesCore()
    {
        AutoArrangeNodesOnCanvas(VisibleNodes.ToList(), VisibleConnections.ToList());
    }

    private void AutoArrangeNodesOnCanvas(IReadOnlyList<IoFlowNodeViewModel> nodes, IReadOnlyList<IoFlowConnectionViewModel> connections)
    {
        var outputNodes = nodes.OfType<OutputNodeViewModel>().ToList();
        if (nodes.Count == 0)
        {
            return;
        }

        const double baseY = 70;
        const double outputColumnX = 1220;
        const double horizontalGap = 240;
        const double verticalGap = 30;
        const double outputGap = 60;

        var incoming = nodes.ToDictionary(node => node, _ => new List<IoFlowNodeViewModel>());
        var outgoing = nodes.ToDictionary(node => node, _ => new List<IoFlowNodeViewModel>());

        foreach (IoFlowConnectionViewModel connection in connections)
        {
            IoFlowNodeViewModel sourceNode = connection.Source.Node;
            IoFlowNodeViewModel targetNode = connection.Target.Node;

            if (outgoing.TryGetValue(sourceNode, out List<IoFlowNodeViewModel>? targets))
            {
                targets.Add(targetNode);
            }

            if (incoming.TryGetValue(targetNode, out List<IoFlowNodeViewModel>? sources))
            {
                sources.Add(sourceNode);
            }
        }

        var depthByNode = new Dictionary<IoFlowNodeViewModel, int>();
        foreach (OutputNodeViewModel outputNode in outputNodes)
        {
            depthByNode[outputNode] = 0;
        }

        void AssignDepthBranch(IoFlowNodeViewModel parent, int parentDepth, int direction, HashSet<IoFlowNodeViewModel> visiting)
        {
            IEnumerable<IoFlowNodeViewModel> neighbors = (direction < 0 ? incoming[parent] : outgoing[parent])
                .Where(node => node is not OutputNodeViewModel)
                .Distinct();

            int targetDepth = parentDepth + direction;

            foreach (IoFlowNodeViewModel neighbor in neighbors)
            {
                bool shouldAssign = !depthByNode.TryGetValue(neighbor, out int existingDepth)
                    || (direction < 0 && targetDepth < existingDepth)
                    || (direction > 0 && targetDepth > existingDepth);

                if (shouldAssign)
                {
                    depthByNode[neighbor] = targetDepth;
                }

                if (!visiting.Add(neighbor))
                {
                    continue;
                }

                AssignDepthBranch(neighbor, depthByNode[neighbor], direction, visiting);
                visiting.Remove(neighbor);
            }
        }

        foreach (OutputNodeViewModel outputNode in outputNodes)
        {
            AssignDepthBranch(outputNode, 0, -1, [outputNode]);
            AssignDepthBranch(outputNode, 0, 1, [outputNode]);
        }

        foreach (IoFlowNodeViewModel node in nodes)
        {
            if (!depthByNode.ContainsKey(node))
            {
                depthByNode[node] = outputNodes.Count == 0 ? 0 : 1;
            }
        }

        var sizeByNode = nodes.ToDictionary(node => node, EstimateNodeSize);

        var widthsByDepth = depthByNode
            .GroupBy(pair => pair.Value)
            .ToDictionary(group => group.Key, group => group.Max(pair => sizeByNode[pair.Key].Width));

        var depthValues = widthsByDepth.Keys.OrderBy(depth => depth).ToList();
        var xByDepth = new Dictionary<int, double>();
        xByDepth[0] = outputColumnX;

        foreach (int depth in depthValues.Where(depth => depth > 0))
        {
            int prevDepth = depth - 1;
            while (!widthsByDepth.ContainsKey(prevDepth))
            {
                prevDepth--;
            }

            xByDepth[depth] = xByDepth[prevDepth] + widthsByDepth[prevDepth] + horizontalGap;
        }

        foreach (int depth in depthValues.Where(depth => depth < 0).OrderByDescending(depth => depth))
        {
            int rightDepth = depth + 1;
            while (!widthsByDepth.ContainsKey(rightDepth))
            {
                rightDepth++;
            }

            xByDepth[depth] = xByDepth[rightDepth] - widthsByDepth[depth] - horizontalGap;
        }

        var positions = new Dictionary<IoFlowNodeViewModel, Point>();
        static double Snap(double value) => Math.Round(value / 10.0) * 10.0;

        IReadOnlyList<OutputNodeViewModel> outputs = outputNodes
            .OrderBy(GetOutputLayoutGroupKey)
            .ThenBy(node => node.ChannelNumber)
            .ToList();
        double outputTop = baseY;
        foreach (OutputNodeViewModel outputNode in outputs)
        {
            positions[outputNode] = new Point(xByDepth[0], Snap(outputTop));
            outputTop += sizeByNode[outputNode].Height + outputGap;
        }

        foreach (IoFlowNodeViewModel node in nodes)
        {
            if (positions.ContainsKey(node))
            {
                continue;
            }

            double preferredCenter = 0;
            int contributorCount = 0;

            foreach (IoFlowNodeViewModel neighbor in incoming[node].Concat(outgoing[node]).Distinct())
            {
                if (!positions.TryGetValue(neighbor, out Point neighborPoint))
                {
                    continue;
                }

                preferredCenter += neighborPoint.Y + (sizeByNode[neighbor].Height / 2.0);
                contributorCount++;
            }

            double preferredTop;
            if (contributorCount > 0)
            {
                preferredTop = (preferredCenter / contributorCount) - (sizeByNode[node].Height / 2.0);
            }
            else
            {
                preferredTop = outputTop;
                outputTop += sizeByNode[node].Height + verticalGap;
            }

            positions[node] = new Point(xByDepth[depthByNode[node]], Snap(preferredTop));
        }

        void PackDepth(int depth)
        {
            List<IoFlowNodeViewModel> columnNodes = depthByNode
                .Where(pair => pair.Value == depth)
                .Select(pair => pair.Key)
                .OrderBy(node => positions[node].Y)
                .ToList();

            if (columnNodes.Count == 0)
            {
                return;
            }

            double cursor = baseY;
            for (int i = 0; i < columnNodes.Count; i++)
            {
                IoFlowNodeViewModel node = columnNodes[i];
                NodeSize size = sizeByNode[node];
                double top = Math.Max(positions[node].Y, cursor);
                positions[node] = new Point(xByDepth[depth], Snap(top));
                cursor = positions[node].Y + size.Height + verticalGap;
            }

            for (int i = columnNodes.Count - 2; i >= 0; i--)
            {
                IoFlowNodeViewModel node = columnNodes[i];
                IoFlowNodeViewModel nextNode = columnNodes[i + 1];
                NodeSize size = sizeByNode[node];
                double maxTop = positions[nextNode].Y - size.Height - verticalGap;
                if (positions[node].Y > maxTop)
                {
                    positions[node] = new Point(xByDepth[depth], Snap(maxTop));
                }
            }
        }

        for (int iteration = 0; iteration < 5; iteration++)
        {
            foreach (IoFlowNodeViewModel node in nodes)
            {
                if (node is OutputNodeViewModel)
                {
                    continue;
                }

                double preferredCenter = 0;
                int contributorCount = 0;

                foreach (IoFlowNodeViewModel neighbor in incoming[node].Concat(outgoing[node]).Distinct())
                {
                    preferredCenter += positions[neighbor].Y + (sizeByNode[neighbor].Height / 2.0);
                    contributorCount++;
                }

                if (contributorCount == 0)
                {
                    continue;
                }

                double preferredTop = (preferredCenter / contributorCount) - (sizeByNode[node].Height / 2.0);
                positions[node] = new Point(xByDepth[depthByNode[node]], Snap(preferredTop));
            }

            foreach (int depth in depthValues)
            {
                PackDepth(depth);
            }
        }

        foreach ((IoFlowNodeViewModel node, Point point) in positions)
        {
            node.Location = point;
        }
    }

    private string GetOutputLayoutGroupKey(OutputNodeViewModel outputNode)
    {
        if (_outputRootLogicNodeByChannel.TryGetValue(outputNode.ChannelNumber, out int rootLogicNodeId))
        {
            return $"LOGIC:{rootLogicNodeId:D4}";
        }

        if (outputNode.Channel.InputControlPin == InputPinCatalog.IgnitionInputPin)
        {
            return "INPUT:IGNITION";
        }

        if (outputNode.Channel.InputControlPin != 0)
        {
            return $"INPUT:{outputNode.Channel.InputControlPin:D3}";
        }

        if (outputNode.Channel.ChanType is OutputChannel.ChannelType.CAN or OutputChannel.ChannelType.CAN_PWM)
        {
            return "INPUT:CAN";
        }

        return $"CHANNEL:{outputNode.ChannelNumber:D3}";
    }

    private readonly record struct NodeSize(double Width, double Height);

    private static NodeSize EstimateNodeSize(IoFlowNodeViewModel node)
    {
        NodeSize estimate = node switch
        {
            DigitalInputNodeViewModel => new NodeSize(310, 340),
            MathRuleNodeViewModel => new NodeSize(310, 460),
            AnalogueInputNodeViewModel => new NodeSize(310, 440),
            OutputNodeViewModel => new NodeSize(310, 500),
            CanTxNodeViewModel => new NodeSize(310, 360),
            MathSourceNodeViewModel => new NodeSize(310, 400),
            _ => new NodeSize(310, 220),
        };
        return new NodeSize(Math.Max(estimate.Width, node.MeasuredSize.Width),
            Math.Max(estimate.Height, node.MeasuredSize.Height));
    }

    public void UpdateLiveChannelSamples(IReadOnlyList<OutputChannel> liveChannels)
    {
        TimeSpan timestamp = IoFlowLiveHistory.Now;
        if (_live != null)
        {
            foreach (MathSourceNodeViewModel source in MathSourceNodes)
            {
                source.UpdateLiveState(_live);
                source.LiveHistory.Record(timestamp, source.GetLiveChartValue(_live));
            }
        }
        foreach (MathRuleNodeViewModel ruleNode in MathRuleNodes)
        {
            ruleNode.ClearLiveResults();
        }

        foreach (OutputNodeViewModel node in OutputNodes)
        {
            int index = node.ChannelNumber - 1;
            if (index < 0 || index >= liveChannels.Count)
            {
                node.LiveHistory.Record(timestamp, null);
                continue;
            }

            OutputChannel liveChannel = liveChannels[index];
            node.UpdateLiveState(liveChannel);
            node.UpdateLiveCurrent(liveChannel.CurrentValue);
            node.LiveHistory.Record(timestamp, liveChannel.CurrentValue);

            if (node.IsMathChannel && !MathConfigurationMatches(node.Channel, liveChannel))
            {
                foreach (MathRuleNodeViewModel rule in MathRuleNodes.Where(rule => rule.OwnerChannelNumber == node.ChannelNumber))
                    rule.MarkPendingControllerConfiguration();
                if (_outputRootLogicNodeByChannel.TryGetValue(node.ChannelNumber, out int pendingRootId)
                    && MathRuleNodes.FirstOrDefault(rule => rule.IsStandalone && rule.LogicNodeId == pendingRootId) is { } pendingRoot
                    && TryBuildOrderedLogicTree(pendingRoot, out var pendingRules, out _))
                    foreach (var rule in pendingRules) rule.MarkPendingControllerConfiguration();
                continue;
            }

            if (liveChannel.ChanType != OutputChannel.ChannelType.MATH)
            {
                continue;
            }

            MathRuleNodeViewModel[] ownedRules = MathRuleNodes.Where(rule =>
                rule.OwnerChannelNumber == node.ChannelNumber
                && rule.RuleIndex >= 0
                && rule.RuleIndex < liveChannel.MathRuleCount
                && rule.RuleIndex < liveChannel.MathRules.Count).ToArray();
            foreach (MathRuleNodeViewModel ownedRule in ownedRules)
            {
                IReadOnlyList<string> consumers = ownedRules
                    .Where(candidate => candidate.RuleIndex != ownedRule.RuleIndex
                        && UsesRuleIndex(candidate.Rule, ownedRule.RuleIndex))
                    .Select(candidate => $"Rule {candidate.RuleIndex + 1}")
                    .ToArray();
                string evaluation = MathRuntimeDisplayFormatter.FormatRuleEvaluation(
                    ownedRule.Rule,
                    liveChannel.MathRules[ownedRule.RuleIndex],
                    _live,
                    node.ChannelNumber,
                    ruleIndex => ruleIndex);
                ownedRule.UpdateLiveResult(
                    node.ChannelNumber,
                    liveChannel.MathRules[ownedRule.RuleIndex],
                    ownedRule.RuleIndex == liveChannel.MathRootRule,
                    liveChannel.MathOutputMode,
                    liveChannel.MathError,
                    liveChannel.MathCalculatedRootValue,
                    consumers,
                    evaluation,
                    MathRuntimeDisplayFormatter.FormatOperand(ownedRule.Rule, true, _live, node.ChannelNumber),
                    MathRuntimeDisplayFormatter.FormatOperand(ownedRule.Rule, false, _live, node.ChannelNumber),
                    _live);
            }

            if (!_outputRootLogicNodeByChannel.TryGetValue(node.ChannelNumber, out int rootLogicNodeId))
            {
                continue;
            }

            MathRuleNodeViewModel? rootNode = MathRuleNodes.FirstOrDefault(rule =>
                rule.IsStandalone && rule.LogicNodeId == rootLogicNodeId);
            if (rootNode == null || !TryBuildOrderedLogicTree(rootNode, out List<MathRuleNodeViewModel> orderedRules, out _))
            {
                continue;
            }

            int mappedRuleCount = Math.Min(orderedRules.Count, liveChannel.MathRules.Count);
            Dictionary<int, int> runtimeRuleIndices = orderedRules
                .Select((rule, index) => (rule.LogicNodeId, index))
                .ToDictionary(item => item.LogicNodeId, item => item.index);
            for (int ruleIndex = 0; ruleIndex < mappedRuleCount; ruleIndex++)
            {
                int? ResolveRuleIndex(byte ruleId) => runtimeRuleIndices.TryGetValue(ruleId, out int sourceRuleIndex)
                    ? sourceRuleIndex
                    : null;
                MathRuleNodeViewModel ruleNode = orderedRules[ruleIndex];
                IReadOnlyList<string> consumers = orderedRules
                    .Where(candidate => candidate.LogicNodeId != ruleNode.LogicNodeId
                        && candidate.LogicNodeId <= byte.MaxValue
                        && GetRuleDependencies(candidate).Contains((byte)ruleNode.LogicNodeId))
                    .Select(candidate => candidate.Title)
                    .ToArray();
                string evaluation = MathRuntimeDisplayFormatter.FormatRuleEvaluation(
                    ruleNode.Rule,
                    liveChannel.MathRules[ruleIndex],
                    _live,
                    node.ChannelNumber,
                    ResolveRuleIndex);
                ruleNode.UpdateLiveResult(
                    node.ChannelNumber,
                    liveChannel.MathRules[ruleIndex],
                    ruleNode.LogicNodeId == rootNode.LogicNodeId,
                    liveChannel.MathOutputMode,
                    liveChannel.MathError,
                    liveChannel.MathCalculatedRootValue,
                    consumers,
                    evaluation,
                    MathRuntimeDisplayFormatter.FormatOperand(ruleNode.Rule, true, _live, node.ChannelNumber, ResolveRuleIndex),
                    MathRuntimeDisplayFormatter.FormatOperand(ruleNode.Rule, false, _live, node.ChannelNumber, ResolveRuleIndex),
                    _live);
            }
        }

        foreach (DigitalInputNodeViewModel digitalNode in DigitalNodes)
        {
            DigitalInput? liveInput = _live?.DigitalInputsLiveData.ElementAtOrDefault(digitalNode.InputNumber - 1);
            digitalNode.UpdateLiveState(liveInput);
            digitalNode.LiveHistory.Record(timestamp, liveInput?.LiveElectricalState is bool state ? state ? 1 : 0 : null);
        }

        foreach (AnalogueInputNodeViewModel analogueNode in AnalogueNodes)
        {
            AnalogueInput? liveInput = _live?.AnalogueInputsLiveData.ElementAtOrDefault(analogueNode.InputNumber - 1);
            analogueNode.UpdateLiveState(liveInput);
            analogueNode.LiveHistory.Record(timestamp, liveInput?.InputValue ?? liveInput?.InputVoltage);
        }

        foreach (MathRuleNodeViewModel rule in MathRuleNodes)
            rule.LiveHistory.Record(timestamp, rule.LiveChartValue);

        foreach (CanTxNodeViewModel node in CanTxNodes)
        {
            // This is the live trigger channel, not a report of CAN frame transmission.
            OutputChannel? trigger = liveChannels.ElementAtOrDefault(node.Message.TxTrigger);
            node.LiveHistory.Record(timestamp, trigger == null ? null : trigger.Enabled != 0 ? 1 : 0);
        }
    }

    public void SynchronizeCanTxNodesFromSettings(DataStructures settings)
    {
        foreach (CanTxNodeViewModel node in CanTxNodes.Where(node => !IsConfiguredCanTxMessage(node.Message)).ToList())
        {
            SelectedNodes.Remove(node);
            Nodes.Remove(node);
        }

        foreach (CustomCanMessage message in settings.CustomCanMessagesStaticData)
        {
            bool assigned = message.TxTrigger < Constants.NUM_OUTPUT_CHANNELS;
            if (!assigned || !IsConfiguredCanTxMessage(message) ||
                CanTxNodes.Any(node => node.MessageIndex == message.Index))
            {
                continue;
            }

            Point fallback = new(1240, 300);
            if (message.IsEnabled && message.TxTrigger != Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS)
            {
                OutputNodeViewModel? triggerOutput = OutputNodes.FirstOrDefault(node => node.ChannelNumber == message.TxTrigger + 1);
                if (triggerOutput != null)
                {
                    NodeSize triggerOutputSize = EstimateNodeSize(triggerOutput);
                    fallback = new Point(triggerOutput.Location.X + triggerOutputSize.Width + 120, triggerOutput.Location.Y);
                }
            }

            CanTxNodeViewModel txNode = CreateCanTxNode(message, default);
            NodeSize size = EstimateNodeSize(txNode);
            txNode.Location = FindNearestAvailableSpawnLocation(
                ClampToSpawnViewport(fallback, size.Width, size.Height), size.Width, size.Height);
            Nodes.Add(txNode);
        }
    }

    public void RebuildConnections(DataStructures settings)
    {
        RefreshMathSources();
        EnsureOperandSourceNodes();
        RefreshNodeSourceOptions();
        RefreshCanTxNodeMessageOptions();
        RefreshAddNodeAvailability();

        Dictionary<byte, IoFlowNodeViewModel> inputsByPin = [];
        CanInputNodeViewModel? canNode = CanNodes.FirstOrDefault();

        foreach (IoFlowNodeViewModel node in Nodes)
        {
            switch (node)
            {
                case DigitalInputNodeViewModel digitalNode when digitalNode.InputNumber >= 1 && digitalNode.InputNumber <= InputPinCatalog.DIChannelInputPins.Length:
                    inputsByPin[InputPinCatalog.DIChannelInputPins[digitalNode.InputNumber - 1]] = digitalNode;
                    break;
                case AnalogueInputNodeViewModel analogueNode when analogueNode.InputNumber >= 1 && analogueNode.InputNumber <= InputPinCatalog.ANAChannelInputPins.Length:
                    inputsByPin[InputPinCatalog.ANAChannelInputPins[analogueNode.InputNumber - 1]] = analogueNode;
                    break;
                case IgnitionOnNodeViewModel:
                    inputsByPin[InputPinCatalog.IgnitionInputPin] = node;
                    break;
            }
        }

        List<IoFlowConnectionViewModel> desiredConnections = [];

        foreach (OutputNodeViewModel outputNode in OutputNodes)
        {
            byte pin = outputNode.Channel.InputControlPin;
            if (pin != 0
                && (outputNode.Channel.ChanType == OutputChannel.ChannelType.MATH
                    || !outputNode.IsInputTypeCompatible(pin, outputNode.Channel.ChanType)))
            {
                outputNode.Channel.InputControlPin = 0;
                pin = 0;
            }

            if (pin != 0 && inputsByPin.TryGetValue(pin, out IoFlowNodeViewModel? sourceNode))
            {
                AddConnection(desiredConnections, sourceNode, outputNode, $"{InputPinCatalog.GetLabelForPin(pin)} → OUT {outputNode.Channel.ChannelNumber}");
            }
            else if ((outputNode.Channel.ChanType == OutputChannel.ChannelType.CAN || outputNode.Channel.ChanType == OutputChannel.ChannelType.CAN_PWM)
                && canNode != null)
            {
                AddConnection(desiredConnections, canNode, outputNode, "CAN message → output");
            }

            if (outputNode.Channel.ChanType == OutputChannel.ChannelType.MATH)
            {
                AddMathConnections(desiredConnections, outputNode);
            }

            if (_outputRootLogicNodeByChannel.TryGetValue(outputNode.ChannelNumber, out int rootLogicNodeId))
            {
                MathRuleNodeViewModel? rootNode = outputNode.Channel.ChanType == OutputChannel.ChannelType.MATH
                    ? MathRuleNodes.FirstOrDefault(node => node.IsStandalone && node.LogicNodeId == rootLogicNodeId)
                    : null;
                if (rootNode != null)
                {
                    AddConnection(desiredConnections, rootNode, outputNode, "Logic result");
                }
                else
                {
                    _outputRootLogicNodeByChannel.Remove(outputNode.ChannelNumber);
                }
            }
        }

        foreach (CanTxNodeViewModel canTxNode in CanTxNodes)
        {
            CustomCanMessage message = canTxNode.Message;
            if (message.IsTx &&
                message.TxTrigger != Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS)
            {
                OutputNodeViewModel? triggerOutput = OutputNodes.FirstOrDefault(node => node.ChannelNumber == message.TxTrigger + 1);
                if (triggerOutput != null)
                {
                    AddConnection(desiredConnections, triggerOutput, canTxNode, $"OUT {triggerOutput.ChannelNumber} → CAN TX 0x{message.Id:X}");
                }
            }
        }

        foreach (MathRuleNodeViewModel ruleNode in MathRuleNodes.Where(node => node.IsStandalone))
        {
            AddMathOperandConnection(desiredConnections, ruleNode, true);
            if (!ruleNode.IsUnaryOperation)
            {
                AddMathOperandConnection(desiredConnections, ruleNode, false);
            }
        }

        ReconcileConnections(desiredConnections);
        RefreshStandaloneOutputLinkState();
        RefreshMathSourceSelectors();
    }

    private void AddMathConnections(List<IoFlowConnectionViewModel> desiredConnections, OutputNodeViewModel outputNode)
    {
        var rules = MathRuleNodes
            .Where(rule => rule.OwnerChannelNumber == outputNode.ChannelNumber)
            .OrderBy(rule => rule.RuleIndex)
            .ToList();

        foreach (MathRuleNodeViewModel ruleNode in rules)
        {
            if (ruleNode.RuleIndex == outputNode.Channel.MathRootRule)
            {
                AddConnection(desiredConnections, ruleNode, outputNode, "Logic result");
            }

            AddMathOperandConnection(desiredConnections, ruleNode, true);
            if (!ruleNode.IsUnaryOperation)
            {
                AddMathOperandConnection(desiredConnections, ruleNode, false);
            }
        }
    }

    private IReadOnlyList<MathOperandIndexOption> GetOperandIndexOptions(MathRuleNodeViewModel ruleNode, byte operandType)
    {
        return (OutputChannel.MathOperandType)operandType switch
        {
            OutputChannel.MathOperandType.Channel => BuildChannelOperandOptions(),
            OutputChannel.MathOperandType.DigitalInput => BuildDigitalInputOperandOptions(),
            OutputChannel.MathOperandType.AnalogueInput => BuildAnalogueInputOperandOptions(),
            OutputChannel.MathOperandType.CanSignal => BuildCanSignalOperandOptions(),
            OutputChannel.MathOperandType.SystemValue => BuildSystemValueOperandOptions(),
            OutputChannel.MathOperandType.Rule => BuildRuleOperandOptions(ruleNode),
            _ => [],
        };
    }

    private IReadOnlyList<MathOperandIndexOption> BuildChannelOperandOptions()
    {
        if (_settings == null)
        {
            return [];
        }

        return _settings.ChannelsStaticData
            .Select((channel, index) => new MathOperandIndexOption((byte)index, $"CH{index + 1} {GetChannelNameSuffix(channel)}".Trim()))
            .ToList();
    }

    private IReadOnlyList<MathOperandIndexOption> BuildDigitalInputOperandOptions()
    {
        if (_settings == null)
        {
            return [];
        }

        return _settings.DigitalInputsStaticData
            .Select(input => new MathOperandIndexOption((byte)(input.InputNumber - 1), $"Digital input {input.InputNumber}"))
            .ToList();
    }

    private IReadOnlyList<MathOperandIndexOption> BuildAnalogueInputOperandOptions()
    {
        if (_settings == null)
        {
            return [];
        }

        return _settings.AnalogueInputsStaticData
            .Where(input => input.InputNumber > 0 && input.InputNumber <= byte.MaxValue)
            .Select(input => new MathOperandIndexOption((byte)(input.InputNumber - 1), $"Analogue input {input.InputNumber}"))
            .ToList();
    }

    private void RefreshStandaloneOutputLinkState()
    {
        HashSet<int> linkedRuleIds = [];
        foreach (int rootLogicNodeId in _outputRootLogicNodeByChannel.Values)
        {
            MathRuleNodeViewModel? root = MathRuleNodes.FirstOrDefault(node =>
                node.IsStandalone && node.LogicNodeId == rootLogicNodeId);
            if (root == null || !TryBuildOrderedLogicTree(root, out List<MathRuleNodeViewModel> orderedRules, out _))
            {
                continue;
            }

            foreach (MathRuleNodeViewModel rule in orderedRules)
            {
                linkedRuleIds.Add(rule.LogicNodeId);
            }
        }

        foreach (MathRuleNodeViewModel rule in MathRuleNodes.Where(node => node.IsStandalone))
        {
            rule.SetLinkedToOutput(linkedRuleIds.Contains(rule.LogicNodeId));
        }
    }

    private IReadOnlyList<MathOperandIndexOption> BuildCanSignalOperandOptions()
    {
        if (_settings == null)
        {
            return [];
        }

        return _settings.CustomCanSignalsStaticData
            .Where(IsCanRxSignal)
            .OrderBy(signal => signal.Index)
            .Select(signal =>
            {
                int slotIndex = Math.Clamp(signal.Index, 0, byte.MaxValue);
                string name = string.IsNullOrWhiteSpace(signal.Name)
                    ? $"CAN Signal {slotIndex + 1}"
                    : signal.Name.Trim();
                return new MathOperandIndexOption((byte)slotIndex, $"{slotIndex + 1}: {name}");
            })
            .ToList();
    }

    private static IReadOnlyList<MathOperandIndexOption> BuildSystemValueOperandOptions()
    {
        return Enum.GetValues<OutputChannel.MathSystemValue>()
            .Select(value => new MathOperandIndexOption((byte)value, value.ToString()))
            .ToList();
    }

    private IReadOnlyList<MathOperandIndexOption> BuildRuleOperandOptions(MathRuleNodeViewModel ruleNode)
    {
        if (ruleNode.IsStandalone)
        {
            return MathRuleNodes
                .Where(node => node.IsStandalone)
                .OrderBy(node => node.LogicNodeId)
                .Select(node => new MathOperandIndexOption((byte)Math.Clamp(node.LogicNodeId, 0, byte.MaxValue), $"Logic {node.LogicNodeId}"))
                .ToList();
        }

        if (_settings == null || ruleNode.OwnerChannelNumber is not int ownerChannelNumber)
        {
            return [];
        }

        OutputChannel? channel = _settings.ChannelsStaticData.ElementAtOrDefault(ownerChannelNumber - 1);
        if (channel == null)
        {
            return [];
        }

        int maxRuleCount = Math.Min(channel.MathRuleCount, channel.MathRules.Count);
        var options = new List<MathOperandIndexOption>(maxRuleCount);
        for (int index = 0; index < maxRuleCount; index++)
        {
            options.Add(new MathOperandIndexOption((byte)index, $"Rule {index + 1}"));
        }

        return options;
    }

    private static string GetChannelNameSuffix(OutputChannel channel)
    {
        if (channel.Name == null)
        {
            return string.Empty;
        }

        string channelName = new string(channel.Name).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(channelName) ? string.Empty : channelName;
    }

    private static IoFlowConnectionViewModel CreateConnection(IoFlowNodeViewModel source, IoFlowNodeViewModel target, string label)
    {
        return new IoFlowConnectionViewModel(source.OutputConnector, target.InputConnector, label);
    }

    private void SynchronizeNodeSelectionState()
    {
        OnPropertyChanged(nameof(SelectedNode));
        OnPropertyChanged(nameof(HasSelectedNode));
        foreach (IoFlowNodeViewModel node in Nodes)
        {
            node.IsSelected = false;
        }

        foreach (IoFlowNodeViewModel selectedNode in SelectedNodes.OfType<IoFlowNodeViewModel>())
        {
            selectedNode.IsSelected = true;
        }

        if (SelectedNodes.Count > 0 && SelectedConnection != null)
        {
            SetSelectedConnection(null);
        }
    }

    private void SetSelectedConnection(IoFlowConnectionViewModel? connection)
    {
        if (SelectedConnection != null)
        {
            SelectedConnection.IsSelected = false;
        }

        SelectedConnection = connection;

        if (SelectedConnection != null)
        {
            SelectedConnection.IsSelected = true;
        }
    }

    private void NotifyGraphChanged(bool controllerSettingsChanged = true)
    {
        GraphChangedAction?.Invoke(controllerSettingsChanged);
    }

    private bool TrySelectOutputChannel(OutputNodeViewModel node, int channelNumber)
    {
        if (_settings == null)
        {
            return false;
        }

        OutputChannel? selectedChannel = _settings.ChannelsStaticData.FirstOrDefault(channel => channel.ChannelNumber == channelNumber);
        if (selectedChannel == null)
        {
            return false;
        }

        if (ReferenceEquals(node.Channel, selectedChannel))
        {
            return true;
        }

        OutputNodeViewModel? existingNode = OutputNodes.FirstOrDefault(outputNode => !ReferenceEquals(outputNode, node) && ReferenceEquals(outputNode.Channel, selectedChannel));
        OutputChannel previousChannel = node.Channel;

        ClearUndoHistory();
        if (existingNode != null)
        {
            existingNode.AssignChannel(previousChannel);
        }

        node.AssignChannel(selectedChannel);

        RebuildConnections(_settings);
        NotifyGraphChanged(controllerSettingsChanged: false);
        return true;
    }

    private static void AddConnection(List<IoFlowConnectionViewModel> desiredConnections, IoFlowNodeViewModel source, IoFlowNodeViewModel target, string label)
    {
        desiredConnections.Add(CreateConnection(source, target, label));
    }

    private void ReconcileConnections(List<IoFlowConnectionViewModel> desiredConnections)
    {
        var existing = Connections.ToDictionary(connection => (connection.Source, connection.Target));
        var desired = new List<IoFlowConnectionViewModel>(desiredConnections.Count);
        foreach (IoFlowConnectionViewModel connection in desiredConnections)
        {
            if (existing.TryGetValue((connection.Source, connection.Target), out IoFlowConnectionViewModel? retained))
            {
                retained.Label = connection.Label;
                desired.Add(retained);
            }
            else
            {
                desired.Add(connection);
            }
        }

        foreach (IoFlowConnectionViewModel connection in Connections.Where(connection => !desired.Contains(connection)).ToList())
        {
            if (ReferenceEquals(SelectedConnection, connection))
                SetSelectedConnection(null);
            Connections.Remove(connection);
        }
        foreach (IoFlowConnectionViewModel connection in desired)
        {
            if (!Connections.Contains(connection))
                Connections.Add(connection);
        }

        var connectedEndpoints = desired.SelectMany(connection => new[] { connection.Source, connection.Target }).ToHashSet();
        foreach (IoFlowNodeViewModel node in Nodes)
        {
            node.InputConnector.IsConnected = connectedEndpoints.Contains(node.InputConnector);
            node.OutputConnector.IsConnected = connectedEndpoints.Contains(node.OutputConnector);
            if (node is MathRuleNodeViewModel math)
            {
                math.OperandAConnector.IsConnected = connectedEndpoints.Contains(math.OperandAConnector);
                math.OperandBConnector.IsConnected = connectedEndpoints.Contains(math.OperandBConnector);
            }
        }
        KeepConnectedNodesOnSameCanvas();
    }

    private static void ResetOutputChannelToIoFlowDefaults(OutputChannel channel)
    {
        channel.ChanType = OutputChannel.ChannelType.Digital;
        channel.Category = OutputChannel.ChannelCategory.Auxiliary;
        channel.InputControlPin = 0;
        channel.PWMSetDuty = 0;
        channel.RetryCount = 0;
        channel.CurrentThresholdLow = 0;
        channel.CurrentThresholdHigh = 0;
        channel.InrushDelay = 0;
        channel.InrushCurrentLimit = 0;
        channel.OnThreshold = 0;
        channel.OffThreshold = 0;
        channel.ScaleMin = 0;
        channel.ScaleMax = 0;
        channel.PWMMin = 0;
        channel.PWMMax = 100;
        channel.IntermittentOnTime = 0;
        channel.IntermittentOffTime = 0;
        channel.DelayedOn = 0;
        channel.DelayedOnTime = 0;
        channel.DelayedOff = 0;
        channel.DelayedOffTime = 0;
        channel.DelayedOffTrigger = 0;
        channel.SoftStartEnabled = 0;
        channel.SoftStartTime = 0;
        channel.SoftStopEnabled = 0;
        channel.SoftStopTime = 0;
        channel.MathRuleCount = 1;
        channel.MathRootRule = 0;
        channel.MathOutputMode = 0;
    }

    private void AddOutputNodeWithOptionalMath(OutputChannel channel, bool useSpawnLocation = false, int? canvasId = null)
    {
        OutputNodeViewModel? outputNode = OutputNodes.FirstOrDefault(node => node.ChannelNumber == channel.ChannelNumber);
        if (outputNode == null)
        {
            Point outputLocation = useSpawnLocation
                ? ConsumeNextNodeSpawnLocation(new Point(830, 50), nodeWidth: 310, nodeHeight: 500)
                : new Point(830, 50);
            outputNode = new OutputNodeViewModel(channel, outputLocation, _settings?.AnalogueInputsStaticData ?? [], _settings?.ChannelsStaticData ?? [], TrySelectOutputChannel);
            outputNode.CanvasId = canvasId ?? SelectedCanvas.Id;
            Nodes.Add(outputNode);
        }

        if (channel.ChanType != OutputChannel.ChannelType.MATH)
        {
            return;
        }

        channel.EnsureMathRuleSlots();
        int activeRuleCount = channel.MathRuleCount;
        for (int ruleIndex = 0; ruleIndex < activeRuleCount && ruleIndex < channel.MathRules.Count; ruleIndex++)
        {
            if (Nodes.OfType<MathRuleNodeViewModel>().Any(node => node.OwnerChannelNumber == channel.ChannelNumber && node.RuleIndex == ruleIndex))
            {
                continue;
            }

            MathRuleNodeViewModel ruleNode = CreateMathRuleNode(channel, ruleIndex, channel.MathRules[ruleIndex], default);
            ruleNode.CanvasId = outputNode.CanvasId;
            NodeSize ruleSize = EstimateNodeSize(ruleNode);
            ruleNode.Location = new Point(outputNode.Location.X - ruleSize.Width - 120, outputNode.Location.Y);
            ruleNode.Location = FindNearestAvailableSpawnLocation(ruleNode.Location, ruleSize.Width, ruleSize.Height);
            Nodes.Add(ruleNode);
        }
    }
}
