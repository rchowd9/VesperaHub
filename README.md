# VesperaHub

A real-time chat app built with ASP.NET Core, SignalR and .NET 10. It has a browser UI and a console client, rooms and direct messages, replies, reactions, editing, file and image sharing, mentions, search and notifications. Messages are stored encrypted in SQLite.

## Features

**Chat**
- **Rooms:** `#general`, `#dev` and `#random` are created automatically, and anyone can create more.
- **Direct messages:** private conversations, delivered to every tab or client you have open.
- **Replies:** quote a message; click the quote to jump to the original.
- **Reactions:** 👍 ❤️ 😂 🎉 😮 😢 🔥 👀, with who reacted shown on hover.
- **Edit and delete** your own messages. Press ↑ in an empty message box to edit your last one.
- **Files and images:** attach with the 📎 button, paste, or drag and drop (up to 10 MB). Images show inline, and you can click one to enlarge it.
- **Formatting:** `**bold**`, `*italic*`, `~~strike~~`, `` `code` ``, ```` ```code blocks``` ````, and clickable links.
- **@mentions** with autocomplete. Messages that mention you are highlighted.
- **Search** across all rooms and your DMs. Click a result to jump to the message.
- **Infinite scroll:** older history loads as you scroll up.

**Awareness**
- **Presence:** who's viewing each room, and online dots for DM contacts.
- **Typing indicators** in rooms and DMs.
- **Unread badges** for every room and DM, a red **@** badge when you were mentioned, and the unread count in the tab title.
- **Desktop notifications** and a sound for DMs and mentions (turn on with the 🔕 button).
- **Light and dark themes** (◐ button). Works on phones too.

**Accounts and profiles**
- **Email verification:** new password accounts must verify their email before signing in. Verification links expire after 24 hours; existing accounts are preserved during the schema upgrade.
- **Google sign-in:** sign in or create an account with a verified Google email.
- **Profile pictures:** upload a PNG, JPEG, GIF, or WebP picture; Google profile photos are used until you upload one.

**Under the hood**
- **Accounts:** passwords are hashed with PBKDF2-SHA256 (210,000 iterations, random salt). JWT authentication covers the API and the hub.
- **Encryption at rest:** message content is stored with AES-256-GCM.
- **Rate limiting:** login and register allow 10 requests per minute per IP, and chat actions are limited per user (bursts of 8, then 2 per second).
- **Persistence:** SQLite through Entity Framework Core. Databases from earlier versions are upgraded automatically on startup, keeping existing accounts and messages.
- **Safe uploads:** files get random 128-bit IDs. Only PNG, JPEG, GIF and WebP display inline; every other type, SVG included, downloads as a file.
- **Console client** for rooms, DMs, history and search.

## Projects

| Project | Description |
|---|---|
| `VesperaHub.Server` | ASP.NET Core app: REST API, SignalR hub, SQLite database and the web UI (`wwwroot`). |
| `VesperaHub.Client` | Console client for the same server. |

```
VesperaHub/
├── VesperaHub.Server/
│   ├── Data/VesperaDbContext.cs        # EF Core model and seed rooms
│   ├── Data/SchemaUpgrader.cs          # Creates or upgrades the database on startup
│   ├── Endpoints/AuthEndpoints.cs      # POST /api/auth/register, /api/auth/login
│   ├── Endpoints/RoomEndpoints.cs      # GET/POST /api/rooms
│   ├── Endpoints/UploadEndpoints.cs    # POST /api/uploads, GET /api/uploads/{id}
│   ├── Hubs/ChatHub.cs                 # Messaging, replies, reactions, edits, search, presence
│   ├── Hubs/NameUserIdProvider.cs      # Routes DMs by username
│   ├── Models/                         # Entities and DTOs
│   ├── Options/JwtOptions.cs
│   ├── Services/ChatRateLimiter.cs     # Per-user flood protection
│   ├── Services/EncryptionService.cs   # AES-256-GCM at rest
│   ├── Services/MessageMapper.cs       # Loads and decrypts messages for clients
│   ├── Services/PasswordHasher.cs      # PBKDF2 password hashing
│   ├── Services/PresenceTracker.cs     # Who is connected and which room they're viewing
│   ├── Services/TokenService.cs        # JWT creation
│   ├── wwwroot/                        # Web UI (HTML, CSS, JS)
│   ├── App_Data/uploads/               # Uploaded files (created at runtime, not committed)
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

Then open **https://localhost:5001** in your browser, choose a username, enter an email and password, and click **Create account**. Verify the email before signing in. To chat with yourself, open a second browser window (or a private window) and register a second user.

### Email verification and Google sign-in setup

Set these environment variables (or equivalent user-secrets) on the server. Keep SMTP and Google credentials out of source control:

| Variable | Purpose |
|---|---|
| `Email__SmtpHost` | SMTP server hostname |
| `Email__SmtpPort` | SMTP TLS port (commonly `587`) |
| `Email__Username` / `Email__Password` | SMTP credentials; use an app password where the provider requires one |
| `Email__FromAddress` | Verified sender address |
| `Email__FromName` | Optional sender display name |
| `Email__PublicUrl` | Public app origin used for verification links (for example `https://chat.example.com`) |
| `Authentication__Google__ClientId` / `Authentication__Google__ClientSecret` | OAuth credentials from Google Cloud |

