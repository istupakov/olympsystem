using System.Windows;

namespace Olymp.QReply.Windows;

public class AppService : IAppService
{
    public void Shutdown()
    {
        Application.Current.Shutdown();
    }
}
