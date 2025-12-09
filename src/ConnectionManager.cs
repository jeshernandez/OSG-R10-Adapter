using System.Text.Json;
using System.Text.Json.Serialization;
using gspro_r10.OpenConnect;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace gspro_r10
{
  public class ConnectionManager: IDisposable
  {
    private R10ConnectionServer? R10Server;
    private List<IOpenConnectClient> OpenConnectClients = new List<IOpenConnectClient>();
    private BluetoothConnection? BluetoothConnection { get; }
    internal HttpPuttingServer? PuttingConnection { get; }
    public event ClubChangedEventHandler? ClubChanged;
    public delegate void ClubChangedEventHandler(object sender, ClubChangedEventArgs e);
    public class ClubChangedEventArgs: EventArgs
    {
      public Club Club { get; set; }
    }

    private JsonSerializerOptions serializerSettings = new JsonSerializerOptions()
    {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private int shotNumber = 0;
    private bool disposedValue;
    private readonly SimulatorType? simulatorOverride;

    public ConnectionManager(IConfigurationRoot configuration, SimulatorType? simulatorOverride = null)
    {
      this.simulatorOverride = simulatorOverride;
      AddOpenConnectClient(configuration.GetSection("openConnect"));

      IConfigurationSection secondaryOpenConnect = configuration.GetSection("secondaryOpenConnect");
      if (bool.Parse(secondaryOpenConnect["enabled"] ?? "false"))
      {
        AddOpenConnectClient(secondaryOpenConnect);
      }

      if (bool.Parse(configuration.GetSection("r10E6Server")["enabled"] ?? "false"))
      {
        R10Server = new R10ConnectionServer(this, configuration.GetSection("r10E6Server"));
        R10Server.Start();
      }

      if (bool.Parse(configuration.GetSection("bluetooth")["enabled"] ?? "false"))
        BluetoothConnection = new BluetoothConnection(this, configuration.GetSection("bluetooth"));

      if (bool.Parse(configuration.GetSection("putting")["enabled"] ?? "false"))
      {
        PuttingConnection = new HttpPuttingServer(this, configuration.GetSection("putting"));
        PuttingConnection.Start();
      }
    }

    private void AddOpenConnectClient(IConfigurationSection configurationSection)
    {
      SimulatorType simulator = DetermineSimulator(configurationSection);
      IOpenConnectClient client = simulator switch
      {
        SimulatorType.OpenShotGolf => new OpenShotGolfClient(this, configurationSection),
        _ => new OpenConnectClient(this, configurationSection)
      };

      OpenConnectClients.Add(client);
      client.ConnectAsync();
      BaseLogger.LogMessage($"Configured {simulator} connection to {(configurationSection["ip"] ?? "127.0.0.1")}:{configurationSection["port"]}", "Main");
    }

    private SimulatorType DetermineSimulator(IConfigurationSection configurationSection)
    {
      if (simulatorOverride.HasValue)
        return simulatorOverride.Value;

      if (int.TryParse(configurationSection["port"], out int port))
      {
        if (port == 49152)
          return SimulatorType.OpenShotGolf;
        if (port == 921)
          return SimulatorType.GSPro;
      }

      // Default to GSPro if unrecognized
      return SimulatorType.GSPro;
    }

    internal void SendShot(OpenConnect.BallData? ballData, OpenConnect.ClubData? clubData)
    {
      string openConnectMessage = JsonSerializer.Serialize(OpenConnectApiMessage.CreateShotData(
        shotNumber++,
        ballData,
        clubData
      ), serializerSettings);

      OpenConnectClients.ForEach(client => client.SendAsync(openConnectMessage));
    }

    public void ClubUpdate(Club club)
    {
      Task.Run(() => {
        ClubChanged?.Invoke(this, new ClubChangedEventArgs()
        {
          Club = club
        });
      });

    }

    internal void SendLaunchMonitorReadyUpdate(bool deviceReady)
    {
      OpenConnectClients.ForEach(client => client.SetDeviceReady(deviceReady));
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          R10Server?.Dispose();
          PuttingConnection?.Dispose();
          BluetoothConnection?.Dispose();
          OpenConnectClients.ForEach(client => {
            client?.DisconnectAndStop();
            client?.Dispose();
          });
        }
        disposedValue = true;
      }
    }

    public void Dispose()
    {
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }
  }
}
