# QuizHub — .NET 10 API port (from Express)

Parity port of `quizhub/backend` (Express 5 + Prisma) to ASP.NET Core 10 + EF Core + PostgreSQL.
Same routes, same cookie auth (`quiz_auth` / `quiz_guest`), same raw-WebSocket protocol.

## Postgres — sudo commands

Postgres 18 is already installed and running here. On a fresh Arch box:

```bash
sudo pacman -S postgresql
sudo systemctl enable --now postgresql.service
```

Create the database (run as the `postgres` OS user):

```bash
sudo -u postgres psql -c "CREATE DATABASE quizhub OWNER mayank;"
sudo -u postgres psql -c "\l"   # verify quizhub is listed
```

No-password variant (works on this machine, trust auth over localhost):

```bash
psql "postgresql://postgres@localhost:5432/postgres" -c "CREATE DATABASE quizhub OWNER mayank;"
```

Why the Express backend "wasn't working": `quizhub/backend/.env` was **empty**,
so `DATABASE_URL` was missing. It now contains:

```env
DATABASE_URL="postgresql://postgres@localhost:5432/quizhub"
PORT=3000
```

## Run the .NET API

```bash
cd quizhub-dotnet
dotnet restore
dotnet run            # listens on http://localhost:3000 (PORT env overrides)
```

Connection string: `appsettings.json` →
`Host=localhost;Port=5432;Database=quizhub;Username=postgres`
(no password = trust auth, same as the `psql` command above).
Tables are auto-created on boot (`EnsureCreated`, mirrors `prisma/schema.prisma`).
Swagger/OpenAPI JSON: `http://localhost:3000/openapi/v1.json`, human docs: `/docs`.

Point the frontend at it:

```bash
VITE_API_BASE_URL=http://localhost:3000 npm run dev
```

## Route parity with Express

| Express | .NET |
|---|---|
| `POST /api/auth/register, /login, /me, /logout, /guest` | `Controllers/AuthController.cs` |
| `POST /api/questions` | `Controllers/RoomsController.cs` |
| `POST /api/rooms`, `/:code/join`, `/:code/participants`, `/:code`, `/:code/start`, `/room/:code/questions`, `/:roomId/answer`, `/:roomId/scoreboard`, `/analytics/:profileId` | `Controllers/RoomsController.cs` |
| `ws` subscribe/unsubscribe/submit_answer + `participants_updated`, `room_started`, `leaderboard_updated`, `room_ended` | `Program.cs` WS middleware + `WebSockets/RoomSockets.cs` |
| dirty-rooms 1s leaderboard flush | `WebSockets/RoomSockets.cs` → `LeaderboardFlusher` |
| request-id, logging, security headers, rate limit, error handler | `Program.cs` middleware |
| avatar emoji/bg | `Utils/AuthHelper.cs` → `AvatarHelper` |

## Notes / intentional differences

- Passwords: fresh `pbkdf2$…` hashes (Node `scrypt$…` hashes from Express are not
  verifiable here — fresh DB, so re-register).
- Auth tokens: same `payload_base64url.signature` HMAC-SHA256 format, cookies compatible.
- EF `EnsureCreated` is used instead of migrations for v1.
