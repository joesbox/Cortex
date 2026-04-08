using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cortex.ViewModels;

namespace Cortex.Views
{
    public partial class SplashWindow : Window
    {
        private readonly SplashViewModel splashState = new();

        public SplashWindow()
        {
            AvaloniaXamlLoader.Load(this);
            DataContext = splashState;
        }

        public void SetProgress(double value, string status)
        {
            splashState.ProgressValue = value;
            splashState.ProgressStatus = status;
        }
    }
}
