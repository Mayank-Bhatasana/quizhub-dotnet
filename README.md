# QuizHub 🧠

A real-time multiplayer quiz platform built with **ASP.NET Core (.NET 10)**, **PostgreSQL**, and **WebSockets**.

Hosts create quiz rooms, share a 6-character code, and participants join to answer questions in real time — with a live leaderboard updating every second.

---

## Features

- **User Authentication** — Register, login, logout with HMAC-signed cookie tokens and PBKDF2 password hashing
- **Guest Access** — Join quizzes without an account using temporary guest profiles
- **Quiz Rooms** — Create rooms with custom questions, unique 6-character join codes, and configurable duration
- **Real-Time WebSockets** — Live participant updates, answer submissions, and leaderboard broadcasts
- **Auto-Expiring Rooms** — Rooms transition from LIVE → ENDED automatically when time runs out
- **Live Leaderboard** — Background service flushes scoreboard updates every second to all connected clients
- **Player Analytics** — Per-player stats: accuracy, scores, rank history, recent sessions
- **Rate Limiting** — Login and registration endpoints are rate-limited per IP
- **Security Headers** — X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, COOP

---

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core (.NET 10) |
| Database | PostgreSQL |
| ORM | Entity Framework Core 10 |
| Real-Time | Raw WebSockets |
| Auth | HMAC-SHA256 signed cookies + PBKDF2 passwords |
| API Docs | OpenAPI / Swagger |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [PostgreSQL](https://www.postgresql.org/download/) (running on port 5432)

---

## Getting Started

### 1. Clone the repository

```bash
git clone https://github.com/Mayank-Bhatasana/quizhub-dotnet.git
cd quizhub-dotnet
```

### 2. Set up the database

Create a PostgreSQL database named `quizhub`:

```sql
CREATE DATABASE quizhub;
```

The default connection string expects PostgreSQL at `localhost:5432` with username `postgres`. To customize it, edit [`appsettings.json`](appsettings.json):

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=quizhub;Username=postgres;Password=yourpassword"
  }
}
```

Or set an environment variable:

```bash
export DATABASE_URL="Host=localhost;Port=5432;Database=quizhub;Username=postgres;Password=yourpassword"
```

### 3. Run the application

```bash
dotnet run
```

The server starts on **http://localhost:3000** by default (configurable via `PORT` env var).

### 4. Verify it's running

```bash
curl http://localhost:3000/health
# → {"status":"ok"}
```

---

## Docker

### Build and run

```bash
docker build -t quizhub:latest .
docker run -d -p 3000:80 \
  -e DATABASE_URL="Host=host.docker.internal;Port=5432;Database=quizhub;Username=postgres;Password=yourpassword" \
  quizhub:latest
```

---

## Project Structure

```
quizhub-dotnet/
├── Program.cs                  # Entry point — services, middleware pipeline, WebSocket handler
├── Controllers/
│   ├── AuthController.cs       # Register, login, logout, guest access, /api/auth/me
│   └── RoomsController.cs      # CRUD rooms, join, start, answer, scoreboard, analytics
├── Data/
│   └── AppDbContext.cs         # EF Core database context and schema configuration
├── Models/
│   └── Entities.cs             # Domain entities: User, Profile, Question, QuizRoom, etc.
├── Services/
│   └── RoomService.cs          # Business logic: room lookup, answer saving, leaderboard
├── Utils/
│   └── AuthHelper.cs           # Password hashing, token signing, rate limiting, avatars
├── WebSockets/
│   └── RoomSockets.cs          # RoomSocketManager (pub/sub) + LeaderboardFlusher (background)
├── Dockerfile                  # Multi-stage Docker build
├── appsettings.json            # Configuration (DB connection string, logging)
└── quizhub-dotnet.csproj       # Project file (.NET 10, NuGet packages)
```

---

## API Reference

### Health

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/` | Basic health check |
| `GET` | `/health` | Health status |
| `GET` | `/docs` | Redirects to OpenAPI spec |

### Authentication

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/auth/register` | Register a new user |
| `POST` | `/api/auth/login` | Login with email & password |
| `GET` | `/api/auth/me` | Get current session user |
| `POST` | `/api/auth/logout` | Logout (clears cookie) |
| `POST` | `/api/guest` | Create a guest profile |

### Questions

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/questions` | Create a new question with options |

### Rooms

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/rooms` | Create a new quiz room |
| `GET` | `/api/rooms/{code}` | Get room details by code |
| `POST` | `/api/rooms/{code}/join` | Join a room |
| `GET` | `/api/rooms/{code}/participants` | List room participants |
| `POST` | `/api/rooms/{code}/start` | Start the quiz (host only) |
| `GET` | `/api/room/{code}/questions` | Get room questions |
| `POST` | `/api/rooms/{roomId}/answer` | Submit an answer (REST) |
| `GET` | `/api/rooms/{roomId}/scoreboard` | Get the leaderboard |

### Analytics

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/analytics/{profileId}` | Player performance analytics |

---

## WebSocket Protocol

Connect to `ws://localhost:3000/` and exchange JSON messages:

### Client → Server

**Subscribe to a room:**
```json
{ "type": "subscribe", "code": "XK7M2P" }
```

**Unsubscribe from a room:**
```json
{ "type": "unsubscribe", "code": "XK7M2P", "tempProfileId": "guid" }
```

**Submit an answer:**
```json
{
  "type": "submit_answer",
  "code": "XK7M2P",
  "participantId": "guid",
  "roomQuestionId": "guid",
  "selectedOptionId": "guid",
  "timeTakenSeconds": 15
}
```

### Server → Client

| Event | Description |
|---|---|
| `participants_updated` | Participant list changed (join/leave) |
| `room_started` | Host started the quiz |
| `leaderboard_updated` | Scores updated (broadcast every 1s) |
| `room_ended` | Quiz time expired |

---

## Data Model

```
User 1──1 Profile 1──* Question
                  │
                  ├──* QuizRoom (as Host)
                  │       ├──* RoomParticipant
                  │       ├──* RoomQuestion ──1 Question
                  │       │       └──* ParticipantAnswer
                  │       └──* ParticipantAnswer
                  │
                  └──* RoomParticipant
                          └──* ParticipantAnswer ──? QuestionOption
```

**Room Lifecycle:** `LOBBY` → `LIVE` → `ENDED`

---

## Environment Variables

| Variable | Description | Default |
|---|---|---|
| `PORT` | Server port | `3000` |
| `DATABASE_URL` | PostgreSQL connection string | `Host=localhost;Port=5432;Database=quizhub;Username=postgres` |
| `DATABASE_URL__DOTNET` | .NET-specific DB connection (higher priority) | — |
| `AUTH_SIGNING_SECRET` | HMAC secret for token signing | `dev-change-me` |
| `ASPNETCORE_ENVIRONMENT` | `Development` or `Production` | `Development` |

---

## License

This project is open source. Feel free to use and modify.
