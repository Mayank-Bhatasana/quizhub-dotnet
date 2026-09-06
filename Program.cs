// =============================================================================
// FILE: Program.cs
// PURPOSE: The ENTRY POINT of the entire application — this is where everything starts.
//
// This file does 4 main things:
//   1. CONFIGURES SERVICES (dependency injection container)
//   2. BUILDS THE APPLICATION (creates the HTTP server)
//   3. SETS UP MIDDLEWARE PIPELINE (request processing chain)
//   4. MAPS ENDPOINTS (connects URLs to handler code)
//
// WHAT IS THE MIDDLEWARE PIPELINE?
//   Every HTTP request flows through a series of "middleware" functions.
//   Each middleware can:
//     - Do something before passing the request to the next middleware (pre-processing)
//     - Do something after getting the response back (post-processing)
//     - Short-circuit the pipeline (return a response without calling next)
//
//   Request flow:  Client → Middleware1 → Middleware2 → ... → Endpoint → Response
//
//   In this app, the pipeline is:
//     Client → Security Headers/Logging → Error Handler → CORS → WebSockets → Controller
//
// TOP-LEVEL STATEMENTS (C# 9+ feature):
//   Notice there's no "class Program" or "static void Main()".
//   In modern C#, the Program.cs file can contain top-level statements —
//   code that runs directly without being wrapped in a class/method.
//   The compiler generates the boilerplate behind the scenes.
// =============================================================================

// These "using" statements import namespaces so we can use their classes
// without typing the full name every time.
using System.Net.WebSockets; // WebSocket, WebSocketState, WebSocketReceiveResult
using System.Text; // Encoding.UTF8, StringBuilder
using System.Text.Json; // JsonDocument, JsonSerializer
using Microsoft.EntityFrameworkCore; // UseNpgsql, EnsureCreatedAsync, Include, etc.
using QuizHub.Data; // AppDbContext
using QuizHub.Models; // RoomStatus, QuizRoom, etc.
using QuizHub.Services; // RoomService, LeaderboardFlusher
using QuizHub.WebSockets; // RoomSocketManager, WsJson

// =============================================================================
// PHASE 1: CONFIGURE SERVICES (Dependency Injection Container)
// =============================================================================
// WebApplication.CreateBuilder() creates a "builder" that lets us configure
// the application before it starts. Think of it as assembling the ingredients
// before cooking.

var builder = WebApplication.CreateBuilder(args);

// ── Register MVC Controllers ──
// AddControllers() tells ASP.NET to scan for classes with [ApiController]
// attribute and register them as request handlers.
// AddJsonOptions() configures how JSON responses are serialized.
builder
    .Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // IgnoreCycles prevents infinite loops when serializing objects that
        // reference each other (e.g., User → Profile → User → Profile → ...).
        // Without this, serializing a User with its Profile would crash.
        o.JsonSerializerOptions.ReferenceHandler = System
            .Text
            .Json
            .Serialization
            .ReferenceHandler
            .IgnoreCycles;
    });

// ── Register OpenAPI/Swagger ──
// Generates API documentation automatically from your controllers.
// Accessible at /openapi/v1.json after the app starts.
builder.Services.AddOpenApi();

// ── Configure Database Connection ──
// Try multiple sources for the connection string, in priority order:
//   1. appsettings.json "ConnectionStrings:Default" (primary)
//   2. DATABASE_URL__DOTNET environment variable (for deployment)
//   3. DATABASE_URL environment variable (fallback, shared with other apps)
//   4. Hardcoded default (last resort, for local development)
var conn =
    builder.Configuration.GetConnectionString("Default")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL__DOTNET")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=localhost;Port=5432;Database=quizhub;Username=postgres";

// Register the database context with PostgreSQL (Npgsql) as the provider.
// AddDbContext registers it as a SCOPED service (one instance per HTTP request).
// This means each request gets its own database connection, preventing cross-request interference.
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));

