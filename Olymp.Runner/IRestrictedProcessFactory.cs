using System.Diagnostics;

namespace Olymp.Runner;

public interface IRestrictedProcess : IDisposable
{
    Process Process { get; }

    public uint ActiveProcesses { get; }
    public TimeSpan TotalUserTime { get; }
    public nuint PeakJobMemoryUsed { get; }

    void Terminate(int exitCode = 0);
}

public interface IRestrictedProcessFactory
{
    IRestrictedProcess Create(string command, string workdir,
        string? user, Dictionary<string, string> envs,
        uint activeProcessLimit, ProcessPriorityClass priorityClass,
        TimeSpan userTimeLimit, nuint memoryLimit);
}
