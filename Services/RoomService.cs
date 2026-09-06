// =============================================================================
// FILE: Services/RoomService.cs
// PURPOSE: Business logic for quiz rooms — finding rooms, saving answers,
//          computing leaderboards, and auto-expiring timed-out rooms.
//
// WHAT IS A "SERVICE" IN ASP.NET?
//   A service is a class that encapsulates business logic — the "rules" of
//   your application. Controllers handle HTTP requests and responses, but
//   the actual work (database queries, calculations, validation) lives here.
//   
//   This separation keeps controllers thin and makes the business logic
//   reusable (e.g., both the REST API and WebSocket handler use RoomService).
//
// REGISTERED AS: Scoped in Program.cs (one instance per HTTP request/scope).
//   "Scoped" means a fresh RoomService is created for each request, so each
//   request gets its own database context and doesn't interfere with others.
// =============================================================================

using Microsoft.EntityFrameworkCore;
using QuizHub.Data;
using QuizHub.Models;
using QuizHub.Utils;
using QuizHub.WebSockets;

namespace QuizHub.Services;

/// <summary>
/// A simple data transfer object (DTO) for leaderboard entries.
/// 
/// WHAT IS A RECORD?
///   A C# record is a lightweight class designed for immutable data.
///   It automatically provides: constructor, equality comparison, ToString(), etc.
///   Perfect for DTOs that just carry data without behavior.
/// 
/// WHAT IS A DTO?
///   Data Transfer Object — a simple container used to pass data between layers.
///   Instead of passing raw database entities to the frontend (which might expose
///   sensitive fields), we create DTOs with only the fields the frontend needs.
/// 
/// This record represents one row on the leaderboard/scoreboard.
/// </summary>
/// <param name="Id">The participant's profile ID.</param>
/// <param name="Name">The participant's display name in the room.</param>
/// <param name="Avatar">An emoji character used as the participant's avatar (e.g., "🧠").</param>
/// <param name="AvatarBg">A Tailwind CSS class for the avatar background color (e.g., "bg-violet-200").</param>
/// <param name="Score">Total points earned (correct answers × question points × 100).</param>
/// <param name="Correct">Number of correctly answered questions.</param>
/// <param name="Total">Total number of questions in the quiz.</param>
/// <param name="Answered">Number of questions the participant has answered (correct or not).</param>
/// <param name="TimeSeconds">Total time spent answering (sum of all TimeTakenSeconds). Used for tiebreaking.</param>
public record LeaderboardEntry(Guid Id, string Name, string Avatar, string AvatarBg,
    int Score, int Correct, int Total, int Answered, int TimeSeconds);

/// <summary>
/// Service class containing all business logic related to quiz rooms.
/// 
/// Handles:
///   - Looking up rooms by code or ID (with auto-expiration)
///   - Saving participant answers (with correctness checking)
///   - Computing leaderboards/scoreboards
/// 
/// DEPENDENCY INJECTION (PRIMARY CONSTRUCTOR):
///   RoomService(AppDbContext db, RoomSocketManager sockets)
///   - db: The database context for querying/updating the database.
///   - sockets: The WebSocket manager for broadcasting real-time updates.
///   Both are automatically provided by the DI container.
/// </summary>
public class RoomService(AppDbContext db, RoomSocketManager sockets)
{
    /// <summary>
    /// Finds a quiz room by its code or ID, and auto-expires it if the time is up.
    /// 
    /// This is the primary method for looking up rooms throughout the app.
    /// It's used by controllers, WebSocket handlers, and the leaderboard flusher.
    /// 
    /// AUTO-EXPIRATION: If a room is LIVE but its EndedAt time has passed,
    /// this method automatically transitions it to ENDED status.
    /// This means rooms don't need a separate timer — they expire lazily
    /// (checked whenever someone looks them up).
    /// </summary>
    /// <param name="codeOrId">
    /// Either a 6-character room code (e.g., "XK7M2P") or a GUID string.
    /// The 'byId' parameter controls which one we're searching by.
    /// </param>
    /// <param name="ct">Cancellation token for async operations.</param>
    /// <param name="byId">
    /// If true, search by room ID (GUID). If false (default), search by room code.
    /// </param>
    /// <returns>The room if found (and possibly auto-expired), or null if not found.</returns>
    public async Task<QuizRoom?> GetActiveRoomAsync(string codeOrId, CancellationToken ct = default, bool byId = false)
    {
        // Look up the room using either its ID or its Code.
        // ToUpperInvariant() normalizes codes to uppercase (case-insensitive lookup).
        QuizRoom? room = byId
            ? await db.QuizRooms.FirstOrDefaultAsync(r => r.Id.ToString() == codeOrId, ct)
            : await db.QuizRooms.FirstOrDefaultAsync(r => r.Code == codeOrId.ToUpperInvariant(), ct);

        // If the room doesn't exist, return null (caller shows "Room not found").
        if (room is null) return null;

        // Check if the room should be auto-expired (time's up).
        return await CheckAndExpireAsync(room, ct);
    }

