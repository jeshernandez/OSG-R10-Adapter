# OSG-R10-Adapter

Utility to bridge R10 launch monitor to GSPro and OpenShotGolf (OSG). Supports the following
  - Existing support from https://github.com/mholow/gsp-r10-adapter which this project is forked from. 
  - This specific project version enables Linux support for Garmin R10, and connectivity to Open Shot Golf (OSG). An open source simulator alternative to GSPro. 
  - Biggest difference is Linux uses raw output, whereas Windows used protobuf measurement data. There may be a slight difference in measurement between the two systems. 
    - One item is ball types are not identifiable on Linux side (that I know of, yet). 

## Table of Contents
- [Dual Simulator Support](#dual-simulator-support)
- [Using Direct Bluetooth Connector](#using-direct-bluetooth-connector)
  - [Windows Bluetooth Setup](#windows-bluetooth-setup)
  - [Linux Bluetooth Support](#linux-bluetooth-support)
- [Using the Putting Integration](#using-the-putting-integration)
- [Building from Source](#building-from-source)
  - [Quick Build](#quick-build)
  - [Build Script Options](#build-script-options)
- [Running the Application](#running)
- [Sample Output](#sample-output)


![Screenshot](assets/images/screenshot.png)

## Dual Simulator Support

The adapter now supports broadcasting shot data to **multiple golf simulators simultaneously**! This allows you to compare how different simulator physics engines interpret the same launch monitor data from your Garmin R10.

![Both Simulators Running](assets/images/both_sims.png)

### How It Works

When you hit a shot, the adapter:
1. Receives shot data from your R10 via Bluetooth
2. Converts it to the OpenConnect API format
3. **Broadcasts the same data to all configured simulators simultaneously**

Both GSPro and Open Shot Golf (or any OpenConnect-compatible simulator) receive identical launch data and run their own physics calculations independently.

### Configuration

Enable dual simulator support in your `settings.json`:

```json
{
  "openConnect": {
    "ip": "127.0.0.1",
    "port": 921         // GSPro (default)
  },
  "secondaryOpenConnect": {
    "enabled": true,    // Set to true to enable dual broadcasting
    "ip": "127.0.0.1",
    "port": 49152       // Open Shot Golf
  }
}
```

**Port Reference:**
- **GSPro**: Port `921`
- **Open Shot Golf (OSG)**: Port `49152`

**Requirements:**
- Both simulator applications must be running
- Both must be listening on their respective ports
- The adapter will automatically broadcast to all enabled connections

This feature is perfect for:
- Comparing simulator accuracy
- Testing different physics engines
- Running practice sessions on multiple platforms
- Validating shot data consistency

## Using Direct Bluetooth Connector

### Windows Bluetooth Setup

To use the direct bluetooth connection on Windows:
- Enable bluetooth in `settings.json` file
- Set the `bluetooth.platform` option to `"windows"` (default)
- Edit `settings.json` to reflect your desired altitude, tee distance, temperature, etc.
- Set device in pairing mode (blue blinking light) by holding power button for few seconds
- **Pair the R10 from the Windows bluetooth settings**
  - On Windows 11 you may need to set "Bluetooth Device Discovery" to `advanced`
  - This step only needs to be done once
  - You may need to disable bluetooth on previously paired devices to prevent them from stealing the connection

### Linux Bluetooth Support

To use the direct bluetooth connection on Linux:
- Enable bluetooth in `settings.json` file
- Set the `bluetooth.platform` option to `"linux"`
- **Set the `bluetoothDeviceAddress` to your R10's MAC address** (e.g., `"**:**:AA:D0:**:**"`)
  - Find your R10's MAC address using `bluetoothctl` or similar tool
  - Set device in pairing mode (blue blinking light) by holding power button for few seconds
  - Scan for devices: `bluetoothctl scan on`
  - Look for "Approach R10" in the scan results
- Edit `settings.json` to reflect your desired altitude, tee distance, temperature, etc.
- The R10 must be paired with your Linux system before first use:
  ```bash
  bluetoothctl
  scan on
  # Wait for "Approach R10" to appear
  pair <MAC_ADDRESS>
  trust <MAC_ADDRESS>
  ```

**Technical Note**: The Linux implementation uses BlueZ and parses raw binary measurement data from the R10, as BlueZ does not receive the same protobuf notifications that Windows gets. Shot metrics are extracted directly from the binary packets sent by the device.


## Open Shot Golf (OSG) and Garmin R10 
This project was created to enable users to hit golf shots from Linux and use OSG. Most users today are not able to use Garmin R10 because its only supported on Windows. Usually software like GSPro, E6, etc. Now OSG exists! 

![Both Systems](/assets/images/osg_and_r10.png)

## Using the putting integration

In order to use the putting integration you must
- Enable putting in `settings.json` file
- Download ball_tracking software from https://github.com/alleexx/cam-putting-py/releases
  - If you want this program to manage opening/closing of putting camera, place ball_tracking in same folder as this program
- Read https://github.com/alleexx/cam-putting-py for webcam setup/troubleshooting
- Read putting section `settings.json` file to determine optimal settings for your setup

  - Webcam putting integration with https://github.com/alleexx/cam-putting-py


## Building from Source

The project includes automated build scripts for both Windows and Linux/macOS that create self-contained executables.

### Quick Build

**Windows (PowerShell):**
```powershell
.\assets\scripts\build.ps1
```

**Linux/macOS (Bash):**
```bash
./assets/scripts/build.sh
```

These commands will create a self-contained executable in `bin/Release/publish/<runtime>/` that includes the .NET runtime and can run on systems without .NET installed.

### Build Script Options

**Windows Options:**
```powershell
# Build for Windows x64 (default)
.\assets\scripts\build.ps1

# Build for Windows ARM64
.\assets\scripts\build.ps1 -Runtime win-arm64

# Debug build
.\assets\scripts\build.ps1 -Configuration Debug

# Cross-compile for Linux
.\assets\scripts\build.ps1 -Runtime linux-x64
```

**Linux/macOS Options:**
```bash
# Build for Linux x64 (default)
./assets/scripts/build.sh

# Build for Raspberry Pi (ARM64)
./assets/scripts/build.sh --runtime linux-arm64

# Debug build
./assets/scripts/build.sh --configuration Debug

# See all options
./assets/scripts/build.sh --help
```

**Available Runtime Identifiers:**
- `win-x64` - Windows 64-bit (Intel/AMD)
- `win-arm64` - Windows ARM64
- `linux-x64` - Linux 64-bit (Intel/AMD)
- `linux-arm64` - Linux ARM64 (Raspberry Pi, etc.)

**Output Location:**
- Windows: `bin\Release\publish\win-x64\gspro-r10.exe`
- Linux: `bin/Release/publish/linux-x64/gspro-r10`

The `settings.json` file is automatically copied to the output directory.

For detailed build documentation, see [assets/scripts/BUILD.md](assets/scripts/BUILD.md).

## Running

### From release

- Download either the standalone or net6 package from the release page. Extract zip to your local machine and run the exe file.
  - Use the standalone package if you are unsure whether your computer has a dotnet runtime installed
  - Use the net6 package if you believe your computer has a dotnet runtime installed.

### From Source

**Option 1: Build and Run (Recommended)**

Use the build scripts to create a standalone executable:
- See [Building from Source](#building-from-source) section above

**Option 2: Direct Execution with dotnet**

- Install a dotnet 9 SDK if you don't have one already
- Run `dotnet run` from the project directory
- You can force the simulator target with `dotnet run -- --sim gspro` or `dotnet run -- --sim osg` (the `--` separates app args from dotnet)
- Without the flag, the simulator is inferred from `openConnect.port` in `settings.json` (`49152` => OpenShotGolf, `921` => GSPro)

## Sample Output

When a shot is detected, the application displays detailed ball and club metrics:

```
===== Shot 7122957 =====
Ball Metrics                            │ Club Metrics
────────────────────────────────────────┼─────────────────────────
 BallSpeed:      78.63 mph              │ Club Speed:  65.36 mph
 VLA:            39.94°                 │ Club Path:   52.31°
 HLA:            -7.81°                 │ Club Face:   4.44°
 Spin Axis:      9.71°                  │ Attack Angle: 3.42°
 Total Spin:     3661 rpm               │
 Ball Type:      Unknown                │
 Spin Calc:      Ratio                  │
────────────────────────────────────────┴─────────────────────────
```

**Metrics Explained:**
- **BallSpeed**: Initial velocity of the ball in mph
- **VLA** (Vertical Launch Angle): Upward angle of ball trajectory in degrees
- **HLA** (Horizontal Launch Angle): Left/right direction (negative = left, positive = right)
- **Spin Axis**: Tilt angle of the ball's spin axis in degrees
- **Total Spin**: Ball rotation speed in revolutions per minute
- **Ball Type**: Type of golf ball (Unknown when type cannot be determined)
- **Spin Calc**: Method used to calculate spin (Ratio, Measured, BallFlight, or Other)
- **Club Speed**: Speed of the club head at impact in mph
- **Club Path**: Direction of club movement through impact zone in degrees
- **Club Face**: Angle of club face relative to target line in degrees
- **Attack Angle**: Upward or downward angle of club head at impact in degrees
