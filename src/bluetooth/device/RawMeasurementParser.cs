using System;
using System.Collections.Generic;
using System.Linq;
using LaunchMonitor.Proto;

namespace gspro_r10.bluetooth
{
  /// <summary>
  /// Parses raw binary measurement data from the R10's measurement characteristic (6a4e3401).
  /// This is used on Linux where protobuf B313 notifications don't work.
  /// </summary>
  public class RawMeasurementParser
  {
    private readonly Dictionary<uint, ShotPacketBuffer> pendingShots = new Dictionary<uint, ShotPacketBuffer>();

    private class ShotPacketBuffer
    {
      public uint ShotId { get; set; }
      public List<byte[]> Packets { get; set; } = new List<byte[]>();
      public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
      public byte SecondPacketFlags { get; set; } = 0; // May contain ball type/spin calc flags
      public bool IsMarkedBall { get; set; } = false; // True for 0x7E/0x3E packet types
    }

    /// <summary>
    /// Process a raw measurement notification packet.
    /// Returns a Metrics object if a complete shot has been received, otherwise null.
    /// </summary>
    public Metrics? ProcessPacket(byte[] data)
    {
      if (data == null || data.Length < 6)
      {
        return null; // Too short to be valid
      }

      // Clean up old pending shots (older than 10 seconds)
      var oldShots = pendingShots.Where(kvp => (DateTime.UtcNow - kvp.Value.LastUpdate).TotalSeconds > 10).ToList();
      foreach (var kvp in oldShots)
      {
        pendingShots.Remove(kvp.Key);
      }

      byte packetType = data[0];
      byte sequenceOrFlags = data[1];

      // Extract shot ID (bytes 2-5, little-endian uint32)
      uint shotId = BitConverter.ToUInt32(data, 2);

      // DEBUG: Log packet info
      BluetoothLogger.Info($"Raw Parser: Received packet type=0x{packetType:X2}, seq/flags=0x{sequenceOrFlags:X2}, shotId={shotId}, length={data.Length}");

      // Handle different packet types
      if (packetType == 0xFF && sequenceOrFlags == 0x00)
      {
        // First packet of a two-packet shot (regular/conventional balls)
        if (!pendingShots.ContainsKey(shotId))
        {
          pendingShots[shotId] = new ShotPacketBuffer { ShotId = shotId };
        }

        var buffer = pendingShots[shotId];
        buffer.Packets.Clear(); // Reset if we're getting a new first packet
        buffer.Packets.Add(data);
        buffer.LastUpdate = DateTime.UtcNow;
        return null; // Need more packets
      }
      else if (packetType == 0x7E && sequenceOrFlags == 0x00)
      {
        // Single-packet shot (marked balls - type 1)
        var buffer = new ShotPacketBuffer
        {
          ShotId = shotId,
          SecondPacketFlags = 0x0C, // Special flag for marked balls (bits 2-3 = 3 = MEASURED)
          IsMarkedBall = true
        };
        buffer.Packets.Add(data);
        return TryParseCompleteShot(buffer);
      }
      else if (packetType == 0x3E && sequenceOrFlags == 0x00)
      {
        // Single-packet shot (marked balls - type 2)
        var buffer = new ShotPacketBuffer
        {
          ShotId = shotId,
          SecondPacketFlags = 0x0C, // Special flag for marked balls (bits 2-3 = 3 = MEASURED)
          IsMarkedBall = true
        };
        buffer.Packets.Add(data);
        return TryParseCompleteShot(buffer);
      }
      else if (packetType == 0x00)
      {
        // Continuation packet (second packet for regular balls)
        if (!pendingShots.ContainsKey(shotId))
          return null;

        var buffer = pendingShots[shotId];
        buffer.Packets.Add(data);
        buffer.LastUpdate = DateTime.UtcNow;
        buffer.SecondPacketFlags = sequenceOrFlags;

        var metrics = TryParseCompleteShot(buffer);
        if (metrics != null)
          pendingShots.Remove(shotId);

        return metrics;
      }
      else
      {
        BluetoothLogger.Info($"Raw Parser: Unknown packet type: {packetType:X2}-{sequenceOrFlags:X2}");
        return null;
      }
    }

