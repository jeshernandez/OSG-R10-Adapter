using System;
using System.Threading.Tasks;

namespace gspro_r10.bluetooth.adapters
{
  public interface IBluetoothDeviceAdapter : IDisposable
  {
    string Id { get; }
    string? Name { get; }

    Task<IBluetoothGattServiceAdapter> GetPrimaryServiceAsync(Guid serviceUuid, TimeSpan? timeout = null);
    Task DisconnectAsync();
  }

  public interface IBluetoothGattServiceAdapter : IDisposable
  {
    Task<IBluetoothGattCharacteristicAdapter> GetCharacteristicAsync(Guid characteristicUuid, TimeSpan? timeout = null);
  }

  public interface IBluetoothGattCharacteristicAdapter : IDisposable
  {
    event EventHandler<byte[]>? ValueChanged;

    Task<byte[]> ReadValueAsync(TimeSpan? timeout = null);
    Task StartNotificationsAsync(TimeSpan? timeout = null);
    Task WriteValueWithResponseAsync(byte[] data, TimeSpan? timeout = null);
  }
}
