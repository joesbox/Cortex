using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.Views;

public partial class ConfigurationBackupDialogWindow : Window
{
    private readonly ListBox _backupList;
    private readonly Button _restoreButton;

    public ConfigurationBackupDialogWindow()
        : this(Array.Empty<ConfigurationBackupEntry>())
    {
    }

    public ConfigurationBackupDialogWindow(IReadOnlyList<ConfigurationBackupEntry> backups)
    {
        InitializeComponent();
        Backups = backups;
        DataContext = this;

        _backupList = this.FindControl<ListBox>("BackupList")
            ?? throw new InvalidOperationException("The configuration backup list was not found.");
        _restoreButton = this.FindControl<Button>("RestoreButton")
            ?? throw new InvalidOperationException("The configuration backup restore button was not found.");

        _backupList.SelectedItem = backups.FirstOrDefault();
        UpdateRestoreButton();
    }

    public IReadOnlyList<ConfigurationBackupEntry> Backups { get; }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnBackupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateRestoreButton();
    }

    private void OnRestoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_backupList.SelectedItem is ConfigurationBackupEntry backup)
        {
            Close(backup);
        }
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(null);
    }

    private void UpdateRestoreButton()
    {
        _restoreButton.IsEnabled = _backupList.SelectedItem is ConfigurationBackupEntry;
    }
}
