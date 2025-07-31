using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SkyrimDataInstaller.Services;

namespace SkyrimDataInstaller;

public partial class ConflictResolutionDialog : Window
{
    public List<string> SelectedFiles { get; private set; } = new();
    public bool WasCancelled { get; private set; } = true;

    private readonly List<ConflictDisplayItem> _conflicts;

    public ConflictResolutionDialog(List<FileConflict> conflicts)
    {
        InitializeComponent();
        _conflicts = conflicts.Select(c => new ConflictDisplayItem
        {
            FileName = c.NewFile.FileName,
            ExistingPath = c.ExistingPath,
            ExistingSize = FormatFileSize(c.ExistingSize),
            ExistingModified = File.GetLastWriteTime(c.ExistingPath).ToString("yyyy-MM-dd HH:mm:ss"),
            NewSize = FormatFileSize(c.NewFile.Size),
            IsSelected = false
        }).ToList();

        ConflictsItemsControl.ItemsSource = _conflicts;
    }

    private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        foreach (var conflict in _conflicts) conflict.IsSelected = true;
        UpdateCheckboxes();
    }

    private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        foreach (var conflict in _conflicts) conflict.IsSelected = false;
        UpdateCheckboxes();
    }

    private void FileCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.DataContext is ConflictDisplayItem item)
        {
            item.IsSelected = true;
            UpdateSelectAllState();
        }
    }

    private void FileCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.DataContext is ConflictDisplayItem item)
        {
            item.IsSelected = false;
            UpdateSelectAllState();
        }
    }

    private void UpdateSelectAllState()
    {
        var allSelected = _conflicts.All(c => c.IsSelected);
        var noneSelected = _conflicts.All(c => !c.IsSelected);

        SelectAllCheckBox.IsChecked = allSelected ? true : noneSelected ? false : null;
    }

    private void UpdateCheckboxes()
    {
        // Force refresh of the ItemsControl to update checkbox states
        ConflictsItemsControl.ItemsSource = null;
        ConflictsItemsControl.ItemsSource = _conflicts;
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedFiles = _conflicts.Where(c => c.IsSelected).Select(c => c.ExistingPath).ToList();
        WasCancelled = false;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        WasCancelled = true;
        DialogResult = false;
        Close();
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}

public class ConflictDisplayItem : INotifyPropertyChanged
{
    public string FileName { get; set; } = string.Empty;
    public string ExistingPath { get; set; } = string.Empty;
    public string ExistingSize { get; set; } = string.Empty;
    public string ExistingModified { get; set; } = string.Empty;
    public string NewSize { get; set; } = string.Empty;

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}