// ── Register Application Services ──

// RoomSocketManager is a SINGLETON — one instance shared across ALL requests.
// WHY SINGLETON? It holds the in-memory WebSocket subscription map, which must
// persist across requests. If it were scoped, each request would get an empty map.
builder.Services.AddSingleton<RoomSocketManager>();

// RoomService is SCOPED — one instance per HTTP request.
// WHY SCOPED? It depends on AppDbContext (also scoped). If RoomService were
// singleton, it would hold onto a stale DbContext from the first request.
builder.Services.AddScoped<RoomService>();

// LeaderboardFlusher is a HOSTED SERVICE — a background task that runs
// continuously. It checks for "dirty rooms" every 1 second and broadcasts
// leaderboard updates via WebSocket. See WebSockets/RoomSockets.cs.
builder.Services.AddHostedService<LeaderboardFlusher>();

// ── Configure CORS (Cross-Origin Resource Sharing) ──
// CORS controls which websites can make API requests to this server.
//
// WHY IS CORS NEEDED?
//   Browsers block requests from one domain to another by default (security).
//   The frontend runs on localhost:5173 (Vite dev server), but the API runs
//   on localhost:3000. Without CORS, the browser would block the frontend's
//   API requests. These settings whitelist the frontend's URLs.
//
// AllowCredentials() is REQUIRED for cookie-based auth to work cross-origin.
// Without it, the browser won't send/receive cookies on cross-origin requests.
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p =>
        p.WithOrigins(
                "http://localhost:5173",
                "http://localhost:5174",
                "http://localhost:4173",
                "http://127.0.0.1:5173",
                "http://127.0.0.1:5174",
                "http://127.0.0.1:4173"
            )
            .AllowAnyMethod() // Allow GET, POST, PUT, DELETE, etc.
            .AllowAnyHeader() // Allow any request headers.
            .AllowCredentials()
    )
); // Allow cookies to be sent cross-origin.

// =============================================================================
// PHASE 2: BUILD THE APPLICATION
// =============================================================================
var app = builder.Build();

// ── Ensure Database Tables Exist ──
// EnsureCreatedAsync() checks if the database tables exist and creates them
// if they don't. This is a simple alternative to EF Core Migrations.
//
// HOW IT WORKS:
//   1. Creates a temporary DI scope (needed to get the scoped DbContext).
//   2. Gets the AppDbContext from the scope.
//   3. Calls EnsureCreatedAsync() which generates CREATE TABLE SQL from
//      the entity classes and AppDbContext.OnModelCreating() configuration.
//
// NOTE: This does NOT update existing tables if you change your models.
//   For production, you'd use EF Core Migrations instead.
//   EnsureCreated is fine for development and v1.
// Ensure tables exist (fresh port: no EF migrations yet, mirrors prisma schema).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// =============================================================================
// PHASE 3: MIDDLEWARE PIPELINE
// =============================================================================
// Middleware runs in the ORDER it's added. Each call to app.Use(...) adds
// a step in the pipeline. The request flows through them top-to-bottom,
// and the response flows back bottom-to-top.

