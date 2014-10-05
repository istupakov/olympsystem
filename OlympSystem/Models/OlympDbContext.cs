using System.Data.Entity;
using Microsoft.AspNet.Identity.EntityFramework;
using Microsoft.AspNet.Identity;
using System;
using System.Security.Principal;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Tasks;
using System.Security.Claims;
using System.Data.Entity.Migrations;

namespace OlympSystem.Models
{
    public class OlympDbContext : IdentityDbContext<ApplicationUser>
    {
        public DbSet<User> OlympUsers { get; set; }
        public DbSet<Competitor> Competitors { get; set; }
        public DbSet<School> Organizations { get; set; }
        public DbSet<Clarification> Clarifications { get; set; }
        public DbSet<News> News { get; set; }
        public DbSet<Compilator> Compilators { get; set; }
        public DbSet<Contest> Contests { get; set; }
        public DbSet<Solution> Solutions { get; set; }
        public DbSet<Problem> Problems { get; set; }
        public DbSet<Test> Tests { get; set; }
        public DbSet<CheckLog> CheckLogs { get; set; }
        public DbSet<BinaryData> BinaryData { get; set; }

        public OlympDbContext() : base("DefaultConnection") { }
        
        static OlympDbContext()
        {
            Database.SetInitializer(new TestDatabaseInitializer());
            //Database.SetInitializer(new CreateDatabaseIfNotExists<OlympDbContext>());
        }

        public static OlympDbContext Create()
        {
            return new OlympDbContext();
        }

        public void DeleteDB()
        {
            Database.Delete();
        }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Competitor>().Map(m => m.Requires(c => c.ContestId).HasValue())
                .HasMany(m => m.Members).WithMany(m => m.Memberships)
                .Map(m => m.MapLeftKey("UserId").MapRightKey("UserDetailId").ToTable("Participations"));
            modelBuilder.Entity<ClarificationToUser>().Map(m => m.Requires(c => c.UserId).HasValue());
        }
    }

    public class TestDatabaseInitializer : DropCreateDatabaseIfModelChanges<OlympDbContext>
    {
        protected override void Seed(OlympDbContext context)
        {
            for (int i = 0; i < 10; i++)
            {
                context.News.Add(new News { Title = "Новость " + i, Text = "Тестовая новость " + i + " :)", PublicationDate = DateTime.Now - TimeSpan.FromDays(i) });
                context.Compilators.Add(new Compilator { Name = "Visual C++ " + i, Language = "C++", SourceExtension = "cpp", CommandLine = "cl !.!", ConfigName = "VS2013.cmd" });
            }
        }
    }

    public class ApplicationUser : IdentityUser
    {
        public ApplicationUser()
        {
            CreateDate = DateTime.Now;
            LastLoginDate = DateTime.Now;
        } 

        public async Task<ClaimsIdentity> GenerateUserIdentityAsync(UserManager<ApplicationUser> manager)
        {
            // Note the authenticationType must match the one defined in CookieAuthenticationOptions.AuthenticationType
            var userIdentity = await manager.CreateIdentityAsync(this, DefaultAuthenticationTypes.ApplicationCookie);
            // Add custom user claims here
            return userIdentity;
        }

        public string DisplayName { get; set; }
        public int OlympUserId { get; set; }
        public User OlympUser { get; set; }
        
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }

        public DateTime CreateDate { get; set; }
        public DateTime LastLoginDate { get; set; }

        public int? Year { get; set; }
        public string Detail { get; set; }
        public string City { get; set; }
        public string Comment { get; set; }
    }

    public static class ModelExtensions
    {
        //public static ApplicationUser GetUser(this IIdentity identity)
        //{
        //    using (var userManager = new ApplicationUserManager())
        //    {
        //        return userManager.FindById(identity.GetUserId());
        //    }
        //}

        public static T GetAttribute<T>(this Enum value)
            where T: Attribute
        {
            return (T)value.GetType().GetField(value.ToString()).GetCustomAttribute(typeof(T));
        }
    }
}
