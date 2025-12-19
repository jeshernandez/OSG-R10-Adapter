# Linux BLE Fragmentation Issue - COBS Decoder Failure

## Summary
Tilt calibration and protobuf message reception (B313, B413, BA13, A013) fail on Linux due to improper handling of BLE ATT fragmentation in the `DEVICE_INTERFACE_NOTIFIER` characteristic. **Shot measurement data is NOT affected** - it uses a separate characteristic with working binary parsing.

## Affected Components
- ✅ **WORKING**: Shot measurement data (MEASUREMENT_CHARACTERISTIC_UUID `6A4E3401`)
- ❌ **BROKEN**: Protobuf messages via DEVICE_INTERFACE_NOTIFIER (`6A4E2812`)
  - Tilt calibration responses
  - Status notifications (B313)
  - Device info responses (A013)
  - Wake/sleep acknowledgments

## Root Cause Analysis

### 1. Platform Difference: Windows vs Linux BLE Libraries
**Windows** (InTheHand.Bluetooth):
- Automatically reassembles fragmented BLE ATT notifications
- Application receives complete COBS-encoded messages
- Existing `ReaderThread` code works correctly

**Linux** (Linux.Bluetooth/BlueZ):
- Delivers raw BLE ATT fragments with `19 XX` framing headers
- Application receives 3 separate fragment packets per message
- Fragment reassembly must be implemented manually

### 2. Fragment Structure Observed
Messages arrive as 3 fragments with `19 XX` framing:

**Fragment 1** (`19 00`):
```
19 00 02 2E 04 A0 13 96 09 26 0E BE 36 CB D0 AE 01 1B 02 0C
^^ ^^ ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
|  |  18 bytes of COBS-encoded data
|  Fragment type 0x00 (start marker)
BLE ATT fragment header (25 bytes max)
```

**Fragment 2** (`19 41`):
```
19 41 70 70 72 6F 61 63 68 20 52 31 30 0B 41 70 70 72 6F 61
^^ ^^ ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
|  |  ASCII text: "Approach R10..." (device info spam)
|  Fragment type 0x41
BLE ATT fragment header
```

**Fragment 3** (`19 63`):
```
19 63 68 52 31 30 01 01 03 62 94 00
^^ ^^ ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
|  |  ASCII text: "chR10..." + trailing 0x00 end marker
|  Fragment type 0x63
BLE ATT fragment header
```

### 3. COBS Decoder Failure
The existing code concatenates all three fragments after stripping the `19` header:
```
Concatenated: 00 02 2E 04... + 41 70 70... + 63 68 52... (66 bytes)
Result: Mix of COBS data + ASCII text
COBS.Decode() fails: Returns empty byte array
```

**Detailed COBS failure trace** (18 bytes from fragment 1 only):
```
Input: 02 2E 04 A0 13 96 09 26 0E BE 36 CB D0 AE 01 1B 02 0C

Position 0:  distance=0x02 (2) → decode 1 byte: 0x2E → add 0x00
Position 2:  distance=0x04 (4) → decode 3 bytes: 0xA0 0x13 0x96 → add 0x00
Position 6:  distance=0x09 (9) → decode 8 bytes → add 0x00
Position 15: distance=0x1B (27) → FAIL! Need 27 bytes but only 3 remain
             18 < 15 + 27 → Return empty array
```

The 18-byte fragment is **incomplete COBS data** - it needs more bytes that aren't in the ASCII fragments.

### 4. Attempted Fragment Reassembly
Implemented fragment reassembly logic in `LinuxBaseDevice.cs` (lines 203-323):
- Detects `0x19` BLE fragment header
- Strips fragment type byte (second byte)
- Attempts to accumulate only type `0x00` fragments (skipping `0x41`, `0x63` device info)
- Adds trailing `0x00` end marker when detected

**Result**: Successfully reassembles 19 bytes (18 + end marker), but COBS still fails because the 18 bytes are incomplete COBS data.

