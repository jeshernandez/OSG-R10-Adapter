namespace gspro_r10
{
  public class SimulatorLogger
  {
    private readonly string component;
    private readonly ConsoleColor color;

    public SimulatorLogger(string component, ConsoleColor color)
    {
      this.component = component;
      this.color = color;
    }

    public void Info(string message) => Log(message, LogMessageType.Informational);
    public void Error(string message) => Log(message, LogMessageType.Error);
    public void Outgoing(string message) => Log(message, LogMessageType.Outgoing);
    public void Incoming(string message) => Log(message, LogMessageType.Incoming);

    private void Log(string message, LogMessageType type)
    {
      BaseLogger.LogMessage(message, component, type, color);
    }
  }
}
