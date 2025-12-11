# Build Instructions

This project includes build scripts for both Windows (PowerShell) and Linux/macOS (Bash) to easily create self-contained executable binaries.

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later

## Quick Start

### Windows (PowerShell)

```powershell
# Simple build (creates win-x64 Release build)
.\build.ps1

# Build for specific runtime
.\build.ps1 -Runtime win-x64

# Debug build
.\build.ps1 -Configuration Debug

# Build without single-file packaging
.\build.ps1 -SingleFile:$false
```

### Linux/macOS (Bash)

```bash
# Simple build (creates linux-x64 Release build)
./build.sh

# Build for specific runtime
./build.sh --runtime linux-x64

# Debug build
./build.sh --configuration Debug

# Build without single-file packaging
./build.sh --no-single-file

# Show all options
./build.sh --help
```

## Runtime Identifiers

The scripts support the following runtime identifiers (RID):

| Platform | RID | Description |
|----------|-----|-------------|
| Windows x64 | `win-x64` | 64-bit Windows (Intel/AMD) |
| Windows ARM64 | `win-arm64` | 64-bit Windows (ARM) |
| Linux x64 | `linux-x64` | 64-bit Linux (Intel/AMD) |
| Linux ARM64 | `linux-arm64` | 64-bit Linux (ARM/Raspberry Pi) |

## PowerShell Script Options

```powershell
.\build.ps1 [[-Runtime] <string>] [[-Configuration] <string>] [-SelfContained] [-SingleFile]
```

**Parameters:**
- `-Runtime` - Runtime identifier (default: `win-x64`)
- `-Configuration` - Build configuration: `Debug` or `Release` (default: `Release`)
- `-SelfContained` - Create self-contained executable (default: `$true`)
- `-SingleFile` - Package as single file executable (default: `$true`)

**Examples:**
```powershell
# Release build for Windows x64
.\build.ps1

# Debug build
.\build.ps1 -Configuration Debug

# Build for Windows ARM64
.\build.ps1 -Runtime win-arm64

# Framework-dependent build (requires .NET runtime on target)
.\build.ps1 -SelfContained:$false

# Multi-file build
.\build.ps1 -SingleFile:$false
```

## Bash Script Options

```bash
./build.sh [options]
```

**Options:**
- `-r, --runtime <RID>` - Runtime identifier (default: `linux-x64`)
- `-c, --configuration <CONFIG>` - Build configuration: `Debug` or `Release` (default: `Release`)
- `--no-self-contained` - Build as framework-dependent
- `--no-single-file` - Don't package as single file
- `-h, --help` - Show help message

**Examples:**
```bash
# Release build for Linux x64
./build.sh

# Debug build
./build.sh --configuration Debug

# Build for Linux ARM64 (Raspberry Pi)
./build.sh --runtime linux-arm64

# Framework-dependent build
./build.sh --no-self-contained

# Multi-file build
./build.sh --no-single-file
```

## Output Location

Built binaries are placed in:
```
bin/<Configuration>/publish/<Runtime>/
```

For example, a Windows x64 Release build will be in:
```
bin/Release/publish/win-x64/
```

## Build Types

### Self-Contained vs Framework-Dependent

**Self-Contained (default):**
- Includes the .NET runtime with the application
- Larger file size (~70-100 MB)
- Can run on systems without .NET installed
- Recommended for distribution

**Framework-Dependent:**
- Requires .NET runtime to be installed on target system
- Smaller file size (~1-5 MB)
- Useful for development or when runtime is guaranteed

### Single-File vs Multi-File

**Single-File (default):**
- Entire application bundled into one executable
- Easy to distribute and run
- Slightly slower first startup (unpacks to temp directory)

**Multi-File:**
- Separate files for application, libraries, and dependencies
- Faster startup
- More complex deployment

## Manual Build

If you prefer to build manually without the scripts:

```bash
# Self-contained single-file Release build
dotnet publish gspro-r10.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Framework-dependent Debug build
dotnet publish gspro-r10.csproj -c Debug -r win-x64 --self-contained false
```

## Troubleshooting

### Windows: "cannot be loaded because running scripts is disabled"

Run this in PowerShell as Administrator:
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

### Linux: "Permission denied"

Make the script executable:
```bash
chmod +x build.sh
```

### Build Errors

1. Ensure .NET 9.0 SDK is installed: `dotnet --version`
2. Restore dependencies: `dotnet restore`
3. Clean the project: `dotnet clean`

## Running the Built Application

After building, navigate to the output directory and run:

**Windows:**
```powershell
cd bin\Release\publish\win-x64
.\gspro-r10.exe
```

**Linux/macOS:**
```bash
cd bin/Release/publish/linux-x64
./gspro-r10
```

Don't forget to copy `settings.json` to the same directory as the executable, or it will use default settings.