    /// <summary>
    /// Checks if a LIVE room has exceeded its time limit and auto-expires it.
    /// 
    /// This implements "lazy expiration" — instead of running a background timer
    /// for each room, we check the expiration whenever the room is accessed.
    /// 
    /// If the room IS expired:
    ///   1. Set Status to ENDED in the database.
    ///   2. Broadcast a "room_ended" WebSocket message to all subscribers.
    ///   3. Return the updated room.
    /// 
    /// If the room is NOT expired (or is in LOBBY/ENDED status), return it as-is.
    /// </summary>
    /// <param name="room">The room to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The room (possibly with updated Status if it was expired).</returns>
    public async Task<QuizRoom> CheckAndExpireAsync(QuizRoom room, CancellationToken ct = default)
    {
        // Only check rooms that are currently LIVE and have an end time set.
        // LOBBY rooms haven't started yet, ENDED rooms are already done.
        if (room.Status == RoomStatus.LIVE && room.EndedAt != null && DateTime.UtcNow > room.EndedAt)
        {
            // Time's up! Transition the room to ENDED status.
            room.Status = RoomStatus.ENDED;
            await db.SaveChangesAsync(ct);

            // Notify all WebSocket subscribers that the room has ended.
            // The frontend uses this to show the "Quiz Ended!" screen.
            await sockets.PublishAsync(room.Code, new
            {
                type = "room_ended",
                room = new { code = room.Code, status = room.Status.ToString(), endedAt = room.EndedAt }
            });
        }
        return room;
    }

