 # SkyrimDataInstaller

A Windows application for automatically extracting and installing Skyrim data files from archives.

## Features

- Supports nested archives (archives within archives)
- Automatically finds and extracts .bsa, .bsl, .bsm, and .esl files
- Handles multiple archive formats: ZIP, RAR, 7Z, TAR, GZ, BZ2
- Real-time progress tracking during installation
- Conflict resolution for existing files
- Installation verification

## Usage

1. Click "Browse" to select a parent archive file
2. Click "Scan Archives" to analyze the contents
3. Review the scan results showing found files by type
4. Click "Next" to proceed to installation
5. Select the installation directory(most will be your game folder "Skyrim Special Edition/Data")
6. Click "Install Files" to extract the mod files

## Requirements

- Windows 10 or later
- .NET 6.0 Runtime