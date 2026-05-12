using System.Diagnostics;

namespace Olymp.Runner;

public interface IRestrictedProcess : IDisposable
{
    Process Process { get; }

    uint ActiveProcesses { get; }
    TimeSpan TotalUserTime { get; }
    nuint PeakJobMemoryUsed { get; }

    void Terminate(int exitCode = 0);
}

public interface IRestrictedProcessFactory
{
    IRestrictedProcess Create(string command, string workdir,
        string? user, Dictionary<string, string> envs,
        uint activeProcessLimit, ProcessPriorityClass priorityClass,
        TimeSpan userTimeLimit, nuint memoryLimit);
}