    private Metrics? TryParseCompleteShot(ShotPacketBuffer buffer)
    {
      try
      {
        // Marked balls come in 1 packet, regular balls in 2 packets
        if (buffer.Packets.Count < 1)
        {
          return null; // Need at least 1 packet
        }

        // Combine packet payloads (skip headers)
        var allData = new List<byte>();
        foreach (var packet in buffer.Packets)
        {
          // Skip first 6 bytes (packet type, sequence, shot ID)
          allData.AddRange(packet.Skip(6));
        }

        byte[] combinedData = allData.ToArray();

        // R10 sends different data lengths for different ball types:
        // - Conventional balls: 18 bytes = 9 int16 values
        // - Marked balls (0x7E): 12 bytes in some cases
        // - Marked balls (0x3E): 10 bytes in some cases

        if (combinedData.Length < 10)
        {
          BluetoothLogger.Info($"Raw Parser: Not enough data ({combinedData.Length} bytes) for shot {buffer.ShotId}");
          return null;
        }

        // Pad with zeros if less than 18 bytes (for marked balls)
        if (combinedData.Length < 18)
        {
          var paddedData = new byte[18];
          Array.Copy(combinedData, paddedData, combinedData.Length);
          combinedData = paddedData;
        }

        // DEBUG: Log raw bytes
        BluetoothLogger.Info($"Raw Parser: Shot {buffer.ShotId} - Combined data length: {combinedData.Length} bytes");
        BluetoothLogger.Info($"Raw Parser: Shot {buffer.ShotId} - Combined hex: {BitConverter.ToString(combinedData)}");

        int offset = 0;

        // Read all 9 int16 values first
        ushort val1 = ReadUInt16(combinedData, ref offset);
        short val2 = ReadInt16(combinedData, ref offset); // Club path needs to support negatives
        short val3 = ReadInt16(combinedData, ref offset);
        ushort val4 = ReadUInt16(combinedData, ref offset);
        short val5 = ReadInt16(combinedData, ref offset);
        ushort val6 = ReadUInt16(combinedData, ref offset);
        short val7 = ReadInt16(combinedData, ref offset);
        short val8 = ReadInt16(combinedData, ref offset);
        short val9 = ReadInt16(combinedData, ref offset);

        // DEBUG: Log all raw values before conversion
        BluetoothLogger.Info($"Raw Parser: Shot {buffer.ShotId} - RawValues: val1={val1}, val2={val2}, val3={val3}, val4={val4}, val5={val5}, val6={val6}, val7={val7}, val8={val8}, val9={val9}");
        BluetoothLogger.Info($"Raw Parser: RAW VALUES:");
        BluetoothLogger.Info($"  val1 (BallSpeed)={val1} -> {val1/100.0f:F2}mph");
        BluetoothLogger.Info($"  val2 (ClubPath)={val2} -> {val2/100.0f:F2}°");
        BluetoothLogger.Info($"  val3 (LaunchDir)={val3} -> {-val3/100.0f:F2}° (negated)");
        BluetoothLogger.Info($"  val4 (TotalSpin)={val4} rpm");
        BluetoothLogger.Info($"  val5 (SpinAxis)={val5} -> {val5/100.0f:F2}°");
        BluetoothLogger.Info($"  val6 (ClubSpeed)={val6} -> {val6/100.0f:F2}mph");
        BluetoothLogger.Info($"  val7 (Field7)={val7} -> {val7/100.0f:F2}");
        BluetoothLogger.Info($"  val8 (Field8)={val8} -> {val8/100.0f:F2}");
        BluetoothLogger.Info($"  val9 (ClubFace)={val9} -> {val9/100.0f:F2}°");

        float launchAngle;
        float attackAngle;
        string interpretation;

        if (buffer.IsMarkedBall)
        {
          // Marked ball packets (0x7E, 0x3E) have different field layout:
          // - val1: Ball Speed
          // - val2: VLA (Launch Angle)
          // - val3: HLA (Launch Direction) - negated
          // - val4: Total Spin
          // - val5: Spin Axis
          // - val6-val9: Not present (zeros)
          launchAngle = val2 / 100.0f;
          attackAngle = 0; // Not available for marked balls
          interpretation = "MarkedBall";
          BluetoothLogger.Info($"Raw Parser: Marked ball detected - using val2 as VLA");
        }
        else
        {
          // Conventional ball packets - two candidate interpretations for VLA/AoA
          // AoA sign is flipped based on Windows raw vs protobuf comparison
          bool IsPlausible(float la, float aoa) => la >= -5 && la <= 45 && Math.Abs(aoa) <= 20;

          float launchAngleA = -val7 / 100.0f; // prior mapping
          float attackAngleA = -val8 / 100.0f; // NEGATED
          float launchAngleB = val8 / 100.0f;  // alternate mapping
          float attackAngleB = -val7 / 100.0f; // NEGATED

          bool plausibleA = IsPlausible(launchAngleA, attackAngleA);
          bool plausibleB = IsPlausible(launchAngleB, attackAngleB);

          launchAngle = launchAngleA;
          attackAngle = attackAngleA;
          interpretation = "A";

          if (!plausibleA && plausibleB)
          {
            launchAngle = launchAngleB;
            attackAngle = attackAngleB;
            interpretation = "B";
          }
          else if (plausibleA && plausibleB)
          {
            // If both are plausible, prefer the one with the smaller |attack| (more typical)
            if (Math.Abs(attackAngleB) < Math.Abs(attackAngleA))
            {
              launchAngle = launchAngleB;
              attackAngle = attackAngleB;
              interpretation = "B";
            }
          }
          else if (!plausibleA && !plausibleB)
          {
            // Both look odd; rescale val7/val8 to bring angles into a plausible range.
            float maxAbs = Math.Max(Math.Abs(val7), Math.Abs(val8));
            float scale = Math.Max(100.0f, maxAbs / 20.0f);

            float launchAngleAScaled = -val7 / scale;
            float attackAngleAScaled = -val8 / scale;
            float launchAngleBScaled = val8 / scale;
            float attackAngleBScaled = -val7 / scale;

            bool plausibleAScaled = IsPlausible(launchAngleAScaled, attackAngleAScaled);
            bool plausibleBScaled = IsPlausible(launchAngleBScaled, attackAngleBScaled);

            if (plausibleAScaled || plausibleBScaled)
            {
              if (!plausibleAScaled || (plausibleBScaled && Math.Abs(attackAngleBScaled) < Math.Abs(attackAngleAScaled)))
              {
                launchAngle = launchAngleBScaled;
                attackAngle = attackAngleBScaled;
                interpretation = $"B (scaled {scale:F1})";
              }
              else
              {
                launchAngle = launchAngleAScaled;
                attackAngle = attackAngleAScaled;
                interpretation = $"A (scaled {scale:F1})";
              }
            }
            else
            {
              // Last resort: keep the smaller launch angle and zero AoA to avoid extreme apex.
              launchAngle = Math.Abs(launchAngleA) <= Math.Abs(launchAngleB) ? launchAngleA : launchAngleB;
              if (launchAngle < -5) launchAngle = -5;
              if (launchAngle > 45) launchAngle = 45;
              attackAngle = 0;
              interpretation = "fallback";
            }
          }
        }

        // Log parsed values
        BluetoothLogger.Info($"Raw Parser: Ball={val1/100.0f:F1}mph, Club={val6/100.0f:F1}mph, LA={launchAngle:F1}°, LD={-val3/100.0f:F1}°, Spin={val4}rpm, AoA={attackAngle:F1}° (interp {interpretation})");

        var metrics = new Metrics
        {
          ShotId = buffer.ShotId,
          ShotType = Metrics.Types.ShotType.Normal,
          BallMetrics = new BallMetrics(),
          ClubMetrics = new ClubMetrics()
        };

        // Dynamic interpretation (A/B) based on plausibility:
        // - speeds in mph * 100 (convert to m/s)
        // - val7/val8 swapped and/or sign-flipped depending on the selected interpretation
        // - val2 must be signed to allow negative club path
        // - spin axis uses raw sign (GSPro conversion flips later)
        const float MPH_TO_MS = 0.44704f;

        metrics.BallMetrics.BallSpeed = (val1 / 100.0f) * MPH_TO_MS; // mph to m/s
        metrics.BallMetrics.LaunchAngle = launchAngle;
        metrics.BallMetrics.LaunchDirection = -val3 / 100.0f; // NEGATED - binary uses opposite sign
        metrics.BallMetrics.TotalSpin = val4;
        metrics.BallMetrics.SpinAxis = val5 / 100.0f; // Keep raw sign; conversion to GSPro flips later

        metrics.ClubMetrics.ClubHeadSpeed = (val6 / 100.0f) * MPH_TO_MS; // mph to m/s
        metrics.ClubMetrics.AttackAngle = attackAngle;
        // For marked balls, val2 is VLA not ClubPath; club data not available
        metrics.ClubMetrics.ClubAnglePath = buffer.IsMarkedBall ? 0 : val2 / 100.0f;
        metrics.ClubMetrics.ClubAngleFace = val9 / 100.0f;

        // Decode ball type and spin calculation from flags
        // Packet type determines ball type:
        // - 0xFF (two packets) = Conventional ball
        // - 0x7E/0x3E (one packet) = Marked ball
        //
        // For regular balls, flags byte 0x03 encodes spin calc in bits 2-3:
        // - Bits 2-3: Spin calculation type (0=ratio, 1=ball_flight, 2=other, 3=measured)
        //
        // For marked balls, we set flags to 0x0C (bits 2-3 = 3 = MEASURED)
        byte flags = buffer.SecondPacketFlags;
        int spinCalcRaw = (flags >> 2) & 0x03; // Bits 2-3

        // Ball type: Marked for 0x7E/0x3E packets, Conventional for 0xFF packets
        var ballType = buffer.IsMarkedBall
          ? BallMetrics.Types.GolfBallType.Marked
          : BallMetrics.Types.GolfBallType.Conventional;

        // Map spin calc type (bits 2-3)
        var spinCalc = spinCalcRaw switch
        {
          0 => BallMetrics.Types.SpinCalculationType.Ratio,
          1 => BallMetrics.Types.SpinCalculationType.BallFlight,
          2 => BallMetrics.Types.SpinCalculationType.Other,
          3 => BallMetrics.Types.SpinCalculationType.Measured,
          _ => BallMetrics.Types.SpinCalculationType.Ratio
        };

        metrics.BallMetrics.GolfBallType = ballType;
        metrics.BallMetrics.SpinCalculationType = spinCalc;

        // Validate that we got reasonable values (basic sanity check)
        if (metrics.BallMetrics.BallSpeed > 10 && metrics.BallMetrics.BallSpeed < 150) // 10-150 m/s is reasonable
        {
          return metrics;
        }
        else
        {
          BluetoothLogger.Info($"Raw Parser: Ball speed {metrics.BallMetrics.BallSpeed} m/s out of range, data may be corrupted or format incorrect");
          return null;
        }
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Raw Parser: Error parsing shot {buffer.ShotId}: {ex.Message}");
        return null;
      }
    }

    private float ReadFloat(byte[] data, ref int offset)
    {
      if (offset + 4 > data.Length)
      {
        throw new ArgumentException("Not enough data to read float");
      }

      float value = BitConverter.ToSingle(data, offset);
      offset += 4;
      return value;
    }

    private ushort ReadUInt16(byte[] data, ref int offset)
    {
      if (offset + 2 > data.Length)
      {
        throw new ArgumentException("Not enough data to read uint16");
      }

      ushort value = BitConverter.ToUInt16(data, offset);
      offset += 2;
      return value;
    }

    private short ReadInt16(byte[] data, ref int offset)
    {
      if (offset + 2 > data.Length)
      {
        throw new ArgumentException("Not enough data to read int16");
      }

      short value = BitConverter.ToInt16(data, offset);
      offset += 2;
      return value;
    }
  }
}
