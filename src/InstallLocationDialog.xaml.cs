using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms; // For FolderBrowserDialog
using SkyrimDataInstaller.Models;
using SkyrimDataInstaller.Services;

namespace SkyrimDataInstaller;

public partial class InstallLocationDialog : Window
{
    public string SelectedInstallPath { get; private set; } = string.Empty;
    private readonly string _metadataFilePath;
    private readonly IInstallationService _installationService;
    private CancellationTokenSource? _cancellationTokenSource;

    // Timer and progress tracking
    private System.Windows.Threading.DispatcherTimer? _timer;
    private DateTime _installStartTime;
    private int _totalFiles;
    private int _processedFiles;
    private long _totalBytes;
    private long _processedBytes;
    private readonly List<double> _throughputSamples = new(); // MB per second
    private double _estimatedTotalSeconds;
    private DateTime _lastProgressUpdate;
    private double _averageThroughput; // MB/s

    public InstallLocationDialog(string metadataFilePath)
    {
        InitializeComponent();
        _metadataFilePath = metadataFilePath;
        _installationService = new InstallationService();

        // Check if metadata file exists
        if (!File.Exists(_metadataFilePath))
        {
            InstructionsTextBlock.Text = "Scan data is missing. Please close this dialog and scan the archive again.";
            InstallButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select installation folder for Skyrim data files",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) InstallPathTextBox.Text = dialog.SelectedPath;
    }

    private void InstallPathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateInstallButtonState();
    }

