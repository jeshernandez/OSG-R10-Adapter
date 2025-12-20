using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Google.Protobuf;
using InTheHand.Bluetooth;
using LaunchMonitor.Proto;
using static LaunchMonitor.Proto.State.Types;
using static LaunchMonitor.Proto.SubscribeResponse.Types;
using static LaunchMonitor.Proto.WakeUpResponse.Types;

namespace gspro_r10.bluetooth
{
  public class LaunchMonitorDevice : BaseDevice
  {
    internal static Guid MEASUREMENT_SERVICE_UUID = Guid.Parse("6A4E3400-667B-11E3-949A-0800200C9A66");
    internal static Guid MEASUREMENT_CHARACTERISTIC_UUID = Guid.Parse("6A4E3401-667B-11E3-949A-0800200C9A66");
    internal static Guid CONTROL_POINT_CHARACTERISTIC_UUID = Guid.Parse("6A4E3402-667B-11E3-949A-0800200C9A66");
    internal static Guid STATUS_CHARACTERISTIC_UUID = Guid.Parse("6A4E3403-667B-11E3-949A-0800200C9A66");

    private HashSet<uint> ProcessedShotIDs = new HashSet<uint>();
    private readonly RawMeasurementParser rawMeasurementParser = new RawMeasurementParser();
    private readonly ConcurrentDictionary<uint, Metrics> rawMetricsByShot = new ConcurrentDictionary<uint, Metrics>();
    private readonly ConcurrentDictionary<uint, Metrics> protoMetricsByShot = new ConcurrentDictionary<uint, Metrics>();

    private StateType _currentState;
    public StateType CurrentState { 
      get { return _currentState; } 
      private set {
        _currentState = value;
        Ready = value == StateType.Waiting;
      }
    }

    public Tilt? DeviceTilt { get; private set; }

    private bool _ready = false;
    public bool Ready { 
      get {return _ready; } 
      private set {
        bool changed = _ready != value;
        _ready = value;
        if (changed)
          ReadinessChanged?.Invoke(this, new ReadinessChangedEventArgs(){ Ready = value });
      }
    }

    public bool AutoWake { get; set; } = true;
    public bool CalibrateTiltOnConnect { get; set; } = true;

    public event ReadinessChangedEventHandler? ReadinessChanged;
    public delegate void ReadinessChangedEventHandler(object sender, ReadinessChangedEventArgs e);
    public class ReadinessChangedEventArgs: EventArgs
    {
      public bool Ready { get; set; }
    }

    public event ErrorEventHandler? Error;
    public delegate void ErrorEventHandler(object sender, ErrorEventArgs e);
    public class ErrorEventArgs: EventArgs
    {
      public string? Message { get; set; }
      public Error.Types.Severity Severity { get; set; }
    }

    public event MetricsEventHandler? ShotMetrics;
    public delegate void MetricsEventHandler(object sender, MetricsEventArgs e);
    public class MetricsEventArgs: EventArgs
    {
      public Metrics? Metrics { get; set; }
    }

    public LaunchMonitorDevice(BluetoothDevice device) : base(device)
    {

    }