    /// <summary>
    /// Saves a participant's answer for a question, checking correctness.
    /// 
    /// This method implements UPSERT behavior:
    ///   - If the participant hasn't answered this question yet → INSERT a new answer.
    ///   - If they've already answered → UPDATE their existing answer.
    /// 
    /// CORRECTNESS CHECK:
    ///   When a participant selects an option, we look up that option in the database
    ///   and check its IsCorrect field. We store the result (IsCorrect) directly
    ///   on the ParticipantAnswer so we don't have to re-check during leaderboard
    ///   calculations (which happen frequently).
    /// 
    /// CALLED FROM:
    ///   1. RoomsController.Answer() — REST API endpoint for submitting answers.
    ///   2. Program.cs WebSocket handler — when type="submit_answer" is received.
    /// </summary>
    /// <param name="roomId">The ID of the room.</param>
    /// <param name="participantId">The ID of the participant (RoomParticipant.Id).</param>
    /// <param name="roomQuestionId">The ID of the room-question being answered.</param>
    /// <param name="selectedOptionId">
    /// The ID of the selected option, or null if the participant skipped the question.
    /// </param>
    /// <param name="timeTaken">How many seconds the participant took to answer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created or updated ParticipantAnswer.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the room question doesn't exist, doesn't belong to this room,
    /// or the selected option is invalid.
    /// </exception>
    public async Task<ParticipantAnswer> SaveAnswerAsync(Guid roomId, Guid participantId,
        Guid roomQuestionId, Guid? selectedOptionId, int? timeTaken, CancellationToken ct = default)
    {
        // Step 1: Verify the room question exists.
        // The ?? throw pattern: if the left side is null, throw the exception.
        var rq = await db.RoomQuestions.FirstOrDefaultAsync(x => x.Id == roomQuestionId, ct)
            ?? throw new InvalidOperationException("Room question not found");

        // Step 2: Verify the question belongs to the specified room.
        // This prevents participants from answering questions in other rooms.
        if (rq.RoomId != roomId) throw new InvalidOperationException("Room question not found");

        // Step 3: Check if the selected option is correct.
        bool isCorrect = false;
        if (selectedOptionId is not null)
        {
            // Look up the selected option and verify it belongs to this question.
            var opt = await db.QuestionOptions.FirstOrDefaultAsync(
                o => o.Id == selectedOptionId && o.QuestionId == rq.QuestionId, ct)
                ?? throw new InvalidOperationException("Invalid selected option");

            // Read the correctness flag from the option.
            isCorrect = opt.IsCorrect;
        }
        // If selectedOptionId is null, the participant skipped → isCorrect stays false.

        // Step 4: Check if the participant has already answered this question (UPSERT).
        var existing = await db.ParticipantAnswers.FirstOrDefaultAsync(
            a => a.RoomQuestionId == roomQuestionId && a.ParticipantId == participantId, ct);

        if (existing is null)
        {
            // No existing answer → CREATE a new one.
            existing = new ParticipantAnswer
            {
                RoomId = roomId,
                RoomQuestionId = roomQuestionId,
                ParticipantId = participantId,
                SelectedOptionId = selectedOptionId,
                IsCorrect = isCorrect,
                TimeTakenSeconds = timeTaken
            };
            db.ParticipantAnswers.Add(existing);
        }
        else
        {
            // Already answered → UPDATE the existing answer.
            // This allows participants to change their answer before the timer runs out.
            existing.SelectedOptionId = selectedOptionId;
            existing.IsCorrect = isCorrect;
            existing.TimeTakenSeconds = timeTaken;
        }

        // Save changes to the database.
        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// Computes the leaderboard/scoreboard for a given room.
    /// 
    /// HOW SCORING WORKS:
    ///   Score = sum of (Points × 100) for each correctly answered question.
    ///   For example, a question worth 1 point gives 100 score points when correct.
    ///   This ×100 multiplier gives finer granularity for future scoring features
    ///   (like bonus points for speed).
    /// 
    /// SORTING (RANKING):
    ///   1. Primary: Score descending (highest score first).
    ///   2. Tiebreaker: TimeSeconds ascending (faster total time wins ties).
    ///   So if two players both score 300, the one who answered faster ranks higher.
    /// 
    /// NOTE: The host (IsHost=true) is EXCLUDED from the leaderboard.
    ///   The host creates and runs the quiz — they already know the answers!
    /// </summary>
    /// <param name="roomId">The ID of the room to compute the leaderboard for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A sorted list of LeaderboardEntry records (highest score first).</returns>
    public async Task<List<LeaderboardEntry>> ComputeLeaderboardAsync(Guid roomId, CancellationToken ct = default)
    {
        // Load all non-host participants with their answers and room-question point values.
        // .Include() and .ThenInclude() tell EF Core to load related data (JOIN in SQL).
        //
        // Without Include: p.Answers would be empty (EF Core uses "lazy loading" only with proxies).
        // With Include: p.Answers is fully populated with the actual answer data.
        var parts = await db.RoomParticipants
            .Where(p => p.RoomId == roomId && !p.IsHost)   // Exclude the host
            .Include(p => p.Answers)                        // Load each participant's answers
                .ThenInclude(a => a.RoomQuestion)           // For each answer, load its RoomQuestion (for points)
            .ToListAsync(ct);

        // Count total questions in this room (for the "X/Y answered" display).
        var total = await db.RoomQuestions.CountAsync(q => q.RoomId == roomId, ct);

        // Transform each participant into a LeaderboardEntry and sort.
        return parts.Select(p =>
        {
            // Count how many questions this participant got right.
            var correct = p.Answers.Count(a => a.IsCorrect);

            // Calculate total score: sum of (Points × 100) for correct answers only.
            // RoomQuestion!.Points is the point value assigned to this question in this room.
            var score = p.Answers.Where(a => a.IsCorrect).Sum(a => a.RoomQuestion!.Points * 100);

            // Calculate total time spent answering (for tiebreaking).
            // TimeTakenSeconds is null for unanswered questions, so we default to 0.
            var time = p.Answers.Sum(a => a.TimeTakenSeconds ?? 0);

            // Generate a deterministic emoji avatar and background color.
            // This gives each participant a consistent visual identity.
            var (emoji, bg) = AvatarHelper.ForName(p.ProfileId.ToString(), p.DisplayName);

            // Create the leaderboard entry.
            return new LeaderboardEntry(p.ProfileId, p.DisplayName, emoji, bg, score, correct, total, p.Answers.Count, time);
        })
        // Sort: highest score first, then fastest time for tiebreaking.
        .OrderByDescending(e => e.Score).ThenBy(e => e.TimeSeconds).ToList();
    }
}
