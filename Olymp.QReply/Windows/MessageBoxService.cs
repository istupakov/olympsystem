using System.Windows;

namespace Olymp.QReply.Windows;

public class MessageBoxService : IMessageBoxService
{
    public static IMessageBoxService Default { get; } = new MessageBoxService();

    public void ShowMessage(string msg)
    {
        MessageBox.Show(msg);
    }
}
