using Microsoft.Extensions.Configuration;

namespace gspro_r10
{
  class OpenShotGolfClient : OpenConnectClient
  {
    public OpenShotGolfClient(ConnectionManager connectionManager, IConfigurationSection configuration)
      : base(connectionManager, configuration, new SimulatorLogger("OSG", ConsoleColor.Green))
    {
    }
  }
}
