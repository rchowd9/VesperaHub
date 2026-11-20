# VesperaHub

A real-time encrypted chat app built with ASP.NET Core SignalR and .NET 10. Users log in to get a JWT and connect to a SignalR hub. Each client encrypts its messages with AES-256 before sending them, so the server only stores and relays ciphertext.

## Projects

| Project | Description |
|---|---|
| `VesperaHub.Server` | ASP.NET Core web app. Exposes a login endpoint that issues JWTs and a SignalR hub (`/chathub`) that relays encrypted messages. |
| `VesperaHub.Client` | Console client. Logs in, connects to the hub, and encrypts/decrypts messages locally. |

```
VesperaHub/
├── VesperaHub.Server/
│   ├── Hubs/ChatHub.cs                    # SignalR hub: history, join/leave notices, broadcast
│   ├── Models/ChatMessage.cs              # Message record (Id, Sender, EncryptedContent, Timestamp)
│   ├── Services/EncryptionService.cs      # AES-256 encrypt/decrypt
│   ├── Services/MessageHistoryService.cs  # In-memory history (last 100 messages)
│   └── Program.cs                         # JWT auth, login endpoint, hub mapping
└── VesperaHub.Client/
    ├── Services/EncryptionService.cs      # Client copy of the AES-256 service
    └── Program.cs                         # Console chat client
```

## How it works

1. **Login:** the client sends `POST /api/auth/login` with a username and password. The server returns a JWT that is valid for 2 hours.
2. **Connect:** the client opens a SignalR connection to `/chathub` with the token. The hub requires authentication (`[Authorize]`). For WebSocket connections, the token is passed in the `access_token` query string.
3. **History:** when a client connects, the server sends the last 100 messages (still encrypted) through `LoadHistory`.
4. **Send:** the client encrypts each message (AES-256, with a random IV prepended to the ciphertext and the result Base64-encoded) and calls `SendMessage`. The server checks that the payload can be decrypted, stores it, and broadcasts it to all clients through `ReceiveMessage`.
5. **Notices:** join and leave notices are sent to the other users from `System`, encrypted the same way.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A trusted HTTPS development certificate:

  ```bash
  dotnet dev-certs https --trust
  ```

## Running

Start the server. It listens on `https://localhost:5001`:

```bash
dotnet run --project VesperaHub.Server
```

In one or more other terminals, start a client:

```bash
dotnet run --project VesperaHub.Client
```

Enter any username and the password `vespera123`. Type messages and press Enter to send them. Type `/quit` to exit.

## Troubleshooting

**"An Application Control policy has blocked this file"** (Windows 11): Smart App Control is blocking the unsigned binaries you built locally. Turn it off in Windows Security → App & browser control → Smart App Control settings. On some Windows versions it can't be turned back on without resetting Windows.

**"You must install or update .NET to run this application"**: the ASP.NET Core 10 runtime is missing. Install the .NET 10 SDK, which includes it.

## Security notes

This is a demo project. Do not use it in production as is:

- The **AES key** is hardcoded and shared between the server and the client (`EncryptionService.cs` in both projects). Anyone with the client program can get the key, and the server can decrypt every message. This is not end-to-end encryption.
- The **JWT signing key** is hardcoded in `VesperaHub.Server/Program.cs`.
- **Authentication** accepts any username with the single shared password `vespera123`.
- **Message history** is kept only in memory and is lost when the server restarts.

To harden it, load secrets from configuration or a secret store (for example user-secrets, environment variables, or Azure Key Vault), use real user accounts, and replace the shared key with per-user key exchange for true end-to-end encryption.
