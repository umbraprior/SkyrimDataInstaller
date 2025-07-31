using System.Threading.Tasks;
using SkyrimDataInstaller.Models;

namespace SkyrimDataInstaller.Services;

public interface IMetadataService
{
    Task<string> SaveMetadataAsync(InstallationMetadata metadata);
    Task<InstallationMetadata> LoadMetadataAsync(string metadataFilePath);
    void CleanupMetadata(string metadataFilePath);
    void CleanupAllTempFiles();
    string GetTempMetadataPath();
}