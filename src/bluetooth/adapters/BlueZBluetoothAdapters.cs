#if !WINDOWS
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace gspro_r10.bluetooth.adapters
{
  public class BlueZBluetoothDeviceAdapter : IBluetoothDeviceAdapter
  {
    private readonly Device device;

    public BlueZBluetoothDeviceAdapter(Device device, string id, string? name)
    {
      this.device = device;
      Id = id;
      Name = name;
    }

    public string Id { get; }
    public string? Name { get; }

    public async Task<IBluetoothGattServiceAdapter> GetPrimaryServiceAsync(Guid serviceUuid, TimeSpan? timeout = null)
    {
      string uuid = serviceUuid.ToString();
      var serviceTask = device.GetServiceAsync(uuid);
      IGattService1 service = timeout.HasValue
        ? await serviceTask.WaitAsync(timeout.Value)
        : await serviceTask;

      return new BlueZGattServiceAdapter(service);
    }

    public Task DisconnectAsync() => device.DisconnectAsync();

    public void Dispose()
    {
      device?.Dispose();
    }
  }

  public class BlueZGattServiceAdapter : IBluetoothGattServiceAdapter
  {
    private readonly IGattService1 service;

    public BlueZGattServiceAdapter(IGattService1 service)
    {
      this.service = service;
    }

    public async Task<IBluetoothGattCharacteristicAdapter> GetCharacteristicAsync(Guid characteristicUuid, TimeSpan? timeout = null)
    {
      string uuid = characteristicUuid.ToString();
      var characteristicTask = service.GetCharacteristicAsync(uuid);
      GattCharacteristic characteristic = timeout.HasValue
        ? await characteristicTask.WaitAsync(timeout.Value)
        : await characteristicTask;

      return new BlueZGattCharacteristicAdapter(characteristic);
    }

    public void Dispose()
    {
      (service as IDisposable)?.Dispose();
    }
  }

  public class BlueZGattCharacteristicAdapter : IBluetoothGattCharacteristicAdapter
  {
    private readonly GattCharacteristic characteristic;
    private readonly Dictionary<string, object> emptyOptions = new Dictionary<string, object>();

    public BlueZGattCharacteristicAdapter(GattCharacteristic characteristic)
    {
      this.characteristic = characteristic;
      this.characteristic.Value += OnValueChangedAsync;
    }

    public event EventHandler<byte[]>? ValueChanged;

    public async Task<byte[]> ReadValueAsync(TimeSpan? timeout = null)
    {
      TimeSpan timeoutValue = timeout ?? TimeSpan.FromSeconds(5);
      return await characteristic.ReadValueAsync(timeoutValue);
    }

    public async Task StartNotificationsAsync(TimeSpan? timeout = null)
    {
      Task notifyTask = characteristic.StartNotifyAsync();
      if (timeout.HasValue)
        await notifyTask.WaitAsync(timeout.Value);
      else
        await notifyTask;
    }

    public async Task WriteValueWithResponseAsync(byte[] data, TimeSpan? timeout = null)
    {
      Task writeTask = characteristic.WriteValueAsync(data, emptyOptions);
      if (timeout.HasValue)
        await writeTask.WaitAsync(timeout.Value);
      else
        await writeTask;
    }

    private Task OnValueChangedAsync(GattCharacteristic sender, GattCharacteristicValueEventArgs args)
    {
      ValueChanged?.Invoke(this, args.Value);
      return Task.CompletedTask;
    }

    public void Dispose()
    {
      characteristic.Value -= OnValueChangedAsync;
      characteristic?.Dispose();
    }
  }
}
#endif
