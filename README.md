# MSN Messenger — Real LAN/Internet Multiplayer

A full MSN Messenger clone that works **for real** over your LAN or the internet. Three projects:

```
MSNMessenger.sln
├── MSNShared/    — Protocol definitions (shared by server & client)
├── MSNServer/    — Console server app (host this)
└── MSNClient/    — WPF client app (everyone runs this)
```

---

## 🚀 Quick Start (LAN)

### 1. Build everything
```bash
cd MSNMessenger
dotnet build
```

### 2. One person runs the server
```bash
cd MSNServer
dotnet run
# or with custom settings:
dotnet run -- 7001 7002 "My MSN Server"
```
The server starts on **TCP port 7001** and listens on **UDP port 7002** for LAN discovery.

### 3. Everyone runs the client
```bash
cd MSNClient
dotnet run
```

- On the **Sign In** tab, click **"Find LAN Servers"** to auto-discover the server
- Or type the server's IP manually (e.g. `192.168.1.10`) and port `7001`
- Click **Create Account** tab to register, then Sign In

---

## 🌐 Over the Internet

### Server setup
1. **Port forward TCP 7001** (and optionally UDP 7002) on your router to the server PC
2. Start the server:
   ```bash
   dotnet run -- 7001 7002 "My Public MSN Server"
   ```
3. Find your **public IP** at https://whatismyip.com
4. Share your public IP + port `7001` with friends

### Clients connecting remotely
- Enter the **public IP** in the Host field
- Enter `7001` in the Port field
- LAN discovery won't work from the internet — just type the IP

### Optional: Run as a persistent server (Windows)
```bash
# Install as a Windows service using NSSM or run in a screen session
# Or use Task Scheduler to auto-start on boot
```

---

## 🎮 Features

| Feature | Description |
|---------|-------------|
| ✅ Accounts | Register/login with username + password (bcrypt hashed) |
| ✅ Presence | Online/Away/Busy/Appear Offline with broadcast |
| ✅ Personal message | Editable status message shown to all |
| ✅ Avatar emoji | Pick from 12 avatars |
| ✅ Direct messages | Real-time 1:1 chat with formatting |
| ✅ Group chats | Create groups, invite members, group messaging |
| ✅ Contact list | Add/remove contacts, organized by group |
| ✅ Typing indicators | See when someone is typing (1:1 and group) |
| ✅ Nudge | Send a nudge — recipient's window shakes! |
| ✅ Message formatting | Font, size, bold, italic, underline, color |
| ✅ Emoticons | Picker with 17 emoticons |
| ✅ LAN discovery | Auto-find servers via UDP broadcast |
| ✅ Reconnection | Kicked when you sign in from another location |
| ✅ Persistence | Users, contacts, groups saved to JSON files |
| ✅ Window flash | Taskbar flashes on new message |

---

## 🏗️ Architecture

```
Client ──TCP──▶ Server
         JSON packets, newline-delimited

UDP broadcast (LAN only):
Client ──UDP broadcast──▶ Server listens on discoveryPort
Server ──UDP reply──▶ Client (with host, port, usercount)
```

### Protocol
All messages are JSON packets with a `t` (type enum) and `d` (data) field, delimited by newlines.

```json
{"t": 5, "id": "abc123", "ts": 1700000000000, "d": {...}}
```

### Data storage
The server stores data in `./data/`:
- `users.json` — accounts (passwords bcrypt hashed)
- `groups.json` — group definitions and member lists

---

## ⚙️ Server Configuration

```bash
# Syntax: MSNServer.exe [tcp-port] [udp-discovery-port] [server name]
dotnet run -- 7001 7002 "Peyaminin kefir can sunucusu"
```

---

## 🛡️ Security Notes

- Passwords are **bcrypt hashed** on the server — never stored in plaintext
- Communication is **not encrypted** (plain TCP) — for a secure setup, run behind a VPN or add TLS
- To add TLS: wrap the `NetworkStream` with `SslStream` on both ends

---

## 🔧 Requirements

- .NET 8.0 SDK
- Windows (for the WPF client)
- Server can run on Windows, Linux, or macOS
