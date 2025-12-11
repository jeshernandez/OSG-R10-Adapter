using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using LaunchMonitor.Proto;
using Linux.Bluetooth.Extensions;
using Linux.Bluetooth;
using static LaunchMonitor.Proto.State.Types;
using static LaunchMonitor.Proto.SubscribeResponse.Types;
using static LaunchMonitor.Proto.WakeUpResponse.Types;

namespace gspro_r10.bluetooth
{
  public class LinuxLaunchMonitorDevice : LinuxBaseDevice
  {
    internal static Guid MEASUREMENT_SERVICE_UUID = Guid.Parse("6A4E3400-667B-11E3-949A-0800200C9A66");
    internal static Guid MEASUREMENT_CHARACTERISTIC_UUID = Guid.Parse("6A4E3401-667B-11E3-949A-0800200C9A66");
    internal static Guid CONTROL_POINT_CHARACTERISTIC_UUID = Guid.Parse("6A4E3402-667B-11E3-949A-0800200C9A66");
    internal static Guid STATUS_CHARACTERISTIC_UUID = Guid.Parse("6A4E3403-667B-11E3-949A-0800200C9A66");

    private HashSet<uint> ProcessedShotIDs = new HashSet<uint>();
    private RawMeasurementParser measurementParser = new RawMeasurementParser();

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

    public LinuxLaunchMonitorDevice(Device device) : base(device)
    {

    }

    private static readonly new TimeSpan GattTimeout = LinuxBaseDevice.GattTimeout;

public override async Task<bool> Setup()
{
  try
  {
    if (DebugLogging)
    {
      BaseLogger.LogDebug("Subscribing to measurement service");

      try
      {
        var properties = await Device.GetPropertiesAsync();
        if (properties.UUIDs != null && properties.UUIDs.Length > 0)
        {
          BaseLogger.LogDebug("Device advertised services:");
          foreach (string uuid in properties.UUIDs)
            BaseLogger.LogDebug($"  - {uuid}");
        }
      }
      catch (Exception ex)
      {
        BaseLogger.LogDebug($"Failed to read device UUID list: {ex.Message}");
      }
    }

    // Measurement service (6a4e3400-...)
    var measurementUuid = MEASUREMENT_SERVICE_UUID.ToString().ToLowerInvariant();
    var measService = await Device.GetServiceAsync(measurementUuid);

    var measCharacteristicUuid = MEASUREMENT_CHARACTERISTIC_UUID.ToString().ToLowerInvariant();
    var measCharacteristic = await measService.GetCharacteristicAsync(measCharacteristicUuid);

    // Start measurement notifications
    await measCharacteristic.StartNotifyAsync();

    // Raw shot payloads come in here – parse and process them
    measCharacteristic.Value += (o, e) =>
    {
      // Parse the raw binary data to extract shot metrics
      var metrics = measurementParser.ProcessPacket(e.Value);
      if (metrics != null)
      {
        if (ProcessedShotIDs.Contains(metrics.ShotId))
        {
          BluetoothLogger.Error($"Received duplicate shot data {metrics.ShotId}. Ignoring");
        }
        else
        {
          ProcessedShotIDs.Add(metrics.ShotId);
          ShotMetrics?.Invoke(this, new MetricsEventArgs() { Metrics = metrics });
        }
      }

      return Task.CompletedTask;
    };

    if (DebugLogging)
      BaseLogger.LogDebug("Subscribing to control service");

    var controlPoint = await measService.GetCharacteristicAsync(
      CONTROL_POINT_CHARACTERISTIC_UUID.ToString().ToLowerInvariant()
    );

    await controlPoint.StartNotifyAsync();

    // Control point responses (protobuf B413 responses don't work on Linux/BlueZ)
    // All necessary data comes through the measurement characteristic instead
    controlPoint.Value += (o, e) =>
    {
      // BlueZ does not receive protobuf responses here like Windows does
      // Leaving handler registered in case future BlueZ versions support it
      return Task.CompletedTask;
    };

    if (DebugLogging)
      BaseLogger.LogDebug("Subscribing to status service");

    var statusCharacteristic = await measService.GetCharacteristicAsync(
      STATUS_CHARACTERISTIC_UUID.ToString().ToLowerInvariant()
    );

    await statusCharacteristic.StartNotifyAsync();

    statusCharacteristic.Value += (o, e) =>
    {
      if (e.Value.Length >= 3)
      {
        bool isAwake = e.Value[1] == (byte)0;
        bool isReady = e.Value[2] == (byte)0;

        if (DebugLogging)
          BluetoothLogger.Info($"Linux LM: Status update: Awake={isAwake}, Ready={isReady}");
      }

      return Task.CompletedTask;
    };

    if (DebugLogging)
      BaseLogger.LogDebug("Measurement subscriptions complete, running base setup");

    bool baseSetupSuccess = await base.Setup();
    if (!baseSetupSuccess)
      throw new Exception("Error during base device setup");

    // Send requests without waiting for responses - Linux uses async notifications
    WakeDevice();
    StatusRequest();
    GetDeviceTilt();
    SubscribeToAlerts();

    // Set a default state for now since we don't get synchronous responses
    CurrentState = StateType.Waiting; // Assume waiting/ready state

    if (CalibrateTiltOnConnect)
    {
      if (DebugLogging)
        BaseLogger.LogDebug("Calibrating tilt on connect");
      StartTiltCalibration();
    }

    BluetoothLogger.Info("R10 connected and ready");
    return true;
  }
  catch (Exception ex)
  {
    BluetoothLogger.Error($"R10 setup failed: {ex.Message}");
    if (DebugLogging)
      BaseLogger.LogDebug(ex.ToString());
    return false;
  }
}


    private T WaitFor<T>(Task<T> task, string description)
    {
      try
      {
        if (DebugLogging)
          BaseLogger.LogDebug($"Waiting for {description}");
        return task.WaitAsync(GattTimeout).GetAwaiter().GetResult();
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"{description} failed - {ex.Message}");
        throw;
      }
    }

    private void WaitFor(Task task, string description)
    {
      try
      {
        if (DebugLogging)
          BaseLogger.LogDebug($"Waiting for {description}");
        task.WaitAsync(GattTimeout).GetAwaiter().GetResult();
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"{description} failed - {ex.Message}");
        throw;
      }
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

    public ResponseStatus? WakeDevice()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { WakeUpRequest = new WakeUpRequest() } }
      );

      BluetoothLogger.Info("Waking device...");
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
