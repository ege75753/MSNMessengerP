using System.Collections.ObjectModel;
using System.ComponentModel;
using MSNShared;

namespace MSNClient
{
    public class ContactVm : INotifyPropertyChanged
    {
        private UserStatus _status = UserStatus.Offline;
        private string _personalMessage = "";
        private string _displayName = "";
        private string _avatarEmoji = "🙂";
        private bool _isTyping;
        private bool _hasProfilePicture;
        private string _profilePicFileId = "";
        private System.Windows.Media.ImageSource? _profilePicture;

        public string Username { get; set; } = "";
        public string Email { get; set; } = "";

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(nameof(DisplayName)); }
        }
        public UserStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
        public string PersonalMessage
        {
            get => _personalMessage;
            set { _personalMessage = value; OnPropertyChanged(nameof(PersonalMessage)); }
        }
        public string AvatarEmoji
        {
            get => _avatarEmoji;
            set { _avatarEmoji = value; OnPropertyChanged(nameof(AvatarEmoji)); }
        }
        public bool IsTyping
        {
            get => _isTyping;
            set { _isTyping = value; OnPropertyChanged(nameof(IsTyping)); OnPropertyChanged(nameof(StatusText)); }
        }
        public bool HasProfilePicture
        {
            get => _hasProfilePicture;
            set { _hasProfilePicture = value; OnPropertyChanged(nameof(HasProfilePicture)); }
        }
        public string ProfilePicFileId
        {
            get => _profilePicFileId;
            set { _profilePicFileId = value; OnPropertyChanged(nameof(ProfilePicFileId)); }
        }
        public System.Windows.Media.ImageSource? ProfilePicture
        {
            get => _profilePicture;
            set { _profilePicture = value; OnPropertyChanged(nameof(ProfilePicture)); OnPropertyChanged(nameof(HasProfilePicture)); }
        }

        // Tic-Tac-Toe game status
        private bool _isInGame;
        private string _gameId = "";
        public bool IsInGame { get => _isInGame; set { _isInGame = value; OnPropertyChanged(nameof(IsInGame)); } }
        public string GameId { get => _gameId; set { _gameId = value; OnPropertyChanged(nameof(GameId)); } }

        public string StatusText
        {
            get
            {
                if (IsTyping) return "typing...";
                return Status switch
                {
                    UserStatus.Online => "Online",
                    UserStatus.Away => "Away",
                    UserStatus.Busy => "Busy",
                    UserStatus.AppearOffline => "Appear Offline",
                    UserStatus.Offline => "Offline",
                    _ => ""
                };
            }
        }

        public string StatusColor => Status switch
        {
            UserStatus.Online => "#2ECC40",
            UserStatus.Away => "#FFAA00",
            UserStatus.Busy => "#E53E3E",
            UserStatus.AppearOffline => "#AAAAAA",
            UserStatus.Offline => "#AAAAAA",
            _ => "#AAAAAA"
        };

        public static ContactVm From(UserInfo u) => new()
        {
            Username = u.Username,
            DisplayName = string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName,
            Email = u.Email,
            Status = u.Status,
            PersonalMessage = u.PersonalMessage,
            AvatarEmoji = u.AvatarEmoji,
            HasProfilePicture = u.HasProfilePicture,
            ProfilePicFileId = u.ProfilePicFileId
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class GroupVm : INotifyPropertyChanged
    {
        private string _name = "";
        public string Id { get; set; } = "";
        public string Name { get => _name; set { _name = value; OnPC(nameof(Name)); } }
        public string Owner { get; set; } = "";
        public string Description { get; set; } = "";
        public ObservableCollection<string> Members { get; set; } = new();

        public static GroupVm From(GroupInfo g) => new()
        {
            Id = g.Id,
            Name = g.Name,
            Owner = g.Owner,
            Description = g.Description,
            Members = new ObservableCollection<string>(g.Members)
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class ClientState : INotifyPropertyChanged
    {
        public NetworkClient Net { get; } = new();

        // ── Me ────────────────────────────────────────────────────────────────
        private string _myUsername = "";
        private string _myDisplayName = "";
        private UserStatus _myStatus = UserStatus.Online;
        private string _myPersonalMessage = "";
        private string _myAvatarEmoji = "🙂";
        private System.Windows.Media.ImageSource? _myProfilePicture;

        public string MyUsername { get => _myUsername; set { _myUsername = value; OnPC(nameof(MyUsername)); } }
        public string MyDisplayName { get => _myDisplayName; set { _myDisplayName = value; OnPC(nameof(MyDisplayName)); } }
        public UserStatus MyStatus { get => _myStatus; set { _myStatus = value; OnPC(nameof(MyStatus)); OnPC(nameof(MyStatusText)); OnPC(nameof(MyStatusColor)); } }
        public string MyPersonalMessage { get => _myPersonalMessage; set { _myPersonalMessage = value; OnPC(nameof(MyPersonalMessage)); } }
        public string MyAvatarEmoji { get => _myAvatarEmoji; set { _myAvatarEmoji = value; OnPC(nameof(MyAvatarEmoji)); } }
        public string MyProfilePicFileId { get; set; } = "";
        public System.Windows.Media.ImageSource? MyProfilePicture
        {
            get => _myProfilePicture;
            set { _myProfilePicture = value; OnPC(nameof(MyProfilePicture)); }
        }

        public string MyStatusText => MyStatus switch
        {
            UserStatus.Online => "Online",
            UserStatus.Away => "Away",
            UserStatus.Busy => "Busy",
            UserStatus.AppearOffline => "Appear Offline",
            _ => "Online"
        };

        public string MyStatusColor => MyStatus switch
        {
            UserStatus.Online => "#2ECC40",
            UserStatus.Away => "#FFAA00",
            UserStatus.Busy => "#E53E3E",
            _ => "#AAAAAA"
        };

        // ── Contacts & Groups ──────────────────────────────────────────────────
        public ObservableCollection<ContactVm> Contacts { get; } = new();
        public ObservableCollection<GroupVm> Groups { get; } = new();

        // ── Open chats (username -> window) ───────────────────────────────────
        public Dictionary<string, ChatWindow> OpenChats { get; } = new();
        public Dictionary<string, GroupChatWindow> OpenGroupChats { get; } = new();
        public Dictionary<string, TicTacToeWindow> OpenTttGames { get; } = new();
        public Dictionary<string, GarticWindow> OpenGarticGames { get; } = new();

        // ── My contact list (usernames) ────────────────────────────────────────
        public HashSet<string> MyContacts { get; } = new();
        public HashSet<string> MyGroupIds { get; } = new();

        public void UpdatePresence(PresenceData d)
        {
            if (d.Username == MyUsername) return;

            var existing = Contacts.FirstOrDefault(c => c.Username == d.Username);
            if (existing != null)
            {
                existing.Status = d.Status;
                existing.PersonalMessage = d.PersonalMessage;
                existing.IsInGame = d.IsInGame;
                existing.GameId = d.GameId;
                if (!string.IsNullOrEmpty(d.DisplayName)) existing.DisplayName = d.DisplayName;
                if (!string.IsNullOrEmpty(d.AvatarEmoji)) existing.AvatarEmoji = d.AvatarEmoji;
                // If profile pic changed, invalidate cached image so it gets re-fetched
                if (existing.ProfilePicFileId != d.ProfilePicFileId)
                {
                    existing.ProfilePicFileId = d.ProfilePicFileId;
                    existing.HasProfilePicture = d.HasProfilePicture;
                    existing.ProfilePicture = null; // triggers re-fetch
                }
            }
        }

        public ContactVm? GetContact(string username) =>
            Contacts.FirstOrDefault(c => c.Username == username);

        public GroupVm? GetGroup(string id) =>
            Groups.FirstOrDefault(g => g.Id == id);

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
