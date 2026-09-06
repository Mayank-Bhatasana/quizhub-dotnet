
using Microsoft.EntityFrameworkCore;
using QuizHub.Models;

namespace QuizHub.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{

    public DbSet<User> Users => Set<User>();

    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<Question> Questions => Set<Question>();

    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();

    public DbSet<QuizRoom> QuizRooms => Set<QuizRoom>();

    public DbSet<RoomParticipant> RoomParticipants => Set<RoomParticipant>();

    public DbSet<RoomQuestion> RoomQuestions => Set<RoomQuestion>();

    public DbSet<ParticipantAnswer> ParticipantAnswers => Set<ParticipantAnswer>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("User");

            e.HasKey(x => x.Id);

            e.HasIndex(x => x.Email).IsUnique();

            e.HasOne(x => x.Profile).WithOne(x => x.User)
                .HasForeignKey<Profile>(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Profile>(e =>
        {
            e.ToTable("Profile");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.UserId).IsUnique();

            e.HasIndex(x => x.GuestToken).IsUnique();

            e.Property(x => x.Role).HasConversion<string>();
        });

        b.Entity<Question>(e =>
        {
            e.ToTable("Question");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.CreatedById);

            e.HasIndex(x => x.IsActive);

            e.HasOne(x => x.CreatedBy).WithMany(x => x.Questions)
                .HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<QuestionOption>(e =>
        {
            e.ToTable("QuestionOption");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.QuestionId);

            e.HasIndex(x => new { x.QuestionId, x.SortOrder }).IsUnique();

            e.HasOne(x => x.Question).WithMany(x => x.Options)
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<QuizRoom>(e =>
        {
            e.ToTable("QuizRoom");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.Code).IsUnique();

            e.HasIndex(x => x.HostId);

            e.Property(x => x.Status).HasConversion<string>();

            e.HasOne(x => x.Host).WithMany(x => x.HostedRooms)
                .HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RoomParticipant>(e =>
        {
            e.ToTable("RoomParticipant");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.RoomId);
            e.HasIndex(x => x.ProfileId);

            e.HasIndex(x => new { x.RoomId, x.ProfileId }).IsUnique();

            e.HasOne(x => x.Room).WithMany(x => x.Participants)
                .HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Profile).WithMany(x => x.Participants)
                .HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RoomQuestion>(e =>
        {
            e.ToTable("RoomQuestion");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.RoomId);
            e.HasIndex(x => x.QuestionId);

            e.HasIndex(x => new { x.RoomId, x.OrderIndex }).IsUnique();

            e.HasOne(x => x.Room).WithMany(x => x.Questions)
                .HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Question).WithMany(x => x.RoomItems)
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ParticipantAnswer>(e =>
        {
            e.ToTable("ParticipantAnswer");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.RoomId);
            e.HasIndex(x => x.ParticipantId);

            e.HasIndex(x => new { x.RoomQuestionId, x.ParticipantId }).IsUnique();

            e.HasOne(x => x.Room).WithMany(x => x.Answers)
                .HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.RoomQuestion).WithMany(x => x.Answers)
                .HasForeignKey(x => x.RoomQuestionId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Participant).WithMany(x => x.Answers)
                .HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.SelectedOption).WithMany(x => x.Answers)
                .HasForeignKey(x => x.SelectedOptionId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