// ── Middleware 1: Request ID + Security Headers + Logging ──
// This runs for EVERY request (HTTP and WebSocket).
// Request-ID + security headers + simple logging
app.Use(
    async (ctx, next) =>
    {
        // ── Request ID ──
        // If the client sent an X-Request-ID header, use it.
        // Otherwise, generate a random 16-character ID.
        // This ID tracks the request through logs for debugging.
        // Example: If a user reports a bug, the request ID helps find the exact log entries.
        var id = ctx.Request.Headers.TryGetValue("X-Request-ID", out var v)
            ? v.ToString()
            : Guid.NewGuid().ToString("N")[..16];
        ctx.Items["RequestId"] = id; // Store for use by other middleware/controllers.
        ctx.Response.Headers["X-Request-ID"] = id; // Echo it back to the client.

        // ── Security Headers ──
        // These headers protect against common web attacks:

        // X-Content-Type-Options: nosniff
        //   Prevents browsers from "sniffing" the content type.
        //   Without this, a browser might execute a .txt file as JavaScript.
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

        // X-Frame-Options: DENY
        //   Prevents this site from being embedded in an iframe.
        //   Protects against clickjacking attacks (where an attacker overlays
        //   invisible iframes to trick users into clicking things).
        ctx.Response.Headers["X-Frame-Options"] = "DENY";

        // Referrer-Policy: strict-origin-when-cross-origin
        //   Controls how much URL info is sent in the Referer header.
        //   Same-origin requests send full URL; cross-origin sends only the origin.
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Permissions-Policy: Disable access to sensitive browser APIs.
        //   This app doesn't need geolocation, microphone, camera, or payments.
        ctx.Response.Headers["Permissions-Policy"] =
            "geolocation=(), microphone=(), camera=(), payment=()";

        // Cross-Origin-Opener-Policy: same-origin
        //   Prevents other websites from accessing this window via window.opener.
        ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";

        // ── Request Timing + Logging ──
        // Start a stopwatch to measure how long the request takes.
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Pass the request to the next middleware in the pipeline.
        // Everything after this line runs AFTER the request is processed.
        await next();

        // Stop the stopwatch and log the request details.
        sw.Stop();
        // Log format: "2024-01-15T10:30:00Z [abc123] GET     /api/auth/me                          200 1.23ms"
        Console.WriteLine(
            $"{DateTime.UtcNow:o} [{id}] {ctx.Request.Method, -7} {ctx.Request.Path, -40} {ctx.Response.StatusCode} {sw.Elapsed.TotalMilliseconds:F2}ms"
        );
    }
);

// ── Middleware 2: Global Error Handler ──
// Catches any unhandled exception from downstream middleware/controllers
// and returns a clean JSON error response instead of an ugly stack trace.
// Global JSON error handler (AppError parity)
app.Use(
    async (ctx, next) =>
    {
        try
        {
            // Try to process the request normally.
            await next();
        }
        catch (Exception ex)
        {
            // Something went wrong! Log the error and return a JSON error response.
            var id = ctx.Items["RequestId"]?.ToString() ?? "-";
            Console.Error.WriteLine(
                $"[ERROR] [{id}] {ctx.Request.Method} {ctx.Request.Path} {ex.GetType().Name}: {ex.Message}"
            );

            // IMPORTANT: Check if the response has already started.
            // For WebSocket connections, the HTTP response is already sent (the "upgrade" response).
            // Trying to set StatusCode or write to the body would throw ANOTHER exception.
            // A hijacked/upgraded request (WebSocket) has already started:
            // touching StatusCode/Body here throws a second exception.
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(
                    new { error = "Internal server error", requestId = id }
                );
            }
        }
    }
);

// ── Enable CORS Middleware ──
// Actually applies the CORS policy configured earlier in AddCors().
// Must be called before MapControllers() so CORS headers are added to responses.
app.UseCors();

// ── Enable WebSocket Middleware ──
// Tells ASP.NET to handle WebSocket upgrade requests.
// Without this, WebSocket connections would be rejected.
app.UseWebSockets();

// ── Map Controller Routes ──
// Scans all registered controllers and maps their [HttpGet], [HttpPost], etc.
// attributes to URL routes. For example:
//   [HttpPost("/api/auth/register")] on AuthController.Register()
//   → POST /api/auth/register is handled by that method.
app.MapControllers();

// =============================================================================
// PHASE 4: MAP ENDPOINTS (Simple inline endpoints)
// =============================================================================

// ── Health Check Endpoints ──
// Simple endpoints to verify the server is running.
// "/" — Returns a basic "working" message. Good for quick browser checks.
app.MapGet("/", () => Results.Json(new { message = "Backend working!" }));

