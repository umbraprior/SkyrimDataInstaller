using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using SkyrimDataInstaller.Models;
using SkyrimDataInstaller.Services;

namespace SkyrimDataInstaller;

public partial class MainWindow : Window
{
    private readonly IArchiveService _archiveService;
    private string _metadataFilePath = string.Empty; // Store metadata file path

    public MainWindow()
    {
        InitializeComponent();
        _archiveService = new ArchiveService();

        // Clean up any leftover temp files from previous sessions
        var metadataService = new MetadataService();
        metadataService.CleanupAllTempFiles();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select Parent Archive File",
            Filter = "Archive Files|*.zip;*.rar;*.7z;*.tar;*.gz;*.bz2|All Files|*.*",
            FilterIndex = 1
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ArchivePathTextBox.Text = dialog.FileName;
            ScanButton.IsEnabled = true;
            StatusTextBlock.Text = "Archive selected. Ready to scan.";
        }
    }

    private void ArchivePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Check if controls are initialized
        if (ArchivePathTextBox == null || ScanButton == null || StatusTextBlock == null)
            return;

        var text = ArchivePathTextBox.Text;

        // Check if text is not empty and is a valid file path
        if (!string.IsNullOrEmpty(text) && File.Exists(text))
        {
            ScanButton.IsEnabled = true;
            StatusTextBlock.Text = "Archive selected. Ready to scan.";
        }
        else
        {
            ScanButton.IsEnabled = false;
            StatusTextBlock.Text = "Ready to scan archives";
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ArchivePathTextBox.Text))
        {
            System.Windows.MessageBox.Show("Please select an archive file first.", "No Archive Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ScanButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        ResultsTextBox.Clear();
        StatusTextBlock.Text = "Scanning archives...";

        try
        {
            var progress = new Progress<string>(UpdateStatus);
            _metadataFilePath = await _archiveService.ScanArchiveAsync(ArchivePathTextBox.Text, progress);
            DisplayResults(_metadataFilePath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error during scanning: {ex.Message}", "Scan Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "Error occurred during scanning.";
        }
        finally
        {
            ScanButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
        }
    }

    private void UpdateStatus(string message)
    {
        StatusTextBlock.Text = message;

        // Append all messages to ResultsTextBox for real-time reporting
        ResultsTextBox.AppendText(message + Environment.NewLine);

        // Force scroll to bottom and update UI
        ResultsScrollViewer.ScrollToBottom();
        ResultsScrollViewer.UpdateLayout();
    }

    private async void DisplayResults(string metadataFilePath)
    {
        ResultsTextBox.AppendText(Environment.NewLine + "=== SCAN COMPLETE ===" + Environment.NewLine);

        try
        {
            var metadataService = new MetadataService();
            var metadata = await metadataService.LoadMetadataAsync(metadataFilePath);

            if (!metadata.TargetFiles.Any())
            {
                ResultsTextBox.AppendText("No target files found in any archives." + Environment.NewLine);
                StatusTextBlock.Text = "Scan complete. No target files found.";
                NextButton.IsEnabled = false;
                return;
            }

            var output = new System.Text.StringBuilder();
            output.AppendLine($"\nFINAL SUMMARY");
            output.AppendLine(new string('=', 60));

            // Overall summary by file type
            foreach (var extGroup in metadata.Summary.FilesByExtension.OrderBy(kvp => kvp.Key))
            {
                var size = metadata.Summary.SizeByExtension[extGroup.Key];
                output.AppendLine($"{extGroup.Key.ToUpper()}: {extGroup.Value} files ({FormatFileSize(size)})");
            }

            output.AppendLine(new string('-', 60));
            output.AppendLine(
                $"TOTAL: {metadata.Summary.TotalFiles} files ({FormatFileSize(metadata.Summary.TotalSize)})");
            output.AppendLine($"ARCHIVES SCANNED: {metadata.Summary.ArchivesScanned}");

            ResultsTextBox.AppendText(output.ToString());
            StatusTextBlock.Text =
                $"Scan complete. Found {metadata.Summary.TotalFiles} target files across {metadata.Summary.ArchivesScanned} archives.";

            // Enable Next button after successful scan
            NextButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ResultsTextBox.AppendText($"Error loading scan results: {ex.Message}" + Environment.NewLine);
            StatusTextBlock.Text = "Error loading scan results.";
            NextButton.IsEnabled = false;
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        // Check if metadata file still exists
        if (!File.Exists(_metadataFilePath))
        {
            var scanAgainResult = System.Windows.MessageBox.Show(
                "The scan data is no longer available. Would you like to scan the archive again?",
                "Scan Data Missing", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (scanAgainResult == MessageBoxResult.Yes)
            {
                // Trigger a new scan
                ScanButton_Click(sender, e);
                return;
            }
            else
            {
                // Reset UI for manual scan
                NextButton.IsEnabled = false;
                StatusTextBlock.Text = "Scan data expired. Please scan again.";
                return;
            }
        }

        var installDialog = new InstallLocationDialog(_metadataFilePath);
        installDialog.Owner = this;

        var dialogResult = installDialog.ShowDialog();
        if (dialogResult == true)
            // Installation was completed successfully
            StatusTextBlock.Text = "Installation completed successfully.";
        else if (dialogResult == false)
            // Installation was cancelled or failed - dialog closed without success
            StatusTextBlock.Text = "Installation cancelled or failed. You can try again by clicking Next.";
        // If dialogResult is null, dialog was closed via X button - no status change needed
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