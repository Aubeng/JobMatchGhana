using JobMatch.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace JobMatch.Infrastructure.Data;

/// <summary>A BOOST administrator who signs in to the admin SPA (ASP.NET Core Identity).</summary>
public class AdminUser : IdentityUser
{
    public string? DisplayName { get; set; }
}

public class JobMatchDbContext : IdentityDbContext<AdminUser>
{
    public JobMatchDbContext(DbContextOptions<JobMatchDbContext> options) : base(options) { }

    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<WorkerSkill> WorkerSkills => Set<WorkerSkill>();
    public DbSet<WorkerArea> WorkerAreas => Set<WorkerArea>();
    public DbSet<WorkerCv> WorkerCvs => Set<WorkerCv>();
    public DbSet<WorkerCvJob> WorkerCvJobs => Set<WorkerCvJob>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<JobPostingSkill> JobPostingSkills => Set<JobPostingSkill>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MessageLog> MessageLogs => Set<MessageLog>();
    public DbSet<HandoffCase> HandoffCases => Set<HandoffCase>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);   // Identity tables

        b.Entity<HandoffCase>(e =>
        {
            e.Property(h => h.WaId).HasMaxLength(32).IsRequired();
            e.Property(h => h.ResolvedBy).HasMaxLength(256);
            e.Property(h => h.Note).HasMaxLength(1000);
            e.HasIndex(h => new { h.WaId, h.ResolvedUtc });
        });

        b.Entity<Worker>(e =>
        {
            e.HasIndex(w => w.WaId).IsUnique();
            e.Property(w => w.WaId).HasMaxLength(32).IsRequired();
            e.Property(w => w.DisplayName).HasMaxLength(120);
            e.Property(w => w.PreferredLanguage).HasMaxLength(8);
            e.Property(w => w.LocationText).HasMaxLength(200);
            e.Property(w => w.TradeCategoryId).HasMaxLength(64);
            e.Property(w => w.TradeName).HasMaxLength(128);
            e.Property(w => w.ExperienceText).HasMaxLength(64);
            e.Property(w => w.Availability).HasMaxLength(64);
            e.Property(w => w.LocationDistrict).HasMaxLength(64);
            e.Property(w => w.LocationRegion).HasMaxLength(32);
            e.HasIndex(w => w.LocationDistrict);
        });

        b.Entity<WorkerArea>(e =>
        {
            e.HasOne(a => a.Worker).WithMany(w => w.Areas).HasForeignKey(a => a.WorkerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(a => a.District).HasMaxLength(64).IsRequired();
            e.Property(a => a.Region).HasMaxLength(32);
            e.Property(a => a.LocationText).HasMaxLength(200);
            e.HasIndex(a => new { a.WorkerId, a.District }).IsUnique();
            e.HasIndex(a => a.District);   // new-job alerts: workers in a job's neighbourhood
        });

        b.Entity<WorkerCv>(e =>
        {
            e.HasOne(c => c.Worker).WithOne().HasForeignKey<WorkerCv>(c => c.WorkerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.WorkerId).IsUnique();
            e.Property(c => c.FullName).HasMaxLength(60).IsRequired();
            e.Property(c => c.Phone).HasMaxLength(20);
            e.Property(c => c.Training).HasMaxLength(320);
            e.Property(c => c.Education).HasMaxLength(60);
            e.Property(c => c.Languages).HasMaxLength(200);
            e.Property(c => c.About).HasMaxLength(300);
        });

        b.Entity<WorkerCvJob>(e =>
        {
            e.HasOne(j => j.WorkerCv).WithMany(c => c.Jobs).HasForeignKey(j => j.WorkerCvId).OnDelete(DeleteBehavior.Cascade);
            e.Property(j => j.Description).HasMaxLength(160).IsRequired();
            e.Property(j => j.Period).HasMaxLength(40);
            e.Property(j => j.SourceUtterance).HasMaxLength(500);
        });

        b.Entity<Skill>(e =>
        {
            e.HasIndex(s => s.CanonicalName).IsUnique();
            e.Property(s => s.CanonicalName).HasMaxLength(120).IsRequired();
            e.Property(s => s.Category).HasMaxLength(80);
        });

        b.Entity<WorkerSkill>(e =>
        {
            e.HasOne(ws => ws.Worker).WithMany(w => w.Skills)
                .HasForeignKey(ws => ws.WorkerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ws => ws.Skill).WithMany(s => s.WorkerSkills)
                .HasForeignKey(ws => ws.SkillId).OnDelete(DeleteBehavior.Restrict);
            // Research data — keep the raw utterance and confidence.
            e.Property(ws => ws.SourceUtterance).HasMaxLength(1000);
            e.HasIndex(ws => new { ws.WorkerId, ws.SkillId }).IsUnique();
        });

        b.Entity<JobPosting>(e =>
        {
            e.Property(j => j.Title).HasMaxLength(200).IsRequired();
            e.Property(j => j.EmployerContact).HasMaxLength(64);
            e.Property(j => j.Status).HasMaxLength(20);
            e.Property(j => j.TradeCategoryId).HasMaxLength(64);
            e.Property(j => j.District).HasMaxLength(64);
            e.Property(j => j.Region).HasMaxLength(32);
            e.Property(j => j.PayMin).HasPrecision(10, 2);
            e.Property(j => j.PayMax).HasPrecision(10, 2);
            e.Property(j => j.PayPeriod).HasMaxLength(16);
            e.Property(j => j.Availability).HasMaxLength(32);
            e.Property(j => j.EmployerName).HasMaxLength(120);
            e.HasIndex(j => new { j.Status, j.District });   // stage 1 geographic filter
        });

        b.Entity<JobPostingSkill>(e =>
        {
            e.HasOne(js => js.JobPosting).WithMany(j => j.Skills)
                .HasForeignKey(js => js.JobPostingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(js => js.Skill).WithMany(s => s.JobPostingSkills)
                .HasForeignKey(js => js.SkillId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Match>(e =>
        {
            e.HasOne(m => m.Worker).WithMany(w => w.Matches)
                .HasForeignKey(m => m.WorkerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.JobPosting).WithMany(j => j.Matches)
                .HasForeignKey(m => m.JobPostingId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.WorkerId, m.JobPostingId }).IsUnique();
            e.Property(m => m.Channel).HasMaxLength(16);
            e.Property(m => m.FeedbackText).HasMaxLength(1000);
        });

        b.Entity<MessageLog>(e =>
        {
            // Idempotency: at-least-once delivery deduped on Meta's message id.
            // Filtered unique index so multiple outbound rows (null id) are allowed.
            e.HasIndex(m => m.WaMessageId).IsUnique().HasFilter("[WaMessageId] IS NOT NULL");
            e.Property(m => m.WaMessageId).HasMaxLength(128);
            e.Property(m => m.WaId).HasMaxLength(32).IsRequired();
            e.Property(m => m.MessageType).HasMaxLength(32);
            e.Property(m => m.NluIntent).HasMaxLength(32);
            e.Property(m => m.AnnotatedIntent).HasMaxLength(32);
            e.Property(m => m.AnnotatedBy).HasMaxLength(256);
            e.Property(m => m.NluModelVersion).HasMaxLength(128);
            e.Property(m => m.DeliveryStatus).HasMaxLength(16);
            e.Property(m => m.DeliveryError).HasMaxLength(400);
            e.HasIndex(m => m.WaId);
            e.HasIndex(m => m.Timestamp);
        });
    }
}
