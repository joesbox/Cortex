using Cortex.Controls;
using Xunit;

namespace Cortex.Tests;

public class CanFrameBitMapControlTests
{
    [Fact]
    public void GetOccupiedBits_Intel_WalksUpFromLsb()
    {
        Assert.Equal(new[] { 36, 37, 38 }, CanFrameBitMapControl.GetOccupiedBits(36, 3, isMotorola: false));
    }

    [Fact]
    public void GetOccupiedBits_Motorola_UsesSawtoothFromMsb()
    {
        Assert.Equal(
            new[] { 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8 },
            CanFrameBitMapControl.GetOccupiedBits(7, 16, isMotorola: true));
        Assert.Equal(new[] { 0, 15 }, CanFrameBitMapControl.GetOccupiedBits(0, 2, isMotorola: true));
    }

    [Fact]
    public void GetOccupiedBits_StopsAtFrameEnd()
    {
        Assert.Equal(new[] { 62, 63 }, CanFrameBitMapControl.GetOccupiedBits(62, 8, isMotorola: false));
    }
}
