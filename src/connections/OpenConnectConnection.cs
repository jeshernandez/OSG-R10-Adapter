using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using gspro_r10.OpenConnect;
using Microsoft.Extensions.Configuration;
using TcpClient = NetCoreServer.TcpClient;

namespace gspro_r10
{
  class OpenConnectClient : TcpClient, IOpenConnectClient
  {
    public Timer? PingTimer { get; private set; }
    public bool InitiallyConnected { get; private set; }
    public ConnectionManager ConnectionManager { get; }
    private SimulatorLogger Logger { get; }
    private bool _stop;

    public OpenConnectClient(ConnectionManager connectionManager, IConfigurationSection configuration, SimulatorLogger? logger = null)
      : base(configuration["ip"] ?? "127.0.0.1", int.Parse(configuration["port"] ?? "921"))
    {
      ConnectionManager = connectionManager;
      Logger = logger ?? new SimulatorLogger("GSPro", ConsoleColor.Green);
    }

    public void DisconnectAndStop()
    {
      _stop = true;
      DisconnectAsync();
      while (IsConnected)
        Thread.Yield();
    }

    protected override void OnConnected()
    {
      InitiallyConnected = true;
      Logger.Info($"TCP client connected a new session with Id {Id}");
      PingTimer = new Timer(SendPing, null, 0, 0);
    }

    private void SendPing(object? state)
    {
      SendAsync(JsonSerializer.Serialize(OpenConnectApiMessage.CreateHeartbeat()));
    }

    public void SetDeviceReady(bool deviceReady)
    {
      SendAsync(JsonSerializer.Serialize(OpenConnectApiMessage.CreateHeartbeat(deviceReady)));
    }

    public override bool ConnectAsync()
    {
      Logger.Info($"Connecting to OpenConnect api ({Address}:{Port})...");
      return base.ConnectAsync();
    }

    public override bool SendAsync(string message)
    {
      Logger.Outgoing(message);
      return base.SendAsync(message);
    }

    protected override void OnDisconnected()
    {
      if (InitiallyConnected)
        Logger.Error($"TCP client disconnected a session with Id {Id}");

      Thread.Sleep(5000);
      if (!_stop)
        ConnectAsync();
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
      string received = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
      Logger.Incoming(received);

      // Sometimes multiple responses received in one buffer. Convert to list format to handle
      // ie "{one}{two}" => "[{one},{two}]"
      string listReceived = $"[{received.Replace("}{", "},{")}]";
      try
      {
        List<OpenConnectApiResponse> responses = JsonSerializer.Deserialize<List<OpenConnectApiResponse>>(listReceived) ?? new List<OpenConnectApiResponse>();
        foreach(OpenConnectApiResponse resp in responses)
        {
          HandleResponse(resp);
        }
      }
      catch
      {
        Logger.Error("Error parsing response");
      }
    }

    private void HandleResponse(OpenConnectApiResponse response)
    {
      if (response.Player != null && response.Player.Club != null)
      {
        ConnectionManager.ClubUpdate(response.Player.Club.Value);
      }
    }

    protected override void OnError(SocketError error)
    {
      if (error != SocketError.TimedOut)
        Logger.Error($"TCP client caught an error with code {error}");
    }
  }
}
