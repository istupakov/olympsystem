using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Manager;

public partial class MainWindow : Window
{
    private OlympContext Context => (OlympContext)DataContext;

    public MainWindow()
    {
        InitializeComponent();
    }

    private OlympContext CreateContext()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<MainWindow>()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        var optionsBuilder = new DbContextOptionsBuilder<OlympContext>()
            .UseSqlServer(connectionString)
            .UseLazyLoadingProxies()
            .UseChangeTrackingProxies();

        return new OlympContext(optionsBuilder.Options);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var context = CreateContext();
        DataContext = context;
        (Resources["ContestsView"] as CollectionViewSource).Source = context.Contests.Local.ToObservableCollection();
        (Resources["CheckersView"] as CollectionViewSource).Source = context.Checkers.Local.ToObservableCollection();
        (Resources["UsersView"] as CollectionViewSource).Source = context.Users.Local.ToObservableCollection();
        (Resources["NewsView"] as CollectionViewSource).Source = context.News.Local.ToObservableCollection();
        (Resources["MessagesView"] as CollectionViewSource).Source = context.Messages.Local.ToObservableCollection();
        (Resources["OrganizationsView"] as CollectionViewSource).Source = context.Organizations.Local.ToObservableCollection();
        (Resources["CompilatorsView"] as CollectionViewSource).Source = context.Compilators.Local.ToObservableCollection();
        (Resources["RetestView"] as CollectionViewSource).Source = null;
    }

    void Update(object sender, RoutedEventArgs e)
    {
        var context = CreateContext();
        DataContext = context;
        context.Contests.Load();
        context.Checkers.Load();
        context.Users.Load();
        context.Messages.Load();
        context.News.Load();
        context.Organizations.Load();
        context.Compilators.Load();

        (Resources["ContestsView"] as CollectionViewSource).Source = context.Contests.Local.ToObservableCollection();
        (Resources["CheckersView"] as CollectionViewSource).Source = context.Checkers.Local.ToObservableCollection();
        (Resources["UsersView"] as CollectionViewSource).Source = context.Users.Local.ToObservableCollection();
        (Resources["NewsView"] as CollectionViewSource).Source = context.News.Local.ToObservableCollection();
        (Resources["MessagesView"] as CollectionViewSource).Source = context.Messages.Local.ToObservableCollection();
        (Resources["OrganizationsView"] as CollectionViewSource).Source = context.Organizations.Local.ToObservableCollection();
        (Resources["CompilatorsView"] as CollectionViewSource).Source = context.Compilators.Local.ToObservableCollection();
        (Resources["RetestView"] as CollectionViewSource).Source = null;
    }

    void RefreshViews()
    {
        (Resources["ContestsView"] as CollectionViewSource).View.Refresh();
        (Resources["CheckersView"] as CollectionViewSource).View.Refresh();
        (Resources["UsersView"] as CollectionViewSource).View.Refresh();
        (Resources["NewsView"] as CollectionViewSource).View.Refresh();
        (Resources["MessagesView"] as CollectionViewSource).View.Refresh();
        (Resources["OrganizationsView"] as CollectionViewSource).View.Refresh();
        (Resources["CompilatorsView"] as CollectionViewSource).View.Refresh();
    }

    private void tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem)
            content.Content = e.NewValue;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            Context.SaveChanges();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
        }

    }

    private void Delete(object sender, RoutedEventArgs e)
    {
        object item = tree.SelectedItem;
        Context.Remove(item);
        RefreshViews();
    }

    private void AddContest(object sender, RoutedEventArgs e)
    {
        var contest = Context.CreateProxy<Contest>(x =>
        {
            x.Name = "Новая Олимпиада";
            x.Abbr = "xxx";
            x.Date = DateTime.Now;
            x.TimeOfStart = new TimeSpan(10, 0, 0);
            x.Duration = new TimeSpan(5, 0, 0);
        });

        Context.Contests.Add(contest);
    }

    private void AddNews(object sender, RoutedEventArgs e)
    {
        var news = Context.CreateProxy<News>(x =>
        {
            x.Title = "Новость";
            x.Text = "Текст";
            x.PublicationDate = DateTimeOffset.Now;
        });

        Context.News.Add(news);
    }

    private void AddProblem(object sender, RoutedEventArgs e)
    {
        if (tree.SelectedItem is Contest catalog)
        {
            var problem = Context.CreateProxy<Problem>(x =>
            {
                x.CheckerId = 1;
                x.Name = "Новая Задача";
            });

            catalog.Problems.Add(problem);
        }
    }

    private void AddTests(object sender, RoutedEventArgs e)
    {
        if (tree.SelectedItem is Problem problem)
        {
            OpenFileDialog dlg = new() { Multiselect = true };

            if (dlg.ShowDialog(this) == true)
            {
                SortedDictionary<int, string> inputNames = new();
                SortedDictionary<int, string> outputNames = new();
                foreach (string file in dlg.FileNames)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith("input") && int.TryParse(name[5..], out int number))
                        inputNames[number] = file;
                    if (name.StartsWith("output") && int.TryParse(name[6..], out number))
                        outputNames[number] = file;
                }

                var tests = from input in inputNames
                            join output in outputNames on input.Key equals output.Key
                            select new { Number = input.Key, Input = input.Value, Output = output.Value };
                foreach (var testFile in tests)
                {
                    var test = Context.CreateProxy<Test>(x =>
                    {
                        x.Number = testFile.Number;
                        x.Input = File.ReadAllBytes(testFile.Input);
                        x.Output = File.ReadAllBytes(testFile.Output);
                        x.IsOpen = testFile.Number == 1;
                        x.IsActive = true;
                    });

                    problem.Tests.Add(test);
                }
            }
        }
    }

    private void ClearTests(object sender, RoutedEventArgs e)
    {
        if (tree.SelectedItem is Problem problem)
        {
            foreach (var test in problem.Tests.ToArray())
                Context.Tests.Remove(test);
            problem.Tests.Clear();
        }
    }

    private void RenumberTests(object sender, RoutedEventArgs e)
    {
        if (tree.SelectedItem is Problem problem)
        {
            int number = 1;
            foreach (var test in problem.Tests.OrderBy(t => t.Number))
                test.Number = number++;
        }
    }

    private void SelectForRetest(object sender, RoutedEventArgs e)
    {
        var query = Context.Submissions.AsQueryable();
        if (ContestBox.SelectedItem != null)
            query = query.Where(x => x.Problem.ContestId == (int)ContestBox.SelectedValue);
        if (ProblemBox.SelectedItem != null)
            query = query.Where(x => x.ProblemId == (int)ProblemBox.SelectedValue);
        if (CompilatorBox.SelectedValue != null)
            query = query.Where(x => x.CompilatorId == (int)CompilatorBox.SelectedValue);
        if (IsRetestOk.IsChecked == false)
            query = query.Where(x => x.StatusCode < 2);

        var list = query.ToList();
        if (!string.IsNullOrWhiteSpace(errorBox.Text))
            list = list.Where(x => x.StatusTemplate.Contains(errorBox.Text)).ToList();

        (Resources["RetestView"] as CollectionViewSource).Source = list;
    }

    private void Retest(object sender, RoutedEventArgs e)
    {
        foreach (var v in retestListView.SelectedItems.Cast<Submission>())
            v.StatusCode = 0;

        (Resources["RetestView"] as CollectionViewSource).View.Refresh();
    }

    private void LoadProblems(object sender, RoutedEventArgs e)
    {
        if (tree.SelectedItem is Contest catalog)
        {
            OpenFileDialog dlg = new() { Filter = "PDF Documents|*.pdf" };

            if (dlg.ShowDialog(this) == true)
            {
                byte[] bytes = File.ReadAllBytes(dlg.FileName);

                if (catalog.ProblemPdf == null)
                    catalog.ProblemPdf = Context.CreateProxy<Domain.Models.Blob>(x => x.Data = bytes);
                else
                    catalog.ProblemPdf.Data = bytes;
            }
        }
    }

    private void LoadTests(object sender, RoutedEventArgs e)
    {
        if (tree.SelectedItem is Contest catalog)
        {
            OpenFileDialog dlg = new() { Filter = "Zip archive|*.zip" };

            if (dlg.ShowDialog(this) == true)
            {
                byte[] bytes = File.ReadAllBytes(dlg.FileName);

                if (catalog.TestZip == null)
                    catalog.TestZip = Context.CreateProxy<Domain.Models.Blob>(x => x.Data = bytes);
                else
                    catalog.TestZip.Data = bytes;
            }
        }
    }

    private void RemoveTestsZip(object sender, RoutedEventArgs e)
    {
        var catalog = tree.SelectedItem as Contest;
        Context.Blobs.Remove(catalog.TestZip);
    }

    private void GenerateLogins(object sender, RoutedEventArgs e)
    {
        var context = CreateContext();
        var olymp = tree.SelectedItem as Contest;
        olymp = context.Contests.Find(olymp.Id);

        var logins = new Dictionary<string, string>();

        OpenFileDialog ofd = new();
        if (ofd.ShowDialog() == true)
        {
            using (var reader = new StreamReader(ofd.FileName))
            {
                while (!reader.EndOfStream)
                {
                    string[] words = reader.ReadLine().Split(' ', ',', ';', '\t');
                    logins.Add(words[0], words[1]);
                }
            }

            foreach (var login in olymp.Competitors.Select(comp => comp.UserName).Where(login => !string.IsNullOrWhiteSpace(login)))
                logins.Remove(login!);

            foreach (var team in olymp.Competitors.Where(team => team.IsApproved && string.IsNullOrWhiteSpace(team.UserName)))
            {
                var login = logins.First();
                team.UserName = login.Key;
                team.PasswordHash = login.Value;
                logins.Remove(team.UserName);
            }

            using (var writer = new StreamWriter(@"logins.csv"))
            {
                foreach (var team in olymp.Competitors.Where(team => team.IsApproved))
                    writer.WriteLine("{0}\t{1}\t{2}", team.Name, team.UserName, team.PasswordHash);
            }

            context.SaveChanges();
        }
    }

    private void ExportCheckToFile(object sender, RoutedEventArgs e)
    {
        var context = CreateContext();
        var olymp = context.Contests.Last();
        var submissions = olymp.Problems.SelectMany(problem => problem.Submissions).OrderBy(submission => submission.Id);

        using var reader = new StreamWriter(@"D:\check_results.txt");
        foreach (var submission in submissions)
            reader.WriteLine("{0};{1};{2};{3};{4};{5};{6}", submission.Id, submission.User.Name, submission.Problem.NameWithNumber, submission.Compilator.Name, submission.CommitTime, submission.StatusCode, submission.StatusText);
    }

    private void SaveTests(object sender, RoutedEventArgs e)
    {
        int id = (tree.SelectedItem as Contest).Id;

        var context = CreateContext();
        var olymp = context.Contests.Find(id);

        string path = @"Test" + olymp.Abbr;
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        foreach (var problem in olymp.Problems.OrderBy(problem => problem.Number))
        {
            string problemPath = Path.Combine(path, problem.NameWithNumber);
            Directory.CreateDirectory(problemPath);
            foreach (var test in problem.Tests.OrderBy(test => test.Number))
            {
                File.WriteAllBytes(Path.Combine(problemPath, $"input{test.Number:00}.txt"), test.Input);
                File.WriteAllBytes(Path.Combine(problemPath, $"output{test.Number:00}.txt"), test.Output);
            }
        }
    }

    private void SaveLog(object sender, RoutedEventArgs e)
    {
        try
        {
            int id = (tree.SelectedItem as Contest).Id;

            var context = CreateContext();
            var olymp = context.Contests.Find(id);

            var submissions = olymp.Competitors.SelectMany(competitior => competitior.Submissions).
                Where(submission => submission.CommitTime > olymp.StartTime && submission.CommitTime < olymp.EndTime).
                OrderBy(submission => submission.CommitTime);

            using var writer = new StreamWriter(@"check_log.txt");
            foreach (var submission in submissions)
                writer.WriteLine("{0};{1};{2};{3};{4};{5}", (submission.CommitTime - olymp.StartTime).ToString("hh\\:mm\\:ss"), submission.User.Name,
                                 submission.Problem.Number, submission.StatusTemplate.Replace(" on test {0}", string.Empty), submission.StatusTestNumber, submission.Compilator.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
        }
    }

    private void RepareTests(object sender, RoutedEventArgs e)
    {
        var problem = tree.SelectedItem as Problem;
        foreach (var test in problem.Tests)
        {
            string input = Encoding.Default.GetString(test.Input);
            var writer = new StringWriter();
            foreach (string line in input.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    writer.WriteLine(line.Trim());
            }

            test.Input = Encoding.Default.GetBytes(writer.ToString());
        }
    }

    private void CreateTestRound(object sender, RoutedEventArgs e)
    {
        var mainContest = tree.SelectedItem as Contest;
        var contest = Context.CreateProxy<Contest>(x =>
        {
            x.Date = DateTime.Now;
            x.TimeOfStart = new TimeSpan(10, 0, 0);
            x.Duration = new TimeSpan(5, 0, 0);
            x.Name = mainContest.Name + " (пробный тур)";
            x.Abbr = mainContest.Abbr + "_test";
        });

        foreach (var competitor in mainContest.Competitors)
        {
            var copyComp = Context.CreateProxy<Competitor>(x =>
            {
                x.CoachId = competitor.CoachId;
                x.MemberInfos = competitor.MemberInfos;
                x.IsApproved = true;
                x.Name = competitor.Name;
                x.Contest = contest;
                x.OrganizationId = competitor.OrganizationId;
                x.RegistrationDate = DateTimeOffset.Now;
                x.IsHidden = competitor.IsHidden;
                x.UserName = competitor.UserName;
                foreach (var c in competitor.Members)
                    x.Members.Add(c);
            });

            contest.Competitors.Add(copyComp);
        }

        Context.Contests.Add(contest);
    }

    private void ScoreTests(object sender, RoutedEventArgs e)
    {
        if (tree.SelectedItem is Problem problem)
        {
            int sum = 100;
            while (sum > 0)
            {
                foreach (var test in problem.Tests.Where(t => !t.IsOpen).OrderByDescending(t => t.Number))
                {
                    if (sum-- == 0)
                        break;
                    if (test.Score == null)
                        test.Score = 1;
                    else
                        test.Score++;
                }
            }
        }
    }
}
