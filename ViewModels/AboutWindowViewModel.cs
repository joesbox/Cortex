using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;

namespace Cortex.ViewModels
{
    public partial class AboutWindowViewModel : ObservableObject
    {
        private const string WikiUrl = "https://wiki.joeblogs.uk";
        private readonly Action closeAction;

        [ObservableProperty]
        private string versionText;

        public AboutWindowViewModel(string versionText, Action closeAction)
        {
            this.versionText = versionText;
            this.closeAction = closeAction;
        }

        [RelayCommand]
        private void Close()
        {
            closeAction();
        }

        [RelayCommand]
        private void OpenWiki()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = WikiUrl,
                    UseShellExecute = true,
                });
            }
            catch
            {
            }
        }
    }
}
