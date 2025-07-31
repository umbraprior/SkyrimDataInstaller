using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkyrimDataInstaller.Models;

namespace SkyrimDataInstaller.Services;

public interface IInstallationService
{
    Task<InstallationResult> InstallFilesAsync(string metadataFilePath, string installPath, IProgress<string> progress,
        CancellationToken cancellationToken = default);

    Task<InstallationResult> InstallFilesAsync(string metadataFilePath, string installPath, IProgress<string> progress,
        Action<int, long> setTotalFilesAndSize, Action<long, string>? updateProgressByBytes = null,
        Action? incrementFileCount = null, CancellationToken cancellationToken = default);
}

public class InstallationResult
{
    public bool Success { get; set; }
    public int FilesInstalled { get; set; }
    public int FilesSkipped { get; set; }
    public int ConflictsResolved { get; set; }
    public List<string> Errors { get; set; } = new();
    public long TotalSizeInstalled { get; set; }
    public VerificationResult? Verification { get; set; }
}

public class VerificationResult
{
    public bool Success { get; set; }
    public int FilesVerified { get; set; }
    public int FilesMissing { get; set; }
    public int FilesSizeMismatch { get; set; }
    public List<string> MissingFiles { get; set; } = new();
    public List<string> SizeMismatchFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class FileConflict
{
    public string ExistingPath { get; set; } = string.Empty;
    public TargetFileMetadata NewFile { get; set; } = new();
    public long ExistingSize { get; set; }
    public bool ShouldOverwrite { get; set; } = false;
}