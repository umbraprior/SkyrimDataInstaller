using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SkyrimDataInstaller.Models;

namespace SkyrimDataInstaller.Services;

public class ArchiveService : IArchiveService
{
    private readonly string[] _targetExtensions = { ".bsm", ".bsl", ".esl", ".esm", ".bsa" };
    private readonly string[] _archiveExtensions = { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2" };
    private readonly IMetadataService _metadataService;

    public ArchiveService()
    {
        _metadataService = new MetadataService();
    }

    public async Task<string> ScanArchiveAsync(string archivePath, IProgress<string> progress)
    {
        var metadata = new InstallationMetadata
        {
            ParentArchivePath = archivePath
        };

        var archiveResults = new Dictionary<string, List<TargetFileMetadata>>();

        try
        {
            using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var archive = ArchiveFactory.Open(fileStream);

            var archiveName = Path.GetFileName(archivePath);
            await ScanArchiveRecursive(archive, new List<string> { archiveName }, archiveResults, metadata, progress);

            // Generate final summary
            GenerateSummary(metadata);

            // Save metadata to temp file
            var metadataPath = await _metadataService.SaveMetadataAsync(metadata);
            progress.Report($"Metadata saved to: {metadataPath}");

            return metadataPath;
        }
        catch (Exception ex)
        {
            progress.Report($"Error processing archive {Path.GetFileName(archivePath)}: {ex.Message}");
            throw;
        }
    }

    private async Task ScanArchiveRecursive(IArchive archive, List<string> archiveChain,
        Dictionary<string, List<TargetFileMetadata>> archiveResults, InstallationMetadata metadata,
        IProgress<string> progress)
    {
        var currentArchiveKey = string.Join(" → ", archiveChain.Select(Path.GetFileName));
        var targetFiles = new List<TargetFileMetadata>();

        var entryCount = 0;
        var totalEntries = archive.Entries.Count();

        foreach (var entry in archive.Entries)
        {
            entryCount++;
            if (entryCount % 50 == 0) await Task.Yield(); // Allow UI to update without blocking

            if (entry.IsDirectory) continue;

            var fileName = entry.Key;
            var extension = Path.GetExtension(fileName).ToLowerInvariant();

            // Check if this is a target file
            if (_targetExtensions.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)))
            {
                var targetFile = new TargetFileMetadata
                {
                    FileName = Path.GetFileName(fileName),
                    Extension = extension,
                    Size = entry.Size,
                    ArchiveChain = new List<string>(archiveChain),
                    RelativePathInArchive = fileName,
                    DisplayArchivePath = currentArchiveKey
                };

                targetFiles.Add(targetFile);
                metadata.TargetFiles.Add(targetFile);
            }
            // Check if this is a nested archive
            else if (_archiveExtensions.Contains(extension))
            {
                try
                {
                    await ScanNestedArchiveRecursive(entry, fileName, archiveChain, archiveResults, metadata, progress);
                }
                catch (Exception ex)
                {
                    progress.Report($"Error processing nested archive {fileName}: {ex.Message}");
                    // Continue with other entries
                }
            }
        }

