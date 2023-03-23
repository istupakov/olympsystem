using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain.Models;

namespace Olymp.Domain;

public partial class OlympContext : IdentityDbContext<User, IdentityRole<int>, int>
{
    protected OlympContext()
    {
    }

    public OlympContext(DbContextOptions<OlympContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Blob> Blobs { get; set; }

    public virtual DbSet<CheckResult> CheckResults { get; set; }

    public virtual DbSet<Checker> Checkers { get; set; }

    public virtual DbSet<Compilator> Compilators { get; set; }

    public virtual DbSet<Message> Messages { get; set; }

    public virtual DbSet<News> News { get; set; }

    public virtual DbSet<Contest> Contests { get; set; }

    public virtual DbSet<Organization> Organizations { get; set; }

    public virtual DbSet<Submission> Submissions { get; set; }

    public virtual DbSet<Problem> Problems { get; set; }

    public virtual DbSet<Test> Tests { get; set; }

    public virtual DbSet<OlympUser> OlympUsers { get; set; }

    public virtual DbSet<Competitor> Competitors { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<User>().HasDiscriminator();

        builder.Entity<OlympUser>()
            .HasMany(x => x.Memberships)
            .WithMany(x => x.Members)
            .UsingEntity("Participations");

        builder.Entity<User>()
            .HasOne(x => x.Coach)
            .WithMany(x => x.Teams);

        builder.Entity<Submission>()
            .HasOne(x => x.Problem)
            .WithMany(x => x.Submissions)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Submission>()
            .HasOne(x => x.Compilator)
            .WithMany(x => x.Submissions)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Submission>()
            .HasOne(x => x.User)
            .WithMany(x => x.Submissions)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Submission>()
            .HasIndex(x => x.CommitTime).IsDescending();

        builder.Entity<News>()
            .HasIndex(x => x.PublicationDate).IsDescending();
    }
}
