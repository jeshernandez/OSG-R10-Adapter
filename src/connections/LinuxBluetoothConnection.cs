#if !WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using gspro_r10.bluetooth;
using gspro_r10.bluetooth.adapters;
using gspro_r10.OpenConnect;
using LaunchMonitor.Proto;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Configuration;

namespace gspro_r10
{
  public class LinuxBluetoothConnection : IDisposable
  {
    private bool disposedValue;
    private Adapter? adapter;
    private Device? device;
    private LaunchMonitorDevice? launchMonitor;
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

    public ConnectionManager ConnectionManager { get; }
    public IConfigurationSection Configuration { get; }
    public int ReconnectInterval { get; }

    public LinuxBluetoothConnection(ConnectionManager connectionManager, IConfigurationSection configuration)
    {
      ConnectionManager = connectionManager;
      Configuration = configuration;
      ReconnectInterval = int.Parse(configuration["reconnectInterval"] ?? "20");

      BluetoothLogger.Info("Initializing Linux Bluetooth provider (BlueZ)");
      Task.Run(async () => await ConnectToDeviceAsync(), cancellation.Token);
    }

    private async Task ConnectToDeviceAsync()
    {
      string deviceName = Configuration["bluetoothDeviceName"] ?? "Approach R10";
      string? deviceAddress = Configuration["bluetoothDeviceAddress"];

      while (!cancellation.IsCancellationRequested)
      {
        try
        {
          adapter = await GetAdapterAsync();
          if (adapter == null)
          {
            BluetoothLogger.Error("No bluetooth adapter detected. Ensure bluetoothd is running.");
            await Task.Delay(TimeSpan.FromSeconds(ReconnectInterval), cancellation.Token);
            continue;
          }

          BluetoothLogger.Info($"Looking for bluetooth device. Name='{deviceName}', Address='{deviceAddress}'");
          device = await FindDeviceAsync(adapter, deviceName, deviceAddress);
          if (device == null)
          {
            BluetoothLogger.Error($"Could not find a device matching name '{deviceName}'" + (string.IsNullOrWhiteSpace(deviceAddress) ? string.Empty : $" or address '{deviceAddress}'") + ".");
            await LogKnownDevicesAsync(adapter);
            await Task.Delay(TimeSpan.FromSeconds(ReconnectInterval), cancellation.Token);
            continue;
          }

          device.Disconnected += OnDeviceDisconnectedAsync;

          if (!await ConnectAndSetupAsync(device))
          {
            await Task.Delay(TimeSpan.FromSeconds(ReconnectInterval), cancellation.Token);
            continue;
          }

          return;
        }
        catch (TaskCanceledException)
        {
          return;
        }
        catch (Exception ex)
        {
          BluetoothLogger.Error($"Unhandled exception during bluetooth connect: {ex}");
          await Task.Delay(TimeSpan.FromSeconds(ReconnectInterval), cancellation.Token);
        }
      }
    }

    private async Task<Adapter?> GetAdapterAsync()
    {
      IReadOnlyList<Adapter> adapters = await BlueZManager.GetAdaptersAsync();
      if (adapters.Count == 0)
        return null;
      string? adapterName = Configuration["bluetoothAdapterName"];
      if (!string.IsNullOrWhiteSpace(adapterName))
        return adapters.FirstOrDefault(a => string.Equals(a.ObjectPath.ToString(), adapterName, StringComparison.OrdinalIgnoreCase)) ?? adapters.First();

      return adapters.First();
    }

    private async Task<Device?> FindDeviceAsync(Adapter adapter, string deviceName, string? deviceAddress)
    {
      if (!string.IsNullOrWhiteSpace(deviceAddress))
      {
        string normalizedAddress = NormalizeAddress(deviceAddress);
        Device? direct = await adapter.GetDeviceAsync(FormatAddress(normalizedAddress));
        if (direct != null)
          return direct;
      }

      IReadOnlyList<Device> devices = await adapter.GetDevicesAsync();
      foreach (Device candidate in devices)
      {
        DeviceProperties props = await candidate.GetPropertiesAsync();
        bool nameMatch = !string.IsNullOrWhiteSpace(props.Name) && props.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase);
        nameMatch = nameMatch || (!string.IsNullOrWhiteSpace(props.Alias) && props.Alias.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

        bool addressMatch = !string.IsNullOrWhiteSpace(deviceAddress) &&
          NormalizeAddress(props.Address ?? string.Empty).Equals(NormalizeAddress(deviceAddress), StringComparison.OrdinalIgnoreCase);

        if (nameMatch || addressMatch)
          return candidate;
      }

      return null;
    }