        // Report archive summary if files found
        if (targetFiles.Count > 0)
        {
            archiveResults[currentArchiveKey] = targetFiles;
            ReportArchiveSummary(currentArchiveKey, targetFiles, progress);
        }
    }

    private async Task ScanNestedArchiveRecursive(IArchiveEntry entry, string nestedArchiveName,
        List<string> parentChain, Dictionary<string, List<TargetFileMetadata>> archiveResults,
        InstallationMetadata metadata, IProgress<string> progress)
    {
        var newChain = new List<string>(parentChain) { nestedArchiveName };

        try
        {
            // Use balanced approach based on archive size  
            if (entry.Size > 100 * 1024 * 1024) // 100MB threshold for scanning - prioritize speed
            {
                // For large archives, use temp file to avoid memory issues
                var tempPath = Path.GetTempFileName();
                try
                {
                    using (var entryStream = entry.OpenEntryStream())
                    using (var tempFileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                               FileShare.None, 64 * 1024))
                    {
                        await StreamToFileAsync(entryStream, tempFileStream);
                    }

                    using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024);
                    using var nestedArchive = ArchiveFactory.Open(fileStream);
                    await ScanArchiveRecursive(nestedArchive, newChain, archiveResults, metadata, progress);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
            else
            {
                // For smaller archives, use memory stream (faster)
                using var entryStream = entry.OpenEntryStream();
                using var memoryStream = new MemoryStream();
                await StreamToMemoryAsync(entryStream, memoryStream);
                memoryStream.Position = 0;

                using var nestedArchive = ArchiveFactory.Open(memoryStream);
                await ScanArchiveRecursive(nestedArchive, newChain, archiveResults, metadata, progress);
            }
        }
        catch (Exception ex)
        {
            progress.Report($"Error processing nested archive {nestedArchiveName}: {ex.Message}");

            // Try alternative methods
            try
            {
                await TryAlternativeNestedArchiveScan(entry, nestedArchiveName, newChain, archiveResults, metadata,
                    progress);
            }
            catch (Exception)
            {
                // Continue with other entries
            }
        }
    }

    private async Task TryAlternativeNestedArchiveScan(IArchiveEntry entry, string nestedArchiveName,
        List<string> archiveChain, Dictionary<string, List<TargetFileMetadata>> archiveResults,
        InstallationMetadata metadata, IProgress<string> progress)
    {
        try
        {
            // Method 1: Buffered stream
            using var entryStream = entry.OpenEntryStream();
            using var bufferedStream = new BufferedStream(entryStream, 65536);
            using var nestedArchive = ArchiveFactory.Open(bufferedStream);

            await ScanArchiveRecursive(nestedArchive, archiveChain, archiveResults, metadata, progress);
            return;
        }
        catch (Exception)
        {
            // Method 2: Chunked reading
            try
            {
                using var entryStream = entry.OpenEntryStream();
                using var memoryStream = new MemoryStream();

                var buffer = new byte[1024 * 1024]; // 1MB chunks
                int bytesRead;
                while ((bytesRead = await entryStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    await memoryStream.WriteAsync(buffer, 0, bytesRead);
                memoryStream.Position = 0;

                using var nestedArchive = ArchiveFactory.Open(memoryStream);
                await ScanArchiveRecursive(nestedArchive, archiveChain, archiveResults, metadata, progress);
            }
            catch (Exception)
            {
                // Final fallback - skip this archive
                throw;
            }
        }
    }

    private void ReportArchiveSummary(string archiveName, List<TargetFileMetadata> files, IProgress<string> progress)
    {
        if (files.Count == 0) return;

        var totalSize = files.Sum(f => f.Size);
        var displayName = archiveName.Split(" → ").LastOrDefault() ?? archiveName;

        progress.Report($"");
        progress.Report($"{displayName}");
        progress.Report($"Contains {files.Count} data file(s) - Total: {FormatFileSize(totalSize)}");
        progress.Report(new string('-', 42));

        var filesByExtension = files.GroupBy(f => f.Extension).OrderBy(g => g.Key);
        foreach (var extGroup in filesByExtension)
        {
            progress.Report($"{extGroup.Key.ToUpper()} Files ({extGroup.Count()}):");
            foreach (var file in extGroup.OrderBy(f => f.FileName))
                progress.Report($"* {file.FileName} ({FormatFileSize(file.Size)})");
        }

        progress.Report($"");
    }

    private void GenerateSummary(InstallationMetadata metadata)
    {
        var summary = metadata.Summary;
        summary.TotalFiles = metadata.TargetFiles.Count;
        summary.TotalSize = metadata.TargetFiles.Sum(f => f.Size);
        summary.ArchivesScanned = metadata.TargetFiles.GroupBy(f => f.DisplayArchivePath).Count();

        // Group by extension
        var filesByExtension = metadata.TargetFiles.GroupBy(f => f.Extension);
        foreach (var group in filesByExtension)
        {
            summary.FilesByExtension[group.Key] = group.Count();
            summary.SizeByExtension[group.Key] = group.Sum(f => f.Size);
        }
    }

    private async Task StreamToFileAsync(Stream source, Stream destination)
    {
        var buffer = new byte[512 * 1024]; // 512KB buffer for better performance
        int bytesRead;
        var totalBytes = 0L;
        var lastYield = DateTime.Now;

        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead);
            totalBytes += bytesRead;

            // Time-based yielding for better responsiveness
            var now = DateTime.Now;
            if ((now - lastYield).TotalMilliseconds > 30) // Yield every 30ms
            {
                await Task.Yield();
                lastYield = now;
            }

            // Smart GC only when needed
            if (totalBytes % (200 * 1024 * 1024) == 0) // Every 200MB
                if (GC.GetTotalMemory(false) > 300 * 1024 * 1024) // Only if using > 300MB
                    GC.Collect(0, GCCollectionMode.Optimized);
        }
    }

    private async Task StreamToMemoryAsync(Stream source, Stream destination)
    {
        var buffer = new byte[1024 * 1024]; // 1MB buffer for fast memory operations
        int bytesRead;
        var totalBytes = 0L;
        var lastYield = DateTime.Now;

        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead);
            totalBytes += bytesRead;

            // Time-based yielding for memory operations
            var now = DateTime.Now;
            if ((now - lastYield).TotalMilliseconds > 20) // Yield every 20ms for memory ops
            {
                await Task.Yield();
                lastYield = now;
            }

            // Minimal GC for memory operations
            if (totalBytes % (50 * 1024 * 1024) == 0) // Every 50MB
                if (GC.GetTotalMemory(false) > 500 * 1024 * 1024) // Only if using > 500MB
                    GC.Collect(0, GCCollectionMode.Optimized);
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
}