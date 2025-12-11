using Google.Protobuf;
using LaunchMonitor.Proto;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace gspro_r10.bluetooth
{
  public abstract class LinuxBaseDevice : IDisposable
  {
    internal static Guid BATTERY_SERVICE_UUID = Guid.Parse("0000180f-0000-1000-8000-00805f9b34fb");
    internal static Guid BATTERY_CHARACTERISTIC_UUID = Guid.Parse("00002a19-0000-1000-8000-00805f9b34fb");
    internal static Guid DEVICE_INFO_SERVICE_UUID = Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb");
    internal static Guid FIRMWARE_CHARACTERISTIC_UUID = Guid.Parse("00002a28-0000-1000-8000-00805f9b34fb");
    internal static Guid MODEL_CHARACTERISTIC_UUID = Guid.Parse("00002a24-0000-1000-8000-00805f9b34fb");
    internal static Guid SERIAL_NUMBER_CHARACTERISTIC_UUID = Guid.Parse("00002a25-0000-1000-8000-00805f9b34fb");
    internal static Guid DEVICE_INTERFACE_SERVICE = Guid.Parse("6A4E2800-667B-11E3-949A-0800200C9A66");
    internal static Guid DEVICE_INTERFACE_NOTIFIER = Guid.Parse("6A4E2812-667B-11E3-949A-0800200C9A66");
    internal static Guid DEVICE_INTERFACE_WRITER = Guid.Parse("6A4E2822-667B-11E3-949A-0800200C9A66");

    public Device Device { get; }
    public int Battery { get { return mBattery; }
      set {
        mBattery = value;
        BatteryLifeUpdated?.Invoke(this, new BatteryEventArgs() { Battery = value });
      }
    }

    public string? Model { get; private set; }
    public string? Firmware { get; private set; }
    public string? Serial { get; private set; }
    public event MessageEventHandler? MessageRecieved;
    public event MessageEventHandler? MessageSent;
    public delegate void MessageEventHandler(object sender, MessageEventArgs e);
    public class MessageEventArgs: EventArgs
    {
      public IMessage? Message { get; set; }
    }

    public event BatteryEventHandler? BatteryLifeUpdated;
    public delegate void BatteryEventHandler(object sender, BatteryEventArgs e);
    public class BatteryEventArgs: EventArgs
    {
      public int Battery { get; set; }
    }

    private EventWaitHandle mWriterSignal = new AutoResetEvent(false);
    private Queue<byte[]> mWriterQueue = new Queue<byte[]>();
    private EventWaitHandle mReaderSignal = new AutoResetEvent(false);
    private Queue<byte[]> mReaderQueue = new Queue<byte[]>();
    private EventWaitHandle mMsgProcessSignal = new AutoResetEvent(false);
    private Queue<byte[]> mMsgProcessQueue = new Queue<byte[]>();
    private ManualResetEventSlim mHandshakeCompleteResetEvent = new ManualResetEventSlim(false);
    private ManualResetEventSlim mProtoResponseResetEvent = new ManualResetEventSlim(false);
    private IMessage? mLastProtoReceived;
    private int mBattery;
    private bool mHandshakeComplete = false;
    private byte mHeader = 0x00;
    private int mProtoRequestCounter = 0;
    private CancellationTokenSource mCancellationToken;
    private Task mWriterTask;
    private Task mReaderTask;
    private Task mMsgProcessingTask;
    private GattCharacteristic? mGattWriter;
    private bool mDisposedValue;
    public bool DebugLogging { get; set; } = false;
    protected static readonly TimeSpan GattTimeout = TimeSpan.FromSeconds(15);

    public LinuxBaseDevice(Device device)
    {
      Device = device;

      mCancellationToken = new CancellationTokenSource();
      mWriterTask = Task.Run(WriterThread, mCancellationToken.Token);
      mReaderTask = Task.Run(ReaderThread, mCancellationToken.Token);
      mMsgProcessingTask = Task.Run(MsgProcessingThread, mCancellationToken.Token);
    }

    public virtual async Task<bool> Setup()
    {
      try
      {
        return await SetupInternalAsync();
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Linux Base setup failed: {ex.Message}");
        if (DebugLogging)
          BaseLogger.LogDebug(ex.ToString());
        return false;
      }
    }

    private async Task<bool> SetupInternalAsync()
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"Getting device info service");
      var deviceInfoService = await AwaitGattAsync(Device.GetServiceAsync(DEVICE_INFO_SERVICE_UUID.ToString().ToLowerInvariant()), "device info service");

      if (DebugLogging)
        BaseLogger.LogDebug($"Reading serial number");
      GattCharacteristic serialCharacteristic = await AwaitGattAsync(deviceInfoService.GetCharacteristicAsync(SERIAL_NUMBER_CHARACTERISTIC_UUID.ToString().ToLowerInvariant()), "serial characteristic");
      Serial = Encoding.ASCII.GetString((await AwaitGattAsync(serialCharacteristic.ReadValueAsync(GattTimeout), "serial read")).ToArray());

      if (DebugLogging)
        BaseLogger.LogDebug($"Reading firmware version");
      GattCharacteristic firmwareCharacteristic = await AwaitGattAsync(deviceInfoService.GetCharacteristicAsync(FIRMWARE_CHARACTERISTIC_UUID.ToString().ToLowerInvariant()), "firmware characteristic");
      Firmware = Encoding.ASCII.GetString((await AwaitGattAsync(firmwareCharacteristic.ReadValueAsync(GattTimeout), "firmware read")).ToArray());

      if (DebugLogging)
        BaseLogger.LogDebug($"Reading model name");
      GattCharacteristic modelCharacteristic = await AwaitGattAsync(deviceInfoService.GetCharacteristicAsync(MODEL_CHARACTERISTIC_UUID.ToString().ToLowerInvariant()), "model characteristic");
      Model = Encoding.ASCII.GetString((await AwaitGattAsync(modelCharacteristic.ReadValueAsync(GattTimeout), "model read")).ToArray());

      if (DebugLogging)
        BaseLogger.LogDebug($"Reading battery life");
      var batteryService = await AwaitGattAsync(Device.GetServiceAsync(BATTERY_SERVICE_UUID.ToString().ToLowerInvariant()), "battery service");
      GattCharacteristic batteryCharacteristic = await AwaitGattAsync(batteryService.GetCharacteristicAsync(BATTERY_CHARACTERISTIC_UUID.ToString().ToLowerInvariant()), "battery characteristic");

      // Register event handler BEFORE starting notifications
      batteryCharacteristic.Value += (o, e) =>
      {
        if (e.Value.Length > 0)
          Battery = e.Value[0];
        return Task.CompletedTask;
      };

      await AwaitGattAsync(batteryCharacteristic.StartNotifyAsync(), "battery notifications");

      if (DebugLogging)
        BaseLogger.LogDebug($"Setting up device interface service");
      var deviceInterfaceService = await AwaitGattAsync(Device.GetServiceAsync(DEVICE_INTERFACE_SERVICE.ToString().ToLowerInvariant()), "device interface service");

      if (DebugLogging)
        BaseLogger.LogDebug($"Getting writer");
      mGattWriter = await AwaitGattAsync(deviceInterfaceService.GetCharacteristicAsync(DEVICE_INTERFACE_WRITER.ToString().ToLowerInvariant()), "device interface writer");

      if (DebugLogging)
        BaseLogger.LogDebug($"Getting reader");
      GattCharacteristic deviceInterfaceNotifier = await AwaitGattAsync(deviceInterfaceService.GetCharacteristicAsync(DEVICE_INTERFACE_NOTIFIER.ToString().ToLowerInvariant()), "device interface notifier");

      // Register event handler BEFORE starting notifications
      deviceInterfaceNotifier.Value += (o, e) =>
      {
        ReadBytes(e.Value);
        return Task.CompletedTask;
      };

      await AwaitGattAsync(deviceInterfaceNotifier.StartNotifyAsync(), "device interface notifier notifications");

      // Give the notification subscription a moment to become fully active
      await Task.Delay(100);

      BluetoothLogger.Info("Linux Base: Performing handshake");
      bool handshakeSuccess = PerformHandShake();
      if (!handshakeSuccess)
        Console.WriteLine("Failed handshake. Something went wrong in setup");
      else
        BluetoothLogger.Info("Linux Base: Handshake complete");
      return handshakeSuccess;
    }

    protected async Task<T> AwaitGattAsync<T>(Task<T> task, string description)
    {
      try
      {
        if (DebugLogging)
          BaseLogger.LogDebug($"Linux GATT: Waiting for {description}");
        return await task.WaitAsync(GattTimeout);
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Linux GATT: {description} failed - {ex.Message}");
        throw;
      }
    }

    protected async Task AwaitGattAsync(Task task, string description)
    {
      try
      {
        BluetoothLogger.Info($"Linux GATT: Waiting for {description}");
        await task.WaitAsync(GattTimeout);
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Linux GATT: {description} failed - {ex.Message}");
        throw;
      }
    }

    private void ReaderThread()
    {
      List<byte> currentMessage = new List<byte>();

      while (!mCancellationToken.IsCancellationRequested)
      {
        if (mReaderQueue.Count > 0)
        {
          IEnumerable<byte> msg = mReaderQueue.Dequeue();

          byte header = msg.First();
          msg = msg.Skip(1);

          if (header == 0 || !mHandshakeComplete)
          {
            ContinueHandShake(msg);
            continue;
          }

          bool readComplete = false;

          if (msg.Last() == 0x00)
          {
            readComplete = true;
            msg = msg.SkipLast(1);
          }
          if (msg.Count() > 0 && msg.First() == 0x00)
          {
            currentMessage.Clear();
            msg = msg.Skip(1);
          }
          currentMessage.AddRange(msg);

          if (readComplete && currentMessage.Count > 0)
          {
            if (DebugLogging)
              BaseLogger.LogDebug($"  -> {currentMessage.ToHexString().PadRight(44)} (encoded)");
            byte[] decoded = COBS.Decode(currentMessage.ToArray()).ToArray();
            if (DebugLogging)
              BaseLogger.LogDebug($"-> {decoded.ToHexString().PadRight(46)} (decoded)");
            mMsgProcessQueue.Enqueue(decoded);
            mMsgProcessSignal.Set();
            currentMessage.Clear();
          }
        }
        else
        {
          mReaderSignal.WaitOne(5000);
        }
      }
    }

    private void WriterThread()
    {
      while (!mCancellationToken.IsCancellationRequested)
      {
        if (mWriterQueue.Count > 0)
        {
          // Try with response type to match Windows WriteValueWithResponseAsync behavior
          var options = new Dictionary<string, object>
          {
            ["type"] = "request" // Request a response from the device
          };
          _ = mGattWriter?.WriteValueAsync(mWriterQueue.Dequeue(), options);
        }
        else
        {
          mWriterSignal.WaitOne(5000);
        }
      }
    }

    private void MsgProcessingThread()
    {
      while (!mCancellationToken.IsCancellationRequested)
        if (mMsgProcessQueue.Count > 0)
          ProcessMessage(mMsgProcessQueue.Dequeue());
        else
          mMsgProcessSignal.WaitOne(5000);
    }

    public bool PerformHandShake()
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"Starting handshake");
      mHandshakeComplete = false;
      mHandshakeCompleteResetEvent.Reset();
      mHeader = 0x00;
      SendBytes("000000000000000000010000");
      return mHandshakeCompleteResetEvent.Wait(TimeSpan.FromSeconds(10));
    }

    private void ContinueHandShake(IEnumerable<byte> msg)
    {
      string msgHex = msg.ToHexString();

      if (msgHex.StartsWith("010000000000000000010000"))
      {
        mHeader = msg.ElementAt(12);
        SendBytes("00");
        mHandshakeComplete = true;
        mHandshakeCompleteResetEvent.Set();
        return;
      }
    }

    private void ProcessMessage(byte[] frame)
    {
      if (BitConverter.ToUInt16(frame.SkipLast(2).Checksum()) != BitConverter.ToUInt16(frame.TakeLast(2).ToArray()))
      {
        Console.WriteLine("CRC ERROR");
      }

      byte[] msg = frame.Skip(2).SkipLast(2).ToArray();
      string hex = msg.ToHexString();

      if (DebugLogging)
        BaseLogger.LogDebug($"ProcessMessage: {hex}");

      List<byte> ackBody = new List<byte>() { 0x00 };

      if (hex.StartsWith("A013"))
      {
        // device info
      }
      else if (hex.StartsWith("BA13"))
      {
        // config
      }
      else if (hex.StartsWith("B413")) // all protobuf responses
      {
        ushort counter = BitConverter.ToUInt16(msg[2..4]);
        ackBody.AddRange(msg[2..4]);
        ackBody.AddRange("00000000000000".ToByteArray());

        if (DebugLogging)
          BaseLogger.LogDebug($"B413 response: counter={counter}, expected={mProtoRequestCounter}");

        if (counter == mProtoRequestCounter)
        {
          mLastProtoReceived = WrapperProto.Parser.ParseFrom(msg.Skip(16).ToArray());
          MessageRecieved?.Invoke(this, new MessageEventArgs() { Message = mLastProtoReceived } );
          mProtoResponseResetEvent.Set();
        }
        else if (DebugLogging)
        {
          BaseLogger.LogDebug($"B413 counter mismatch! Ignoring response.");
        }
      }
      else if (hex.StartsWith("B313")) // all protobuf requests
      {
        if (DebugLogging)
          BaseLogger.LogDebug($"B313 protobuf request received from device!");

        ackBody.AddRange(msg[2..4]);
        ackBody.AddRange("00000000000000".ToByteArray());
        Task.Run(() => {
          var request = WrapperProto.Parser.ParseFrom(msg.Skip(16).ToArray());
          if (DebugLogging)
            BaseLogger.LogDebug($"Parsed protobuf: {request}");
          MessageRecieved?.Invoke(this, new MessageEventArgs() { Message = request} );
          HandleProtobufRequest(request);
        });
      }

      AcknowledgeMessage(msg, ackBody);
    }

    public abstract void HandleProtobufRequest(IMessage request);

    private void AcknowledgeMessage(IEnumerable<byte> msg, IEnumerable<byte> respBody)
    {
      WriteMessage("8813".ToByteArray().Concat(msg.Take(2)).Concat(respBody).ToArray());
    }

    public IMessage? SendProtobufRequest(IMessage proto)
    {
      
      mProtoResponseResetEvent.Reset();

      byte[] bytes = proto.ToByteArray();
      int l = bytes.Length;
      byte[] fullMsg = "B313".ToByteArray()
        .Concat(BitConverter.GetBytes(mProtoRequestCounter))
        .Append<byte>(0x00)
        .Append<byte>(0x00)
        .Concat(BitConverter.GetBytes(l))
        .Concat(BitConverter.GetBytes(l))
        .Concat(bytes)
        .ToArray();

      WriteMessage(fullMsg);
      MessageSent?.Invoke(this, new MessageEventArgs(){ Message = proto });
      if (mProtoResponseResetEvent.Wait(5000))
      {
        mProtoRequestCounter++;
        return mLastProtoReceived;
      }
      else
      {
        Console.WriteLine($"Failed to get response for proto {mProtoRequestCounter}");
        return null;
      }
    }

    private void ReadBytes(byte[] bytes)
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"      -> {bytes.ToHexString().PadRight(40)} (ble read)");
      mReaderQueue.Enqueue(bytes);
      mReaderSignal.Set();
    }
    private void SendBytes(IEnumerable<byte> bytes)
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"      <- {bytes.ToHexString().PadRight(40)} (ble write)");
      mWriterQueue.Enqueue(bytes.Prepend(mHeader).ToArray());
      mWriterSignal.Set();
    }

    public void SendBytes(string hexBytes) => SendBytes(hexBytes.ToByteArray());

    public void WriteMessage(byte[] bytes)
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"<- {bytes.ToHexString().PadRight(46)} (raw)");

      // Length of message + 2 bytes for length field + 2 bytes for crc field
      ushort length = (ushort)(2 + bytes.Length + 2);
      IEnumerable<byte> bytesWithLength = BitConverter.GetBytes(length).Concat(bytes);
      IEnumerable<byte> fullFrame = bytesWithLength.Concat(bytesWithLength.Checksum());

      if (DebugLogging)
        BaseLogger.LogDebug($"  <- {fullFrame.ToHexString().PadRight(44)} (framed)");

      List<byte> encoded = COBS.Encode(fullFrame).Prepend<byte>(0x00).Append<byte>(0x00).ToList();
      if (DebugLogging)
        BaseLogger.LogDebug($"    <- {encoded.ToArray().ToHexString().PadRight(42)} (encoded)");

      while (encoded.Count > 19)
      {
        SendBytes(encoded.Take(19));
        encoded = encoded.Skip(19).ToList();
      }
      if (encoded.Count > 0)
        SendBytes(encoded);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!mDisposedValue)
      {
        if (disposing)
        {
          mCancellationToken.Cancel();
          mWriterTask.Wait();
          mReaderTask.Wait();
          mMsgProcessingTask.Wait();

          foreach (var d in MessageSent?.GetInvocationList() ?? Array.Empty<Delegate>())
            MessageSent -= (d as MessageEventHandler);

          foreach (var d in MessageRecieved?.GetInvocationList() ?? Array.Empty<Delegate>())
            MessageRecieved -= (d as MessageEventHandler);

          foreach (var d in BatteryLifeUpdated?.GetInvocationList() ?? Array.Empty<Delegate>())
            BatteryLifeUpdated -= (d as BatteryEventHandler);

          try
          {
            Device?.DisconnectAsync().Wait();
          }
          catch { }
        }

        mDisposedValue = true;
      }
    }

    public void Dispose()
    {
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }
  }
}
