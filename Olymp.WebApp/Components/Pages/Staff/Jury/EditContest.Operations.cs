using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class EditContest
{
    private ContestOperation? _pendingOperation;
    private bool _hasTestZip, _canFinish, _operationCompleted;
    private DateTimeOffset _endTime;

    private bool CanPublishTests { get => field && _endTime < TimeProvider.GetUtcNow(); set; }
    private string OperationLabel(ContestOperation operation) => operation switch
    {
        ContestOperation.PublishTests => _hasTestZip ? SiteResources.StaffRebuildTests : SiteResources.StaffPublishTests,
        ContestOperation.RemoveTests => SiteResources.StaffRemoveTests,
        _ => SiteResources.StaffFinishContest
    };
    private static string OperationConfirmation(ContestOperation operation) => operation switch
    {
        ContestOperation.PublishTests => SiteResources.StaffPublishTestsConfirm,
        ContestOperation.RemoveTests => SiteResources.StaffRemoveTestsConfirm,
        _ => SiteResources.StaffFinishContestConfirm
    };

    private async Task LoadOperationsAsync(OlympContext context, int id)
    {
        var state = await context.Contests.Where(item => item.Id == id).Select(item => new
        {
            item.EndTime,
            HasZip = item.TestZipId != null,
            ActiveTests = item.Problems.Any(problem => problem.IsActive && problem.Tests.Any(test => test.IsActive)),
            NeedsFinish = item.FreezeTime != null || !item.IsOpen || item.Competitors.Any(author => author.UserName != null || author.PasswordHash != null)
        }).SingleAsync();
        _endTime = state.EndTime;
        _hasTestZip = state.HasZip;
        CanPublishTests = state.ActiveTests;
        _canFinish = _endTime < TimeProvider.GetUtcNow() && state.NeedsFinish;
    }

    private async Task RunOperationAsync()
    {
        if (_saving || Id is not int id || _pendingOperation is not { } operation) return;
        _saving = true;
        _error = null;
        _operationCompleted = false;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var actor = (await AuthenticationState).User;
            if (operation == ContestOperation.Finish)
            {
                await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>().FinishContestAsync(actor, id);
                _input.Freeze = false;
                _input.IsOpen = true;
            }
            else
            {
                var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
                if (operation == ContestOperation.PublishTests) await service.PublishTestZipAsync(actor, id);
                else await service.RemoveTestZipAsync(actor, id);
            }
            await LoadOperationsAsync(scope.ServiceProvider.GetRequiredService<OlympContext>(), id);
            _pendingOperation = null;
            _operationCompleted = true;
        }
        catch (OperationValidationException exception) { _error = exception.Message; }
        catch (EntityNotFoundException) { _error = SiteResources.PageNotFoundMessage; }
        catch (IdentityOperationException exception) { _error = string.Join(" ", exception.Errors.Select(item => item.Description)); }
        catch (InvalidDataException) { _error = SiteResources.StaffArchiveFailed; }
        catch (Exception exception) when (exception is System.Data.Common.DbException or DbUpdateException)
        {
            Logger.LogError(exception, "Contest operation {Operation} failed for {ContestId}", operation, id);
            _error = SiteResources.StaffSaveFailed;
        }
        finally { _saving = false; }
    }

    private enum ContestOperation { PublishTests, RemoveTests, Finish }
}
