using Microsoft.EntityFrameworkCore;
using PlottR0._2.Domain;

namespace PlottR0._2.Repository.Persistence;

public class PlottR0DbContext : DbContext
{
    public PlottR0DbContext(DbContextOptions<PlottR0DbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Trial> Trials => Set<Trial>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<VoiceRecording> VoiceRecordings => Set<VoiceRecording>();
    public DbSet<TrialAssignment> Assignments => Set<TrialAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAuditableEntity<User>(modelBuilder);
        ConfigureAuditableEntity<Trial>(modelBuilder);
        ConfigureAuditableEntity<VoiceRecording>(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Name)
                .IsRequired()
                .HasMaxLength(100);
            entity.Property(u => u.Email)
                .IsRequired()
                .HasMaxLength(320);
            entity.HasIndex(u => u.Email)
                .IsUnique();

            entity.Property(u => u.Role)
                .HasConversion<string>()
                .HasMaxLength(20);
            entity.Property(u => u.PasswordHash)
                .IsRequired()
                .HasMaxLength(100);
        });

        modelBuilder.Entity<Trial>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.ExternalId)
                .IsRequired()
                .HasMaxLength(500);
            entity.Property(t => t.FileName)
                .IsRequired()
                .HasMaxLength(500);
            entity.Property(t => t.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasDefaultValue(TrialStatus.Created);
            entity.Property(t => t.SourceWorkbookPath)
                .HasMaxLength(500);

            entity.HasOne(t => t.CreatedByUser)
                .WithMany()
                .HasForeignKey(t => t.CreatedBy)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(rt => rt.Id);

            entity.Property(rt => rt.Token)
                .IsRequired()
                .HasMaxLength(128);

            entity.HasIndex(rt => rt.Token)
                .IsUnique();

            entity.Property(rt => rt.UserId)
                .IsRequired();

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(rt => rt.ExpiresAt)
                .IsRequired();

            entity.Property(rt => rt.CreatedAt)
                .IsRequired();

            entity.Property(rt => rt.IsRevoked)
                .IsRequired();

            entity.HasIndex(rt => new { rt.UserId, rt.IsRevoked });
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(a => a.Id);

            entity.Property(a => a.Action)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(a => a.EntityType)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(a => a.EntityId)
                .HasMaxLength(100);

            entity.Property(a => a.ActorName)
                .HasMaxLength(200);

            entity.Property(a => a.Timestamp)
                .IsRequired();

            entity.HasIndex(a => a.Timestamp);
            entity.HasIndex(a => new { a.EntityType, a.EntityId });
        });

        modelBuilder.Entity<VoiceRecording>(entity =>
        {
            entity.HasKey(v => v.Id);

            entity.Property(v => v.ClientRecordingId)
                .IsRequired()
                .HasMaxLength(100);
            entity.HasIndex(v => v.ClientRecordingId)
                .IsUnique();

            entity.HasOne<Trial>()
                .WithMany()
                .HasForeignKey(v => v.TrialId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(v => v.BlobPath)
                .IsRequired()
                .HasMaxLength(500);
            entity.Property(v => v.MimeType)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(v => v.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasDefaultValue(VoiceRecordingStatus.Received);
        });

        modelBuilder.Entity<TrialAssignment>(entity =>
        {
            entity.HasKey(a => a.Id);

            entity.Property(a => a.TrialId)
                .IsRequired();
            entity.Property(a => a.UserId)
                .IsRequired();
            entity.Property(a => a.AssignedBy)
                .IsRequired();
            entity.Property(a => a.AssignedAt)
                .IsRequired();

            entity.HasIndex(a => new { a.TrialId, a.UserId })
                .IsUnique();

            entity.HasOne<Trial>()
                .WithMany(t => t.Assignments)
                .HasForeignKey(a => a.TrialId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<User>()
                .WithMany(u => u.Assignments)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAuditableEntity<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.Property(nameof(AuditableEntity.CreatedAt))
                .IsRequired()
                .HasDefaultValueSql("NOW()");

            entity.Property(nameof(AuditableEntity.UpdatedAt))
                .IsRequired()
                .HasDefaultValueSql("NOW()");
        });
    }
}
