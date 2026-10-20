using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Cortex.Models
{
    public enum ConnectorPinRole
    {
        Output,
        DigitalInput,
        AnalogueInput,
        Ignition,
        CanLow,
        CanHigh,
        Reference5V,
        Ground,
    }

    public sealed class ConnectorPinDefinition
    {
        public int PinNumber { get; }

        public string PhysicalFunction { get; }

        public ConnectorPinRole Role { get; }

        public int? OutputChannelNumber { get; }

        public int? DigitalInputNumber { get; }

        public int? AnalogueInputNumber { get; }

        public double X { get; }

        public double Y { get; }

        public ConnectorPinDefinition(
            int pinNumber,
            string physicalFunction,
            ConnectorPinRole role,
            double x,
            double y,
            int? outputChannelNumber = null,
            int? digitalInputNumber = null,
            int? analogueInputNumber = null)
        {
            PinNumber = pinNumber;
            PhysicalFunction = physicalFunction;
            Role = role;
            OutputChannelNumber = outputChannelNumber;
            DigitalInputNumber = digitalInputNumber;
            AnalogueInputNumber = analogueInputNumber;
            X = x;
            Y = y;
        }
    }

    public static class ConnectorPinCatalog
    {
        public const double ViewWidth = 1024;
        public const double ViewHeight = 768;
        public const double PinRadius = 15;

        public static IReadOnlyList<ConnectorPinDefinition> Pins { get; } =
            new ReadOnlyCollection<ConnectorPinDefinition>(
            [
                new ConnectorPinDefinition(1, "Output 3", ConnectorPinRole.Output, 140.177, 333.297, outputChannelNumber: 3),
                new ConnectorPinDefinition(2, "Output 4", ConnectorPinRole.Output, 207.781, 333.297, outputChannelNumber: 4),
                new ConnectorPinDefinition(3, "Output 5", ConnectorPinRole.Output, 275.385, 333.297, outputChannelNumber: 5),
                new ConnectorPinDefinition(4, "Output 6", ConnectorPinRole.Output, 342.990, 333.297, outputChannelNumber: 6),
                new ConnectorPinDefinition(5, "Output 7", ConnectorPinRole.Output, 410.594, 333.297, outputChannelNumber: 7),
                new ConnectorPinDefinition(6, "CAN L", ConnectorPinRole.CanLow, 478.198, 333.297),
                new ConnectorPinDefinition(7, "CAN H", ConnectorPinRole.CanHigh, 545.802, 333.297),
                new ConnectorPinDefinition(8, "Output 8", ConnectorPinRole.Output, 613.406, 333.297, outputChannelNumber: 8),
                new ConnectorPinDefinition(9, "Output 9", ConnectorPinRole.Output, 681.010, 333.297, outputChannelNumber: 9),
                new ConnectorPinDefinition(10, "Output 10", ConnectorPinRole.Output, 748.615, 333.297, outputChannelNumber: 10),
                new ConnectorPinDefinition(11, "Output 11", ConnectorPinRole.Output, 816.219, 333.297, outputChannelNumber: 11),
                new ConnectorPinDefinition(12, "Output 12", ConnectorPinRole.Output, 883.823, 333.297, outputChannelNumber: 12),
                new ConnectorPinDefinition(13, "Output 2", ConnectorPinRole.Output, 173.979, 400.901, outputChannelNumber: 2),
                new ConnectorPinDefinition(14, "Digital in 1", ConnectorPinRole.DigitalInput, 241.583, 400.901, digitalInputNumber: 1),
                new ConnectorPinDefinition(15, "Digital in 2", ConnectorPinRole.DigitalInput, 309.188, 400.901, digitalInputNumber: 2),
                new ConnectorPinDefinition(16, "Digital in 3", ConnectorPinRole.DigitalInput, 376.792, 400.901, digitalInputNumber: 3),
                new ConnectorPinDefinition(17, "Digital in 4", ConnectorPinRole.DigitalInput, 444.396, 400.901, digitalInputNumber: 4),
                new ConnectorPinDefinition(18, "Digital in 5", ConnectorPinRole.DigitalInput, 512.000, 400.901, digitalInputNumber: 5),
                new ConnectorPinDefinition(19, "Digital in 6", ConnectorPinRole.DigitalInput, 579.604, 400.901, digitalInputNumber: 6),
                new ConnectorPinDefinition(20, "Digital in 7", ConnectorPinRole.DigitalInput, 647.208, 400.901, digitalInputNumber: 7),
                new ConnectorPinDefinition(21, "Digital in 8", ConnectorPinRole.DigitalInput, 714.812, 400.901, digitalInputNumber: 8),
                new ConnectorPinDefinition(22, "Ignition", ConnectorPinRole.Ignition, 782.417, 400.901),
                new ConnectorPinDefinition(23, "Output 13", ConnectorPinRole.Output, 850.021, 400.901, outputChannelNumber: 13),
                new ConnectorPinDefinition(24, "Output 1", ConnectorPinRole.Output, 140.177, 468.505, outputChannelNumber: 1),
                new ConnectorPinDefinition(25, "Analogue in 1", ConnectorPinRole.AnalogueInput, 207.781, 468.505, analogueInputNumber: 1),
                new ConnectorPinDefinition(26, "Analogue in 2", ConnectorPinRole.AnalogueInput, 275.385, 468.505, analogueInputNumber: 2),
                new ConnectorPinDefinition(27, "Analogue in 3", ConnectorPinRole.AnalogueInput, 342.990, 468.505, analogueInputNumber: 3),
                new ConnectorPinDefinition(28, "Analogue in 4", ConnectorPinRole.AnalogueInput, 410.594, 468.505, analogueInputNumber: 4),
                new ConnectorPinDefinition(29, "Analogue in 5", ConnectorPinRole.AnalogueInput, 478.198, 468.505, analogueInputNumber: 5),
                new ConnectorPinDefinition(30, "Analogue in 6", ConnectorPinRole.AnalogueInput, 545.802, 468.505, analogueInputNumber: 6),
                new ConnectorPinDefinition(31, "Analogue in 7", ConnectorPinRole.AnalogueInput, 613.406, 468.505, analogueInputNumber: 7),
                new ConnectorPinDefinition(32, "Analogue in 8", ConnectorPinRole.AnalogueInput, 681.010, 468.505, analogueInputNumber: 8),
                new ConnectorPinDefinition(33, "5V Reference", ConnectorPinRole.Reference5V, 748.615, 468.505),
                new ConnectorPinDefinition(34, "Ground", ConnectorPinRole.Ground, 816.219, 468.505),
                new ConnectorPinDefinition(35, "Output 14", ConnectorPinRole.Output, 883.823, 468.505, outputChannelNumber: 14),
            ]);
    }
}
