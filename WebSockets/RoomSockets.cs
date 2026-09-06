
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace QuizHub.WebSockets;

public static class WsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public class RoomSocketManager
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, byte>> _rooms = new();

    public void Subscribe(string code, WebSocket ws)
    {
        var key = code.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(key)) return;

        _rooms.GetOrAdd(key, _ => new()).TryAdd(ws, 0);
    }

    public void Unsubscribe(WebSocket ws)
    {
        foreach (var (key, set) in _rooms)
        {
            set.TryRemove(ws, out _);

            if (set.IsEmpty) _rooms.TryRemove(key, out _);
        }
    }

    public async Task PublishAsync(string code, object payload)
    {
        var key = code.Trim().ToUpperInvariant();

        if (!_rooms.TryGetValue(key, out var set)) return;

        var msg = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, WsJson.Options));

        foreach (var ws in set.Keys)
        {
            if (ws.State != WebSocketState.Open) continue;

            try
            {
                await ws.SendAsync(msg, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
            }
        }
    }
}


public class LeaderboardFlusher(RoomSocketManager sockets, IServiceProvider sp) : BackgroundService
{
    public static readonly ConcurrentDictionary<string, byte> DirtyRooms = new();

    public static void MarkDirty(string code) => DirtyRooms[code.Trim().ToUpperInvariant()] = 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (DirtyRooms.IsEmpty) continue;

            var codes = DirtyRooms.Keys.ToArray();
            DirtyRooms.Clear();

            using var scope = sp.CreateScope();
            var rooms = scope.ServiceProvider.GetRequiredService<Services.RoomService>();

            foreach (var code in codes)
            {
                try
                {
                    var room = await rooms.GetActiveRoomAsync(code, stoppingToken);
                    if (room is null) continue;

                    var board = await rooms.ComputeLeaderboardAsync(room.Id, stoppingToken);

                    var allDone = board.Count > 0 && board.All(e => e.Answered >= e.Total);

                    await sockets.PublishAsync(code, new
                    {
                        type = "leaderboard_updated",
                        roomId = room.Id,
                        scoreboard = board,
                        allCompleted = allDone
                    });
                }
                catch
                {
                }
            }
        }
    }
}
