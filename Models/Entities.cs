
namespace QuizHub.Models;


public enum Role { USER, ADMIN, MODERATOR }

public enum RoomStatus { LOBBY, LIVE, ENDED }


public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Profile? Profile { get; set; }
}

public class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? UserId { get; set; }

    public string? Username { get; set; }

    public string? AvatarUrl { get; set; }

    public Role Role { get; set; } = Role.USER;

    public int TotalPoints { get; set; } = 0;

    public bool IsTemporary { get; set; } = false;

    public string? GuestToken { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;


    public User? User { get; set; }

    public List<Question> Questions { get; set; } = new();

    public List<QuizRoom> HostedRooms { get; set; } = new();

    public List<RoomParticipant> Participants { get; set; } = new();
}

public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CreatedById { get; set; }

    public string Text { get; set; } = "";

    public string? Explanation { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


    public Profile? CreatedBy { get; set; }

    public List<QuestionOption> Options { get; set; } = new();

    public List<RoomQuestion> RoomItems { get; set; } = new();
}

public class QuestionOption
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid QuestionId { get; set; }

    public string Text { get; set; } = "";

    public bool IsCorrect { get; set; } = false;

    public int SortOrder { get; set; } = 0;


    public Question? Question { get; set; }

    public List<ParticipantAnswer> Answers { get; set; } = new();
}

public class QuizRoom
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid HostId { get; set; }

    public string Code { get; set; } = "";

    public RoomStatus Status { get; set; } = RoomStatus.LOBBY;

    public int CurrentQuestionIndex { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }


    public Profile? Host { get; set; }

    public List<RoomParticipant> Participants { get; set; } = new();

    public List<RoomQuestion> Questions { get; set; } = new();

    public List<ParticipantAnswer> Answers { get; set; } = new();
}

public class RoomParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }

    public Guid ProfileId { get; set; }

    public string DisplayName { get; set; } = "";

    public bool IsHost { get; set; } = false;

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;


    public QuizRoom? Room { get; set; }

    public Profile? Profile { get; set; }

    public List<ParticipantAnswer> Answers { get; set; } = new();
}

public class RoomQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }

    public Guid QuestionId { get; set; }

    public int OrderIndex { get; set; }

    public int Points { get; set; }


    public QuizRoom? Room { get; set; }

    public Question? Question { get; set; }

    public List<ParticipantAnswer> Answers { get; set; } = new();
}

public class ParticipantAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }

    public Guid RoomQuestionId { get; set; }

    public Guid ParticipantId { get; set; }

    public Guid? SelectedOptionId { get; set; }

    public bool IsCorrect { get; set; }

    public DateTime AnsweredAt { get; set; } = DateTime.UtcNow;

    public int? TimeTakenSeconds { get; set; }


    public QuizRoom? Room { get; set; }

    public RoomQuestion? RoomQuestion { get; set; }

    public RoomParticipant? Participant { get; set; }

    public QuestionOption? SelectedOption { get; set; }
}
