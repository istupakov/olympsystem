using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Olymp.Runner.Linux;

[SupportedOSPlatform("linux")]
public sealed partial class DevelopmentLinuxProcessFactory : IRestrictedProcessFactory
{
    private sealed partial class DevelopmentProcess(Process process, bool readOnly) : IRestrictedProcess
    {
        private readonly Lock _terminationLock = new();
        private bool _terminated;
        private nuint _peakMemory;

        public Process Process => process;
        public uint ActiveProcesses => process.HasExited ? 0u : 1u;

        public TimeSpan TotalUserTime
        {
            get
            {
                lock (_terminationLock)
                {
                    UpdateConsumption();
                    return field;
                }
            }
            private set;
        }

        public nuint PeakJobMemoryUsed
        {
            get
            {
                lock (_terminationLock)
                {
                    UpdateConsumption();
                    return _peakMemory;
                }
            }
        }

        private void UpdateConsumption()
        {
            if (!readOnly || _terminated) return;
            process.Refresh();
            if (process.HasExited) return;
            try
            {
                TotalUserTime = process.UserProcessorTime;
                _peakMemory = nuint.Max(_peakMemory, (nuint)process.PeakWorkingSet64);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { } // Process exited during sampling.
        }

        public void Terminate(int exitCode = 0)
        {
            lock (_terminationLock)
            {
                if (_terminated) return;
                UpdateConsumption();
                // SIGKILL the group, including children remaining after the shell exits.
                if (KillProcessGroup(process.Id, 9) != 0 && Marshal.GetLastPInvokeError() is not 3) // ESRCH
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                _terminated = true;
            }
        }

        public void Dispose()
        {
            try
            {
                Terminate();
                process.WaitForExit();
            }
            finally { process.Dispose(); }
        }

        [LibraryImport("libc", EntryPoint = "killpg", SetLastError = true)]
        private static partial int KillProcessGroup(int groupId, int signal);
    }

    public IRestrictedProcess Create(string command, string workdir, string? user,
        Dictionary<string, string> envs, uint activeProcessLimit, ProcessPriorityClass priorityClass,
        TimeSpan userTimeLimit, nuint memoryLimit, bool readOnly = false)
    {
        if (!string.IsNullOrEmpty(user))
            throw new NotSupportedException("The Linux development runner does not support switching users.");

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = workdir,
            StartDetached = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (readOnly)
        {
            // Use .NET argument quoting for direct execution, without shell expansion.
            var parts = ProgramCommand().Match(command);
            if (!parts.Success) throw new ArgumentException("Invalid program command.", nameof(command));
            string fileName = parts.Groups[1].Success ? parts.Groups[1].Value : parts.Groups[2].Value;
            startInfo.FileName = fileName.Contains('/')
                ? Path.GetFullPath(fileName, workdir)
                : envs.GetValueOrDefault("PATH", "/usr/bin:/bin").Split(':')
                    .Select(path => Path.GetFullPath(Path.Combine(path, fileName), workdir))
                    .FirstOrDefault(File.Exists) ?? throw new Win32Exception(2, $"Program not found: {fileName}");
            startInfo.Arguments = parts.Groups[3].Value;
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }
        startInfo.Environment.Clear();
        foreach (var (name, value) in envs) startInfo.Environment[name] = value;

        return new DevelopmentProcess(Process.Start(startInfo)!, readOnly);
    }

    [GeneratedRegex("^\\s*(?:\"([^\"]+)\"|(\\S+))(?:\\s+(.*))?$", RegexOptions.Singleline)]
    private static partial Regex ProgramCommand();
}
