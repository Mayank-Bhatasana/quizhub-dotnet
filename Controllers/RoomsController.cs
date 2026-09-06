
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizHub.Data;
using QuizHub.Models;
using QuizHub.Services;
using QuizHub.Utils;
using QuizHub.WebSockets;

namespace QuizHub.Controllers;

[ApiController]
public class RoomsController(AppDbContext db, RoomService rooms, RoomSocketManager sockets)
    : ControllerBase
{

    public record OptionDto(string Text, bool IsCorrect, int? SortOrder);

    public record CreateQuestionDto(
        Guid CreatedById,
        string Text,
        string? Explanation,
        List<OptionDto> Options
    );

    public record RoomQuestionDto(Guid QuestionId, int? Points, int? OrderIndex);

    public record CreateRoomDto(Guid HostProfileId, List<RoomQuestionDto> Questions);

    public record JoinDto(Guid ProfileId, string DisplayName);

    public record StartDto(Guid ProfileId, int? DurationSeconds);

    public record AnswerDto(
        Guid ParticipantId,
        Guid RoomQuestionId,
        Guid? SelectedOptionId,
        int? TimeTakenSeconds
    );


    [HttpPost("/api/questions")]
    public async Task<IActionResult> CreateQuestion([FromBody] CreateQuestionDto d)
    {
        if (
            d.CreatedById == Guid.Empty
            || string.IsNullOrWhiteSpace(d.Text)
            || d.Options is null
            || d.Options.Count == 0
        )
            return BadRequest(new { error = "createdById, text, options are required" });

        var opts = d
            .Options.Select(
                (o, i) =>
                    new QuestionOption
                    {
                        Text = (o.Text ?? "").Trim(),
                        IsCorrect = o.IsCorrect,
                        SortOrder = o.SortOrder ?? i,
                    }
            )
            .ToList();

        if (opts.Any(o => string.IsNullOrEmpty(o.Text)))
            return BadRequest(new { error = "option text is required" });

        var q = new Question
        {
            CreatedById = d.CreatedById,
            Text = d.Text.Trim(),
            Explanation = d.Explanation,
            Options = opts,
        };
        db.Questions.Add(q);
        await db.SaveChangesAsync();

        return Ok(
            new
            {
                question = new
                {
                    id = q.Id,
                    createdById = q.CreatedById,
                    text = q.Text,
                    explanation = q.Explanation,
                    options = q.Options.Select(o => new
                    {
                        id = o.Id,
                        text = o.Text,
                        isCorrect = o.IsCorrect,
                        sortOrder = o.SortOrder,
                    }),
                },
            }
        );
    }


    [HttpPost("/api/rooms")]
    public async Task<IActionResult> CreateRoom([FromBody] CreateRoomDto d)
    {
        if (d.HostProfileId == Guid.Empty || d.Questions is null || d.Questions.Count == 0)
            return BadRequest(new { error = "hostProfileId and questions are required" });

        var code = AuthHelper.GenerateRoomCode();

        for (var i = 0; i < 5 && await db.QuizRooms.AnyAsync(r => r.Code == code); i++)
            code = AuthHelper.GenerateRoomCode();

        var room = new QuizRoom
        {
            HostId = d.HostProfileId,
            Code = code,
            Participants =
            [
                new RoomParticipant
                {
                    ProfileId = d.HostProfileId,
                    DisplayName = "Host",
                    IsHost = true,
                },
            ],
            Questions = d
                .Questions.Select(
                    (x, i) =>
                        new RoomQuestion
                        {
                            QuestionId = x.QuestionId,
                            OrderIndex = x.OrderIndex ?? i,
                            Points = x.Points ?? 1,
                        }
                )
                .ToList(),
        };
        db.QuizRooms.Add(room);
        await db.SaveChangesAsync();

        return Ok(
            new
            {
                room = new
                {
                    id = room.Id,
                    code = room.Code,
                    status = room.Status.ToString(),
                    hostId = room.HostId,
                    questions = room.Questions.Select(x => new
                    {
                        id = x.Id,
                        questionId = x.QuestionId,
                        orderIndex = x.OrderIndex,
                        points = x.Points,
                    }),
                    participants = room.Participants.Select(p => new
                    {
                        id = p.Id,
                        profileId = p.ProfileId,
                        displayName = p.DisplayName,
                        isHost = p.IsHost,
                    }),
                },
            }
        );
    }


    [HttpPost("/api/rooms/{code}/join")]
    public async Task<IActionResult> Join(string code, [FromBody] JoinDto d)
    {
        if (d.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(d.DisplayName))
            return BadRequest(new { error = "profileId and displayName are required" });

        var room = await rooms.GetActiveRoomAsync(code);
        if (room is null)
            return NotFound(new { error = "Room not found" });

        if (room.Status != RoomStatus.LOBBY)
            return Conflict(new { error = "Quiz has already started" });

        var existing = await db.RoomParticipants.FirstOrDefaultAsync(p =>
            p.RoomId == room.Id && p.ProfileId == d.ProfileId
        );

        RoomParticipant p;
        if (existing is null)
        {
            p = new RoomParticipant
            {
                RoomId = room.Id,
                ProfileId = d.ProfileId,
                DisplayName = d.DisplayName.Trim(),
            };
            db.RoomParticipants.Add(p);
        }
        else
        {
            existing.DisplayName = d.DisplayName.Trim();
            p = existing;
        }
        await db.SaveChangesAsync();

        await BroadcastParticipantsAsync(room);

        return Ok(new { roomId = room.Id, participant = p });
    }


    [HttpGet("/api/rooms/{code}/participants")]
    public async Task<IActionResult> Participants(string code)
    {
        var room = await rooms.GetActiveRoomAsync(code);
        if (room is null)
            return NotFound(new { error = "Room not found" });

        var list = await db
            .RoomParticipants.Where(p => p.RoomId == room.Id)
            .OrderBy(p => p.JoinedAt)
            .Include(p => p.Profile)
            .ToListAsync();

        return Ok(new { participants = list.Select(ToParticipantDto) });
    }


    [HttpGet("/api/rooms/{code}")]
    public async Task<IActionResult> Details(string code)
    {
        var room = await rooms.GetActiveRoomAsync(code);
        if (room is null)
            return NotFound(new { error = "Room not found" });

        var count = await db.RoomQuestions.CountAsync(q => q.RoomId == room.Id);

        return Ok(
            new
            {
                room = new
                {
                    id = room.Id,
                    code = room.Code,
                    status = room.Status.ToString(),
                    hostId = room.HostId,
                    startedAt = room.StartedAt,
                    questionCount = count,
                },
            }
        );
    }


    [HttpPost("/api/rooms/{code}/start")]
    public async Task<IActionResult> Start(string code, [FromBody] StartDto d)
    {
        if (d.ProfileId == Guid.Empty)
            return BadRequest(new { error = "profileId is required" });

        var room = await db
            .QuizRooms.Include(r => r.Questions)
            .FirstOrDefaultAsync(r => r.Code == code.ToUpperInvariant());
        if (room is null)
            return NotFound(new { error = "Room not found" });

        if (room.HostId != d.ProfileId)
            return StatusCode(403, new { error = "Only the host can start the quiz" });

        if (room.Status != RoomStatus.LOBBY)
            return BadRequest(new { error = "Room is not in LOBBY status" });

        var n = room.Questions.Count;
        var finalSecs = n * 20 * 2;

        if (d.DurationSeconds is > 0)
            finalSecs = Math.Max(n * 20, d.DurationSeconds.Value);

        room.Status = RoomStatus.LIVE;
        room.StartedAt = DateTime.UtcNow;
        room.EndedAt = room.StartedAt.Value.AddSeconds(finalSecs);
        await db.SaveChangesAsync();

        await sockets.PublishAsync(
            room.Code,
            new
            {
                type = "room_started",
                room = new
                {
                    code = room.Code,
                    status = room.Status.ToString(),
                    startedAt = room.StartedAt,
                    endedAt = room.EndedAt,
                },
            }
        );

        return Ok(
            new
            {
                room = new
                {
                    id = room.Id,
                    code = room.Code,
                    status = room.Status.ToString(),
                    hostId = room.HostId,
                    startedAt = room.StartedAt,
                    endedAt = room.EndedAt,
                },
            }
        );
    }


    [HttpGet("/api/room/{code}/questions")]
    public async Task<IActionResult> Questions(string code)
    {
        var active = await rooms.GetActiveRoomAsync(code);
        if (active is null)
            return NotFound(new { error = "Room not found" });

        var rq = await db
            .RoomQuestions.Where(x => x.RoomId == active.Id)
            .OrderBy(x => x.OrderIndex)
            .Include(x => x.Question!)
                .ThenInclude(q => q.Options)
            .ToListAsync();

        var out_ = rq.Select(x => new
        {
            id = x.Question!.Id,
            roomQuestionId = x.Id,
            text = x.Question.Text,
            points = x.Points,
            explanation = x.Question.Explanation,
            options = x
                .Question.Options.OrderBy(o => o.SortOrder)
                .Select(o => new
                {
                    id = o.Id,
                    text = o.Text,
                    isCorrect = o.IsCorrect,
                }),
        });
        return Ok(new { questions = out_ });
    }


    [HttpPost("/api/rooms/{roomId}/answer")]
    public async Task<IActionResult> Answer(string roomId, [FromBody] AnswerDto d)
    {
        if (d.ParticipantId == Guid.Empty || d.RoomQuestionId == Guid.Empty)
            return BadRequest(new { error = "participantId and roomQuestionId are required" });

        if (!Guid.TryParse(roomId, out var rid))
            return NotFound(new { error = "Room not found" });

        if (!await db.QuizRooms.AnyAsync(r => r.Id == rid))
            return NotFound(new { error = "Room not found" });

        try
        {
            var a = await rooms.SaveAnswerAsync(
                rid,
                d.ParticipantId,
                d.RoomQuestionId,
                d.SelectedOptionId,
                d.TimeTakenSeconds
            );

            var room = await db.QuizRooms.FirstAsync(r => r.Id == rid);
            LeaderboardFlusher.MarkDirty(room.Code);

            return Ok(new { answer = a });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }


    [HttpGet("/api/rooms/{roomId}/scoreboard")]
    public async Task<IActionResult> Scoreboard(string roomId)
    {
        if (!Guid.TryParse(roomId, out var rid))
            return Ok(new { scoreboard = Array.Empty<object>() });

        var board = await rooms.ComputeLeaderboardAsync(rid);
        return Ok(new { scoreboard = board });
    }


    [HttpGet("/api/analytics/{profileId}")]
    public async Task<IActionResult> Analytics(string profileId)
    {
        if (!Guid.TryParse(profileId, out var pid))
            return BadRequest(new { error = "Invalid profileId" });

        var parts = await db
            .RoomParticipants.Where(p => p.ProfileId == pid && !p.IsHost)
            .Include(p => p.Room)
            .Include(p => p.Answers)
                .ThenInclude(a => a.RoomQuestion)
            .OrderByDescending(p => p.JoinedAt)
            .ToListAsync();

        var recent = new List<object>();
        foreach (var p in parts.Take(10))
        {
            var correct = p.Answers.Count(a => a.IsCorrect);

            var score = p.Answers.Where(a => a.IsCorrect).Sum(a => a.RoomQuestion!.Points * 100);

            var time = p.Answers.Sum(a => a.TimeTakenSeconds ?? 0);

            var totalQs = await db.RoomQuestions.CountAsync(q => q.RoomId == p.RoomId);

            var mates = await db
                .RoomParticipants.Where(x => x.RoomId == p.RoomId && !x.IsHost)
                .Include(x => x.Answers)
                    .ThenInclude(a => a.RoomQuestion)
                .ToListAsync();

            var ranked = mates
                .Select(m => new
                {
                    m.ProfileId,
                    Score = m
                        .Answers.Where(a => a.IsCorrect)
                        .Sum(a => a.RoomQuestion!.Points * 100),
                    Time = m.Answers.Sum(a => a.TimeTakenSeconds ?? 0),
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Time)
                .ToList();

            var rank = ranked.FindIndex(x => x.ProfileId == pid) + 1;

            recent.Add(
                new
                {
                    roomCode = p.Room!.Code,
                    roomId = p.RoomId,
                    status = p.Room.Status.ToString(),
                    playedAt = p.Room.StartedAt ?? p.JoinedAt,
                    correct,
                    total = totalQs,
                    answered = p.Answers.Count,
                    score,
                    accuracy = totalQs > 0 ? (int)Math.Round(correct * 100.0 / totalQs) : 0,
                    timeSec = time,
                    rank,
                    totalParticipants = mates.Count,
                }
            );
        }

        var totalCorrect = parts.SelectMany(p => p.Answers).Count(a => a.IsCorrect);
        var totalAnswered = parts.SelectMany(p => p.Answers).Count();
        var totalTime = parts.SelectMany(p => p.Answers).Sum(a => a.TimeTakenSeconds ?? 0);

        var scores = parts
            .Select(p => p.Answers.Where(a => a.IsCorrect).Sum(a => a.RoomQuestion!.Points * 100))
            .ToList();

        return Ok(
            new
            {
                analytics = new
                {
                    totalSessions = parts.Count,
                    totalCorrect,
                    totalAnswered,
                    overallAccuracy = totalAnswered > 0
                        ? (int)Math.Round(totalCorrect * 100.0 / totalAnswered)
                        : 0,
                    bestScore = scores.Count > 0 ? scores.Max() : 0,
                    totalScore = scores.Sum(),
                    avgTimeSec = totalAnswered > 0
                        ? (int)Math.Round(totalTime * 1.0 / totalAnswered)
                        : 0,
                    recentSessions = recent,
                },
            }
        );
    }


    private async Task BroadcastParticipantsAsync(QuizRoom room)
    {
        var list = await db
            .RoomParticipants.Where(p => p.RoomId == room.Id)
            .OrderBy(p => p.JoinedAt)
            .Include(p => p.Profile)
            .ToListAsync();

        await sockets.PublishAsync(
            room.Code,
            new { type = "participants_updated", participants = list.Select(ToParticipantDto) }
        );
    }

    private static object ToParticipantDto(RoomParticipant p) =>
        new
        {
            id = p.Id,
            profileId = p.ProfileId,
            displayName = p.DisplayName,
            isHost = p.IsHost,
            joinedAt = p.JoinedAt,
            avatarUrl = p.Profile != null ? p.Profile.AvatarUrl : null,
        };
}
