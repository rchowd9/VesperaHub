# VesperaHub

A real-time chat app built with ASP.NET Core, SignalR and .NET 10. It has a browser UI and a console client, chat rooms, direct messages, presence and typing indicators, and user accounts. Messages are stored encrypted in SQLite.

## Features

- **Web app:** a responsive chat UI served by the server at `https://localhost:5001`.
- **Accounts:** register and log in. Passwords are hashed with PBKDF2-SHA256 (210,000 iterations, random salt).
- **JWT authentication** for both the REST API and the SignalR hub.
- **Rooms:** `#general`, `#dev` and `#random` are created automatically, and anyone can create more. Each room keeps its message history.
- **Direct messages:** private conversations, delivered to every tab or client the user has open.
- **Presence:** see who's in the current room and who's online, live.
- **Typing indicators:** "alice is typing…".
- **Unread badges** for rooms and DMs you're not looking at.
- **Encryption at rest:** message content is stored with AES-256-GCM (authenticated encryption).
- **Rate limiting:** login and register allow 10 requests per minute per IP.
- **Persistence:** SQLite through Entity Framework Core. History is kept when the server restarts.
- **Console client** with commands for rooms, DMs and history.

## Projects

| Project | Description |
|---|---|
| `VesperaHub.Server` | ASP.NET Core app: REST API, SignalR hub, SQLite database and the web UI (`wwwroot`). |
| `VesperaHub.Client` | Console client for the same server. |

```
VesperaHub/
├── VesperaHub.Server/
│   ├── Data/VesperaDbContext.cs        # EF Core model and seed rooms
│   ├── Endpoints/AuthEndpoints.cs      # POST /api/auth/register, /api/auth/login
│   ├── Endpoints/RoomEndpoints.cs      # GET/POST /api/rooms
│   ├── Hubs/ChatHub.cs                 # Real-time messaging, DMs, presence, typing
│   ├── Hubs/NameUserIdProvider.cs      # Routes DMs by username
│   ├── Models/                         # Entities and DTOs
│   ├── Options/JwtOptions.cs
│   ├── Services/EncryptionService.cs   # AES-256-GCM at rest
│   ├── Services/PasswordHasher.cs      # PBKDF2 password hashing
│   ├── Services/PresenceTracker.cs     # Who is connected and in which room
│   ├── Services/TokenService.cs        # JWT creation
│   ├── wwwroot/                        # Web UI (HTML, CSS, JS)
│   ├── appsettings.json                # Keys, connection string, URLs
│   └── Program.cs                      # App setup and middleware
└── VesperaHub.Client/
    └── Program.cs                      # Console client
```

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A trusted HTTPS development certificate:

  ```bash
  dotnet dev-certs https --trust
  ```

- An internet connection the first time you load the web UI, because the SignalR browser library is loaded from a CDN.

## Running

Start the server:

```bash
dotnet run --project VesperaHub.Server
```

Then open **https://localhost:5001** in your browser, choose a username and password, and click **Create account**. To chat with yourself, open a second browser window (or a private window) and register a second user.

The console client works against the same server:

```bash
dotnet run --project VesperaHub.Client
# or point it at another server:
dotnet run --project VesperaHub.Client -- https://myserver:5001
```

Console commands:

| Command | Action |
|---|---|
| `<text>` | Send a message to the current room |
| `/rooms` | List rooms |
| `/join <room>` | Switch rooms |
| `/create <room> [description]` | Create a room and join it |
| `/dm <user> <message>` | Send a direct message |
| `/history <user>` | Show direct messages with a user |
| `/help`, `/quit` | Show help, exit |

## API

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/auth/register` | none | Create an account. Body: `{ "username", "password" }`. Returns `{ token, username, expiresAt }`. |
| `POST` | `/api/auth/login` | none | Log in. Same body and response as register. |
| `GET` | `/api/rooms` | JWT | List rooms with online counts. |
| `POST` | `/api/rooms` | JWT | Create a room. Body: `{ "name", "description" }`. |
| `GET` | `/health` | none | Health check. |
| WS | `/chathub` | JWT | SignalR hub (see below). |

**Hub methods (client → server):** `JoinRoom(room)`, `SendMessage(text)`, `SendDirectMessage(user, text)`, `GetDirectHistory(user)`, `GetConversations()`, `Typing(isTyping)`.

**Hub events (server → client):** `MessageReceived`, `DirectMessageReceived`, `PresenceChanged`, `OnlineUsersChanged`, `UserTyping`, `RoomCreated`.

## Configuration

Settings are in `VesperaHub.Server/appsettings.json`:

| Key | Purpose |
|---|---|
| `Urls` | Address the server listens on (default `https://localhost:5001`) |
| `ConnectionStrings:Default` | SQLite database file (default `vesperahub.db`) |
| `Jwt:Key` | Token signing key (at least 32 characters) |
| `Jwt:LifetimeHours` | How long a login lasts |
| `Encryption:Key` | Base64-encoded 32-byte AES key for messages at rest |

You can override any setting with environment variables, using `__` as the separator (for example `Jwt__Key`), or with `dotnet user-secrets`. To reset all data, stop the server and delete `vesperahub.db`.

## Troubleshooting

**"An Application Control policy has blocked this file"** (Windows 11): Smart App Control blocks unsigned `.exe` files. The projects set `UseAppHost=false`, so `dotnet run` starts the app through the signed `dotnet.exe` instead, which avoids the block. If it still happens, turn off Smart App Control in Windows Security → App & browser control. On some Windows versions it can't be turned back on without resetting Windows.

**"You must install or update .NET to run this application"**: the ASP.NET Core 10 runtime is missing. Install the .NET 10 SDK, which includes it.

**The build fails with "file is locked by .NET Host"**: the server is still running. Stop it (Ctrl+C) before rebuilding.

**The browser shows a certificate warning**: run `dotnet dev-certs https --trust` and restart the browser.

## Security notes

This is a learning project, not a hardened production service:

- The keys in `appsettings.json` are **development keys**. Replace them with your own, stored outside source control, before deploying anywhere.
- Messages are encrypted **at rest**, and HTTPS protects them in transit. They are **not end-to-end encrypted**: the server can read them.
- There is no email verification, password reset or moderation.
