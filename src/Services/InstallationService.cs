using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SkyrimDataInstaller.Models;

namespace SkyrimDataInstaller.Services;

public class InstallationService : IInstallationService
{
    private readonly IMetadataService _metadataService;
    private long _currentProcessedBytes;
    private Action<long, string>? _updateProgressByBytes;
    private Action? _incrementFileCount;
    private readonly List<string> _actuallyInstalledFiles = new();

    public InstallationService()
    {
        _metadataService = new MetadataService();
    }

    public async Task<InstallationResult> InstallFilesAsync(string metadataFilePath, string installPath,
        IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        return await InstallFilesAsync(metadataFilePath, installPath, progress, null, null, null, cancellationToken);
    }

    public async Task<InstallationResult> InstallFilesAsync(string metadataFilePath, string installPath,
        IProgress<string> progress, Action<int, long> setTotalFilesAndSize,
        Action<long, string>? updateProgressByBytes = null,
        Action? incrementFileCount = null, CancellationToken cancellationToken = default)
    {
        var result = new InstallationResult();

        // Initialize progress tracking
        _currentProcessedBytes = 0;
        _updateProgressByBytes = updateProgressByBytes;
        _incrementFileCount = incrementFileCount;
        _actuallyInstalledFiles.Clear();

        try
        {
            progress.Report("Loading installation metadata...");

            // Load metadata
            var metadata = await _metadataService.LoadMetadataAsync(metadataFilePath);

            // Create install directory if it doesn't exist
            if (!Directory.Exists(installPath))
            {
                Directory.CreateDirectory(installPath);
                progress.Report($"Created installation directory: {installPath}");
            }

            // Scan for all potential conflicts first
            var conflicts = await ScanForConflicts(metadata.TargetFiles, installPath, progress);
            var globalResolution = ConflictResolution.Overwrite; // Default to overwrite if no conflicts

            if (conflicts.Any())
            {
                globalResolution = await ResolveAllConflicts(conflicts);
                if (globalResolution == ConflictResolution.Cancel)
                {
                    result.Success = false;
                    result.Errors.Add("Installation cancelled by user due to conflicts.");
                    progress.Report("Installation cancelled by user.");
                    return result;
                }
            }

            // Calculate actual files that will be installed after conflict resolution
            var actualFilesToInstall =
                CalculateActualFilesToInstall(metadata.TargetFiles, conflicts, globalResolution, installPath);

            // Set the correct total files and size for progress tracking
            var totalSizeToInstall =
                CalculateActualSizeToInstall(metadata.TargetFiles, conflicts, globalResolution, installPath);
            setTotalFilesAndSize?.Invoke(actualFilesToInstall, totalSizeToInstall);

            progress.Report("Starting installation...");

            // Group files by their archive chain to optimize extraction
            var filesByArchiveChain = metadata.TargetFiles.GroupBy(f => string.Join("|", f.ArchiveChain)).ToList();
            progress.Report(
                $"Processing {filesByArchiveChain.Count} archive chains with {metadata.TargetFiles.Count} target files");

            foreach (var archiveGroup in filesByArchiveChain)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var archiveChain = archiveGroup.First().ArchiveChain;
                var archiveFiles = archiveGroup.ToList();

                try
                {
                    await ProcessArchiveChain(metadata.ParentArchivePath, archiveChain, archiveFiles, installPath,
                        result, progress, globalResolution, conflicts, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result.Success = false;
                    result.Errors.Add("Installation was cancelled by user.");
                    progress.Report("Installation cancelled.");
                    return result;
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Error processing files: {ex.Message}";
                    result.Errors.Add(errorMsg);
                    progress.Report(errorMsg);
                }
            }

            result.Success = true;
            // Verify installation silently in the background
            result.Verification =
                await VerifyInstallation(metadata, installPath, _actuallyInstalledFiles, cancellationToken);

            progress.Report($"Installation complete. {result.FilesInstalled} files installed successfully.");

            // Always cleanup metadata file after installation completes (verification issues are warnings, not failures)
            _metadataService.CleanupMetadata(metadataFilePath);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Installation failed: {ex.Message}");
            progress.Report($"Installation failed: {ex.Message}");
        }

        return result;
    }

