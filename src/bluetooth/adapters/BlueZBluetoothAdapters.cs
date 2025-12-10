#if !WINDOWS
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

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
      IGattService1 service = await WaitForAsync(
        () => device.GetServiceAsync(uuid),
        $"service {uuid}",
        timeout ?? TimeSpan.FromSeconds(30)
      );
      return new BlueZGattServiceAdapter(service, uuid, timeout ?? TimeSpan.FromSeconds(30));
    }

    public Task DisconnectAsync() => device.DisconnectAsync();

    public void Dispose()
    {
      device?.Dispose();
    }

  private async Task<T> WaitForAsync<T>(Func<Task<T>> action, string description, TimeSpan timeout)
  {
    TimeSpan pollDelay = TimeSpan.FromMilliseconds(500);
    DateTime deadline = DateTime.UtcNow + timeout;
    Exception? last = null;

    while (true)
    {
      try
      {
        // No inner 5s WaitAsync – just call the action
        return await action();
      }
      catch (Exception ex) when (IsTransientDbus(ex))
      {
        last = ex;

        if (DateTime.UtcNow >= deadline)
          break;

        await Task.Delay(pollDelay);
      }
    }

    throw new TimeoutException($"Timed out waiting for {description}", last);
  }

    private bool IsTransientDbus(Exception ex) =>
      ex is DBusException ||
      (ex.InnerException != null && IsTransientDbus(ex.InnerException));
  }

  public class BlueZGattServiceAdapter : IBluetoothGattServiceAdapter
  {
    private readonly IGattService1 service;
    private readonly string uuid;
    private readonly TimeSpan defaultTimeout;

    public BlueZGattServiceAdapter(IGattService1 service, string uuid, TimeSpan timeout)
    {
      this.service = service;
      this.uuid = uuid;
      defaultTimeout = timeout;
    }

    public async Task<IBluetoothGattCharacteristicAdapter> GetCharacteristicAsync(Guid characteristicUuid, TimeSpan? timeout = null)
    {
      string charUuid = characteristicUuid.ToString();
      GattCharacteristic characteristic = await WaitForAsync(
        () => service.GetCharacteristicAsync(charUuid),
        $"characteristic {charUuid} (service {uuid})",
        timeout ?? defaultTimeout
      );

      return new BlueZGattCharacteristicAdapter(characteristic);
    }

    public void Dispose()
    {
      (service as IDisposable)?.Dispose();
    }

private async Task<T> WaitForAsync<T>(Func<Task<T>> action, string description, TimeSpan timeout)
{
  TimeSpan pollDelay = TimeSpan.FromMilliseconds(500);
  DateTime deadline = DateTime.UtcNow + timeout;
  Exception? last = null;

  while (true)
  {
    try
    {
      // No inner 5s WaitAsync – just call the action
      return await action();
    }
    catch (Exception ex) when (IsTransientDbus(ex))
    {
      last = ex;

      if (DateTime.UtcNow >= deadline)
        break;

      await Task.Delay(pollDelay);
    }
  }

  throw new TimeoutException($"Timed out waiting for {description}", last);
}


    private bool IsTransientDbus(Exception ex) =>
      ex is DBusException ||
      (ex.InnerException != null && IsTransientDbus(ex.InnerException));
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
