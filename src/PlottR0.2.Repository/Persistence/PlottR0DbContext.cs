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
    public DbSet<Plot> Plots => Set<Plot>();
    public DbSet<Subsample> Subsamples => Set<Subsample>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<VoiceRecording> VoiceRecordings => Set<VoiceRecording>();
    public DbSet<TrialAssignment> Assignments => Set<TrialAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── Shared audit columns for every AuditableEntity ──────────────────
        ConfigureAuditableEntity<User>(modelBuilder);
        ConfigureAuditableEntity<Trial>(modelBuilder);
        ConfigureAuditableEntity<Plot>(modelBuilder);
        ConfigureAuditableEntity<Subsample>(modelBuilder);
        ConfigureAuditableEntity<Rating>(modelBuilder);
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
            // Role is a nullable enum persisted AS A STRING so the existing
            // character varying column is only narrowed (100 → 20) instead of
            // being dropped/recreated — RefreshToken.UserId and Trial.CreatedBy
            // reference the Users table and must keep their FKs intact.
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

            // Trial 1 -> N Plot. Plot has no TrialId property, so EF materializes
            // a shadow FK (TrialId) GUID column on the Plots table.
            entity.HasMany(t => t.Plots)
                .WithOne()
                .HasForeignKey("TrialId")
                .OnDelete(DeleteBehavior.Cascade);

            // Ownership FK (design.md #13): CreatedBy → User, SetNull on delete
            // so orphaned trials remain accessible.
            entity.HasOne(t => t.CreatedByUser)
                .WithMany()
                .HasForeignKey(t => t.CreatedBy)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Plot>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.PlotNumber)
                .IsRequired();

            // Plot 1 -> N Subsample. Subsample has no PlotId property, so EF
            // materializes a shadow FK (PlotId) GUID column on the Subsamples table.
            entity.HasMany(p => p.Subsamples)
                .WithOne()
                .HasForeignKey("PlotId")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Subsample>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.SubsampleIndex)
                .IsRequired();

            // Subsample 1 -> N Rating. Rating has no SubsampleId property, so EF
            // materializes a shadow FK (SubsampleId) GUID column on the Ratings table.
            entity.HasMany(s => s.Ratings)
                .WithOne()
                .HasForeignKey("SubsampleId")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Rating>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Value)
                .IsRequired();

            // TraitId is a plain GUID column. There is no Trait entity persisted
            // (Trait is a Domain value object), so no FK navigation and no FK
            // constraint is created for it.
            entity.Property(r => r.TraitId)
                .IsRequired();
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

        // ── AuditLog (append-only) ──────────────────────────────────────────
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

        // ── VoiceRecording (Voice-to-Excel) ─────────────────────────────────
        modelBuilder.Entity<VoiceRecording>(entity =>
        {
            entity.HasKey(v => v.Id);

            // ClientRecordingId is the PWA-emitted idempotency key
            // (crypto.randomUUID); unique so a duplicate upload replays the
            // existing row instead of creating a duplicate.
            entity.Property(v => v.ClientRecordingId)
                .IsRequired()
                .HasMaxLength(100);
            entity.HasIndex(v => v.ClientRecordingId)
                .IsUnique();

            // TrialId FK → Trial. SetNull on delete so recordings survive a
            // trial deletion (audit evidence is preserved). Nullable for the
            // same reason — recordings may be orphaned intentionally.
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

        // ── TrialAssignment (Trial ↔ User, many-to-many via join entity) ─────
        modelBuilder.Entity<TrialAssignment>(entity =>
        {
            entity.HasKey(a => a.Id);

            // Both FKs are required — an assignment cannot exist without its
            // trial and user — and both cascade so deleting either end cleans up
            // the join rows.
            entity.Property(a => a.TrialId)
                .IsRequired();
            entity.Property(a => a.UserId)
                .IsRequired();
            entity.Property(a => a.AssignedBy)
                .IsRequired();
            entity.Property(a => a.AssignedAt)
                .IsRequired();

            // A user may be assigned to a given trial at most once.
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

    /// <summary>
    /// Configures the four inherited <see cref="AuditableEntity"/> columns for a
    /// given entity type. Called once per audited entity in <see cref="OnModelCreating"/>.
    /// </summary>
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

            // CreatedBy / UpdatedBy are nullable Guid — no extra constraints.
        });
    }
}