    private async Task ProcessArchiveChain(string parentArchivePath, List<string> archiveChain,
        List<TargetFileMetadata> targetFiles, string installPath, InstallationResult result,
        IProgress<string> progress, ConflictResolution globalResolution, List<FileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        // Open the parent archive
        using var fileStream = new FileStream(parentArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var archive = ArchiveFactory.Open(fileStream);

        // Navigate through the archive chain to find the target files
        await NavigateAndExtractFiles(archive, archiveChain.Skip(1).ToList(), targetFiles, installPath, result,
            progress, globalResolution, conflicts, cancellationToken);
    }

    private async Task NavigateAndExtractFiles(IArchive archive, List<string> remainingChain,
        List<TargetFileMetadata> targetFiles, string installPath, InstallationResult result,
        IProgress<string> progress, ConflictResolution globalResolution, List<FileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        if (!remainingChain.Any())
        {
            // We've reached the target archive, extract the files
            await ExtractTargetFiles(archive, targetFiles, installPath, result, progress, globalResolution, conflicts,
                cancellationToken);
            return;
        }

        var nextArchiveName = remainingChain.First();
        var remainingChainPath = remainingChain.Skip(1).ToList();

        // Find the nested archive entry
        var nestedEntry = archive.Entries.FirstOrDefault(e =>
            e.Key.EndsWith(nextArchiveName) || Path.GetFileName(e.Key) == nextArchiveName);
        if (nestedEntry == null) throw new InvalidOperationException($"Nested archive not found: {nextArchiveName}");

        // Balanced approach for nested archives
        if (nestedEntry.Size > 150 * 1024 * 1024) // 150MB threshold - balance speed vs memory
        {
            // For very large archives, use temp file approach
            await ProcessLargeNestedArchive(nestedEntry, remainingChainPath, targetFiles, installPath, result, progress,
                globalResolution, conflicts, cancellationToken);
        }
        else
        {
            // Use memory for reasonably sized archives (< 150MB) - much faster
            using var entryStream = nestedEntry.OpenEntryStream();
            using var memoryStream = new MemoryStream((int)Math.Min(nestedEntry.Size, int.MaxValue));

            // Fast streaming for memory operations (no progress tracking for nested archives)
            await StreamWithProgressSimple(entryStream, memoryStream, cancellationToken);
            memoryStream.Position = 0;

            using var nestedArchive = ArchiveFactory.Open(memoryStream);

            // Continue navigating
            await NavigateAndExtractFiles(nestedArchive, remainingChainPath, targetFiles, installPath, result, progress,
                globalResolution, conflicts, cancellationToken);
        }
    }

    private async Task ExtractTargetFiles(IArchive archive, List<TargetFileMetadata> targetFiles,
        string installPath, InstallationResult result, IProgress<string> progress, ConflictResolution globalResolution,
        List<FileConflict> conflicts, CancellationToken cancellationToken)
    {
        var fileCount = 0;
        foreach (var targetFile in targetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Yield control every few files to prevent UI freezing
            if (++fileCount % 5 == 0) await Task.Yield();

            try
            {
                // Find the entry in the archive
                var entry = archive.Entries.FirstOrDefault(e => e.Key == targetFile.RelativePathInArchive);
                if (entry == null)
                {
                    result.FilesSkipped++;
                    continue;
                }

                var destinationPath = Path.Combine(installPath, targetFile.FileName);

                // Check for conflicts and apply resolution
                if (File.Exists(destinationPath))
                {
                    result.ConflictsResolved++;

                    if (globalResolution == ConflictResolution.Skip)
                    {
                        result.FilesSkipped++;
                        continue;
                    }
                    else if (globalResolution == ConflictResolution.Selective)
                    {
                        // Check if this specific file should be overwritten
                        var conflict = conflicts.FirstOrDefault(c => c.ExistingPath == destinationPath);
                        if (conflict == null || !conflict.ShouldOverwrite)
                        {
                            result.FilesSkipped++;
                            continue;
                        }
                    }
                    // If Overwrite or selected for overwrite, continue with extraction
                }

                // Show simplified progress message
                progress.Report($"Installing {targetFile.FileName}");

                // Track starting bytes for this file
                var startingBytes = _currentProcessedBytes;

                // Use different extraction methods based on file size
                if (targetFile.Size > 100 * 1024 * 1024) // 100MB+ files get optimized treatment
                {
                    await ExtractLargeFile(entry, destinationPath, targetFile, startingBytes, cancellationToken);
                }
                else
                {
                    // Fast extraction for smaller files with optimized settings
                    using var entryStream = entry.OpenEntryStream();
                    using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                        FileShare.None,
                        128 * 1024, // 128KB OS buffer
                        FileOptions.SequentialScan);
                    await StreamWithProgress(entryStream, fileStream, startingBytes, targetFile.Size,
                        targetFile.FileName, cancellationToken);
                }

                result.FilesInstalled++;
                result.TotalSizeInstalled += targetFile.Size;

                // Track this file as successfully installed
                _actuallyInstalledFiles.Add(targetFile.FileName);

                // Update progress tracking
                _currentProcessedBytes += targetFile.Size;
                _incrementFileCount?.Invoke();
                _updateProgressByBytes?.Invoke(_currentProcessedBytes, targetFile.FileName);
            }
            catch (OperationCanceledException)
            {
                throw; // Re-throw cancellation
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error extracting {targetFile.FileName}: {ex.Message}";
                result.Errors.Add(errorMsg);
            }
        }
    }

