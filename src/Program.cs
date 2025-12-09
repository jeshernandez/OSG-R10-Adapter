using Microsoft.Extensions.Configuration;

namespace gspro_r10
{
  class Program
  {
    public static void Main(string[] args)
    {
      SimulatorType? simulatorOverride = ParseSimulatorArgument(args);

      IConfigurationBuilder builder = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory());

      if (File.Exists(Path.Join(Directory.GetCurrentDirectory(), "settings.json")))
      {
        builder.AddJsonFile("settings.json");
      }
      else
      {
        BaseLogger.LogMessage($"settings.json file not found or could not be opened in {Directory.GetCurrentDirectory()}", "Main", LogMessageType.Error);
      }

      IConfigurationRoot configuration = builder.Build();

      Console.Title = "GSP-R10 Connect";
      BaseLogger.LogMessage("GSP - R10 Bridge starting. Press enter key to close", "Main");
      if (simulatorOverride.HasValue)
      {
        BaseLogger.LogMessage($"Simulator override set to {simulatorOverride.Value}", "Main");
      }
      ConnectionManager manager = new ConnectionManager(configuration, simulatorOverride);
      Console.ReadLine();
      BaseLogger.LogMessage("Shutting down...", "Main");
      manager.Dispose();
      BaseLogger.LogMessage("Exiting...", "Main");
    }

    private static SimulatorType? ParseSimulatorArgument(string[] args)
    {
      for (int i = 0; i < args.Length; i++)
      {
        string current = args[i];

        if (current.Equals("--sim", StringComparison.OrdinalIgnoreCase) ||
            current.Equals("-s", StringComparison.OrdinalIgnoreCase))
        {
          if (i + 1 < args.Length && TryParseSimulator(args[i + 1], out var simulator))
            return simulator;
        }
        else if (current.StartsWith("--sim=", StringComparison.OrdinalIgnoreCase))
        {
          string value = current.Substring("--sim=".Length);
          if (TryParseSimulator(value, out var simulator))
            return simulator;
        }
      }

      return null;
    }

    private static bool TryParseSimulator(string value, out SimulatorType simulator)
    {
      switch (value.ToLowerInvariant())
      {
        case "gspro":
        case "gsp":
          simulator = SimulatorType.GSPro;
          return true;
        case "osg":
        case "openshotgolf":
          simulator = SimulatorType.OpenShotGolf;
          return true;
        default:
          simulator = SimulatorType.GSPro;
          BaseLogger.LogMessage($"Unknown simulator '{value}'. Supported values: gspro, osg.", "Main", LogMessageType.Error);
          return false;
      }
    }
  }
}
