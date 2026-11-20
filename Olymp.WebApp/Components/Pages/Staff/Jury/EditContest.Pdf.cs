using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class EditContest
{
    private IBrowserFile? _pdfFile;
    private bool _hasPdf, _confirmPdf, _pdfSaved, _pdfRemoved;
    private ContestProblemService.PdfRemoval? _pdfRemoval;
    private string? _pdfError;
    private int _pdfInputVersion;

    private void ResetPdf()
    {
        _pdfFile = null;
        _hasPdf = _confirmPdf = _pdfSaved = _pdfRemoved = false;
        _pdfRemoval = null;
        _pdfError = null;
        _pdfInputVersion++;
    }

    private void SelectPdf(InputFileChangeEventArgs args)
    {
        if (_saving) return;
        _pdfFile = args.FileCount == 1 ? args.File : null;
        _confirmPdf = _pdfSaved = _pdfRemoved = false;
        _pdfRemoval = null;
        _pdfError = null;
    }

    private async Task UploadPdfAsync(bool replace)
    {
        if (_saving || Id is not int id || _pdfFile is not { } file) return;
        _saving = true;
        _pdfError = null;
        _pdfSaved = false;
        _pdfRemoved = false;
        _pdfRemoval = null;
        try
        {
            long limit = ImportOptions.Value.MaxPdfBytes;
            if (file.Size <= 0 || file.Size > limit)
                throw new InvalidDataException();
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            _hasPdf = await db.Contests.AnyAsync(item => item.Id == id && item.ProblemPdfId != null);
            if (_hasPdf && !replace)
            {
                _confirmPdf = true;
                return;
            }
            using var input = file.OpenReadStream(limit);
            using var data = new MemoryStream();
            await input.CopyToAsync(data);
            await scope.ServiceProvider.GetRequiredService<ContestProblemService>()
                .UploadPdfAsync((await AuthenticationState).User, id, data.ToArray(), replace);
            _hasPdf = _pdfSaved = true;
            _confirmPdf = false;
            _pdfFile = null;
            _pdfInputVersion++;
        }
        catch (OperationValidationException exception) { _pdfError = exception.Message; }
        catch (EntityNotFoundException) { _pdfError = SiteResources.PageNotFoundMessage; }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _pdfError = string.Format(SiteResources.StaffPdfInvalid, ImportOptions.Value.MaxPdfBytes / 1024 / 1024);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or DbUpdateException)
        {
            Logger.LogError(exception, "Failed to upload contest PDF for {ContestId}", id);
            _pdfError = SiteResources.StaffSaveFailed;
        }
        finally { _saving = false; }
    }

    private async Task RemovePdfAsync(bool confirm)
    {
        if (_saving || Id is not int id) return;
        _saving = true;
        _pdfError = null;
        _confirmPdf = _pdfSaved = _pdfRemoved = false;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
            var actor = (await AuthenticationState).User;
            if (!confirm)
            {
                _pdfRemoval = await service.PrepareRemovePdfAsync(actor, id);
                _hasPdf = _pdfRemoval is not null;
                return;
            }
            if (_pdfRemoval is not { } preview || preview.ContestId != id) return;
            await service.RemovePdfAsync(actor, preview);
            ResetPdf();
            _pdfRemoved = true;
        }
        catch (OperationValidationException exception) { _pdfRemoval = null; _pdfError = exception.Message; }
        catch (EntityNotFoundException) { _pdfRemoval = null; _pdfError = SiteResources.PageNotFoundMessage; }
        catch (Exception exception) when (exception is System.Data.Common.DbException or DbUpdateException)
        {
            _pdfRemoval = null;
            Logger.LogError(exception, "Failed to remove contest PDF for {ContestId}", id);
            _pdfError = SiteResources.StaffSaveFailed;
        }
        finally { _saving = false; }
    }
}
