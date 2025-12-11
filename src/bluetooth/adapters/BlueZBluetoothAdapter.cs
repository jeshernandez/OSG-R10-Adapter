using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Configuration;
using gspro_r10;
using gspro_r10.bluetooth;
using LaunchMonitor.Proto;

namespace gspro_r10.bluetooth.adapters
{
  public class BlueZBluetoothAdapter
  {
    private static readonly string BatteryServiceUuid = BaseDevice.BATTERY_SERVICE_UUID.ToString().ToLowerInvariant();
    private static readonly string BatteryLevelCharUuid = BaseDevice.BATTERY_CHARACTERISTIC_UUID.ToString().ToLowerInvariant();
    private static readonly string DeviceInfoServiceUuid = BaseDevice.DEVICE_INFO_SERVICE_UUID.ToString().ToLowerInvariant();

    private static readonly (string Uuid, string Label)[] DeviceInfoCharacteristics = new (string, string)[]
    {
      ("00002a29-0000-1000-8000-00805f9b34fb", "Manufacturer Name"),
      (BaseDevice.MODEL_CHARACTERISTIC_UUID.ToString().ToLowerInvariant(), "Model Number"),
      (BaseDevice.SERIAL_NUMBER_CHARACTERISTIC_UUID.ToString().ToLowerInvariant(), "Serial Number"),
      (BaseDevice.FIRMWARE_CHARACTERISTIC_UUID.ToString().ToLowerInvariant(), "Firmware Revision")
    };

    private readonly IConfigurationSection configuration;
    private readonly bool debugLogging;
    private readonly ConnectionManager connectionManager;
    private LinuxLaunchMonitorDevice? launchMonitor;

    public BlueZBluetoothAdapter(BluetoothConnection owner, IConfigurationSection configurationSection)
    {
      connectionManager = owner.ConnectionManager;
      configuration = configurationSection;
      debugLogging = bool.Parse(configurationSection["debugLogging"] ?? "false");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
      try
      {
        await RunInternalAsync(cancellationToken);
      }
      catch (OperationCanceledException)
      {
        BluetoothLogger.Info("Linux Bluetooth worker cancelled.");
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Linux Bluetooth error: {ex.Message}");
        if (debugLogging)
          BaseLogger.LogDebug(ex.ToString());
      }
      finally
      {
        launchMonitor?.Dispose();
      }
    }

