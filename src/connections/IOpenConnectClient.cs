namespace gspro_r10
{
  public interface IOpenConnectClient : IDisposable
  {
    bool ConnectAsync();
    void DisconnectAndStop();
    void SetDeviceReady(bool deviceReady);
    bool SendAsync(string message);
  }
}