    private void UpdateInstallButtonState()
    {
        // Check if controls are initialized
        if (InstallPathTextBox == null || InstallButton == null)
            return;

        var text = InstallPathTextBox.Text;

        // Simple check - just see if there's any text that looks like a path
        var shouldEnable = !string.IsNullOrEmpty(text) && text.Length > 3 && text.Contains(@"\");

        InstallButton.IsEnabled = shouldEnable;
    }

    private bool IsValidPath(string path)
    {
        try
        {
            // Check if the path format is valid
            var fullPath = Path.GetFullPath(path);

            // Check if the drive exists (for Windows paths)
            if (Path.IsPathRooted(path))
            {
                var root = Path.GetPathRoot(path);
                return Directory.Exists(root);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(InstallPathTextBox.Text))
        {
            SelectedInstallPath = InstallPathTextBox.Text;

            // Create cancellation token source
            _cancellationTokenSource = new CancellationTokenSource();

            // Disable install and browse buttons during installation
            InstallButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;

            // Update cancel button text to indicate it will cancel installation
            CancelButton.Content = "Cancel Installation";

            try
            {
                var progress = new Progress<string>(message => { UpdateInstallationProgress(message); });

                // Use the overload that will set the correct total files after conflict resolution
                var result = await _installationService.InstallFilesAsync(_metadataFilePath, SelectedInstallPath,
                    progress, SetActualTotalFilesAndSize, UpdateProgressByBytes, IncrementFileCount,
                    _cancellationTokenSource.Token);

                if (result.Success)
                {
                    // Check verification results before declaring success
                    if (result.Verification != null && !result.Verification.Success)
                    {
                        // Show verification errors in a message box
                        var verificationErrors = new List<string>();

                        if (result.Verification.FilesMissing > 0)
                        {
                            verificationErrors.Add($"Missing files ({result.Verification.FilesMissing}):");
                            verificationErrors.AddRange(result.Verification.MissingFiles.Take(5));
                            if (result.Verification.MissingFiles.Count > 5)
                                verificationErrors.Add($"... and {result.Verification.MissingFiles.Count - 5} more");
                        }

                        if (result.Verification.FilesSizeMismatch > 0)
                        {
                            verificationErrors.Add($"\nSize mismatches ({result.Verification.FilesSizeMismatch}):");
                            verificationErrors.AddRange(result.Verification.SizeMismatchFiles.Take(3));
                            if (result.Verification.SizeMismatchFiles.Count > 3)
                                verificationErrors.Add(
                                    $"... and {result.Verification.SizeMismatchFiles.Count - 3} more");
                        }

                        if (result.Verification.Errors.Count > 0)
                        {
                            var nonInfoErrors = result.Verification.Errors.Where(e => !e.StartsWith("Info:")).ToList();
                            if (nonInfoErrors.Count > 0)
                            {
                                verificationErrors.Add("\nOther verification errors:");
                                verificationErrors.AddRange(nonInfoErrors.Take(3));
                                if (nonInfoErrors.Count > 3)
                                    verificationErrors.Add($"... and {nonInfoErrors.Count - 3} more");
                            }
                        }

                        var verificationMessage =
                            $"Installation completed, but verification found issues:\n\n{string.Join("\n", verificationErrors)}";

                        System.Windows.MessageBox.Show(verificationMessage, "Verification Issues",
                            MessageBoxButton.OK, MessageBoxImage.Warning);

                        InstructionsTextBlock.Text =
                            $"Installation completed with verification issues. {result.FilesInstalled} files installed, {result.Verification.FilesVerified} verified.";
                    }
                    else
                    {
                        // Pure success - installation and verification both passed
                        var summary =
                            $"Installation Complete! {result.FilesInstalled} files installed ({FormatFileSize(result.TotalSizeInstalled)})";

                        InstructionsTextBlock.Text = summary;
                    }

                    // Keep dialog open but disable installation controls
                    InstallButton.IsEnabled = false;
                    BrowseButton.IsEnabled = false;
                    InstallPathTextBox.IsReadOnly = true;

                    // Hide timer/progress UI since installation is complete
                    StopTimer();
                    TimerPanel.Visibility = Visibility.Collapsed;

                    // Change cancel button to "Close" since installation is complete
                    CancelButton.Content = "Close";
                }
                else
                {
                    // Installation failed - reset UI for retry
                    var errorMessage = "❌ Installation failed: " + string.Join(", ", result.Errors.Take(3));
                    if (result.Errors.Count > 3) errorMessage += $" (and {result.Errors.Count - 3} more errors)";

                    InstructionsTextBlock.Text = errorMessage;

                    // Show detailed errors in message box
                    var detailedErrors = string.Join("\n", result.Errors);
                    System.Windows.MessageBox.Show(detailedErrors, "Installation Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);

                    // Reset UI for potential retry
                    StopTimer();
                    TimerPanel.Visibility = Visibility.Collapsed;
                    InstallButton.IsEnabled = true;
                    BrowseButton.IsEnabled = true;
                    InstallPathTextBox.IsReadOnly = false;
                    CancelButton.Content = "Cancel";
                }
            }
            catch (OperationCanceledException)
            {
                InstructionsTextBlock.Text = "Installation cancelled by user.";

                // Reset UI for potential retry after cancellation
                StopTimer();
                TimerPanel.Visibility = Visibility.Collapsed;
                InstallButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                InstallPathTextBox.IsReadOnly = false;
                CancelButton.Content = "Cancel";
            }
            catch (Exception ex)
            {
                InstructionsTextBlock.Text = $"Installation error: {ex.Message}";

                // Reset UI for potential retry after error
                StopTimer();
                TimerPanel.Visibility = Visibility.Collapsed;
                InstallButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                InstallPathTextBox.IsReadOnly = false;
                CancelButton.Content = "Cancel";
            }
            finally
            {
                // Reset cancellation token
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }
        else
        {
            System.Windows.MessageBox.Show("Please select an installation folder first.", "No Folder Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellationTokenSource != null)
        {
            // Installation is running, cancel it
            _cancellationTokenSource.Cancel();
            // Don't close dialog immediately, let the installation cleanup finish
            // The dialog will be re-enabled in the finally block
        }
        else
        {
            // No installation running, or installation completed - close the dialog
            DialogResult = true; // Set to true since installation may have completed
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Cancel any ongoing installation when dialog is closed
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        StopTimer();
        base.OnClosed(e);
    }

    private void SetActualTotalFilesAndSize(int actualTotal, long totalSizeBytes)
    {
        // This is called by the installation service after conflict resolution
        _totalFiles = actualTotal;
        _processedFiles = 0;
        _totalBytes = totalSizeBytes;
        _processedBytes = 0;
        _throughputSamples.Clear();

        // Show timer panel and start timer with the correct total
        TimerPanel.Visibility = Visibility.Visible;
        InstallProgressBar.Maximum = 100; // Use percentage
        InstallProgressBar.Value = 0;
        ProgressTextBlock.Text = $"0 / {_totalFiles} files (0 MB / {FormatFileSize(_totalBytes)})";
        _installStartTime = DateTime.Now;

        // Better initial estimate based on file size and typical throughput
        // Assume 10-50 MB/s depending on file size mix
        var totalMB = _totalBytes / (1024.0 * 1024.0);
        var estimatedThroughput = totalMB > 1000 ? 15.0 : 25.0; // MB/s
        _averageThroughput = estimatedThroughput;
        _estimatedTotalSeconds = Math.Max(totalMB / estimatedThroughput, _totalFiles * 5); // At least 5s per file

        _lastProgressUpdate = DateTime.Now;

        StartTimer();
    }

    private void StartTimer()
    {
        _timer = new System.Windows.Threading.DispatcherTimer();
        _timer.Interval = TimeSpan.FromSeconds(1); // Update every second for consistent countdown
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // Recalculate time estimate based on current progress
        RecalculateTimeEstimate();

        // Update time display
        UpdateTimeEstimate();
    }

    private void UpdateInstallationProgress(string message)
    {
        // Update the instructions text
        InstructionsTextBlock.Text = message;

        // Progress will be updated via byte-based tracking
    }

    public void UpdateProgressByBytes(long bytesProcessed, string currentFileName)
    {
        _processedBytes = bytesProcessed;

        var now = DateTime.Now;
        var elapsedSeconds = (now - _installStartTime).TotalSeconds;

        if (elapsedSeconds > 0.5) // Only calculate after half a second
        {
            // Calculate current throughput in MB/s
            var processedMB = _processedBytes / (1024.0 * 1024.0);
            var currentThroughput = processedMB / elapsedSeconds;

            // Add to samples for averaging
            _throughputSamples.Add(currentThroughput);
            if (_throughputSamples.Count > 20) _throughputSamples.RemoveAt(0); // Keep last 20 samples

            // Update average throughput
            _averageThroughput = _throughputSamples.Average();

            // Update UI
            var progressPercent = Math.Min(100.0, (double)_processedBytes / _totalBytes * 100);
            InstallProgressBar.Value = progressPercent;
            ProgressTextBlock.Text =
                $"{_processedFiles} / {_totalFiles} files ({FormatFileSize(_processedBytes)} / {FormatFileSize(_totalBytes)})";
        }
    }

    public void IncrementFileCount()
    {
        _processedFiles++;
    }

    private void RecalculateTimeEstimate()
    {
        if (_processedBytes == 0 || _totalBytes == 0 ||
            _averageThroughput <= 0) return; // Keep initial estimate until we have some data

        try
        {
            // Calculate remaining bytes and estimate time based on current throughput
            var remainingBytes = _totalBytes - _processedBytes;
            var remainingMB = remainingBytes / (1024.0 * 1024.0);
            var estimatedRemainingSeconds = remainingMB / _averageThroughput;

            // Total estimated time is elapsed + remaining
            var elapsedSeconds = (DateTime.Now - _installStartTime).TotalSeconds;
            _estimatedTotalSeconds = elapsedSeconds + estimatedRemainingSeconds;

            _lastProgressUpdate = DateTime.Now;
        }
        catch (Exception)
        {
            // Keep current estimate on error
        }
    }

    private void UpdateTimeEstimate()
    {
        if (_processedBytes == 0 || _totalBytes == 0 || _estimatedTotalSeconds <= 0)
        {
            TimerTextBlock.Text = "Estimated time remaining: Calculating...";
            return;
        }

        try
        {
            // Calculate how much time should have elapsed based on our estimate
            var elapsedTime = (DateTime.Now - _installStartTime).TotalSeconds;
            var remainingSeconds = Math.Max(0, _estimatedTotalSeconds - elapsedTime);

            if (remainingSeconds < 1)
            {
                TimerTextBlock.Text = "Estimated time remaining: Almost done!";
            }
            else if (remainingSeconds < 60)
            {
                TimerTextBlock.Text = $"Estimated time remaining: {(int)remainingSeconds}s";
            }
            else
            {
                var minutes = (int)(remainingSeconds / 60);
                var seconds = (int)(remainingSeconds % 60);
                TimerTextBlock.Text = $"Estimated time remaining: {minutes}:{seconds:D2}";
            }
        }
        catch (Exception)
        {
            TimerTextBlock.Text = "Estimated time remaining: Calculating...";
        }
    }
}