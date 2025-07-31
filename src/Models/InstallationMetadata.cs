using System;
using System.Collections.Generic;

namespace SkyrimDataInstaller.Models;

public class InstallationMetadata
{
    public string ScanTimestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string ParentArchivePath { get; set; } = string.Empty;
    public List<TargetFileMetadata> TargetFiles { get; set; } = new();
    public ScanSummary Summary { get; set; } = new();
}

public class TargetFileMetadata
{
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public List<string> ArchiveChain { get; set; } = new(); // Full path from parent to nested archive
    public string RelativePathInArchive { get; set; } = string.Empty; // Path within the final archive
    public string DisplayArchivePath { get; set; } = string.Empty; // Human-readable archive chain for UI
}

public class ScanSummary
{
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public int ArchivesScanned { get; set; }
    public Dictionary<string, int> FilesByExtension { get; set; } = new();
    public Dictionary<string, long> SizeByExtension { get; set; } = new();
}