using InTheHand.Bluetooth;
using LaunchMonitor.Proto;
using gspro_r10.OpenConnect;
using gspro_r10.bluetooth;
using gspro_r10.bluetooth.adapters;
using Microsoft.Extensions.Configuration;

namespace gspro_r10
{
  public class BluetoothConnection : IDisposable
  {
    private bool disposedValue;

    public ConnectionManager ConnectionManager { get; }
    public IConfigurationSection Configuration { get; }
    public int ReconnectInterval { get; }
    public LaunchMonitorDevice? LaunchMonitor { get; private set; }
    public BluetoothDevice? Device { get; private set; }

    public BluetoothConnection(ConnectionManager connectionManager, IConfigurationSection configuration)
    {
      ConnectionManager = connectionManager;
      Configuration = configuration;
      ReconnectInterval = int.Parse(configuration["reconnectInterval"] ?? "5");
      BluetoothLogger.Info("Initializing bluetooth connection task");
      Task.Run(ConnectToDevice);

    }

    private void ConnectToDevice()
    {
      try
      {
        string deviceName = Configuration["bluetoothDeviceName"] ?? "F49DAAD00505";
        string? deviceAddress = Configuration["bluetoothDeviceAddress"];
        BluetoothLogger.Info($"Looking for bluetooth device. Name='{deviceName}', Address='{deviceAddress}'");
        Device = FindDevice(deviceName, deviceAddress);
        if (Device == null)
        {
          BluetoothLogger.Error($"Could not find a paired device matching name '{deviceName}'" + (string.IsNullOrWhiteSpace(deviceAddress) ? string.Empty : $" or address '{deviceAddress}'") + ".");
          BluetoothLogger.Error("Device must be paired through computer bluetooth settings before running");
          BluetoothLogger.Error("If device is paired, make sure name/address matches what is set in 'bluetoothDeviceName'/'bluetoothDeviceAddress' in settings.json");
          LogKnownDevices();
          return;
        }
        BluetoothLogger.Info($"Found device candidate: {Device.Name ?? "<no name>"} ({Device.Id})");

        do
        {
          BluetoothLogger.Info($"Connecting to {Device.Name}: {Device.Id}");
          Device.Gatt.ConnectAsync().Wait();

          if (!Device.Gatt.IsConnected)
          {
            BluetoothLogger.Info($"Could not connect to bluetooth device. Waiting {ReconnectInterval} seconds before trying again");
            Thread.Sleep(TimeSpan.FromSeconds(ReconnectInterval));
          }
        }
        while (!Device.Gatt.IsConnected);

        Device.Gatt.AutoConnect = true;

        BluetoothLogger.Info($"Successfully connected to bluetooth device {Device.Name ?? "<no name>"} ({Device.Id})");
        BluetoothLogger.Info($"Connected to Launch Monitor");
        LaunchMonitor = SetupLaunchMonitor(Device);
        Device.GattServerDisconnected += OnDeviceDisconnected;
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Unhandled exception during bluetooth connect: {ex}");
      }
    }

    private void OnDeviceDisconnected(object? sender, EventArgs args)
    {
      BluetoothLogger.Error("Lost bluetooth connection");
      if (Device != null)
        Device.GattServerDisconnected -= OnDeviceDisconnected;
      LaunchMonitor?.Dispose();

      Task.Run(ConnectToDevice);
    }

    private LaunchMonitorDevice? SetupLaunchMonitor(BluetoothDevice device)
    {
      LaunchMonitorDevice lm = new LaunchMonitorDevice(new InTheHandBluetoothDeviceAdapter(device));
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
        lm.ReadinessChanged += (o, e) =>
        {
          ConnectionManager.SendLaunchMonitorReadyUpdate(e.Ready);
        };
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

    private BluetoothDevice? FindDevice(string deviceName, string? deviceAddress)
    {
      if (!string.IsNullOrWhiteSpace(deviceAddress))
      {
        try
        {
          BluetoothLogger.Info($"Attempting direct lookup by address '{deviceAddress}'");
          BluetoothDevice? direct = BluetoothDevice.FromIdAsync(deviceAddress).Result;
          if (direct != null)
            return direct;
        }
        catch (AggregateException ex) when (ex.InnerException is PlatformNotSupportedException)
        {
          BluetoothLogger.Error("Direct address lookup not supported on this platform.");
        }
        catch (PlatformNotSupportedException)
        {
          BluetoothLogger.Error("Direct address lookup not supported on this platform.");
        }
        catch (Exception ex)
        {
          BluetoothLogger.Error($"Direct address lookup failed: {ex.Message}");
        }
      }

      try
      {
        foreach (BluetoothDevice pairedDev in Bluetooth.GetPairedDevicesAsync().Result)
        {
          bool matchesName = !string.IsNullOrWhiteSpace(pairedDev.Name) && pairedDev.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase);
          bool matchesAddress = !string.IsNullOrWhiteSpace(deviceAddress) &&
                                (pairedDev.Id.Equals(deviceAddress, StringComparison.OrdinalIgnoreCase) ||
                                 (!string.IsNullOrWhiteSpace(pairedDev.Name) && pairedDev.Name.Equals(deviceAddress, StringComparison.OrdinalIgnoreCase)));

          if (matchesName || matchesAddress)
            return pairedDev;
        }
      }
      catch (AggregateException ex) when (ex.InnerException is PlatformNotSupportedException)
      {
        BluetoothLogger.Error("Enumerating paired devices is not supported on this platform. Provide 'bluetoothDeviceAddress' in settings.json.");
      }
      catch (PlatformNotSupportedException)
      {
        BluetoothLogger.Error("Enumerating paired devices is not supported on this platform. Provide 'bluetoothDeviceAddress' in settings.json.");
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Error while enumerating paired devices: {ex.Message}");
      }
      return null;
    }

    private void LogKnownDevices()
    {
      try
      {
        BluetoothLogger.Info("Paired bluetooth devices:");
        foreach (BluetoothDevice pairedDev in Bluetooth.GetPairedDevicesAsync().Result)
        {
          BluetoothLogger.Info($"  {pairedDev.Name ?? "<no name>"} ({pairedDev.Id})");
        }
      }
      catch (AggregateException ex) when (ex.InnerException is PlatformNotSupportedException)
      {
        BluetoothLogger.Error("Cannot list paired devices on this platform. Use 'bluetoothDeviceAddress' in settings.json to connect directly.");
      }
      catch (PlatformNotSupportedException)
      {
        BluetoothLogger.Error("Cannot list paired devices on this platform. Use 'bluetoothDeviceAddress' in settings.json to connect directly.");
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Unable to list paired devices: {ex.Message}");
      }
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          if (Device != null)
            Device.GattServerDisconnected -= OnDeviceDisconnected;
          LaunchMonitor?.Dispose();
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

  public static class BluetoothLogger
  {
    public static void Info(string message) => LogBluetoothMessage(message, LogMessageType.Informational);
    public static void Error(string message) => LogBluetoothMessage(message, LogMessageType.Error);
    public static void Outgoing(string message) => LogBluetoothMessage(message, LogMessageType.Outgoing);
    public static void Incoming(string message) => LogBluetoothMessage(message, LogMessageType.Incoming);
    public static void LogBluetoothMessage(string message, LogMessageType type) => BaseLogger.LogMessage(message, "R10-BT", type, ConsoleColor.Magenta);
  }
}