// "/health" — Standard health check endpoint used by monitoring tools,
// load balancers, and container orchestrators (Docker, Kubernetes).
app.MapGet("/health", () => Results.Json(new { status = "ok" }));

// ── OpenAPI/Swagger Documentation ──
// MapOpenApi() serves the auto-generated API documentation JSON.
app.MapOpenApi();

// /docs redirects to the OpenAPI JSON for convenience.
app.MapGet("/docs", () => Results.Redirect("/openapi/v1.json"));

// =============================================================================
// PHASE 5: WEBSOCKET HANDLER
// =============================================================================
// This is the raw WebSocket handler that mirrors the Express.js WebSocket server.
// It handles the real-time communication protocol between the frontend and backend.
//
// HOW IT WORKS:
//   1. Client opens a WebSocket connection to ws://localhost:3000/
//   2. This middleware intercepts the WebSocket upgrade request.
//   3. It enters a loop, reading messages from the client.
//   4. Based on the message type, it performs actions:
//      - "subscribe":    Register for room updates
//      - "unsubscribe":  Leave a room
//      - "submit_answer": Submit an answer to a question
//   5. The loop continues until the client disconnects.
//
// MESSAGE PROTOCOL (JSON over WebSocket):
//   Client → Server:
//     { "type": "subscribe", "code": "XK7M2P" }
//     { "type": "unsubscribe", "code": "XK7M2P", "tempProfileId": "guid" }
//     { "type": "submit_answer", "code": "XK7M2P", "participantId": "guid",
//       "roomQuestionId": "guid", "selectedOptionId": "guid", "timeTakenSeconds": 15 }
//
//   Server → Client (via PublishAsync):
//     { "type": "participants_updated", "participants": [...] }
//     { "type": "room_started", "room": {...} }
//     { "type": "leaderboard_updated", "scoreboard": [...] }
//     { "type": "room_ended", "room": {...} }
//
// ── Raw WebSocket handler (parity with Express `ws` server) ──
// Frontend connects to ws://<host>/ and sends:
// {type:"subscribe",code}, {type:"unsubscribe",code,tempProfileId},
// {type:"submit_answer",code,participantId,roomQuestionId,selectedOptionId,timeTakenSeconds}
// Intercepts WS upgrades at any path, like `new WebSocketServer({ server })` does.
app.Use(
    async (HttpContext ctx, RequestDelegate next) =>
    {
        // ── Check if this is a WebSocket request ──
        // Not all requests are WebSocket upgrades — only pass through if it IS one.
        // Regular HTTP requests (GET, POST, etc.) should continue to other middleware.
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            await next(ctx); // Pass to the next middleware (controllers, etc.)
            return;
        }

        // ── Get required services ──
        // Get the singleton WebSocket manager.
        var sockets = ctx.RequestServices.GetRequiredService<RoomSocketManager>();

        // Create a new DI scope for this WebSocket connection.
        // WHY? RoomService and DbContext are SCOPED services (one per request).
        // A WebSocket connection can last minutes — we need our own scope.
        // RoomService + DbContext must be scoped per connection
        using var scope = ctx.RequestServices.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // ── Accept the WebSocket connection ──
        // This sends the HTTP 101 "Switching Protocols" response to the client,
        // upgrading from HTTP to the WebSocket protocol.
        WebSocket? ws = null;
        try
        {
            ws = await ctx.WebSockets.AcceptWebSocketAsync();
        }
        catch (WebSocketException)
        {
            return; // handshake failed — nothing to do
        }

        // ── WebSocket Message Loop ──
        // This loop reads messages from the client until the connection closes.
        // "using (ws)" ensures the WebSocket is properly disposed when we're done.
        using (ws)
        {
            // Buffer for receiving messages. 64KB is plenty for JSON messages.
            var buf = new byte[64 * 1024];
            try
            {
                // Keep reading messages as long as the connection is open.
                while (ws.State == WebSocketState.Open)
                {
                    // ── Read a message from the client ──
                    WebSocketReceiveResult res;
                    try
                    {
                        // ReceiveAsync blocks until a message arrives.
                        // It fills 'buf' with the message bytes and returns metadata.
                        res = await ws.ReceiveAsync(buf, CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        break; // tab closed / navigated away without close handshake — normal
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Server is shutting down.
                    }

                    // If the client sent a "close" frame, exit the loop gracefully.
                    if (res.MessageType == WebSocketMessageType.Close)
                        break;

                    // ── Parse the JSON message ──
                    JsonDocument doc;
                    try
                    {
                        // Convert the received bytes to a string, then parse as JSON.
                        // res.Count is the number of bytes received (the message length).
                        doc = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, res.Count));
                    }
                    catch
                    {
                        continue; // Invalid JSON — skip this message and wait for the next one.
                    }

                    // Extract the "type" and "code" fields from the JSON.
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

                    // ============================================================
                    // SUBSCRIBE — Client wants to receive updates for a room.
                    // ============================================================
                    if (type == "subscribe")
                    {
                        // Register this WebSocket for the room's broadcast channel.
                        sockets.Subscribe(code, ws);

                        // Send the current participant list immediately so the client
                        // doesn't have to wait for the next change.
                        var room = await rooms.GetActiveRoomAsync(code);
                        if (room is not null)
                        {
                            // Load all participants in the room with their profiles.
                            var parts = await db
                                .RoomParticipants.Where(p => p.RoomId == room.Id)
                                .OrderBy(p => p.JoinedAt)
                                .Include(p => p.Profile)
                                .ToListAsync();

                            // Serialize the participant list to JSON bytes.
                            var payload = JsonSerializer.SerializeToUtf8Bytes(
                                new
                                {
                                    type = "participants_updated",
                                    participants = parts.Select(p => new
                                    {
                                        id = p.Id,
                                        profileId = p.ProfileId,
                                        displayName = p.DisplayName,
                                        isHost = p.IsHost,
                                        joinedAt = p.JoinedAt,
                                        avatarUrl = p.Profile != null ? p.Profile.AvatarUrl : null,
                                    }),
                                },
                                WsJson.Options
                            );

                            // Send to THIS client only (not broadcast — just the initial state).
                            if (ws.State == WebSocketState.Open)
                            {
                                try
                                {
                                    await ws.SendAsync(
                                        payload,
                                        WebSocketMessageType.Text,
                                        true,
                                        CancellationToken.None
                                    );
                                }
                                catch (WebSocketException)
                                {
                                    break;
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    // ============================================================
                    // UNSUBSCRIBE — Client is leaving a room.
                    // ============================================================
                    else if (type == "unsubscribe")
                    {
                        // Remove this WebSocket from all room subscriptions.
                        sockets.Unsubscribe(ws);

                        // If a tempProfileId is provided, remove the guest participant.
                        // This happens when a temporary/guest user leaves the lobby.
                        var roomCode = code.Trim().ToUpperInvariant();
                        var room = await rooms.GetActiveRoomAsync(roomCode);
                        if (room is not null)
                        {
                            // Check if the unsubscribe message includes a temp profile ID.
                            if (
                                root.TryGetProperty("tempProfileId", out var tp)
                                && Guid.TryParse(tp.GetString(), out var profId)
                            )
                            {
                                // Find and remove the guest participant from the room.
                                var part = await db.RoomParticipants.FirstOrDefaultAsync(p =>
                                    p.ProfileId == profId
                                );
                                if (part is not null)
                                {
                                    db.RoomParticipants.Remove(part);
                                    await db.SaveChangesAsync();
                                }
                            }

                            // Broadcast the updated participant list to remaining subscribers.
                            // Other people in the lobby will see this player disappear.
                            var rest = await db
                                .RoomParticipants.Where(p => p.RoomId == room.Id)
                                .OrderBy(p => p.JoinedAt)
                                .Include(p => p.Profile)
                                .ToListAsync();
                            await sockets.PublishAsync(
                                roomCode,
                                new
                                {
                                    type = "participants_updated",
                                    participants = rest.Select(p => new
                                    {
                                        id = p.Id,
                                        profileId = p.ProfileId,
                                        displayName = p.DisplayName,
                                        isHost = p.IsHost,
                                        joinedAt = p.JoinedAt,
                                        avatarUrl = p.Profile != null ? p.Profile.AvatarUrl : null,
                                    }),
                                }
                            );
                        }

                        // Close the WebSocket connection gracefully.
                        try
                        {
                            await ws.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "bye",
                                CancellationToken.None
                            );
                        }
                        catch (WebSocketException)
                        { /* already gone */
                        }
                        catch (OperationCanceledException) { }
                        break; // Exit the message loop.
                    }
                    // ============================================================
                    // SUBMIT ANSWER — Client submits an answer via WebSocket.
                    // ============================================================
                    else if (type == "submit_answer")
                    {
                        try
                        {
                            // Verify the room exists and is in LIVE status.
                            var room = await rooms.GetActiveRoomAsync(code);
                            if (room is null || room.Status != RoomStatus.LIVE)
                                continue;

                            // Extract all the answer fields from the JSON message.
                            // The '!' (null-forgiving operator) tells the compiler
                            // "I know this isn't null" — we'd get an exception if it is.
                            var pid = Guid.Parse(root.GetProperty("participantId").GetString()!);
                            var rqid = Guid.Parse(root.GetProperty("roomQuestionId").GetString()!);

                            // selectedOptionId is optional (null if the participant skipped).
                            Guid? optId = null;
                            if (
                                root.TryGetProperty("selectedOptionId", out var so)
                                && so.ValueKind == JsonValueKind.String
                                && Guid.TryParse(so.GetString(), out var g)
                            )
                                optId = g;

                            // timeTakenSeconds is optional.
                            int? secs =
                                root.TryGetProperty("timeTakenSeconds", out var tt)
                                && tt.ValueKind == JsonValueKind.Number
                                    ? tt.GetInt32()
                                    : null;

                            // Save the answer (delegates to RoomService for correctness checking).
                            await rooms.SaveAnswerAsync(room.Id, pid, rqid, optId, secs);

                            // Mark the room as "dirty" so the LeaderboardFlusher broadcasts
                            // an updated leaderboard within the next 1-second tick.
                            LeaderboardFlusher.MarkDirty(code);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"WS submit_answer error: {ex.Message}");
                        }
                    }
                }
            }
            catch (WebSocketException)
            {
                // Abrupt client disconnect mid-loop — normal, just clean up.
            }
            catch (OperationCanceledException) { }
            finally
            {
                // ALWAYS clean up the WebSocket subscription, even if an error occurred.
                // This prevents "ghost" subscriptions that would leak memory.
                sockets.Unsubscribe(ws);
            }
        }
    }
);

// =============================================================================
// PHASE 6: START THE SERVER
// =============================================================================

// ── Configure the listening port ──
// Use the PORT environment variable if set (common in deployment platforms
// like Heroku, Railway, etc.), otherwise default to port 3000.
var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";

// Clear any default URLs (like http://localhost:5161 from launchSettings.json)
// and set our own URL.
app.Urls.Clear();
app.Urls.Add($"http://localhost:{port}");

// Log startup information.
Console.WriteLine($"Server running on http://localhost:{port}");
Console.WriteLine($"Swagger UI at http://localhost:{port}/openapi/v1.json");

// ── Start listening for requests ──
// app.Run() blocks the main thread and keeps the server running until
// it's shut down (Ctrl+C, SIGTERM, etc.). This is the last line that executes.
app.Run();
