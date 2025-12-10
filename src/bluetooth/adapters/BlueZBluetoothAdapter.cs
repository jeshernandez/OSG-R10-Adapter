using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Configuration;
using gspro_r10;
using gspro_r10.bluetooth;

namespace gspro_r10.bluetooth.adapters
{
  public class BlueZBluetoothAdapter
  {
    private static readonly string BatteryServiceUuid = BaseDevice.BATTERY_SERVICE_UUID.ToString();
    private static readonly string BatteryLevelCharUuid = BaseDevice.BATTERY_CHARACTERISTIC_UUID.ToString();
    private static readonly string DeviceInfoServiceUuid = BaseDevice.DEVICE_INFO_SERVICE_UUID.ToString();

    private static readonly (string Uuid, string Label)[] DeviceInfoCharacteristics = new (string, string)[]
    {
      ("00002a29-0000-1000-8000-00805f9b34fb", "Manufacturer Name"),
      (BaseDevice.MODEL_CHARACTERISTIC_UUID.ToString(), "Model Number"),
      (BaseDevice.SERIAL_NUMBER_CHARACTERISTIC_UUID.ToString(), "Serial Number"),
      (BaseDevice.FIRMWARE_CHARACTERISTIC_UUID.ToString(), "Firmware Revision")
    };

    private readonly IConfigurationSection configuration;
    private readonly bool debugLogging;

    public BlueZBluetoothAdapter(IConfigurationSection configurationSection)
    {
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

        batteryChar.Value += (sender, args) =>
        {
          var data = args.Value;
          if (data.Length > 0)
            BluetoothLogger.Info($"Battery Level Updated: {data[0]}%");
          return Task.CompletedTask;
        };

        await batteryChar.StartNotifyAsync();
        Debug("Subscribed to battery notifications.");
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

    private void Debug(string message)
    {
      if (debugLogging)
        BaseLogger.LogDebug(message);
    }
  }
}