### 5. Duplicate Event Handler Issue
BLE notifications fire **twice** for each packet (visible in logs):
```
15:12:05.081  DEBUG ||    BLE Fragment: type=0x00, payload=18 bytes
15:12:05.081  DEBUG ||    Fragment START (0x00) - clearing fragment buffer
15:12:05.081  DEBUG ||    WARNING: Fragment 0x00 but buffer not empty (18 bytes)! Duplicate packet?
```

This suggests the event handler is registered multiple times or BlueZ delivers duplicate notifications.

## Technical Details

### File Locations
- **Fragment handling**: `src/bluetooth/device/LinuxBaseDevice.cs` lines 203-323 (ReaderThread)
- **COBS decoder**: `src/util/Cobs.cs` lines 31-55
- **Shot data parser**: `src/bluetooth/device/RawMeasurementParser.cs` (UNAFFECTED)
- **Measurement subscription**: `src/bluetooth/device/LinuxLaunchMonitorDevice.cs` lines 108-132

### Characteristics
- `DEVICE_INTERFACE_NOTIFIER`: `6A4E2812-667B-11E3-949A-0800200C9A66` (BROKEN - fragmentation issue)
- `MEASUREMENT_CHARACTERISTIC`: `6A4E3401-667B-11E3-949A-0800200C9A66` (WORKING - shot data)
- `CONTROL_POINT_CHARACTERISTIC`: `6A4E3402-667B-11E3-949A-0800200C9A66` (Not receiving B413 on Linux)
- `STATUS_CHARACTERISTIC`: `6A4E3403-667B-11E3-949A-0800200C9A66` (Receives basic status)

### Debug Output Example
```
15:12:05.081  DEBUG ||    BLE Fragment: type=0x00, payload=18 bytes
15:12:05.081  DEBUG ||    Fragment START (0x00) - clearing fragment buffer
15:12:05.081  DEBUG ||    Accumulated 18 bytes -> fragment buffer now 18 bytes
15:12:05.083  DEBUG ||    BLE Fragment: type=0x41, payload=18 bytes
15:12:05.083  DEBUG ||    SKIP suspected device info fragment (type=0x41)
15:12:05.084  DEBUG ||    BLE Fragment: type=0x63, payload=10 bytes
15:12:05.084  DEBUG ||    SKIP suspected device info fragment (type=0x63)
15:12:05.084  DEBUG ||    Fragment END marker (trailing 0x00) - finalizing message
15:12:05.084  DEBUG ||    Reassembled message: 19 bytes total
15:12:05.084  DEBUG ||   DECODING: buffer has 18 bytes
15:12:05.084  DEBUG ||   -> 022E04A0139609260EBE36CBD0AE011B020C         (encoded)
15:12:05.084  DEBUG || ->                                                (decoded)
```

## Impact Assessment

### ✅ WORKING (No Impact)
**Shot Measurement Data** - Core functionality is completely unaffected:
- Uses `MEASUREMENT_CHARACTERISTIC` (`6A4E3401`) - different BLE characteristic
- `RawMeasurementParser.ProcessPacket()` - binary format, not COBS/protobuf
- Parses 9 int16 values directly from raw bytes
- Has its own packet fragmentation logic (1-2 packet sequences)
- **Ball speed, club speed, spin, launch angle, etc. all work correctly**

Evidence from `RawMeasurementParser.cs`:
```csharp
/// Parses raw binary measurement data from the R10's measurement characteristic (6a4e3401).
/// This is used on Linux where protobuf B313 notifications don't work.
```

### ❌ BROKEN (Affected by Fragmentation Issue)
**Protobuf Messages via DEVICE_INTERFACE_NOTIFIER**:
1. **Tilt Calibration** - No calibration data received
   - `GetDeviceTilt()` times out waiting for B413 response
   - `DeviceTilt` remains null/empty

2. **Status Notifications** - B313 messages not decoded
   - Device state changes not properly tracked
   - Wake/sleep state updates lost

3. **Device Info** - A013 messages not decoded
   - Device information responses fail

4. **Control Responses** - B413 protobuf responses
   - Request/response pattern broken on Linux
   - Tilt, status, config requests receive no decoded response

