using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.QReply;

internal class OlympDataContext
{
    class OlympContextWPF : OlympContext
    {
        public OlympContextWPF(DbContextOptions<OlympContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.HasChangeTrackingStrategy(ChangeTrackingStrategy.ChangingAndChangedNotificationsWithOriginalValues);
        }
    }

    private readonly OlympContext _context;

    public ObservableCollection<Contest> Contests => _context.Contests.Local.ToObservableCollection();

    public OlympDataContext()
    {
        var configuration = new ConfigurationBuilder()
                        .SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                        .AddUserSecrets<OlympDataContext>()
                        .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        var optionsBuilder = new DbContextOptionsBuilder<OlympContext>()
            .UseSqlServer(connectionString)
            .UseLazyLoadingProxies()
            .UseChangeTrackingProxies();

        _context = new OlympContextWPF(optionsBuilder.Options);
    }

    public void RefreshContestMessages(Contest? contest)
    {
        if (contest == null)
            return;
        foreach (var msg in contest.Messages)
        {
            _context.Entry(msg).Reload();
        }
        _context.Entry(contest).Collection(x => x.Messages).IsLoaded = false;
        _context.Entry(contest).Collection(x => x.Messages).Load();
    }

    public void InitDataContext()
    {
        LoadContests();
    }

    public void Save()
    {
        _context.SaveChanges();
    }

    public Message CreateMessage()
    {
        return _context.CreateProxy<Message>();
    }

    public void InsertMessage(Message msg)
    {
        _context.Messages.Add(msg);
    }

    public DateTimeOffset GetCurrentDateTime()
    {
        return _context.Database.SqlQuery<DateTimeOffset>($"SELECT CURRENT_TIMESTAMP;").Single();
    }

    private void LoadContests()
    {
        _context.Contests.Load();
    }
}
