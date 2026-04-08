using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cortex.ViewModels;
using System.Reflection;

namespace Cortex.Views
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            AvaloniaXamlLoader.Load(this);
            DataContext = new AboutWindowViewModel(GetVersionText(), Close);
        }

        private static string GetVersionText()
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutWindow).Assembly;
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var cleanedInformationalVersion = informationalVersion?.Split('+')[0];
            var version = string.IsNullOrWhiteSpace(cleanedInformationalVersion)
                ? assembly.GetName().Version?.ToString() ?? "unknown"
                : cleanedInformationalVersion;

            return $"Version {version}";
        }
    }
}
