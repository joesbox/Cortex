namespace Cortex.Views
{
    using Avalonia.Controls;
    using Avalonia.Platform.Storage;
    using Cortex.Models;
    using Cortex.Services;
    using Cortex.ViewModels;
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;

    public partial class MainWindow : Window, IAppCloser
    {
        private bool _allowCloseWithoutUnsavedPrompt;

        public MainWindow()
        {
            InitializeComponent();

            var vm = new MainWindowViewModel(this);
            DataContext = vm;

            _ = vm.RestoreProvisioningSessionAsync();

            Closing += async (_, args) =>
            {
                if (DataContext is not MainWindowViewModel mainViewModel)
                {
                    return;
                }

                if (_allowCloseWithoutUnsavedPrompt)
                {
                    mainViewModel.OnWindowClosing();
                    return;
                }

                if (!mainViewModel.HasPendingConfigChanges)
                {
                    mainViewModel.OnWindowClosing();
                    return;
                }

                args.Cancel = true;

                bool exitConfirmed = await ConfirmAsync(
                    "Unsaved Changes",
                    "Configuration changes have not been written to the controller. Exit anyway?");

                if (!exitConfirmed)
                {
                    return;
                }

                _allowCloseWithoutUnsavedPrompt = true;
                mainViewModel.OnWindowClosing();
                Close();
            };

            vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(vm.IsConnected))
                    {
                        UpdateConnectionState(vm.IsConnected);
                    }

                    if (args.PropertyName == nameof(vm.SdOK))
                    {
                        UpdateSDStatus(vm.SdOK);
                    }

                    if (args.PropertyName == nameof(vm.OverCurrent))
                    {
                        UpdateCurrentStatus(vm.OverCurrent);
                    }

                    if (args.PropertyName == nameof(vm.OverTemperature))
                    {
                        UpdateTempStatus(vm.OverTemperature);
                    }

                    if (args.PropertyName == nameof(vm.UnderVoltage))
                    {
                        UpdateVoltStatus(vm.UnderVoltage);
                    }

                    if (args.PropertyName == nameof(vm.GpsOK))
                    {
                        UpdateGPSStatus(vm.GpsOK);
                    }
                };

            UpdateConnectionState(vm.IsConnected);
            UpdateSDStatus(vm.SdOK);
            UpdateCurrentStatus(vm.OverCurrent);
            UpdateTempStatus(vm.OverTemperature);
            UpdateVoltStatus(vm.UnderVoltage);
            UpdateGPSStatus(vm.GpsOK);
        }

        public void CloseApp()
        {
            Close();
        }

        public async Task<string?> OpenPdmFileContentAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Controller Configuration",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("PDM Configuration")
                    {
                        Patterns = ["*.pdm"],
                    },
                    FilePickerFileTypes.All,
                ],
            });

            var selectedFile = files.FirstOrDefault();
            if (selectedFile == null)
            {
                return null;
            }

            await using var readStream = await selectedFile.OpenReadAsync();
            using var reader = new StreamReader(readStream);
            return await reader.ReadToEndAsync();
        }

        public async Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory)
        {
            var options = new FilePickerOpenOptions
            {
                Title = "Open Synapse PDM Log",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Synapse PDM Log")
                    {
                        Patterns = ["*.csv"],
                    },
                    FilePickerFileTypes.All,
                ],
            };

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(initialDirectory);
            }

            var selectedFile = (await StorageProvider.OpenFilePickerAsync(options)).FirstOrDefault();
            return selectedFile?.TryGetLocalPath();
        }

        public async Task<bool> SavePdmFileContentAsync(string content)
        {
            var storageFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Controller Configuration",
                SuggestedFileName = $"SynapsePDM_Config-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultExtension = "pdm",
                FileTypeChoices =
                [
                    new FilePickerFileType("PDM Configuration")
                    {
                        Patterns = ["*.pdm"],
                    },
                ],
            });

            if (storageFile == null)
            {
                return false;
            }

            await using var writeStream = await storageFile.OpenWriteAsync();
            writeStream.SetLength(0);

            await using var writer = new StreamWriter(writeStream);
            await writer.WriteAsync(content);
            await writer.FlushAsync();
            return true;
        }

        public async Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL")
        {
            var dialog = new Window
            {
                Title = title,
                Width = 430,
                Height = 190,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Grid
                {
                    Margin = new Avalonia.Thickness(16),
                    RowDefinitions = new RowDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 10,
                            Children =
                            {
                                new Button
                                {
                                    Name = "CancelButton",
                                    Content = cancelButtonText,
                                    MinWidth = 90,
                                },
                                new Button
                                {
                                    Name = "ConfirmButton",
                                    Content = confirmButtonText,
                                    MinWidth = 90,
                                },
                            },
                            [Grid.RowProperty] = 1,
                        },
                    },
                },
            };

            if (dialog.Content is Grid grid && grid.Children.Count > 1 && grid.Children[1] is StackPanel buttonPanel)
            {
                var cancelButton = buttonPanel.Children.OfType<Button>().FirstOrDefault(b => b.Name == "CancelButton");
                var confirmButton = buttonPanel.Children.OfType<Button>().FirstOrDefault(b => b.Name == "ConfirmButton");

                cancelButton!.Click += (_, _) => dialog.Close(false);
                confirmButton!.Click += (_, _) => dialog.Close(true);
            }

            var result = await dialog.ShowDialog<bool>(this);
            return result;
        }

        public Task OpenUrlAsync(string url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });

            return Task.CompletedTask;
        }

        public async Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync()
        {
            var file = (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select firmware update package",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Firmware Update Package")
                    {
                        Patterns = ["*.zip"],
                    },
                    FilePickerFileTypes.All,
                ],
            })).FirstOrDefault();

            string? packagePath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                return null;
            }

            return new LocalFirmwareUpdateSelection
            {
                PackagePath = packagePath,
                DisplayName = Path.GetFileName(packagePath),
            };
        }

        public async Task ShowAboutAsync()
        {
            var aboutWindow = new AboutWindow();
            await aboutWindow.ShowDialog(this);
        }

        public async Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel)
        {
            var firmwareUpdateWindow = new FirmwareUpdateWindow
            {
                DataContext = viewModel,
            };

            void CloseRequested()
            {
                firmwareUpdateWindow.Close();
            }

            viewModel.CloseRequested += CloseRequested;
            try
            {
                await firmwareUpdateWindow.ShowDialog(this);
            }
            finally
            {
                viewModel.CloseRequested -= CloseRequested;
            }
        }

        private void UpdateConnectionState(bool isConnected)
        {
            if (!isConnected)
            {
                MainTabs.SelectedIndex = 0;
            }

            StatusRect.Classes.Set("connected", isConnected);
            StatusRect.Classes.Set("disconnected", !isConnected);

            StatusIcon.Classes.Set("connected", isConnected);
            StatusIcon.Classes.Set("disconnected", !isConnected);
        }

        private void UpdateSDStatus(bool sdOK)
        {
            SDIcon.Classes.Set("sdOK", sdOK);
            SDIcon.Classes.Set("sdError", !sdOK);

            SDRect.Classes.Set("sdOK", sdOK);
            SDRect.Classes.Set("sdError", !sdOK);
        }

        private void UpdateCurrentStatus(bool currentOK)
        {
            currentIcon.Classes.Set("currentOK", currentOK);
            currentIcon.Classes.Set("overCurrrent", !currentOK);

            currentRect.Classes.Set("currentOK", currentOK);
            currentRect.Classes.Set("overCurrrent", !currentOK);
        }

        private void UpdateTempStatus(bool tempOK)
        {
            tempIcon.Classes.Set("tempOK", tempOK);
            tempIcon.Classes.Set("overTemp", !tempOK);

            tempRect.Classes.Set("tempOK", tempOK);
            tempRect.Classes.Set("overTemp", !tempOK);
        }

        private void UpdateVoltStatus(bool undervoltage)
        {
            voltIcon.Classes.Set("voltsOK", !undervoltage);
            voltIcon.Classes.Set("underVolts", undervoltage);

            voltRect.Classes.Set("voltsOK", !undervoltage);
            voltRect.Classes.Set("underVolts", undervoltage);
        }

        private void UpdateGPSStatus(bool gpsOk)
        {
            GPSRect.Classes.Set("gpsOK", gpsOk);
            GPSRect.Classes.Set("gpsError", !gpsOk);

            GPSIcon.Classes.Set("gpsOK", gpsOk);
            GPSIcon.Classes.Set("gpsError", !gpsOk);
        }
    }
}