Add the Google OAuth callback URL `https://<your-host>/signin-google` to the authorized redirect URIs in Google Cloud. The Google sign-in button stays hidden until both Google credentials are configured. Email verification requires a working SMTP host and sender address; registration is disabled with an explicit configuration error otherwise.

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
| `/search <text>` | Search messages |
| `/help`, `/quit` | Show help, exit |

Replies, reactions, edits and uploads are only available in the web app.

## API

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/auth/register` | none | Create an account. Body: `{ "username", "password", "email" }`. Sends a verification link; no session is issued until verified. |
| `POST` | `/api/auth/login` | none | Log in with `{ "username", "password" }`. Returns `{ "token", "username", "expiresAt" }` for verified accounts. |
| `GET` | `/api/auth/verify?token=...` | none | Confirm an email verification link. |
| `POST` | `/api/auth/resend-verification` | none | Request another verification link. Body: `{ "email" }`. |
| `GET` | `/api/auth/google` | none | Start Google sign-in (when configured). |
| `GET` | `/api/profile` | JWT | Get the signed-in user's profile. |
| `POST` | `/api/profile/avatar` | JWT | Set an uploaded image as the profile picture. |
| `GET` | `/api/rooms` | JWT | List rooms with online counts. |
| `POST` | `/api/rooms` | JWT | Create a room. Body: `{ "name", "description" }`. |
| `POST` | `/api/uploads` | JWT | Upload a file (multipart field `file`, max 10 MB). Returns an attachment to send with a message. |
| `GET` | `/api/uploads/{id}` | none | Download an uploaded file. The random ID acts as the access key. |
| `GET` | `/health` | none | Health check. |
| WS | `/chathub` | JWT | SignalR hub (see below). |

Conversations are addressed as `room:<name>` or `dm:<username>`.

**Hub methods (client → server):**

| Method | Description |
|---|---|
| `JoinRoom(room)` | View a room. Returns its members and latest messages. |
| `LeaveRoom()` | Stop viewing a room (used while a DM is open). |
| `GetHistory(conversation, beforeId)` | Page of 50 messages older than `beforeId` (or the latest). |
| `SendMessage({ conversation, content, replyToId, attachmentId })` | Send a message. Returns it. |
| `EditMessage(id, content)`, `DeleteMessage(id)` | Change or remove your own message. |
| `ToggleReaction(id, emoji)` | Add or remove your reaction. |
| `Search(query)` | Search rooms and your DMs (up to 30 results). |
| `GetConversations()` | Users you have DMs with. |
| `Typing(conversation, isTyping)` | Typing indicator. |

**Hub events (server → client):** `MessageReceived`, `MessageUpdated` (edits, deletes, reactions), `PresenceChanged`, `OnlineUsersChanged`, `UserTyping`, `RoomCreated`.

## Configuration

Settings are in `VesperaHub.Server/appsettings.json`:

| Key | Purpose |
|---|---|
| `Urls` | Address the server listens on (default `https://localhost:5001`) |
| `ConnectionStrings:Default` | SQLite database file (default `vesperahub.db`) |
| `Jwt:Key` | Token signing key (at least 32 characters) |
| `Jwt:LifetimeHours` | How long a login lasts |
| `Encryption:Key` | Base64-encoded 32-byte AES key for messages at rest |

You can override any setting with environment variables, using `__` as the separator (for example `Jwt__Key`), or with `dotnet user-secrets`. To reset all data, stop the server and delete `vesperahub.db` and the `App_Data` folder.

## Troubleshooting

**"An Application Control policy has blocked this file"** (Windows 11): Smart App Control blocks unsigned programs, including ones you just built. The projects set `UseAppHost=false`, so no `.exe` is made, but Windows can still block the freshly built `.dll` for a while after each build. Waiting and running again sometimes works. The reliable fix is to turn off Smart App Control in Windows Security → App & browser control. On some Windows versions it can't be turned back on without resetting Windows.

**The console client says "Ignoring … not a server URL"**: you ran `dotnet run <something>` from inside the client folder, which passes `<something>` to the app. Run `dotnet run` on its own there, or `dotnet run --project VesperaHub.Client` from the repository root.

**"You must install or update .NET to run this application"**: the ASP.NET Core 10 runtime is missing. Install the .NET 10 SDK, which includes it.

**The build fails with "file is locked by .NET Host"**: the server is still running. Stop it (Ctrl+C) before rebuilding.

**The browser shows a certificate warning**: run `dotnet dev-certs https --trust` and restart the browser.

## Security notes

This is a learning project, not a hardened production service:

- The keys in `appsettings.json` are **development keys**. Replace them with your own, stored outside source control, before deploying anywhere.
- Messages are encrypted **at rest**, and HTTPS protects them in transit. They are **not end-to-end encrypted**: the server can read them.
- Uploaded files are **not encrypted** on disk, and anyone who has a file's link can download it.
- There is no email verification, password reset or moderation.
