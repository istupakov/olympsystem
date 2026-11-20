using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed class StatementPdfService(ILogger<StatementPdfService> logger) : IDisposable
{
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public async Task<byte[]> CompileAsync(Stream archive, CancellationToken token = default)
    {
        await _buildLock.WaitAsync(token);
        DirectoryInfo? directory = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("olymp-pdf-");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await ZipFile.ExtractToDirectoryAsync(archive, directory.FullName, timeout.Token);
            for (int pass = 0; pass < 2; pass++)
                await RunAsync(directory.FullName, timeout.Token);
            return await File.ReadAllBytesAsync(Path.Combine(directory.FullName, "statements.pdf"), timeout.Token);
        }
        catch (Win32Exception exception)
        {
            logger.LogError(exception, "Could not start pdflatex");
            throw new OperationValidationException(SiteResources.StatementPdfUnavailable);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new OperationValidationException(SiteResources.StatementPdfTimeout);
        }
        finally
        {
            try
            {
                directory?.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove PDF build directory {Directory}", directory?.FullName);
            }
            finally
            {
                _buildLock.Release();
            }
        }
    }

    private async Task RunAsync(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var nullHandle = File.OpenNullHandle();
        var startInfo = new ProcessStartInfo("pdflatex")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            StandardInputHandle = nullHandle,
            StandardOutputHandle = nullHandle,
            StandardErrorHandle = nullHandle,
            ArgumentList = { "-no-shell-escape", "-halt-on-error", "-interaction=nonstopmode", "statements.tex" }
        };
        var status = await Process.RunAsync(startInfo, token);
        token.ThrowIfCancellationRequested();
        if (status.ExitCode != 0)
        {
            string log = Path.Combine(directory, "statements.log");
            string details = "";
            if (File.Exists(log))
            {
                await using var stream = File.OpenRead(log);
                stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                details = await reader.ReadToEndAsync(token);
            }
            logger.LogWarning("pdflatex exited with {ExitCode}: {Details}", status.ExitCode, details);
            throw new OperationValidationException(SiteResources.StatementPdfFailed);
        }
    }

    public void Dispose() => _buildLock.Dispose();
}