### Workarounds Currently in Place
From `LinuxLaunchMonitorDevice.cs` line 143-144:
```csharp
// Control point responses (protobuf B413 responses don't work on Linux/BlueZ)
// All necessary data comes through the measurement characteristic instead
```

The code already acknowledges B413 doesn't work on Linux and relies on the measurement characteristic for essential shot data.

## Reproduction Steps

1. **Environment**: Linux with BlueZ, settings.json configured with `"platform": "linux"`
2. **Connect**: Device connects successfully, reads battery/firmware
3. **Run**: `dotnet run` with `"debugLogging": true`
4. **Observe**: Look for COBS decode failures in logs:
   ```
   DEBUG ||   -> 022E04A0139609260EBE36CBD0AE011B020C (encoded)
   DEBUG || ->                                        (decoded)
   ```
5. **Check**: `GetDeviceTilt: No valid response! resp=null`
6. **Verify**: `Tilt:` field is empty in Device Setup Complete

## Proposed Solutions

### Option 1: Complete BLE ATT Reassembly (Most Correct)
Implement proper BLE ATT fragment reassembly:
- Understand the true meaning of fragment types (`0x00`, `0x41`, `0x63`)
- Determine if they're sequence numbers, packet types, or something else
- Properly reassemble all fragments into complete COBS message
- Requires understanding R10's custom BLE fragmentation protocol

**Complexity**: High - requires reverse engineering R10's BLE protocol

### Option 2: Use Linux.Bluetooth Configuration
Check if Linux.Bluetooth library has options to:
- Enable automatic fragment reassembly
- Set ATT MTU to avoid fragmentation
- Handle fragmented notifications transparently

**Complexity**: Medium - requires library investigation

### Option 3: Alternative Message Path (Quick Fix)
Investigate if protobuf messages can be received through other characteristics:
- `CONTROL_POINT_CHARACTERISTIC` (`6A4E3402`)
- `STATUS_CHARACTERISTIC` (`6A4E3403`)
- Request/response pattern modifications

**Complexity**: Medium - may not be possible

### Option 4: Accept Limitation (Current State)
Document that Linux implementation:
- ✅ Full shot measurement accuracy (core feature works)
- ❌ No tilt calibration data
- ❌ Limited device status tracking
- Works well enough for primary use case (hitting balls)

**Complexity**: Low - just documentation

## Additional Notes

### Windows vs Linux Code Paths
The codebase has **identical** `ReaderThread` logic in:
- `src/bluetooth/device/BaseDevice.cs` (Windows)
- `src/bluetooth/device/LinuxBaseDevice.cs` (Linux - before our changes)

This confirms Windows BLE library handles fragmentation transparently, while Linux does not.

### Event Handler Duplication
The duplicate notification issue should be investigated:
```csharp
deviceInterfaceNotifier.Value += (o, e) => { ... }
```
May be subscribed multiple times or BlueZ sends notifications twice.

### COBS Encoding Validity
The 18-byte fragment starting with `02 2E 04 A0...` appears to be valid COBS data, but is **incomplete**. The distance marker `0x1B` (27) at position 15 requires more data that's missing.

This suggests:
1. The complete COBS message is longer than 18 bytes
2. Fragment types `0x41` and `0x63` may contain continuation data (not just device info)
3. The ASCII "Approach R10" text may be coincidental overlapping data

## References
- Linux.Bluetooth library: https://github.com/dotnet/iot
- InTheHand.Bluetooth library: https://github.com/inthehand/32feet
- BLE ATT Protocol specification
- COBS encoding: https://en.wikipedia.org/wiki/Consistent_Overhead_Byte_Stuffing

## Environment
- **OS**: Linux (kernel version from logs: 6.17.0-8-generic)
- **Device**: Garmin Approach R10 (Firmware 4.30)
- **Bluetooth Stack**: BlueZ (via Linux.Bluetooth library)
- **.NET**: 9.0
- **Settings**: `"platform": "linux"`, `"debugLogging": true`, `"calibrateTiltOnConnect": true`
