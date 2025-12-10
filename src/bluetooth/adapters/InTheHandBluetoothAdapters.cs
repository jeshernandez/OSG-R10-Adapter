using InTheHand.Bluetooth;
using System;
using System.Threading.Tasks;

namespace gspro_r10.bluetooth.adapters
{
  public class InTheHandBluetoothDeviceAdapter : IBluetoothDeviceAdapter
  {
    private readonly BluetoothDevice device;

    public InTheHandBluetoothDeviceAdapter(BluetoothDevice device)
    {
      this.device = device;
    }

    public string Id => device.Id;
    public string? Name => device.Name;

    public async Task<IBluetoothGattServiceAdapter> GetPrimaryServiceAsync(Guid serviceUuid, TimeSpan? timeout = null)
    {
      GattService service = timeout.HasValue
        ? await device.Gatt.GetPrimaryServiceAsync(serviceUuid).WaitAsync(timeout.Value)
        : await device.Gatt.GetPrimaryServiceAsync(serviceUuid);

      return new InTheHandGattServiceAdapter(service);
    }

    public Task DisconnectAsync()
    {
      device.Gatt?.Disconnect();
      return Task.CompletedTask;
    }

    public void Dispose() { }
  }

  public class InTheHandGattServiceAdapter : IBluetoothGattServiceAdapter
  {
    private readonly GattService service;

    public InTheHandGattServiceAdapter(GattService service)
    {
      this.service = service;
    }

    public async Task<IBluetoothGattCharacteristicAdapter> GetCharacteristicAsync(Guid characteristicUuid, TimeSpan? timeout = null)
    {
      GattCharacteristic characteristic = timeout.HasValue
        ? await service.GetCharacteristicAsync(characteristicUuid).WaitAsync(timeout.Value)
        : await service.GetCharacteristicAsync(characteristicUuid);

      return new InTheHandGattCharacteristicAdapter(characteristic);
    }

    public void Dispose() { }
  }

  public class InTheHandGattCharacteristicAdapter : IBluetoothGattCharacteristicAdapter
  {
    private readonly GattCharacteristic characteristic;

    public InTheHandGattCharacteristicAdapter(GattCharacteristic characteristic)
    {
      this.characteristic = characteristic;
      this.characteristic.CharacteristicValueChanged += OnValueChanged;
    }

    public event EventHandler<byte[]>? ValueChanged;

    public async Task<byte[]> ReadValueAsync(TimeSpan? timeout = null)
    {
      return timeout.HasValue
        ? await characteristic.ReadValueAsync().WaitAsync(timeout.Value)
        : await characteristic.ReadValueAsync();
    }

    public async Task StartNotificationsAsync(TimeSpan? timeout = null)
    {
      if (timeout.HasValue)
        await characteristic.StartNotificationsAsync().WaitAsync(timeout.Value);
      else
        await characteristic.StartNotificationsAsync();
    }

    public async Task WriteValueWithResponseAsync(byte[] data, TimeSpan? timeout = null)
    {
      if (timeout.HasValue)
        await characteristic.WriteValueWithResponseAsync(data).WaitAsync(timeout.Value);
      else
        await characteristic.WriteValueWithResponseAsync(data);
    }

    private void OnValueChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
      ValueChanged?.Invoke(this, e.Value);
    }

    public void Dispose()
    {
      characteristic.CharacteristicValueChanged -= OnValueChanged;
    }
  }
}