    public override bool Setup()
    {
      if (DebugLogging)
        BaseLogger.LogDebug("Subscribing to measurement service");
      GattService measService = Device.Gatt.GetPrimaryServiceAsync(MEASUREMENT_SERVICE_UUID).WaitAsync(TimeSpan.FromSeconds(5)).Result;
      GattCharacteristic measCharacteristic = measService.GetCharacteristicAsync(MEASUREMENT_CHARACTERISTIC_UUID).WaitAsync(TimeSpan.FromSeconds(5)).Result;
      if (!measCharacteristic.StartNotificationsAsync().Wait(TimeSpan.FromSeconds(5)))
      {
        BluetoothLogger.Error("Error subscribing to measurement characteristic");
      }

      // Raw measurement packets (useful for comparing Windows vs Linux)
      measCharacteristic.CharacteristicValueChanged += (o, e) =>
      {
        if (!DebugLogging)
          return;

        if (e.Value == null || e.Value.Length == 0)
        {
          BluetoothLogger.Info("Windows Raw Measurement: empty notification");
          return;
        }

        if (e.Value.Length >= 6)
        {
          byte packetType = e.Value[0];
          byte sequenceOrFlags = e.Value[1];
          uint shotId = BitConverter.ToUInt32(e.Value, 2);
          BluetoothLogger.Info($"Windows Raw Measurement: type=0x{packetType:X2}, seq/flags=0x{sequenceOrFlags:X2}, shotId={shotId}, length={e.Value.Length}");
        }
        else
        {
          BluetoothLogger.Info($"Windows Raw Measurement: length={e.Value.Length}");
        }

        BluetoothLogger.Info($"Windows Raw Measurement Hex: {BitConverter.ToString(e.Value)}");
        if (e.Value.Length > 6)
        {
          var payload = e.Value.Skip(6).ToArray();
          BluetoothLogger.Info($"Windows Raw Measurement Payload: {BitConverter.ToString(payload)}");
        }

        // Parse raw measurement packets using the Linux parser for cross-validation.
        var rawMetrics = rawMeasurementParser.ProcessPacket(e.Value);
        if (DebugLogging && rawMetrics != null)
        {
          rawMetricsByShot[rawMetrics.ShotId] = rawMetrics;
          TryLogRawVsProto(rawMetrics.ShotId);
        }
      };
      if (DebugLogging)
        BaseLogger.LogDebug("Subscribing to control service");
      GattCharacteristic controlPoint = measService.GetCharacteristicAsync(CONTROL_POINT_CHARACTERISTIC_UUID).WaitAsync(TimeSpan.FromSeconds(5)).Result;
      if (!controlPoint.StartNotificationsAsync().Wait(TimeSpan.FromSeconds(5)))
      {
        BluetoothLogger.Error("Error subscribing to the control characteristic");
      }
      // Response to waiting device through controlPointInterface. Unused for now
      controlPoint.CharacteristicValueChanged += (o, e) => { };

      if (DebugLogging)
        BaseLogger.LogDebug("Subscribing to status service");
      GattCharacteristic statusCharacteristic = measService.GetCharacteristicAsync(STATUS_CHARACTERISTIC_UUID).WaitAsync(TimeSpan.FromSeconds(5)).Result;
      if (!statusCharacteristic.StartNotificationsAsync().Wait(TimeSpan.FromSeconds(5)))
      {
        BluetoothLogger.Error("Error subscribing to the status characteristic");
      }
      statusCharacteristic.CharacteristicValueChanged += (o, e) =>
      {
        bool isAwake = e.Value[1] == (byte)0;
        bool isReady = e.Value[2] == (byte)0;

        // the following is unused in favor of the status change notifications and wake control provided by the protobuf service
        // if (!isAwake)
        // {
        //   controlPoint.WriteValueWithResponseAsync(new byte[] { 0x00 }).Wait();
        // }
      };


      bool baseSetupSuccess = base.Setup();
      if (!baseSetupSuccess)
      {
        BluetoothLogger.Error("Error during base device setup");
        return false;
      }


      WakeDevice();
      CurrentState = StatusRequest() ?? StateType.Error;
      DeviceTilt = GetDeviceTilt();
      SubscribeToAlerts().First();

      if (CalibrateTiltOnConnect)
        StartTiltCalibration();

      return true;
    }

    public override void HandleProtobufRequest(IMessage request)
    {
      if (request is WrapperProto WrapperProtoRequest)
      {
        AlertDetails notification = WrapperProtoRequest.Event.Notification.AlertNotification_;
        if (notification.State != null)
        {
          CurrentState = notification.State.State_;
          if (notification.State.State_ == StateType.Standby)
          {
            if (AutoWake)
            {
              BluetoothLogger.Info("Device asleep. Sending wakeup call");
              WakeDevice();
            }
            else
            {
              BluetoothLogger.Error("Device asleep. Wake device using button (or enable autowake in settings)");
            }
          }
        }
        if (notification.Error != null && notification.Error.HasCode)
        {
          Error?.Invoke(this, new ErrorEventArgs() { Message = $"{notification.Error.Code.ToString()} {notification.Error.DeviceTilt}", Severity = notification.Error.Severity });
        }
        if (notification.Metrics != null)
        {
          if (ProcessedShotIDs.Contains(notification.Metrics.ShotId))
          {
            BluetoothLogger.Error($"Received duplicate shot data {notification.Metrics.ShotId}.  Ignoring");
          }
          else
          {
            ProcessedShotIDs.Add(notification.Metrics.ShotId);
            ShotMetrics?.Invoke(this, new MetricsEventArgs() { Metrics = notification.Metrics });
          }

          if (DebugLogging)
          {
            protoMetricsByShot[notification.Metrics.ShotId] = notification.Metrics;
            TryLogRawVsProto(notification.Metrics.ShotId);
          }
        }
        if (notification.TiltCalibration != null)
        {
          DeviceTilt = GetDeviceTilt();
        }
      }
    }

