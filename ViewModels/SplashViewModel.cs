using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Reflection;

namespace Cortex.ViewModels
{
    public partial class SplashViewModel : ObservableObject
    {
        [ObservableProperty]
        private double progressValue;

        [ObservableProperty]
        private string progressStatus = "Starting...";

        public string VersionText { get; } = BuildVersionText();

        private static string BuildVersionText()
        {
            string? informationalVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                string cleanVersion = informationalVersion.Split('+')[0].Trim();
                if (!string.IsNullOrWhiteSpace(cleanVersion))
                {
                    return cleanVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? cleanVersion
                        : $"v{cleanVersion}";
                }
            }

            Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion != null)
            {
                return $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            }

            return "v0.0.0";
        }
    }
}
