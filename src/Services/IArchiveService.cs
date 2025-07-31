using System;
using System.Threading.Tasks;

namespace SkyrimDataInstaller.Services;

public interface IArchiveService
{
    Task<string> ScanArchiveAsync(string archivePath, IProgress<string> progress);
}