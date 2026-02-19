using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSNShared
{
    // ─── Packet types ───────────────────────────────────────────────────────────
    public enum PacketType
    {
        // Auth
        Login,
        LoginAck,
        Register,
        RegisterAck,
        Logout,

        // Presence
        PresenceUpdate,       // client -> server: my status changed
        PresenceBroadcast,    // server -> clients: someone's presence changed
        UserList,             // server -> client: full user list on connect

        // Direct messages
        ChatMessage,
        ChatMessageDelivered,
        ChatTyping,

        // Groups
        CreateGroup,
        CreateGroupAck,
        InviteToGroup,
        GroupInviteReceived,
        JoinGroup,
        LeaveGroup,
        GroupMessage,
        GroupList,            // server -> client: all groups user is in
        GroupMemberUpdate,    // group member joined/left

        // Contact management
        AddContact,
        RemoveContact,
        ContactRequest,       // someone wants to add you
        ContactRequestAck,    // accept/deny

        // Server info (LAN discovery)
        ServerAnnounce,       // UDP broadcast from server
        ServerDiscovery,      // UDP broadcast from client seeking servers

        // Nudge
        Nudge,

        // File transfer
        FileSend,             // client -> server: initiate file send to user/group
        FileSendAck,          // server -> sender: fileId assigned
        FileReceive,          // server -> recipient: someone sent you a file
        FileRequest,          // client -> server: request a file by fileId
        FileData,             // server -> client: file bytes (base64)
        ProfilePictureUpdate, // client -> server: upload new profile picture
        ProfilePictureAck,    // server -> client: profile picture stored ok
        RequestProfilePic,    // client -> server: fetch someone's profile pic
        ProfilePicData,       // server -> client: profile picture bytes

        // Error
        Error,

        // Tic-Tac-Toe
        TicTacToe,
        TttListGames,    // client requests list of active games
        TttGameList,     // server responds with active games

        // Gartic (drawing game)
        Gartic,            // all gartic game packets
        GarticLobbyList,   // client requests list of open lobbies
        GarticLobbies,     // server responds with lobbies

        // Gartic Phone
        GarticPhone,           // all gartic phone packets
        GarticPhoneLobbyList,  // client requests list of phone lobbies
        GarticPhoneLobbies,    // server responds with phone lobbies

        // Stickers
        StickerSend,

        // Ping/pong
        Ping,
        Pong,
    }

    public enum UserStatus
    {
        Online,
        Away,
        Busy,
        AppearOffline,
        Offline
    }

    // ─── Base packet ────────────────────────────────────────────────────────────
    public class Packet
    {
        [JsonPropertyName("t")] public PacketType Type { get; set; }
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        [JsonPropertyName("ts")] public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        [JsonPropertyName("d")] public JsonElement? Data { get; set; }

        public static Packet Create<T>(PacketType type, T data)
        {
            return new Packet
            {
                Type = type,
                Data = JsonSerializer.SerializeToElement(data)
            };
        }

        public T? GetData<T>() where T : class
        {
            if (Data is null) return null;
            return JsonSerializer.Deserialize<T>(Data.Value.GetRawText());
        }

        public string Serialize() => JsonSerializer.Serialize(this) + "\n";

        public static Packet? Deserialize(string json)
        {
            try { return JsonSerializer.Deserialize<Packet>(json); }
            catch { return null; }
        }
    }

    // ─── Data payloads ──────────────────────────────────────────────────────────
    public class LoginData
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public UserStatus Status { get; set; } = UserStatus.Online;
        public string PersonalMessage { get; set; } = "";
    }

    public class LoginAckData
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string SessionId { get; set; } = "";
        public UserInfo? User { get; set; }
    }

    public class RegisterData
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public class RegisterAckData
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    public class UserInfo
    {
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public UserStatus Status { get; set; } = UserStatus.Offline;
        public string PersonalMessage { get; set; } = "";
        public string AvatarEmoji { get; set; } = "🙂";
        public bool HasProfilePicture { get; set; }
        public string ProfilePicFileId { get; set; } = "";
        public List<string> Contacts { get; set; } = new();
        public List<string> Groups { get; set; } = new();
    }

    public class PresenceData
    {
        public string Username { get; set; } = "";
        public UserStatus Status { get; set; }
        public string PersonalMessage { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AvatarEmoji { get; set; } = "🙂";
        public bool HasProfilePicture { get; set; }
        public string ProfilePicFileId { get; set; } = "";
        public bool IsInGame { get; set; }
        public string GameId { get; set; } = "";
    }

    public class ChatMessageData
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";       // username (direct) or groupId
        public string Content { get; set; } = "";
        public string FontFamily { get; set; } = "Tahoma";
        public int FontSize { get; set; } = 10;
        public string Color { get; set; } = "#000080";
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public bool IsGroup { get; set; }
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public class TypingData
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public bool IsGroup { get; set; }
        public bool IsTyping { get; set; }
    }

    public class NudgeData
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public bool IsGroup { get; set; }
        public string GroupId { get; set; } = "";
    }

    public class StickerData
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public bool IsGroup { get; set; }
        public string GroupId { get; set; } = "";
        public string StickerName { get; set; } = "";
        public string StickerBase64 { get; set; } = "";
        public string MimeType { get; set; } = "image/png";
    }

    public class GroupInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Members { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class CreateGroupData
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> InitialMembers { get; set; } = new();
    }

    public class InviteToGroupData
    {
        public string GroupId { get; set; } = "";
        public string Username { get; set; } = "";
    }

    public class GroupInviteData
    {
        public string GroupId { get; set; } = "";
        public string GroupName { get; set; } = "";
        public string InvitedBy { get; set; } = "";
    }

    public class JoinLeaveGroupData
    {
        public string GroupId { get; set; } = "";
        public string Username { get; set; } = "";
        public bool Joined { get; set; }  // true = joined, false = left
    }

    public class GroupMessageData : ChatMessageData
    {
        public string GroupId { get; set; } = "";
        public string GroupName { get; set; } = "";
    }

    public class UserListData
    {
        public List<UserInfo> Users { get; set; } = new();
        public List<GroupInfo> Groups { get; set; } = new();
    }

    public class ContactRequestData
    {
        public string From { get; set; } = "";
        public string FromDisplayName { get; set; } = "";
        public bool Accept { get; set; }
    }

    public class ErrorData
    {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class ServerAnnounceData
    {
        public string ServerName { get; set; } = "MSN Messenger Server";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public int UserCount { get; set; }
        public string Version { get; set; } = "1.0";
    }

    // ─── File Transfer ──────────────────────────────────────────────────────────

    /// <summary>Client tells server: I want to send this file to To (user or group)</summary>
    public class FileSendData
    {
        public string To { get; set; } = "";          // username or groupId
        public bool IsGroup { get; set; }
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public string MimeType { get; set; } = "application/octet-stream";
        public string DataBase64 { get; set; } = "";  // full file encoded as base64
    }

    /// <summary>Server assigns a fileId and notifies sender</summary>
    public class FileSendAckData
    {
        public bool Success { get; set; }
        public string FileId { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>Server tells recipient(s): someone sent you a file</summary>
    public class FileReceiveData
    {
        public string From { get; set; } = "";
        public string FromDisplayName { get; set; } = "";
        public string FileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public string MimeType { get; set; } = "application/octet-stream";
        public bool IsGroup { get; set; }
        public string GroupId { get; set; } = "";
        public string GroupName { get; set; } = "";
        // For small images the server can inline the data immediately
        public string? InlineDataBase64 { get; set; }
    }

    /// <summary>Client requests file bytes by fileId</summary>
    public class FileRequestData
    {
        public string FileId { get; set; } = "";
    }

    /// <summary>Server sends file bytes</summary>
    public class FileDataResponse
    {
        public string FileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string DataBase64 { get; set; } = "";
        public bool Found { get; set; }
    }

    /// <summary>Client uploads a new profile picture</summary>
    public class ProfilePictureUpdateData
    {
        public string MimeType { get; set; } = "image/png";
        public string DataBase64 { get; set; } = "";
    }

    public class ProfilePictureAckData
    {
        public bool Success { get; set; }
        public string FileId { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>Client requests someone's profile picture</summary>
    public class RequestProfilePicData
    {
        public string Username { get; set; } = "";
    }

    /// <summary>Server responds with profile picture bytes</summary>
    public class ProfilePicDataResponse
    {
        public string Username { get; set; } = "";
        public string FileId { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string DataBase64 { get; set; } = "";
        public bool Found { get; set; }
    }

    // Helpers
    public static class MimeTypes
    {
        public static bool IsImage(string mime) =>
            mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        public static string FromFileName(string filename)
        {
            var ext = Path.GetExtension(filename).ToLower();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".zip" => "application/zip",
                ".mp3" => "audio/mpeg",
                ".mp4" => "video/mp4",
                _ => "application/octet-stream"
            };
        }

        public static string FriendlySize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }
    }
    // ─── Tic-Tac-Toe ────────────────────────────────────────────────────────────

    public enum TttMsgType
    {
        Invite,          // challenger -> server -> opponent
        InviteAccept,    // opponent -> server -> challenger
        InviteDecline,   // opponent -> server -> challenger
        Move,            // player -> server -> opponent + spectators
        GameState,       // server -> new spectator: current board
        GameOver,        // server -> players + spectators
        SpectateRequest, // client -> server: I want to watch gameId
        SpectateJoin,    // server -> spectator: here is current state
        SpectateLeave,   // spectator left
        Abandon,         // player quit mid-game
    }

    public class TttPacket
    {
        public TttMsgType Msg { get; set; }
        public string GameId { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public int[]? Board { get; set; }
        public int Cell { get; set; } = -1;
        public int Winner { get; set; } = 0;        // 0=none 1=X 2=O 3=draw
        public int[]? WinLine { get; set; }
        public bool IsXTurn { get; set; } = true;
        public List<string> Spectators { get; set; } = new();
    }

    public class TttGameInfo
    {
        public string GameId { get; set; } = "";
        public string PlayerX { get; set; } = "";
        public string PlayerO { get; set; } = "";
        public string PlayerXDisplay { get; set; } = "";
        public string PlayerODisplay { get; set; } = "";
        public int[] Board { get; set; } = new int[9];
        public bool IsXTurn { get; set; } = true;
        public bool IsOver { get; set; }
        public List<string> Spectators { get; set; } = new();
    }

    // ─── Gartic (Drawing Game) ───────────────────────────────────────────────

    public enum GarticMsgType
    {
        CreateLobby,    // player creates a new lobby
        LobbyState,     // server → all: current lobby state
        JoinLobby,      // player joins existing lobby
        LeaveLobby,     // player leaves lobby
        StartGame,      // host starts the game
        DrawData,       // drawing strokes from drawer → server → all
        ClearCanvas,    // drawer clears the canvas
        ChatGuess,      // player submits a guess
        ChatMessage,    // system / chat messages
        RoundState,     // server → all: round info
        CorrectGuess,   // server → all: someone guessed correctly
        WordReveal,     // server → all: reveal word at end of round
        GameOver,       // server → all: final scores
        NextRound,      // server → all: advance to next round
    }

    public class GarticPacket
    {
        public GarticMsgType Msg { get; set; }
        public string LobbyId { get; set; } = "";
        public string From { get; set; } = "";
        // Lobby
        public string LobbyName { get; set; } = "";
        public int MaxPlayers { get; set; } = 8;
        public int RoundCount { get; set; } = 3;
        public int RoundTimeSeconds { get; set; } = 60;
        public List<string> Players { get; set; } = new();
        public Dictionary<string, string> PlayerDisplayNames { get; set; } = new();
        public Dictionary<string, int> Scores { get; set; } = new();
        public string Host { get; set; } = "";
        public bool GameStarted { get; set; }
        public string Language { get; set; } = "en";
        // Drawing
        public string? DrawDataJson { get; set; }
        // Chat / Guess
        public string Message { get; set; } = "";
        public string DisplayName { get; set; } = "";
        // Round
        public string CurrentDrawer { get; set; } = "";
        public string WordHint { get; set; } = "";
        public string Word { get; set; } = "";
        public int Round { get; set; }
        public int TotalRounds { get; set; }
        public int TimeLeft { get; set; }
        public string Guesser { get; set; } = "";
    }

    public class GarticLobbyInfo
    {
        public string LobbyId { get; set; } = "";
        public string LobbyName { get; set; } = "";
        public string Host { get; set; } = "";
        public string HostDisplayName { get; set; } = "";
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public bool GameStarted { get; set; }
    }

    // ─── Gartic Phone ────────────────────────────────────────────────────────

    public enum GarticPhoneMsgType
    {
        CreateLobby,
        LobbyState,
        JoinLobby,
        LeaveLobby,
        StartGame,
        PhaseState,         // server → player: your current phase (write / draw / describe / wait)
        SubmitPhrase,       // player → server: initial phrase to draw
        SubmitDrawing,      // player → server: base64 drawing
        SubmitDescription,  // player → server: text description
        ChainResult,        // server → all: one complete chain reveal
        NextChain,          // host → server: advance to next chain in reveal
        GameOver,           // server → all: all chains shown, game over
    }

    public class GarticPhonePacket
    {
        public GarticPhoneMsgType Msg { get; set; }
        public string LobbyId { get; set; } = "";
        public string From { get; set; } = "";
        // Lobby
        public string LobbyName { get; set; } = "";
        public int MaxPlayers { get; set; } = 8;
        public int DrawTimeSeconds { get; set; } = 60;
        public int DescribeTimeSeconds { get; set; } = 30;
        public List<string> Players { get; set; } = new();
        public Dictionary<string, string> PlayerDisplayNames { get; set; } = new();
        public string Host { get; set; } = "";
        public bool GameStarted { get; set; }
        public string Language { get; set; } = "en";
        // Phase
        public string PhaseType { get; set; } = "";  // "draw" or "describe"
        public int PhaseIndex { get; set; }           // which step in the chain
        public int TotalPhases { get; set; }
        public int TimeLeft { get; set; }
        public string Prompt { get; set; } = "";      // word to draw or description to draw
        public string DrawingBase64 { get; set; } = ""; // drawing to describe (base64 PNG)
        public string Description { get; set; } = "";   // submitted description
        // Chain result
        public string ChainOwner { get; set; } = "";    // who started this chain
        public string ChainOwnerDisplay { get; set; } = "";
        public List<GarticPhoneChainStep> ChainSteps { get; set; } = new();
        public int ChainIndex { get; set; }             // which chain (0-based)
        public int TotalChains { get; set; }            // total chains to reveal
        // Message
        public string Message { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public class GarticPhoneChainStep
    {
        public string Player { get; set; } = "";
        public string PlayerDisplay { get; set; } = "";
        public string Type { get; set; } = "";         // "word", "drawing", "description"
        public string Content { get; set; } = "";       // the word, base64 image, or description text
    }

    public class GarticPhoneLobbyInfo
    {
        public string LobbyId { get; set; } = "";
        public string LobbyName { get; set; } = "";
        public string Host { get; set; } = "";
        public string HostDisplayName { get; set; } = "";
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public bool GameStarted { get; set; }
    }

}
