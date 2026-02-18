using System.Windows;

namespace MSNClient
{
    public partial class App : Application
    {
        public static ClientState State { get; } = new();
        public static FileTransferManager FileTransfer { get; } = new(State);
    }
}
