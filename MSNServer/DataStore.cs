using System.Text.Json;
using MSNShared;

namespace MSNServer
{
    public class StoredUser
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string AvatarEmoji { get; set; } = "🙂";
        public string ProfilePicFileId { get; set; } = "";   // empty = no custom pic
        public List<string> Contacts { get; set; } = new();
        public List<string> Groups { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class DataStore
    {
        private readonly string _dataDir;
        private readonly string _usersFile;
        private readonly string _groupsFile;
        private readonly object _lock = new();

        public Dictionary<string, StoredUser> Users { get; private set; } = new();
        public Dictionary<string, GroupInfo> Groups { get; private set; } = new();

        public DataStore(string dataDir = "data")
        {
            _dataDir = dataDir;
            Directory.CreateDirectory(dataDir);
            _usersFile = Path.Combine(dataDir, "users.json");
            _groupsFile = Path.Combine(dataDir, "groups.json");
            Load();
        }

        private void Load()
        {
            if (File.Exists(_usersFile))
            {
                try
                {
                    var json = File.ReadAllText(_usersFile);
                    Users = JsonSerializer.Deserialize<Dictionary<string, StoredUser>>(json) ?? new();
                }
                catch { Users = new(); }
            }

            if (File.Exists(_groupsFile))
            {
                try
                {
                    var json = File.ReadAllText(_groupsFile);
                    Groups = JsonSerializer.Deserialize<Dictionary<string, GroupInfo>>(json) ?? new();
                }
                catch { Groups = new(); }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_usersFile, JsonSerializer.Serialize(Users, opts));
                File.WriteAllText(_groupsFile, JsonSerializer.Serialize(Groups, opts));
            }
        }

        public bool RegisterUser(string username, string passwordHash, string displayName, string email)
        {
            lock (_lock)
            {
                if (Users.ContainsKey(username.ToLower())) return false;
                Users[username.ToLower()] = new StoredUser
                {
                    Username = username.ToLower(),
                    PasswordHash = passwordHash,
                    DisplayName = displayName,
                    Email = email
                };
                Save();
                return true;
            }
        }

        public StoredUser? GetUser(string username) =>
            Users.TryGetValue(username.ToLower(), out var u) ? u : null;

        public bool AddContact(string username, string contactUsername)
        {
            lock (_lock)
            {
                var user = GetUser(username);
                if (user is null) return false;
                var contactLower = contactUsername.ToLower();
                if (!user.Contacts.Contains(contactLower))
                {
                    user.Contacts.Add(contactLower);
                    Save();
                }
                return true;
            }
        }

        public bool RemoveContact(string username, string contactUsername)
        {
            lock (_lock)
            {
                var user = GetUser(username);
                if (user is null) return false;
                user.Contacts.Remove(contactUsername.ToLower());
                Save();
                return true;
            }
        }

        public GroupInfo CreateGroup(string name, string description, string owner, List<string> members)
        {
            lock (_lock)
            {
                var group = new GroupInfo
                {
                    Name = name,
                    Description = description,
                    Owner = owner,
                    Members = new List<string>(members) { }
                };
                if (!group.Members.Contains(owner)) group.Members.Add(owner);

                Groups[group.Id] = group;

                // Add group to each member's group list
                foreach (var m in group.Members)
                {
                    var u = GetUser(m);
                    if (u != null && !u.Groups.Contains(group.Id))
                        u.Groups.Add(group.Id);
                }

                Save();
                return group;
            }
        }

        public bool AddMemberToGroup(string groupId, string username)
        {
            lock (_lock)
            {
                if (!Groups.TryGetValue(groupId, out var group)) return false;
                var lower = username.ToLower();
                if (!group.Members.Contains(lower)) group.Members.Add(lower);
                var u = GetUser(lower);
                if (u != null && !u.Groups.Contains(groupId)) u.Groups.Add(groupId);
                Save();
                return true;
            }
        }

        public bool RemoveMemberFromGroup(string groupId, string username)
        {
            lock (_lock)
            {
                if (!Groups.TryGetValue(groupId, out var group)) return false;
                group.Members.Remove(username.ToLower());
                var u = GetUser(username.ToLower());
                if (u != null) u.Groups.Remove(groupId);
                if (group.Members.Count == 0) Groups.Remove(groupId);
                Save();
                return true;
            }
        }

        public void SetProfilePicture(string username, string fileId)
        {
            lock (_lock)
            {
                var u = GetUser(username);
                if (u is null) return;
                u.ProfilePicFileId = fileId;
                Save();
            }
        }
    }
}
