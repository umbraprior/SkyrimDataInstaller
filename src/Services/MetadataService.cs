using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SkyrimDataInstaller.Models;

namespace SkyrimDataInstaller.Services;

public class MetadataService : IMetadataService
{
    private readonly JsonSerializerOptions _jsonOptions;

    public MetadataService()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<string> SaveMetadataAsync(InstallationMetadata metadata)
    {
        var tempPath = GetTempMetadataPath();
        var json = JsonSerializer.Serialize(metadata, _jsonOptions);

        await File.WriteAllTextAsync(tempPath, json);
        return tempPath;
    }

    public async Task<InstallationMetadata> LoadMetadataAsync(string metadataFilePath)
    {
        if (!File.Exists(metadataFilePath))
            throw new FileNotFoundException($"Metadata file not found: {metadataFilePath}");

        var json = await File.ReadAllTextAsync(metadataFilePath);
        var metadata = JsonSerializer.Deserialize<InstallationMetadata>(json, _jsonOptions);

        if (metadata == null) throw new InvalidOperationException("Failed to deserialize metadata file");

        return metadata;
    }

    public void CleanupMetadata(string metadataFilePath)
    {
        try
        {
            if (File.Exists(metadataFilePath)) File.Delete(metadataFilePath);
        }
        catch (Exception)
        {
            // Ignore cleanup errors - temp files will be cleaned up by OS eventually
        }
    }

    public void CleanupAllTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            var pattern = "SkyrimDataInstaller_Metadata_*.json";
            var files = Directory.GetFiles(tempDir, pattern);

            foreach (var file in files)
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Ignore individual file cleanup errors
                }
        }
        catch (Exception)
        {
            // Ignore cleanup errors - temp files will be cleaned up by OS eventually
        }
    }

    public string GetTempMetadataPath()
    {
        var tempDir = Path.GetTempPath();
        var fileName = $"SkyrimDataInstaller_Metadata_{Guid.NewGuid():N}.json";
        return Path.Combine(tempDir, fileName);
    }
}