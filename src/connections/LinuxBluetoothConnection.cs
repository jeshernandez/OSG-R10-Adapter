using Microsoft.Extensions.Configuration;

namespace gspro_r10
{
  // Scaffold for a Linux-specific Bluetooth connection using BlueZ.
  // TODO: replace stub implementation with a BlueZ.NET-backed connector.
  public class LinuxBluetoothConnection : IDisposable
  {
    private bool disposedValue;

    public ConnectionManager ConnectionManager { get; }
    public IConfigurationSection Configuration { get; }
    public int ReconnectInterval { get; }

    public LinuxBluetoothConnection(ConnectionManager connectionManager, IConfigurationSection configuration)
    {
      ConnectionManager = connectionManager;
      Configuration = configuration;
      ReconnectInterval = int.Parse(configuration["reconnectInterval"] ?? "5");

      BluetoothLogger.Info("Initializing Linux Bluetooth provider (BlueZ)");
      Task.Run(ConnectToDevice);
    }

    private void ConnectToDevice()
    {
      // TODO: implement BlueZ.NET discovery/connection and wire into LaunchMonitor pipeline.
      BluetoothLogger.Error("BlueZ Linux Bluetooth connection not implemented yet. This is a scaffold.");
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose of BlueZ resources when implemented
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
