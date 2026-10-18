using Frontend.Shared.Services;
using Frontend.Shared.Api;
using Frontend.Shared.Models;
using Frontend.Shared.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Frontend.Student.Pages;

public partial class Dashboard
{
    [Inject] private MeApi Me { get; set; } = null!;
    [Inject] private CurrentSubject CurrentSubject { get; set; } = null!;
    [Inject] private ResultsApi Results { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IStringLocalizer<AppStrings> Loc { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;
    [Inject] private Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider Authentication { get; set; } = null!;

    private List<SubjectDto> _subjects = [];
    private SubjectDto? _selectedSubject;
    private SubjectResultsDto? _results;
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _subjects = await Me.GetMySubjectsAsync();
            var id = await CurrentSubject.PickAsync(_subjects.Select(s => s.Id));
            if ((_subjects.FirstOrDefault(s => s.Id == id) ?? _subjects.FirstOrDefault()) is { } subject)
                await OnSubjectSelectedAsync(subject);
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSubjectSelectedAsync(SubjectDto? subject)
    {
        if (subject is null)
            return;

        _selectedSubject = subject;
        await CurrentSubject.SetAsync(subject.Id);
        _loading = true;
        _results = null;

        try
        {
            await LoadAsync(subject.Id);
        }
        catch (ApiException ex)
        {
            Snackbar.Add(string.Format(Loc["Common_LoadResultsError"], ex.Message), Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private Guid? _myId;
    private StudentSubjectResultsDto? _mine;
    private List<QueueEventDto> _queues = [];
    private CancellationTokenSource? _refresh;

    /// <summary>Таблица группы, свои результаты и свои очереди — параллельно.</summary>
    private async Task LoadAsync(int subjectId)
    {
        if (_myId is null)
        {
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            if (Guid.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)) _myId = id;
        }
        var tableTask = Results.GetSubjectResultsAsync(subjectId);
        var mineTask = Me.GetMyResultsAsync(subjectId);
        var queuesTask = Me.GetMyQueueEventsAsync();
        _results = await tableTask;
        try { _mine = await mineTask; } catch (ApiException) { _mine = null; }
        try { _queues = await queuesTask; } catch (ApiException) { _queues = []; }
    }

    /// <summary>
    /// Живое обновление: таблица перечитывается тихо (пачка событий — одним запросом),
    /// а про свою задачу студент получает уведомление со ссылкой на комментарий.
    /// </summary>
    private async Task OnResultUpdatedAsync(ResultUpdateDto update)
    {
        if (_selectedSubject is null) return;
        if (update.StudentId == _myId)
        {
            var name = _mine?.TaskResults.FirstOrDefault(t => t.TaskId == update.TaskId)?.TaskName ?? "";
            var accepted = update.NewStatus == nameof(SubmissionStatus.Accepted);
            if (update.NewStatus is nameof(SubmissionStatus.Accepted) or nameof(SubmissionStatus.Rejected))
                Snackbar.Add(Loc[accepted ? "Results_ToastAccepted" : "Results_ToastRework", name], accepted ? Severity.Success : Severity.Warning, config =>
                {
                    config.Action = Loc["Results_ToastOpen"];
                    config.OnClick = _ => { Nav.NavigateTo($"/student/logs?task={update.TaskId}"); return Task.CompletedTask; };
                });
        }
        _refresh?.Cancel();
        var refresh = _refresh = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, refresh.Token);
            await LoadAsync(_selectedSubject.Id);
            StateHasChanged();
        }
        catch (OperationCanceledException) { }
        catch (ApiException) { /* Следующее событие или ручное обновление подтянут данные. */ }
    }
}
