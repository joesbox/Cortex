using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cortex.Views;
using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading.Tasks;

namespace Cortex;

public partial class App : Application
{
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();

                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var splash = new SplashWindow();
                splash.SetProgress(2, "Starting...");
                splash.Show();

                _ = StartMainWindowAsync(desktop, splash);
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            /*var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }*/
        }

        private static async Task StartMainWindowAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
        {
            try
            {
                splash.SetProgress(10, "Loading platform services...");
                await Task.Delay(80);

                splash.SetProgress(28, "Scanning serial interfaces...");
                await Task.Run(() =>
                {
                    _ = SerialPort.GetPortNames();
                });

                splash.SetProgress(52, "Preparing UI resources...");
                await Task.Delay(80);

                splash.SetProgress(72, "Creating main window...");
                MainWindow? mainWindow = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    mainWindow = new MainWindow();
                });

                splash.SetProgress(92, "Finalizing startup...");
                await Task.Delay(120);

                splash.SetProgress(100, "Ready");
                await Task.Delay(2000);

                await ShowMainWindowAsync(desktop, splash, mainWindow);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Startup failed: {ex}");
                Console.Error.WriteLine($"Startup failed: {ex}");

                MainWindow? fallbackWindow = null;
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        fallbackWindow = new MainWindow();
                    });

                    await ShowMainWindowAsync(desktop, splash, fallbackWindow);
                }
                catch (Exception fallbackEx)
                {
                    Trace.WriteLine($"Fallback startup failed: {fallbackEx}");
                    Console.Error.WriteLine($"Fallback startup failed: {fallbackEx}");

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (splash.IsVisible)
                        {
                            splash.Close();
                        }

                        desktop.Shutdown();
                    });
                }
            }
        }

        private static async Task ShowMainWindowAsync(
            IClassicDesktopStyleApplicationLifetime desktop,
            SplashWindow splash,
            MainWindow? mainWindow)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (mainWindow == null)
                {
                    return;
                }

                desktop.MainWindow = mainWindow;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

                mainWindow.Show();
                mainWindow.Activate();

                if (splash.IsVisible)
                {
                    splash.Close();
                }
            });
        }
    }