    private async Task RunInternalAsync(CancellationToken cancellationToken)
    {
      BluetoothLogger.Info("R10 Linux.Bluetooth test starting...");

      string deviceAddress = configuration["bluetoothDeviceAddress"] ?? string.Empty;
      if (string.IsNullOrWhiteSpace(deviceAddress))
      {
        BluetoothLogger.Error("bluetoothDeviceAddress must be set in settings.json when bluetooth.platform=linux.");
        return;
      }

      var adapters = await BlueZManager.GetAdaptersAsync();
      if (adapters.Count == 0)
      {
        BluetoothLogger.Error("No Bluetooth adapters found.");
        return;
      }

      var adapter = adapters.First();
      Debug($"Using adapter: {adapter.ObjectPath}");

      BluetoothLogger.Info($"Looking up device {deviceAddress} via adapter.GetDeviceAsync...");
      var device = await adapter.GetDeviceAsync(deviceAddress);
      if (device == null)
      {
        BluetoothLogger.Error("adapter.GetDeviceAsync returned null. R10 not found.");
        return;
      }

      await LogDeviceProperties(device);

      BluetoothLogger.Info("Connecting...");
      await device.ConnectAsync();

      var timeout = TimeSpan.FromSeconds(20);
      await device.WaitForPropertyValueAsync("Connected", true, timeout);
      await device.WaitForPropertyValueAsync("ServicesResolved", true, timeout);

      BluetoothLogger.Info("Connected to device via Linux.Bluetooth (BlueZ).");

      await LogAvailableServices(device);
      await LogBatteryStatus(device);
      await LogDeviceInformation(device);

      launchMonitor = await SetupLaunchMonitorAsync(device);
      if (launchMonitor == null)
      {
        BluetoothLogger.Error("Linux launch monitor setup failed.");
        return;
      }

      BluetoothLogger.Info("Linux Bluetooth initialization complete. Waiting for shutdown signal...");
      await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private async Task LogDeviceProperties(Device device)
    {
      var properties = await device.GetPropertiesAsync();
      BluetoothLogger.Info($"Found device: {properties.Name ?? properties.Alias ?? properties.Address} ({properties.Address})");
      BluetoothLogger.Info($"Paired={properties.Paired}, Trusted={properties.Trusted}, ServicesResolved={properties.ServicesResolved}");
    }

    private async Task LogAvailableServices(Device device)
    {
      var properties = await device.GetPropertiesAsync();
      if (properties.UUIDs == null || properties.UUIDs.Length == 0)
      {
        Debug("Device reported no service UUIDs.");
        return;
      }

      BluetoothLogger.Info("Device service UUIDs:");
      foreach (string uuid in properties.UUIDs)
      {
        BluetoothLogger.Info($"  {uuid}");
      }
    }

    private async Task LogBatteryStatus(Device device)
    {
      try
      {
        var batteryService = await device.GetServiceAsync(BatteryServiceUuid);
        Debug("Battery service found.");

        var batteryChar = await batteryService.GetCharacteristicAsync(BatteryLevelCharUuid);
        Debug("Battery level characteristic found.");
        byte[] value = await batteryChar.ReadValueAsync(TimeSpan.FromSeconds(5));
        int batteryPercent = value.Length > 0 ? value[0] : -1;
        BluetoothLogger.Info($"Battery Level: {batteryPercent}%");
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Battery service read failed: {ex.Message}");
        if (debugLogging)
          BaseLogger.LogDebug(ex.ToString());
      }
    }

    private async Task LogDeviceInformation(Device device)
    {
      try
      {
        var devInfoService = await device.GetServiceAsync(DeviceInfoServiceUuid);
        BluetoothLogger.Info("Reading Device Information Service...");

        foreach (var (uuid, label) in DeviceInfoCharacteristics)
        {
          try
          {
            var characteristic = await devInfoService.GetCharacteristicAsync(uuid);
            byte[] raw = await characteristic.ReadValueAsync(TimeSpan.FromSeconds(5));
            string text = Encoding.UTF8.GetString(raw).Trim('\0');
            BluetoothLogger.Info($"{label}: {text}");
          }
          catch (Exception ex)
          {
            BluetoothLogger.Info($"{label}: <not available> ({ex.Message})");
            if (debugLogging)
              BaseLogger.LogDebug(ex.ToString());
          }
        }
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Device information read failed: {ex.Message}");
        if (debugLogging)
          BaseLogger.LogDebug(ex.ToString());
      }
    }

    private async Task<LinuxLaunchMonitorDevice?> SetupLaunchMonitorAsync(Device device)
    {
      var lm = new LinuxLaunchMonitorDevice(device);
      lm.AutoWake = bool.Parse(configuration["autoWake"] ?? "false");
      lm.CalibrateTiltOnConnect = bool.Parse(configuration["calibrateTiltOnConnect"] ?? "false");
      lm.DebugLogging = debugLogging;

      lm.MessageRecieved += (o, e) => BluetoothLogger.Incoming(e.Message?.ToString() ?? string.Empty);
      lm.MessageSent += (o, e) => BluetoothLogger.Outgoing(e.Message?.ToString() ?? string.Empty);
      lm.BatteryLifeUpdated += (o, e) => BluetoothLogger.Info($"Battery Life Updated: {e.Battery}%");
      lm.Error += (o, e) => BluetoothLogger.Error($"{e.Severity}: {e.Message}");

      if (bool.Parse(configuration["sendStatusChangesToGSP"] ?? "false"))
      {
        lm.ReadinessChanged += (o, e) =>
        {
          connectionManager.SendLaunchMonitorReadyUpdate(e.Ready);
        };
      }

      lm.ShotMetrics += (o, e) =>
      {
        BluetoothConnection.LogMetrics(e.Metrics);
        connectionManager.SendShot(
          BluetoothConnection.BallDataFromLaunchMonitorMetrics(e.Metrics?.BallMetrics),
          BluetoothConnection.ClubDataFromLaunchMonitorMetrics(e.Metrics?.ClubMetrics)
        );
      };

      if (!await lm.Setup())
      {
        BluetoothLogger.Error("Failed Device Setup");
        return null;
      }

      float temperature = float.Parse(configuration["temperature"] ?? "60");
      float humidity = float.Parse(configuration["humidity"] ?? "1");
      float altitude = float.Parse(configuration["altitude"] ?? "0");
      float airDensity = float.Parse(configuration["airDensity"] ?? "1");
      float teeDistanceInFeet = float.Parse(configuration["teeDistanceInFeet"] ?? "7");
      float teeRange = teeDistanceInFeet * (1 / 3.281f);

      lm.ShotConfig(temperature, humidity, altitude, airDensity, teeRange);

      BluetoothLogger.Info($"Device Setup Complete: ");
      BluetoothLogger.Info($"   Model: {lm.Model}");
      BluetoothLogger.Info($"   Firmware: {lm.Firmware}");
      BluetoothLogger.Info($"   Bluetooth ID: {lm.Device.ObjectPath}");
      BluetoothLogger.Info($"   Battery: {lm.Battery}%");
      BluetoothLogger.Info($"   Current State: {lm.CurrentState}");
      BluetoothLogger.Info($"   Tilt: {lm.DeviceTilt}");

      return lm;
    }

    private void Debug(string message)
    {
      if (debugLogging)
        BaseLogger.LogDebug(message);
    }
  }
}
