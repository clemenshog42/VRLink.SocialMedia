using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Data;

public class PublisherDb(DbContextOptions<PublisherDb> options) : DbContext(options)
{
    public DbSet<Clip> Clips => Set<Clip>();
    public DbSet<PlatformPost> Posts => Set<PlatformPost>();
    public DbSet<StatSnapshot> Stats => Set<StatSnapshot>();
    public DbSet<AccountConnection> Accounts => Set<AccountConnection>();
    public DbSet<AppCredential> Credentials => Set<AppCredential>();
    public DbSet<KeyValueSetting> Settings => Set<KeyValueSetting>();
    public DbSet<ErrorLogEntry> Log => Set<ErrorLogEntry>();
    public DbSet<SharePack> SharePacks => Set<SharePack>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Clip>(e =>
        {
            e.HasIndex(x => x.HighlightId).IsUnique();
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<PlatformPost>(e =>
        {
            e.HasIndex(x => new { x.ClipId, x.Platform }).IsUnique();
            e.HasIndex(x => new { x.Platform, x.Status });
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Platform).HasConversion<string>();
            e.HasOne(x => x.Clip).WithMany(c => c.Posts).HasForeignKey(x => x.ClipId);
        });
        b.Entity<StatSnapshot>(e =>
        {
            e.HasIndex(x => new { x.PlatformPostId, x.TakenUtc });
            e.HasOne(x => x.PlatformPost).WithMany(p => p.Stats).HasForeignKey(x => x.PlatformPostId);
        });
        b.Entity<AccountConnection>(e =>
        {
            e.HasKey(x => x.Platform);
            e.Property(x => x.Platform).HasConversion<string>();
        });
        b.Entity<AppCredential>(e =>
        {
            e.HasKey(x => x.Platform);
            e.Property(x => x.Platform).HasConversion<string>();
        });
        b.Entity<KeyValueSetting>().HasKey(x => x.Key);
        b.Entity<SharePack>(e =>
        {
            e.HasIndex(x => x.MatchId);
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<ErrorLogEntry>(e =>
        {
            e.HasIndex(x => x.Utc);
            e.Property(x => x.Level).HasConversion<string>();
        });
    }
}
