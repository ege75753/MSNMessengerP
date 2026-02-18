using System.Net.Sockets;
using System.Text;
using MSNShared;

namespace MSNServer
{
    public class ConnectedClient
    {
        public string? Username { get; set; }
        public string? SessionId { get; set; }
        public UserStatus Status { get; set; } = UserStatus.Online;
        public string PersonalMessage { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AvatarEmoji { get; set; } = "🙂";
        public TcpClient TcpClient { get; }
        public NetworkStream Stream { get; }
        public DateTime ConnectedAt { get; } = DateTime.UtcNow;
        public bool IsAuthenticated => Username != null;

        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ConnectedClient(TcpClient client)
        {
            TcpClient = client;
            Stream = client.GetStream();
        }

        public async Task SendAsync(Packet packet)
        {
            await _sendLock.WaitAsync();
            try
            {
                var data = Encoding.UTF8.GetBytes(packet.Serialize());
                await Stream.WriteAsync(data);
            }
            catch
            {
                // Client disconnected
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public UserInfo ToUserInfo(DataStore store)
        {
            var stored = store.GetUser(Username ?? "");
            return new UserInfo
            {
                Username = Username ?? "",
                DisplayName = DisplayName,
                Email = stored?.Email ?? "",
                Status = Status,
                PersonalMessage = PersonalMessage,
                AvatarEmoji = AvatarEmoji,
                HasProfilePicture = !string.IsNullOrEmpty(stored?.ProfilePicFileId),
                ProfilePicFileId = stored?.ProfilePicFileId ?? "",
                Contacts = stored?.Contacts ?? new(),
                Groups = stored?.Groups ?? new()
            };
        }

        public void Close()
        {
            try { Stream.Close(); } catch { }
            try { TcpClient.Close(); } catch { }
        }
    }
}
