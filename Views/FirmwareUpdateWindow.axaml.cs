using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cortex.Views
{
    public partial class FirmwareUpdateWindow : Window
    {
        public FirmwareUpdateWindow()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}