    private async Task<List<FileConflict>> ScanForConflicts(List<TargetFileMetadata> files, string installPath,
        IProgress<string> progress)
    {
        var conflicts = new List<FileConflict>();

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                var destinationPath = Path.Combine(installPath, file.FileName);
                if (File.Exists(destinationPath))
                {
                    var existingInfo = new FileInfo(destinationPath);
                    conflicts.Add(new FileConflict
                    {
                        ExistingPath = destinationPath,
                        NewFile = file,
                        ExistingSize = existingInfo.Length
                    });
                }
            }
        });

        if (conflicts.Any()) progress.Report($"Found {conflicts.Count} potential file conflicts");

        return conflicts;
    }

    private async Task<ConflictResolution> ResolveAllConflicts(List<FileConflict> conflicts)
    {
        return await Task.Run(() =>
        {
            var result = ConflictResolution.Cancel;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new ConflictResolutionDialog(conflicts);
                var dialogResult = dialog.ShowDialog();

                if (dialogResult == true && !dialog.WasCancelled)
                {
                    // Store which files user selected to overwrite
                    foreach (var conflict in conflicts)
                        conflict.ShouldOverwrite = dialog.SelectedFiles.Contains(conflict.ExistingPath);
                    result = ConflictResolution.Selective;
                }
            });

            return result;
        });
    }

    private async Task StreamWithProgress(Stream source, Stream destination, long startingBytes, long fileSize,
        string fileName, CancellationToken cancellationToken)
    {
        // Optimized for speed while maintaining responsiveness and real-time progress
        var buffer = new byte[512 * 1024]; // 512KB buffer for good performance
        int bytesRead;
        var processedBytes = 0L;
        var lastYield = DateTime.Now;
        var lastProgress = DateTime.Now;

        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;

            var now = DateTime.Now;

            // Update progress every 100ms for smooth updates during large files
            if ((now - lastProgress).TotalMilliseconds > 100)
            {
                var currentTotalBytes = startingBytes + processedBytes;
                _updateProgressByBytes?.Invoke(currentTotalBytes, fileName);
                lastProgress = now;
            }

            // Time-based yielding for consistent responsiveness
            if ((now - lastYield).TotalMilliseconds > 25) // Yield every 25ms for smaller files
            {
                await Task.Yield();
                lastYield = now;
            }

            // Minimal GC - only when really needed
            if (processedBytes % (100 * 1024 * 1024) == 0) // Every 100MB
                if (GC.GetTotalMemory(false) > 200 * 1024 * 1024) // Only if using > 200MB
                    GC.Collect(0, GCCollectionMode.Optimized);
        }
    }

    private async Task StreamWithProgressSimple(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        // Simple streaming without progress reporting (for internal archive operations)
        var buffer = new byte[512 * 1024]; // 512KB buffer for good performance
        int bytesRead;
        var lastYield = DateTime.Now;

        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);

            // Time-based yielding for consistent responsiveness
            var now = DateTime.Now;
            if ((now - lastYield).TotalMilliseconds > 25) // Yield every 25ms
            {
                await Task.Yield();
                lastYield = now;
            }
        }
    }

    private async Task ExtractLargeFile(IArchiveEntry entry, string destinationPath, TargetFileMetadata targetFile,
        long startingBytes, CancellationToken cancellationToken)
    {
        // Optimized approach for large files - balance speed vs memory
        using var entryStream = entry.OpenEntryStream();
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            256 * 1024, // 256KB OS buffer for better performance
            FileOptions.SequentialScan | FileOptions.WriteThrough);

        // Use larger buffer but with smart memory management and progress reporting
        var buffer = new byte[1024 * 1024]; // 1MB buffer for speed
        int bytesRead;
        var processedBytes = 0L;
        var lastYield = DateTime.Now;
        var lastProgress = DateTime.Now;

        while ((bytesRead = await entryStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;

            var now = DateTime.Now;

            // Update progress every 200ms for large files (less frequent for better performance)
            if ((now - lastProgress).TotalMilliseconds > 200)
            {
                var currentTotalBytes = startingBytes + processedBytes;
                _updateProgressByBytes?.Invoke(currentTotalBytes, targetFile.FileName);
                lastProgress = now;
            }

            // Time-based yielding instead of byte-based for better responsiveness
            if ((now - lastYield).TotalMilliseconds > 50) // Yield every 50ms max
            {
                await Task.Yield();
                lastYield = now;

                // Smart GC - only if we've processed a lot and it's been a while
                if (processedBytes % (50 * 1024 * 1024) == 0) // Every 50MB
                    if (GC.GetTotalMemory(false) > 100 * 1024 * 1024) // Only if using > 100MB
                        GC.Collect(0, GCCollectionMode.Optimized);
            }
        }
    }

    private async Task ProcessLargeNestedArchive(IArchiveEntry entry, List<string> remainingChain,
        List<TargetFileMetadata> targetFiles, string installPath, InstallationResult result,
        IProgress<string> progress, ConflictResolution globalResolution, List<FileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        // For very large nested archives, extract to temp file instead of memory
        var tempPath = Path.GetTempFileName();

        try
        {
            // Stream to temp file with optimized settings
            using (var entryStream = entry.OpenEntryStream())
            using (var tempFileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       256 * 1024, // 256KB buffer for better performance
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await StreamWithProgressSimple(entryStream, tempFileStream, cancellationToken);
            }

            // Open archive from temp file with optimized settings
            using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                256 * 1024, // 256KB buffer for reading
                FileOptions.SequentialScan | FileOptions.RandomAccess);
            using var nestedArchive = ArchiveFactory.Open(fileStream);

            // Continue navigating
            await NavigateAndExtractFiles(nestedArchive, remainingChain, targetFiles, installPath, result, progress,
                globalResolution, conflicts, cancellationToken);
        }
        finally
        {
            // Clean up temp file
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception)
            {
                // Ignore cleanup errors
            }
        }
    }

    private int CalculateActualFilesToInstall(List<TargetFileMetadata> allFiles, List<FileConflict> conflicts,
        ConflictResolution globalResolution, string installPath)
    {
        var filesToInstall = 0;

        foreach (var file in allFiles)
        {
            var destinationPath = Path.Combine(installPath, file.FileName);
            var hasConflict = conflicts.Any(c => c.ExistingPath == destinationPath);

            if (!hasConflict)
                // No conflict, file will be installed
                filesToInstall++;
            else
                // Has conflict, check resolution
                switch (globalResolution)
                {
                    case ConflictResolution.Overwrite:
                        filesToInstall++; // All conflicting files will be overwritten
                        break;
                    case ConflictResolution.Skip:
                        // Conflicting files will be skipped, don't count them
                        break;
                    case ConflictResolution.Selective:
                        // Check if this specific file was selected for overwrite
                        var conflict = conflicts.FirstOrDefault(c => c.ExistingPath == destinationPath);
                        if (conflict?.ShouldOverwrite == true) filesToInstall++;
                        break;
                }
        }

        return filesToInstall;
    }

    private long CalculateActualSizeToInstall(List<TargetFileMetadata> allFiles, List<FileConflict> conflicts,
        ConflictResolution globalResolution, string installPath)
    {
        long totalSize = 0;

        foreach (var file in allFiles)
        {
            var destinationPath = Path.Combine(installPath, file.FileName);
            var hasConflict = conflicts.Any(c => c.ExistingPath == destinationPath);

            if (!hasConflict)
                // No conflict, file will be installed
                totalSize += file.Size;
            else
                // Has conflict, check resolution
                switch (globalResolution)
                {
                    case ConflictResolution.Overwrite:
                        totalSize += file.Size; // All conflicting files will be overwritten
                        break;
                    case ConflictResolution.Skip:
                        // Conflicting files will be skipped, don't count them
                        break;
                    case ConflictResolution.Selective:
                        // Check if this specific file was selected for overwrite
                        var conflict = conflicts.FirstOrDefault(c => c.ExistingPath == destinationPath);
                        if (conflict?.ShouldOverwrite == true) totalSize += file.Size;
                        break;
                }
        }

        return totalSize;
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

    private async Task<VerificationResult> VerifyInstallation(InstallationMetadata metadata, string installPath,
        List<string> actuallyInstalledFiles, CancellationToken cancellationToken)
    {
        var result = new VerificationResult();

        try
        {
            // Only verify files that were actually installed (not skipped due to conflicts)
            var filesToVerify = metadata.TargetFiles.Where(f =>
                actuallyInstalledFiles.Contains(f.FileName)).ToList();

            // Silently verify files without progress reporting

            foreach (var targetFile in filesToVerify)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var expectedPath = Path.Combine(installPath, targetFile.FileName);

                // Check if file exists (this should always pass since we tracked successful installations)
                if (!File.Exists(expectedPath))
                {
                    result.FilesMissing++;
                    result.MissingFiles.Add($"{targetFile.FileName} (was marked as installed but file is missing!)");
                    continue;
                }

                // Check file size
                try
                {
                    var fileInfo = new FileInfo(expectedPath);
                    if (fileInfo.Length != targetFile.Size)
                    {
                        result.FilesSizeMismatch++;
                        result.SizeMismatchFiles.Add(
                            $"{targetFile.FileName} (expected: {FormatFileSize(targetFile.Size)}, actual: {FormatFileSize(fileInfo.Length)})");
                    }
                    else
                    {
                        result.FilesVerified++;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Error checking {targetFile.FileName}: {ex.Message}");
                }

                // Keep UI responsive during verification
                if ((result.FilesVerified + result.FilesMissing + result.FilesSizeMismatch) % 50 == 0)
                    await Task.Yield(); // Keep UI responsive
            }

            // Determine overall success (based on technical verification, not skipped files)
            result.Success = result.FilesMissing == 0 && result.FilesSizeMismatch == 0 && result.Errors.Count == 0;

            if (!result.Success)
            {
                var summary = new List<string>();
                if (result.FilesMissing > 0) summary.Add($"{result.FilesMissing} files missing");
                if (result.FilesSizeMismatch > 0) summary.Add($"{result.FilesSizeMismatch} size mismatches");
                if (result.Errors.Count > 0) summary.Add($"{result.Errors.Count} verification errors");

                result.Errors.Insert(0, $"Verification failed");
            }

            // Add informational note about skipped files (not an error if verification succeeds)
            var skippedCount = metadata.TargetFiles.Count - filesToVerify.Count;
            if (skippedCount > 0) result.Errors.Add($"Info: {skippedCount} files were skipped");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Verification process failed: {ex.Message}");
        }

        return result;
    }
}

public enum ConflictResolution
{
    Overwrite,
    Skip,
    Cancel,
    Selective
}