    public Tilt? GetDeviceTilt()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { TiltRequest = new TiltRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.TiltResponse.Tilt;
      
      return null;
    }

    private void TryLogRawVsProto(uint shotId)
    {
      if (!DebugLogging)
        return;

      if (!rawMetricsByShot.TryGetValue(shotId, out var raw))
        return;
      if (!protoMetricsByShot.TryGetValue(shotId, out var proto))
        return;

      rawMetricsByShot.TryRemove(shotId, out _);
      protoMetricsByShot.TryRemove(shotId, out _);

      float? rawLa = raw.BallMetrics?.LaunchAngle;
      float? protoLa = proto.BallMetrics?.LaunchAngle;
      float? rawLd = raw.BallMetrics?.LaunchDirection;
      float? protoLd = proto.BallMetrics?.LaunchDirection;
      float? rawAoA = raw.ClubMetrics?.AttackAngle;
      float? protoAoA = proto.ClubMetrics?.AttackAngle;

      string Format(float? value) => value.HasValue ? value.Value.ToString("F4") : "null";
      string Diff(float? a, float? b) => (a.HasValue && b.HasValue) ? (a.Value - b.Value).ToString("F4") : "null";

      BluetoothLogger.Info($"Raw/Proto compare (shot {shotId}): VLA raw={Format(rawLa)} proto={Format(protoLa)} diff={Diff(rawLa, protoLa)}");
      BluetoothLogger.Info($"Raw/Proto compare (shot {shotId}): HLA raw={Format(rawLd)} proto={Format(protoLd)} diff={Diff(rawLd, protoLd)} | AoA raw={Format(rawAoA)} proto={Format(protoAoA)} diff={Diff(rawAoA, protoAoA)}");
    }

    public ResponseStatus? WakeDevice()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { WakeUpRequest = new WakeUpRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.WakeUpResponse.Status;

      return null;
    }

    public StateType? StatusRequest()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { StatusRequest = new StatusRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.StatusResponse.State.State_;

      return null;
    }

    public List<AlertStatusMessage> SubscribeToAlerts()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto()
        {
          Event = new EventSharing()
          {
            SubscribeRequest = new SubscribeRequest()
            {
              Alerts = { new List<AlertMessage>() { new AlertMessage() { Type = AlertNotification.Types.AlertType.LaunchMonitor } } }
            }
          }
        }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Event.SubscribeRespose.AlertStatus.ToList();

      return new List<AlertStatusMessage>();

    }

    public bool ShotConfig(float temperature, float humidity, float altitude, float airDensity, float teeRange)
    {
      IMessage? resp = SendProtobufRequest(new WrapperProto()
      {
        Service = new LaunchMonitorService()
        {
          ShotConfigRequest = new ShotConfigRequest()
          {
            Temperature = temperature,
            Humidity = humidity,
            Altitude = altitude,
            AirDensity = airDensity,
            TeeRange = teeRange
          }
        }
      });

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.ShotConfigResponse.Success;

      return false;
    }

    public ResetTiltCalibrationResponse.Types.Status? ResetTiltCalibrartion(bool shouldReset = true)
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { ResetTiltCalRequest = new ResetTiltCalibrationRequest() { ShouldReset = shouldReset } } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.ResetTiltCalResponse.Status;

      return null;
    }

    public StartTiltCalibrationResponse.Types.CalibrationStatus? StartTiltCalibration(bool shouldReset = true)
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { StartTiltCalRequest = new StartTiltCalibrationRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.StartTiltCalResponse.Status;

      return null;
    }

    protected override void Dispose(bool disposing)
    {
      foreach (var d in ReadinessChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        ReadinessChanged -= (d as ReadinessChangedEventHandler);

      foreach (var d in Error?.GetInvocationList() ?? Array.Empty<Delegate>())
        Error -= (d as ErrorEventHandler);

      foreach (var d in ShotMetrics?.GetInvocationList() ?? Array.Empty<Delegate>())
        ShotMetrics -= (d as MetricsEventHandler);

      base.Dispose(disposing);
    }
  }
}
