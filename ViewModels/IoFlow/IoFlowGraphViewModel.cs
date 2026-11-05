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
    private Point? _nextNodeSpawnLocation;
    private Point _spawnViewportOrigin;
    private Size _spawnViewportSize;
    private string _addNodeWarning = string.Empty;

    public bool UseControllerConfigurationValues { get; set; }

    public Func<string, string, string, string, Task<bool>>? ConfirmAction { get; set; }

    /// <summary>
    /// Persists a Custom CAN TX message's trigger channel and enabled state to the
    /// controller: (messageIndex, triggerChannelZeroBased, enabled) → success.
    /// </summary>
    public Func<int, byte, bool, Task<bool>>? SetCustomCanTxStateAction { get; set; }

    /// <summary>
    /// Persists a Custom CAN TX message's timing mode and timeout/period to the
    /// controller: (messageIndex, isPeriodic, timeoutMs) → success.
    /// </summary>
    public Func<int, bool, ushort, Task<bool>>? SetCustomCanTxModeAction { get; set; }

    public Action<bool>? GraphChangedAction { get; set; }

    public ObservableCollection<IoFlowNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<IoFlowConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<object> SelectedNodes { get; } = [];

    public IoFlowConnectionViewModel? SelectedConnection { get; private set; }

    public IRelayCommand<object?> SelectConnectionCommand { get; }

    public IRelayCommand RemoveSelectedConnectionCommand { get; }

    public IRelayCommand<object?> RemoveConnectionCommand { get; }

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
        SelectConnectionCommand = new RelayCommand<object?>(SelectConnection);
        RemoveSelectedConnectionCommand = new RelayCommand(RemoveSelectedConnection);
        RemoveConnectionCommand = new RelayCommand<object?>(RemoveConnection);
        SelectedNodes.CollectionChanged += (_, _) => SynchronizeNodeSelectionState();
        Nodes.CollectionChanged += (_, _) => RefreshAddNodeAvailability();
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

    private Point FindNearestAvailableSpawnLocation(Point preferred, double nodeWidth, double nodeHeight)
    {
        if (IsSpawnCandidateAvailable(preferred, nodeWidth, nodeHeight))
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
                    Point candidate = ClampToSpawnViewport(rawCandidate, nodeWidth, nodeHeight);
                    if (IsSpawnCandidateAvailable(candidate, nodeWidth, nodeHeight))
                    {
                        return candidate;
                    }
                }
            }
        }

        return preferred;
    }

    private bool IsSpawnCandidateAvailable(Point location, double nodeWidth, double nodeHeight)
    {
        Rect candidateRect = new(location, new Size(nodeWidth, nodeHeight));

        foreach (IoFlowNodeViewModel existingNode in Nodes)
        {
            NodeSize existingSize = EstimateNodeSize(existingNode);
            Rect existingRect = ExpandRect(new Rect(existingNode.Location, new Size(existingSize.Width, existingSize.Height)), 20);
            if (existingRect.Intersects(candidateRect))
            {
                return false;
            }
        }

        if (IsOverlappingConnectionSegment(candidateRect))
        {
            return false;
        }

        return true;
    }

    private bool IsOverlappingConnectionSegment(Rect candidateRect)
    {
        Rect expandedCandidate = ExpandRect(candidateRect, 10);

        foreach (IoFlowConnectionViewModel connection in Connections)
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
        _settings = settings;
        _live = live;
        _nextLogicNodeId = 1;
        _outputRootLogicNodeByChannel.Clear();
        UseControllerConfigurationValues = false;
        Nodes.Clear();
        Connections.Clear();
        SelectedNodes.Clear();
        SetSelectedConnection(null);
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

        foreach (CustomCanMessage message in GetCanTxEligibleMessages())
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

        foreach (OutputChannel channel in _settings.ChannelsStaticData)
        {
            channel.EnsureMathRuleSlots();
            int activeRuleCount = Math.Min(channel.MathRuleCount, channel.MathRules.Count);
            for (int ruleIndex = 0; ruleIndex < activeRuleCount; ruleIndex++)
            {
                OutputChannel.MathRule rule = channel.MathRules[ruleIndex];
                if (rule.OperandAType == (byte)OutputChannel.MathOperandType.CanSignal
                    || rule.OperandBType == (byte)OutputChannel.MathOperandType.CanSignal)
                {
                    return true;
                }
            }
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
                CollectUsedInputNumbersFromOperand(rule.OperandBType, rule.OperandBIndex, used, null);
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
                CollectUsedInputNumbersFromOperand(rule.OperandBType, rule.OperandBIndex, null, used);
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

    public IoFlowGraphSnapshot CreateSnapshot()
    {
        var snapshot = new IoFlowGraphSnapshot();

        foreach (IoFlowNodeViewModel node in Nodes)
        {
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
            }
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
                    Nodes.Add(new MathRuleNodeViewModel(node.Id, new Point(node.X, node.Y), GetOperandIndexOptions));
                    break;
            }
        }

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
        Nodes.Add(new DigitalInputNodeViewModel(next, ConsumeNextNodeSpawnLocation(new Point(50, 50), nodeWidth: 280, nodeHeight: 170)));
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

        Nodes.Add(new AnalogueInputNodeViewModel(next, ConsumeNextNodeSpawnLocation(new Point(430, 50), nodeWidth: 320, nodeHeight: 260)));
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

        Nodes.Add(new CanInputNodeViewModel(ConsumeNextNodeSpawnLocation(new Point(430, 50), nodeWidth: 320, nodeHeight: 160)));
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
        return new CanTxNodeViewModel(message, location, options, OnCanTxNodeMessageSelectionChanged);
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

        Nodes.Add(CreateCanTxNode(next, ConsumeNextNodeSpawnLocation(new Point(1240, 300), nodeWidth: 420, nodeHeight: 260)));
        RebuildConnections(_settings);
        ClearAddNodeWarning();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private async Task ApplyCanTxNodeSettings(CanTxNodeViewModel? node)
    {
        if (node == null || SetCustomCanTxModeAction == null)
        {
            return;
        }

        int timeoutCandidate = node.EditTimeoutMs;
        if (timeoutCandidate < 0 || timeoutCandidate > 60000)
        {
            node.SetValidationMessage("Timeout/period must be between 0 and 60,000 ms.");
            return;
        }

        if (node.EditIsPeriodic && timeoutCandidate < 1)
        {
            node.SetValidationMessage("Periodic mode requires a value between 1 and 60,000 ms.");
            return;
        }

        bool persisted = await SetCustomCanTxModeAction(node.MessageIndex, node.EditIsPeriodic, (ushort)timeoutCandidate);
        if (!persisted)
        {
            node.SetValidationMessage("Unable to save CAN TX settings to controller.");
            node.RefreshEditFieldsFromMessage();
            return;
        }

        node.ClearValidationMessage();
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

        Nodes.Add(new IgnitionOnNodeViewModel(ConsumeNextNodeSpawnLocation(new Point(50, 50), nodeWidth: 300, nodeHeight: 140)));
        RebuildConnections(_settings);
        ClearAddNodeWarning();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void AddLogicNode()
    {
        Nodes.Add(new MathRuleNodeViewModel(_nextLogicNodeId++, ConsumeNextNodeSpawnLocation(new Point(1240, 50), nodeWidth: 420, nodeHeight: 420), GetOperandIndexOptions));

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

        IoFlowNodeViewModel sourceNode = source.Node;
        IoFlowNodeViewModel targetNode = target.Node;

        if (CanConnect(sourceNode, targetNode))
        {
            await ConnectNodes(sourceNode, targetNode);
            return;
        }

        if (CanConnect(targetNode, sourceNode))
        {
            await ConnectNodes(targetNode, sourceNode);
        }
    }

    [RelayCommand]
    private void ItemsDragCompleted()
    {
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    private void SelectConnection(object? parameter)
    {
        SetSelectedConnection(ExtractConnection(parameter));
    }

    private void RemoveSelectedConnection()
    {
        if (SelectedConnection == null)
        {
            return;
        }

        RemoveConnectionCore(SelectedConnection);
    }

    private void RemoveConnection(object? parameter)
    {
        IoFlowConnectionViewModel? connection = ExtractConnection(parameter);
        if (connection == null)
        {
            return;
        }

        RemoveConnectionCore(connection);
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

    private static bool CanConnect(IoFlowNodeViewModel source, IoFlowNodeViewModel target)
    {
        if (target is OutputNodeViewModel)
        {
            return source is DigitalInputNodeViewModel
                || source is AnalogueInputNodeViewModel
                || source is IgnitionOnNodeViewModel
                || source is CanInputNodeViewModel
                || source is MathRuleNodeViewModel;
        }

        if (target is MathRuleNodeViewModel)
        {
            return source is DigitalInputNodeViewModel
                || source is AnalogueInputNodeViewModel
                || source is OutputNodeViewModel
                || source is MathRuleNodeViewModel
                || source is CanInputNodeViewModel;
        }

        if (target is CanTxNodeViewModel)
        {
            // A CAN TX message can only be triggered by an output channel's state.
            return source is OutputNodeViewModel;
        }

        return false;
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

    public async Task ConnectNodes(IoFlowNodeViewModel source, IoFlowNodeViewModel target)
    {
        if (_settings == null || source == target)
        {
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

                    outputTarget.Channel.InputControlPin = 0;
                }

                if (!ApplyLogicTreeToOutput(sourceRule, outputTarget))
                {
                    return;
                }

                _outputRootLogicNodeByChannel[outputTarget.ChannelNumber] = sourceRule.LogicNodeId;
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
                outputTarget.Channel.ChanType = outputTarget.Channel.ChanType == OutputChannel.ChannelType.MATH
                    ? OutputChannel.ChannelType.Digital
                    : outputTarget.Channel.ChanType;
                ClearInputPinFromOtherOutputs(pin, outputTarget.ChannelNumber);
                outputTarget.Channel.InputControlPin = pin;
            }
            else
            {
                return;
            }
        }
        else if (target is MathRuleNodeViewModel targetRule)
        {
            ApplySourceToRuleOperand(targetRule, source);
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

    private async Task DisableCanTxMessageAsync(CanTxNodeViewModel node)
    {
        if (SetCustomCanTxStateAction == null || _settings == null)
        {
            return;
        }

        await SetCustomCanTxStateAction(node.MessageIndex, node.Message.TxTrigger, false);
        RebuildConnections(_settings);
        NotifyGraphChanged();
    }

    private void RemoveConnectionCore(IoFlowConnectionViewModel connection)
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
                && _outputRootLogicNodeByChannel.TryGetValue(outputTarget.ChannelNumber, out int mappedRoot)
                && mappedRoot == sourceRule.LogicNodeId)
            {
                _outputRootLogicNodeByChannel.Remove(outputTarget.ChannelNumber);
                ResetOutputChannelToIoFlowDefaults(outputTarget.Channel);
            }
        }
        else if (target is MathRuleNodeViewModel targetRule)
        {
            RemoveSourceFromRuleOperand(targetRule, source);
        }
        else if (target is CanTxNodeViewModel canTxTarget && source is OutputNodeViewModel outputSource
            && canTxTarget.Message.TxTrigger == outputSource.ChannelNumber - 1)
        {
            // Keep the trigger but stop transmitting: an unwired TX message is disabled
            // rather than switched to "Always", which would change its semantics.
            if (SetCustomCanTxStateAction != null)
            {
                _ = DisableCanTxMessageAsync(canTxTarget);
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
                    && operandIndex == (byte)(targetRule.IsStandalone || ruleNode.IsStandalone ? ruleNode.LogicNodeId : ruleNode.RuleIndex),
                CanInputNodeViewModel => false,
                _ => false,
            };
        }

        if (MatchesOperand(targetRule.Rule.OperandAType, targetRule.Rule.OperandAIndex))
        {
            targetRule.Rule.OperandAType = (byte)OutputChannel.MathOperandType.Constant;
            targetRule.Rule.OperandAIndex = 0;
            targetRule.Rule.OperandAConstant = 0;
        }

        if (MatchesOperand(targetRule.Rule.OperandBType, targetRule.Rule.OperandBIndex))
        {
            targetRule.Rule.OperandBType = (byte)OutputChannel.MathOperandType.Constant;
            targetRule.Rule.OperandBIndex = 0;
            targetRule.Rule.OperandBConstant = 0;
        }
    }

    private void ClearInputPinFromOtherOutputs(byte inputPin, int keepChannelNumber)
    {
        foreach (OutputNodeViewModel outputNode in OutputNodes)
        {
            if (outputNode.ChannelNumber == keepChannelNumber)
            {
                continue;
            }

            if (outputNode.Channel.InputControlPin == inputPin)
            {
                _outputRootLogicNodeByChannel.Remove(outputNode.ChannelNumber);
                ResetOutputChannelToIoFlowDefaults(outputNode.Channel);
            }
        }
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

        var allLogicNodes = MathRuleNodes.ToDictionary(node => node.LogicNodeId);
        var visited = new HashSet<int>();
        var visiting = new HashSet<int>();
        var ordered = new List<MathRuleNodeViewModel>();

        bool Visit(MathRuleNodeViewModel node)
        {
            if (visited.Contains(node.LogicNodeId))
            {
                return true;
            }

            if (!visiting.Add(node.LogicNodeId))
            {
                return false;
            }

            foreach (byte dependencyId in GetRuleDependencies(node))
            {
                if (!allLogicNodes.TryGetValue(dependencyId, out MathRuleNodeViewModel? dependencyNode))
                {
                    return false;
                }

                if (!Visit(dependencyNode))
                {
                    return false;
                }
            }

            visiting.Remove(node.LogicNodeId);
            visited.Add(node.LogicNodeId);
            ordered.Add(node);
            return true;
        }

        if (!Visit(rootNode) || ordered.Count == 0 || ordered.Count > Constants.NUM_MATH_RULE_SLOTS)
        {
            return false;
        }

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

        return true;
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

    [RelayCommand]
    private void RestoreAllNodes()
    {
        if (_settings == null)
        {
            return;
        }

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
        AutoArrangeNodesCore();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void RemoveSelectedNodes()
    {
        if (SelectedNodes.Count == 0)
        {
            return;
        }

        var toRemove = SelectedNodes.OfType<IoFlowNodeViewModel>().ToList();
        foreach (var node in toRemove)
        {
            if (node is OutputNodeViewModel outputNode)
            {
                _outputRootLogicNodeByChannel.Remove(outputNode.ChannelNumber);
                var relatedRules = MathRuleNodes.Where(rule => rule.OwnerChannelNumber == outputNode.ChannelNumber).ToList();
                foreach (var ruleNode in relatedRules)
                {
                    Nodes.Remove(ruleNode);
                }
            }

            if (node is MathRuleNodeViewModel logicNode)
            {
                foreach (int channelNumber in _outputRootLogicNodeByChannel
                    .Where(pair => pair.Value == logicNode.LogicNodeId)
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    _outputRootLogicNodeByChannel.Remove(channelNumber);
                }
            }

            Nodes.Remove(node);
        }

        SelectedNodes.Clear();
        SetSelectedConnection(null);

        if (_settings != null)
        {
            RebuildConnections(_settings);
        }

        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    [RelayCommand]
    private void AutoArrangeNodes()
    {
        if (_settings != null)
        {
            RebuildConnections(_settings);
        }

        AutoArrangeNodesCore();
        NotifyGraphChanged(controllerSettingsChanged: false);
    }

    private void AutoArrangeNodesCore()
    {
        if (Nodes.Count == 0)
        {
            return;
        }

        const double baseY = 70;
        const double outputColumnX = 1220;
        const double horizontalGap = 120;
        const double verticalGap = 30;
        const double outputGap = 60;

        var incoming = Nodes.ToDictionary(node => node, _ => new List<IoFlowNodeViewModel>());
        var outgoing = Nodes.ToDictionary(node => node, _ => new List<IoFlowNodeViewModel>());

        foreach (IoFlowConnectionViewModel connection in Connections)
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
        foreach (OutputNodeViewModel outputNode in OutputNodes)
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

        foreach (OutputNodeViewModel outputNode in OutputNodes)
        {
            AssignDepthBranch(outputNode, 0, -1, [outputNode]);
            AssignDepthBranch(outputNode, 0, 1, [outputNode]);
        }

        foreach (IoFlowNodeViewModel node in Nodes)
        {
            if (!depthByNode.ContainsKey(node))
            {
                depthByNode[node] = OutputNodes.Count == 0 ? 0 : 1;
            }
        }

        var sizeByNode = Nodes.ToDictionary(node => node, EstimateNodeSize);

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

        IReadOnlyList<OutputNodeViewModel> outputs = OutputNodes
            .OrderBy(GetOutputLayoutGroupKey)
            .ThenBy(node => node.ChannelNumber)
            .ToList();
        double outputTop = baseY;
        foreach (OutputNodeViewModel outputNode in outputs)
        {
            positions[outputNode] = new Point(xByDepth[0], Snap(outputTop));
            outputTop += sizeByNode[outputNode].Height + outputGap;
        }

        foreach (IoFlowNodeViewModel node in Nodes)
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
            foreach (IoFlowNodeViewModel node in Nodes)
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
        return node switch
        {
            DigitalInputNodeViewModel => new NodeSize(340, 170),
            IgnitionOnNodeViewModel => new NodeSize(340, 150),
            CanInputNodeViewModel => new NodeSize(340, 170),
            MathRuleNodeViewModel mathNode => new NodeSize(340, EstimateMathNodeHeight(mathNode)),
            AnalogueInputNodeViewModel analogueNode => new NodeSize(340, EstimateAnalogueInputNodeHeight(analogueNode)),
            OutputNodeViewModel outputNode => new NodeSize(560, EstimateOutputNodeHeight(outputNode)),
            _ => new NodeSize(340, 220),
        };
    }

    private static double EstimateAnalogueInputNodeHeight(AnalogueInputNodeViewModel node)
    {
        double height = 360;

        if (node.Input.IsCalibrationMode)
        {
            height += node.Input.IsThreePointCalibration ? 210 : 160;
        }

        return height;
    }

    private static double EstimateMathNodeHeight(MathRuleNodeViewModel node)
    {
        double height = 270;
        height += node.ShowOperandAIndex || node.ShowOperandAConstant ? 70 : 0;

        if (node.ShowOperandB)
        {
            height += 70;
            height += node.ShowOperandBIndex || node.ShowOperandBConstant ? 70 : 0;
        }

        return height;
    }

    private static double EstimateOutputNodeHeight(OutputNodeViewModel node)
    {
        double height = 250;

        if (node.ShowConnectInputHint)
        {
            height += 40;
        }

        if (node.IsInputChannelTypeSelectionRequired)
        {
            height += 40;
        }

        if (node.ShowInputChannelTypeOptions)
        {
            height += 110;
        }

        if (node.ShowInputDependentConfiguration)
        {
            height += 150;

            double currentTabHeight = node.ShowCurrentInrushConfiguration ? 280 : 0;
            double analogueTabHeight = node.ShowAnalogueConfiguration ? 260 : 0;
            double timingTabHeight = node.ShowTimingConfiguration ? 420 : 0;

            height += Math.Max(currentTabHeight, Math.Max(analogueTabHeight, timingTabHeight));
        }

        return height;
    }

    public void UpdateLiveChannelSamples(IReadOnlyList<OutputChannel> liveChannels)
    {
        foreach (OutputNodeViewModel node in OutputNodes)
        {
            int index = node.ChannelNumber - 1;
            if (index < 0 || index >= liveChannels.Count)
            {
                continue;
            }

            node.UpdateLiveCurrent(liveChannels[index].CurrentValue);
        }

        foreach (DigitalInputNodeViewModel digitalNode in DigitalNodes)
        {
            DigitalInput? liveInput = _live?.DigitalInputsLiveData.ElementAtOrDefault(digitalNode.InputNumber - 1);
            digitalNode.UpdateLiveState(liveInput);
        }

        foreach (AnalogueInputNodeViewModel analogueNode in AnalogueNodes)
        {
            AnalogueInput? liveInput = _live?.AnalogueInputsLiveData.ElementAtOrDefault(analogueNode.InputNumber - 1);
            analogueNode.UpdateLiveState(liveInput);
        }
    }

    private void SynchronizeCanTxNodesFromSettings(DataStructures settings)
    {
        foreach (CustomCanMessage message in settings.CustomCanMessagesStaticData.Where(IsConfiguredCanTxMessage))
        {
            if (CanTxNodes.Any(node => node.MessageIndex == message.Index))
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

            Point location = FindNearestAvailableSpawnLocation(ClampToSpawnViewport(fallback, 420, 260), 420, 260);
            Nodes.Add(CreateCanTxNode(message, location));
        }
    }

    public void RebuildConnections(DataStructures settings)
    {
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

        foreach (IoFlowNodeViewModel node in Nodes)
        {
            node.InputConnector.IsConnected = false;
            node.OutputConnector.IsConnected = false;
        }

        Connections.Clear();

        foreach (OutputNodeViewModel outputNode in OutputNodes)
        {
            byte pin = outputNode.Channel.InputControlPin;
            if (pin != 0 && inputsByPin.TryGetValue(pin, out IoFlowNodeViewModel? sourceNode))
            {
                AddConnection(sourceNode, outputNode, $"{InputPinCatalog.GetLabelForPin(pin)} → OUT {outputNode.Channel.ChannelNumber}");
            }
            else if ((outputNode.Channel.ChanType == OutputChannel.ChannelType.CAN || outputNode.Channel.ChanType == OutputChannel.ChannelType.CAN_PWM)
                && canNode != null)
            {
                AddConnection(canNode, outputNode, "CAN message → output");
            }

            if (outputNode.Channel.ChanType == OutputChannel.ChannelType.MATH)
            {
                AddMathConnections(outputNode);
            }

            if (_outputRootLogicNodeByChannel.TryGetValue(outputNode.ChannelNumber, out int rootLogicNodeId))
            {
                MathRuleNodeViewModel? rootNode = MathRuleNodes.FirstOrDefault(node => node.LogicNodeId == rootLogicNodeId);
                if (rootNode != null)
                {
                    AddConnection(rootNode, outputNode, "Logic root");
                }
            }
        }

        foreach (CanTxNodeViewModel canTxNode in CanTxNodes)
        {
            CustomCanMessage message = canTxNode.Message;
            if (message.IsEnabled && message.IsTx &&
                message.TxTrigger != Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS)
            {
                OutputNodeViewModel? triggerOutput = OutputNodes.FirstOrDefault(node => node.ChannelNumber == message.TxTrigger + 1);
                if (triggerOutput != null)
                {
                    AddConnection(triggerOutput, canTxNode, $"OUT {triggerOutput.ChannelNumber} → CAN TX 0x{message.Id:X}");
                }
            }
        }

        foreach (MathRuleNodeViewModel ruleNode in MathRuleNodes.Where(node => node.IsStandalone))
        {
            AddStandaloneOperandConnection(ruleNode, ruleNode.Rule.OperandAType, ruleNode.Rule.OperandAIndex, "A");
            AddStandaloneOperandConnection(ruleNode, ruleNode.Rule.OperandBType, ruleNode.Rule.OperandBIndex, "B");
        }
    }

    private void ApplySourceToRuleOperand(MathRuleNodeViewModel targetRule, IoFlowNodeViewModel source)
    {
        bool useA = targetRule.Rule.OperandAType == (byte)OutputChannel.MathOperandType.Constant;

        void Assign(byte operandType, byte operandIndex)
        {
            if (useA)
            {
                targetRule.Rule.OperandAType = operandType;
                targetRule.Rule.OperandAIndex = operandIndex;
            }
            else
            {
                targetRule.Rule.OperandBType = operandType;
                targetRule.Rule.OperandBIndex = operandIndex;
            }
        }

        switch (source)
        {
            case DigitalInputNodeViewModel digitalNode:
                Assign((byte)OutputChannel.MathOperandType.DigitalInput, (byte)(digitalNode.InputNumber - 1));
                break;
            case AnalogueInputNodeViewModel analogueNode:
                Assign((byte)OutputChannel.MathOperandType.AnalogueInput, (byte)(analogueNode.InputNumber - 1));
                break;
            case OutputNodeViewModel outputNode:
                Assign((byte)OutputChannel.MathOperandType.Channel, (byte)(outputNode.ChannelNumber - 1));
                break;
            case MathRuleNodeViewModel sourceRule:
                byte ruleIndex = (byte)(targetRule.IsStandalone || sourceRule.IsStandalone
                    ? sourceRule.LogicNodeId
                    : sourceRule.RuleIndex);
                Assign((byte)OutputChannel.MathOperandType.Rule, ruleIndex);
                break;
            case CanInputNodeViewModel:
                Assign((byte)OutputChannel.MathOperandType.CanSignal, GetDefaultCanSignalIndex());
                break;
        }
    }

    private byte GetDefaultCanSignalIndex()
    {
        if (_settings == null)
        {
            return 0;
        }

        CustomCanSignal? firstEnabledSignal = _settings.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled)
            .OrderBy(signal => signal.Index)
            .FirstOrDefault();

        if (firstEnabledSignal == null)
        {
            return 0;
        }

        return (byte)Math.Clamp(firstEnabledSignal.Index, 0, byte.MaxValue);
    }

    private void AddMathConnections(OutputNodeViewModel outputNode)
    {
        var rules = MathRuleNodes
            .Where(rule => rule.OwnerChannelNumber == outputNode.ChannelNumber)
            .OrderBy(rule => rule.RuleIndex)
            .ToList();

        foreach (MathRuleNodeViewModel ruleNode in rules)
        {
            if (ruleNode.RuleIndex == outputNode.Channel.MathRootRule)
            {
                AddConnection(ruleNode, outputNode, "Root rule");
            }

            AddOperandConnectionIfRule(ruleNode, ruleNode.Rule.OperandAType, ruleNode.Rule.OperandAIndex, "A", rules);
            AddOperandConnectionIfRule(ruleNode, ruleNode.Rule.OperandBType, ruleNode.Rule.OperandBIndex, "B", rules);
        }
    }

    private void AddOperandConnectionIfRule(MathRuleNodeViewModel targetRule, byte operandType, byte operandIndex, string operandLabel, List<MathRuleNodeViewModel> rules)
    {
        if (operandType != (byte)OutputChannel.MathOperandType.Rule)
        {
            return;
        }

        MathRuleNodeViewModel? sourceRule = rules.FirstOrDefault(rule => rule.RuleIndex == operandIndex);
        if (sourceRule == null)
        {
            return;
        }

        AddConnection(sourceRule, targetRule, $"Rule {operandLabel}");
    }

    private void AddStandaloneOperandConnection(MathRuleNodeViewModel targetRule, byte operandType, byte operandIndex, string operandLabel)
    {
        IoFlowNodeViewModel? sourceNode = operandType switch
        {
            (byte)OutputChannel.MathOperandType.Rule => MathRuleNodes.FirstOrDefault(node => node.LogicNodeId == operandIndex),
            (byte)OutputChannel.MathOperandType.DigitalInput => DigitalNodes.FirstOrDefault(node => node.InputNumber == operandIndex + 1),
            (byte)OutputChannel.MathOperandType.AnalogueInput => AnalogueNodes.FirstOrDefault(node => node.InputNumber == operandIndex + 1),
            (byte)OutputChannel.MathOperandType.Channel => OutputNodes.FirstOrDefault(node => node.ChannelNumber == operandIndex + 1),
            (byte)OutputChannel.MathOperandType.CanSignal => CanNodes.FirstOrDefault(),
            _ => null,
        };

        if (sourceNode != null)
        {
            AddConnection(sourceNode, targetRule, $"Logic {operandLabel}");
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
            .Select((input, index) => new MathOperandIndexOption((byte)index, $"Digital {input.InputNumber}"))
            .ToList();
    }

    private IReadOnlyList<MathOperandIndexOption> BuildAnalogueInputOperandOptions()
    {
        if (_settings == null)
        {
            return [];
        }

        return _settings.AnalogueInputsStaticData
            .Select((input, index) => new MathOperandIndexOption((byte)index, $"Ana/Dig {input.InputNumber}"))
            .ToList();
    }

    private IReadOnlyList<MathOperandIndexOption> BuildCanSignalOperandOptions()
    {
        if (_settings == null)
        {
            return [];
        }

        return _settings.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled)
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

        if (existingNode != null)
        {
            existingNode.AssignChannel(previousChannel);
        }

        node.AssignChannel(selectedChannel);

        RebuildConnections(_settings);
        NotifyGraphChanged(controllerSettingsChanged: false);
        return true;
    }

    private void AddConnection(IoFlowNodeViewModel source, IoFlowNodeViewModel target, string label)
    {
        source.OutputConnector.IsConnected = true;
        target.InputConnector.IsConnected = true;
        Connections.Add(CreateConnection(source, target, label));
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

    private void AddOutputNodeWithOptionalMath(OutputChannel channel, bool useSpawnLocation = false)
    {
        if (Nodes.OfType<OutputNodeViewModel>().All(node => node.ChannelNumber != channel.ChannelNumber))
        {
            Point outputLocation = useSpawnLocation
                ? ConsumeNextNodeSpawnLocation(new Point(830, 50), nodeWidth: 560, nodeHeight: 340)
                : new Point(830, 50);
            Nodes.Add(new OutputNodeViewModel(channel, outputLocation, _settings?.AnalogueInputsStaticData ?? [], _settings?.ChannelsStaticData ?? [], TrySelectOutputChannel));
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

            Nodes.Add(new MathRuleNodeViewModel(channel, ruleIndex, channel.MathRules[ruleIndex], new Point(1240, 50), GetOperandIndexOptions));
        }
    }
}