    private static string NormalizeAddress(string address) => address.Replace(":", string.Empty).Replace("-", string.Empty).ToUpperInvariant();

    private static string FormatAddress(string normalized)
    {
      if (string.IsNullOrWhiteSpace(normalized))
        return normalized;
      normalized = normalized.ToUpperInvariant();
      if (normalized.Contains(":"))
        return normalized;
      List<string> chunks = new List<string>();
      for (int i = 0; i < normalized.Length; i += 2)
      {
        int length = Math.Min(2, normalized.Length - i);
        chunks.Add(normalized.Substring(i, length));
      }
      return string.Join(":", chunks);
    }

    private async Task<bool> ConnectAndSetupAsync(Device targetDevice)
    {
      try
      {
        DeviceProperties props = await targetDevice.GetPropertiesAsync();
        BluetoothLogger.Info($"Connecting to {props.Name ?? props.Alias ?? props.Address}: {props.Address}");
        BluetoothLogger.Info($"Device state: Paired={props.Paired}, Trusted={props.Trusted}, ServicesResolved={props.ServicesResolved}");
        if (!props.Paired)
        {
          BluetoothLogger.Error("Device is not paired. Put the R10 in pairing mode and pair it first via `bluetoothctl` (commands: `default-agent`, `scan on`, `pair <MAC>`, `trust <MAC>`).");
          return false;
        }
        await targetDevice.ConnectAsync();
        TimeSpan timeout = TimeSpan.FromSeconds(20);
        await targetDevice.WaitForPropertyValueAsync("Connected", true, timeout);
        await targetDevice.WaitForPropertyValueAsync("ServicesResolved", true, timeout);
        props = await targetDevice.GetPropertiesAsync();
        if (!props.Trusted)
        {
          try
          {
            BluetoothLogger.Info("Marking device as trusted with BlueZ");
            await targetDevice.SetAsync("Trusted", true);
          }
          catch (Exception trustEx)
          {
            BluetoothLogger.Error($"Unable to mark device as trusted: {trustEx.Message}");
          }
        }

        BluetoothLogger.Info($"Successfully connected to bluetooth device {props.Name ?? "<no name>"} ({props.Address})");
        launchMonitor = SetupLaunchMonitor(targetDevice, props);
        return launchMonitor != null;
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Failed to connect/setup device: {ex.Message}");
        await LogAvailableServicesAsync(targetDevice);
        return false;
      }
    }

