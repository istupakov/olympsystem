using System.Diagnostics;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Olymp.RunnerProtocol;

namespace Olymp.Runner;

public sealed class CommandExecutor(IRestrictedProcessFactory processFactory, ILogger<CommandExecutor> logger)
{
    public async Task LogWhoami(string? user, string workDirectory, Dictionary<string, string> environment, CancellationToken token)
    {
        using var process = processFactory.Create("whoami", workDirectory,
            user, environment,
            2, ProcessPriorityClass.Normal, TimeSpan.FromSeconds(1), 128_000_000);

        await process.Process.WaitForExitAsync(token);

        logger.LogInformation("""
            whoami as: {username}
            exitcode: {exitcode}
            stdout: {stdout}
            stderr: {stderr}
            """,
            user, process.Process.ExitCode,
            await process.Process.StandardOutput.ReadToEndAsync(token),
            await process.Process.StandardError.ReadToEndAsync(token));
    }

    public async Task<CommandResponse> Run(CommandRequest request, string workDirectory,
        string? user, Dictionary<string, string> environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int stdoutLimit = checked((int)request.ResourceLimits.StdoutBytes);
        int stderrLimit = checked((int)request.ResourceLimits.StderrBytes);
        var userTimeLimit = request.ResourceLimits.UserTime.ToTimeSpan();
        var totalTimeLimit = request.ResourceLimits.TotalTime.ToTimeSpan();
        nuint memoryLimit = new(request.ResourceLimits.MemoryBytes);
        using var process = processFactory.Create(request.Command, workDirectory,
            user, environment, 5, ProcessPriorityClass.Normal, userTimeLimit, memoryLimit, request.ReadOnly);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var elapsed = Stopwatch.StartNew();
        int stopReason = -1;

        void Stop(CommandStatus reason)
        {
            if (Interlocked.CompareExchange(ref stopReason, (int)reason, -1) == -1)
                process.Terminate(0xffff);
        }

        async Task CancelOnFailure(Task operation)
        {
            try { await operation; }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        }

        async Task WriteInput()
        {
            try
            {
                await process.Process.StandardInput.BaseStream.WriteAsync(request.Stdin.Memory, cancellation.Token);
            }
            catch (IOException) { }
            finally
            {
                try { process.Process.StandardInput.Close(); }
                catch (IOException) { }
            }
        }

        async Task<Memory<byte>> ReadOutput(Stream stream, int limit, CommandStatus exceeded)
        {
            var buffer = new byte[checked(limit + 1)];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), cancellation.Token);
                if (read == 0) break;
                count += read;
            }

            if (count > limit) Stop(exceeded);
            return buffer.AsMemory(0, Math.Min(count, limit));
        }

        async Task WaitForExit()
        {
            var exited = process.Process.WaitForExitAsync(CancellationToken.None);
            try { await exited.WaitAsync(cancellation.Token); }
            finally
            {
                elapsed.Stop();
                // Stop descendants that may still hold the redirected streams.
                process.Terminate(0xffff);
                await exited;
            }
        }

        var exit = CancelOnFailure(WaitForExit());
        async Task MonitorLimits()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (!exit.IsCompleted)
            {
                if (process.PeakJobMemoryUsed > memoryLimit) Stop(CommandStatus.MemoryLimit);
                else if (process.TotalUserTime > userTimeLimit) Stop(CommandStatus.UserTimeLimit);
                else if (elapsed.Elapsed > totalTimeLimit) Stop(CommandStatus.TotalTimeLimit);
                if (!await timer.WaitForNextTickAsync(cancellation.Token)) break;
            }
        }

        var stdout = ReadOutput(process.Process.StandardOutput.BaseStream, stdoutLimit, CommandStatus.StdoutLimit);
        var stderr = ReadOutput(process.Process.StandardError.BaseStream, stderrLimit, CommandStatus.StderrLimit);
        await Task.WhenAll(exit, CancelOnFailure(WriteInput()), CancelOnFailure(stdout), CancelOnFailure(stderr), CancelOnFailure(MonitorLimits()));
        token.ThrowIfCancellationRequested();

        var userTime = process.TotalUserTime;
        var peakMemory = process.PeakJobMemoryUsed;
        var status = stopReason != -1 ? (CommandStatus)stopReason
            : peakMemory > memoryLimit ? CommandStatus.MemoryLimit
            : userTime > userTimeLimit ? CommandStatus.UserTimeLimit
            : elapsed.Elapsed > totalTimeLimit ? CommandStatus.TotalTimeLimit
            : process.Process.ExitCode != 0 ? CommandStatus.Error : CommandStatus.Completed;
        return new CommandResponse
        {
            Status = status,
            Stdout = ByteString.CopyFrom(stdout.Result.Span),
            Stderr = ByteString.CopyFrom(stderr.Result.Span),
            ExitCode = process.Process.ExitCode,
            ResourceConsumption = new()
            {
                UserTime = Duration.FromTimeSpan(userTime),
                TotalTime = Duration.FromTimeSpan(elapsed.Elapsed),
                MemoryBytes = peakMemory,
                StdoutBytes = (ulong)stdout.Result.Length,
                StderrBytes = (ulong)stderr.Result.Length
            }
        };
    }
}
