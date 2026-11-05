using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Cortex.Controls;
using Cortex.Models;
using System.Collections.ObjectModel;
using Xunit;

namespace Cortex.Ui.Tests;

public class CanFrameBitMapControlTests
{
    // 400x400 control: 44px cells, grid origin (46, 20); column 0 is bit 7 of each byte.
    private static Point CellCenter(int bit) =>
        new(46 + ((7 - (bit % 8)) * 44) + 22, 20 + ((bit / 8) * 44) + 22);

    [AvaloniaFact]
    public void ClickingABit_SelectsTheSignalThatOwnsIt()
    {
        var status = new CustomCanSignal { Name = "STATUS", StartBit = 36, BitLength = 3, Flags = 0x01 };
        var sleep = new CustomCanSignal { Name = "SLEEP", StartBit = 39, BitLength = 1, Flags = 0x01 };
        var map = new CanFrameBitMapControl { Signals = new ObservableCollection<CustomCanSignal> { status, sleep } };
        var window = new Window { Width = 400, Height = 400, Content = map };

        try
        {
            window.Show();
            window.UpdateLayout();

            window.MouseDown(CellCenter(39), MouseButton.Left);
            window.MouseUp(CellCenter(39), MouseButton.Left);
            Assert.Same(sleep, map.SelectedSignal);

            window.MouseDown(CellCenter(37), MouseButton.Left);
            window.MouseUp(CellCenter(37), MouseButton.Left);
            Assert.Same(status, map.SelectedSignal);

            window.MouseDown(CellCenter(0), MouseButton.Left);
            window.MouseUp(CellCenter(0), MouseButton.Left);
            Assert.Same(status, map.SelectedSignal);
        }
        finally
        {
            window.Close();
        }
    }
}