    private LaunchMonitorDevice? SetupLaunchMonitor(Device targetDevice, DeviceProperties props)
    {
      LaunchMonitorDevice lm = new LaunchMonitorDevice(new BlueZBluetoothDeviceAdapter(targetDevice, props.Address ?? targetDevice.ObjectPath.ToString(), props.Name ?? props.Alias));
      lm.AutoWake = bool.Parse(Configuration["autoWake"] ?? "false");
      lm.CalibrateTiltOnConnect = bool.Parse(Configuration["calibrateTiltOnConnect"] ?? "false");

      lm.DebugLogging = bool.Parse(Configuration["debugLogging"] ?? "false");
      lm.SniffLogging = bool.Parse(Configuration["sniffLogging"] ?? "false");

      lm.MessageRecieved += (o, e) => BluetoothLogger.Incoming(e.Message?.ToString() ?? string.Empty);
      lm.MessageSent += (o, e) => BluetoothLogger.Outgoing(e.Message?.ToString() ?? string.Empty);
      lm.BatteryLifeUpdated += (o, e) => BluetoothLogger.Info($"Battery Life Updated: {e.Battery}%");
      lm.Error += (o, e) => BluetoothLogger.Error($"{e.Severity}: {e.Message}");

      if (bool.Parse(Configuration["sendStatusChangesToGSP"] ?? "false"))
      {
        lm.ReadinessChanged += (o, e) => ConnectionManager.SendLaunchMonitorReadyUpdate(e.Ready);
      }

      lm.ShotMetrics += (o, e) =>
      {
        LaunchMonitorMetricsHelper.LogMetrics(e.Metrics);
        ConnectionManager.SendShot(
          LaunchMonitorMetricsHelper.BallDataFromLaunchMonitorMetrics(e.Metrics?.BallMetrics),
          LaunchMonitorMetricsHelper.ClubDataFromLaunchMonitorMetrics(e.Metrics?.ClubMetrics)
        );
      };

      if (!lm.Setup())
      {
        BluetoothLogger.Error("Failed Device Setup");
        return null;
      }

      float temperature = float.Parse(Configuration["temperature"] ?? "60");
      float humidity = float.Parse(Configuration["humidity"] ?? "1");
      float altitude = float.Parse(Configuration["altitude"] ?? "0");
      float airDensity = float.Parse(Configuration["airDensity"] ?? "1");
      float teeDistanceInFeet = float.Parse(Configuration["teeDistanceInFeet"] ?? "7");
      float teeRange = LaunchMonitorMetricsHelper.CalculateTeeRange(teeDistanceInFeet);

      lm.ShotConfig(temperature, humidity, altitude, airDensity, teeRange);

      BluetoothLogger.Info($"Device Setup Complete: ");
      BluetoothLogger.Info($"   Model: {lm.Model}");
      BluetoothLogger.Info($"   Firmware: {lm.Firmware}");
      BluetoothLogger.Info($"   Bluetooth ID: {lm.DeviceId}");
      BluetoothLogger.Info($"   Battery: {lm.Battery}%");
      BluetoothLogger.Info($"   Current State: {lm.CurrentState}");
      BluetoothLogger.Info($"   Tilt: {lm.DeviceTilt}");

      return lm;
    }

    private Task OnDeviceDisconnectedAsync(Device sender, BlueZEventArgs args)
    {
      BluetoothLogger.Error("Lost bluetooth connection");
      launchMonitor?.Dispose();
      launchMonitor = null;
      Task.Run(async () => await ConnectToDeviceAsync());
      return Task.CompletedTask;
    }

    private async Task LogKnownDevicesAsync(Adapter adapter)
    {
      try
      {
        IReadOnlyList<Device> devices = await adapter.GetDevicesAsync();
        BluetoothLogger.Info("Known bluetooth devices:");
        foreach (Device dev in devices)
        {
          DeviceProperties props = await dev.GetPropertiesAsync();
          BluetoothLogger.Info($"  {props.Name ?? props.Alias ?? "<no name>"} ({props.Address})");
        }
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Unable to list devices: {ex.Message}");
      }
    }

    private async Task LogAvailableServicesAsync(Device targetDevice)
    {
      try
      {
        DeviceProperties props = await targetDevice.GetPropertiesAsync();
        if (props.UUIDs == null || props.UUIDs.Length == 0)
        {
          BluetoothLogger.Info("Device reported no service UUIDs (likely not paired/trusted).");
          return;
        }
        BluetoothLogger.Info("Device reported GATT services:");
        foreach (string uuid in props.UUIDs)
          BluetoothLogger.Info($"  {uuid}");
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Unable to enumerate services: {ex.Message}");
      }
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          cancellation.Cancel();
      launchMonitor?.Dispose();
      if (device != null)
        device.Disconnected -= OnDeviceDisconnectedAsync;
      device?.Dispose();
          adapter?.Dispose();
        }

        disposedValue = true;
      }
    }

    public void Dispose()
    {
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }
  }
}
#else
using System;
using Microsoft.Extensions.Configuration;

namespace gspro_r10
{
  public class LinuxBluetoothConnection : IDisposable
  {
    public LinuxBluetoothConnection(ConnectionManager connectionManager, IConfigurationSection configuration)
    {
      throw new PlatformNotSupportedException("Linux bluetooth provider is not available on this platform.");
    }

    public void Dispose()
    {
    }
  }
}
#endif
