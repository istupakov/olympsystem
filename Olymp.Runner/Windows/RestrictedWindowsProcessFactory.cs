using System.Diagnostics;
using System.Runtime.Versioning;

namespace Olymp.Runner.Windows;

[SupportedOSPlatform("windows5.1.2600")]
public class RestrictedWindowsProcessFactory : IRestrictedProcessFactory
{
    private class RestrictedProcess : IRestrictedProcess
    {
        public required Process Process { get; init; }
        public required JobObject Job { get; init; }

        public uint ActiveProcesses => Job.ActiveProcesses;
        public TimeSpan TotalUserTime => Job.TotalUserTime;
        public nuint PeakJobMemoryUsed => Job.PeakJobMemoryUsed;

        public void Terminate(int exitCode = 0) => Job.Terminate(exitCode);

        public void Dispose()
        {
            Process.Dispose();
            Job.Dispose();
        }
    }

    public IRestrictedProcess Create(string command, string workdir, string? user,
        Dictionary<string, string> envs, uint activeProcessLimit, ProcessPriorityClass priorityClass,
        TimeSpan userTimeLimit, nuint memoryLimit)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd",
            Arguments = $"/C pause > nul & {command}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workdir
        };

        if (user is not null)
        {
            startInfo.UserName = user;
        }

        startInfo.Environment.Clear();
        foreach (var (name, value) in envs)
            startInfo.Environment[name] = value;

        var process = new RestrictedProcess
        {
            Process = new Process { StartInfo = startInfo },
            Job = new JobObject("OlympRunnerJob")
        };

        try
        {
            process.Job.SetLimits(activeProcessLimit, priorityClass, userTimeLimit, memoryLimit);
            process.Job.SetNetRateControl(0);
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") is not "true")
                process.Job.SetAllUIRestrictions();

            process.Process.Start();
            process.Job.Assign(process.Process);

            // Start paused process
            process.Process.StandardInput.Write("#");

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
