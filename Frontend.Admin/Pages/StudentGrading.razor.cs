using Frontend.Admin.Dialogs;
using Frontend.Shared.Api;
using Frontend.Shared.Models;
using Frontend.Shared.Resources;
using Frontend.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Frontend.Admin.Pages;

public partial class StudentGrading
{
    [Inject] private MeApi Me { get; set; } = null!;
    [Inject] private SubjectsApi Subjects { get; set; } = null!;
    [Inject] private ResultsApi Results { get; set; } = null!;
    [Inject] private CurrentSubject CurrentSubject { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IStringLocalizer<AppStrings> Loc { get; set; } = null!;

    private List<SubjectDto> _subjects = [];
    private Dictionary<int, string> _groupNames = [];
    // Список студентов предмета грузится один раз; поиск и фильтр по группе работают на клиенте.
    private List<StudentDetailsDto> _students = [];
    private Dictionary<Guid, (int Accepted, int Rework)> _progress = [];
    private int _taskCount;
    private HashSet<int> _restrictedGroups = [];
    private int? _subjectId;
    private int? _groupFilter;
    private string _query = "";
    private bool _loading;

    private List<StudentDetailsDto> Visible
    {
        get
        {
            IEnumerable<StudentDetailsDto> items = _students;
            if (_groupFilter is int group) items = items.Where(s => s.GroupId == group);
            var query = _query.Trim();
            if (query.Length > 0)
                items = items.Where(s => $"{s.LastName} {s.FirstName}".Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                         (s.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            return items.ToList();
        }
    }

    private List<(int Id, string Name)> StudentGroups => _students.Where(s => s.GroupId.HasValue).Select(s => s.GroupId!.Value).Distinct()
        .Select(id => (id, _groupNames.GetValueOrDefault(id, "—"))).OrderBy(g => g.Item2).ToList();

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var subjectsTask = Me.GetVisibleSubjectsAsync();
            var groupsTask = Me.GetVisibleGroupsAsync();
            _subjects = await subjectsTask;
            _groupNames = (await groupsTask).ToDictionary(g => g.Id, g => g.Name);
            _subjectId = await CurrentSubject.PickAsync(_subjects.Select(s => s.Id));
            if (_subjectId is not null) await LoadStudentsAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    private async Task OnSubjectChangedAsync()
    {
        _groupFilter = null;
        await CurrentSubject.SetAsync(_subjectId);
        await LoadStudentsAsync();
    }

    /// <summary>Студенты предмета и их прогресс (из таблицы результатов) — параллельно, одним заходом.</summary>
    private async Task LoadStudentsAsync(bool showSpinner = true)
    {
        if (_subjectId is not int subjectId)
        {
            _students = [];
            return;
        }
        _loading = showSpinner;
        try
        {
            var accessTask = Me.GetMyAccessAsync();
            var studentsTask = Subjects.GetStudentsAsync(subjectId);
            var resultsTask = Results.GetSubjectResultsAsync(subjectId);
            _restrictedGroups = [.. (await accessTask).RestrictedGroupIds];
            _students = (await studentsTask).OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ToList();
            var results = await resultsTask;
            _taskCount = results.TaskHeaders.Count;
            _progress = results.UserResults.ToDictionary(r => r.StudentId, r => (
                r.Results.Values.Count(c => c.Status == SubmissionStatus.Accepted),
                r.Results.Values.Count(c => c.Status == SubmissionStatus.Rejected)));
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

    private async Task OpenAsync(StudentDetailsDto student)
    {
        if (IsGroupRestricted(student))
            return;

        if (_subjectId is null)
        {
            Snackbar.Add(Loc["Grading_SelectSubjectFirst"], Severity.Warning);
            return;
        }

        // Тот же интерфейс, что и из очереди, но без queue-статусов (EventId = null).
        var parameters = new DialogParameters<StudentInteractionDialog>
        {
            { x => x.StudentId, student.Id },
            { x => x.StudentName, $"{student.LastName} {student.FirstName}" },
            { x => x.GroupName, student.GroupId is int g ? _groupNames.GetValueOrDefault(g) : null },
            { x => x.EventId, (int?)null },
            { x => x.SubjectId, _subjectId.Value },
            { x => x.StudentColor, student.Color },
            { x => x.OnColorApplied, EventCallback.Factory.Create<StudentColorUpdateDto>(this, UpdateStudentColor) },
        };

        var dialog = await DialogService.ShowAsync<StudentInteractionDialog>(
            Loc["Grading_DialogTitle"],
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        // Обновляем прогресс тихо: список, поиск и прокрутка остаются на месте.
        if (await dialog.Result is { Canceled: false })
            await LoadStudentsAsync(showSpinner: false);
    }

    private bool IsGroupRestricted(StudentDetailsDto student) =>
        student.GroupId is int groupId && _restrictedGroups.Contains(groupId);

    private string ProgressText(StudentDetailsDto student)
    {
        var (accepted, rework) = _progress.GetValueOrDefault(student.Id);
        return rework > 0
            ? Loc["Grading_ProgressWithRework", accepted, _taskCount, rework]
            : Loc["Grading_Progress", accepted, _taskCount];
    }

    private void UpdateStudentColor(StudentColorUpdateDto update) =>
        _students = _students.Select(student => student.Id == update.StudentId
            ? student with { Color = update.Color }
            : student).ToList();

    private async Task EditAsync(StudentDetailsDto student)
    {
        var parameters = new DialogParameters<EditStudentDialog>
        {
            { x => x.StudentId, student.Id },
            { x => x.CurrentDetails, student }
        };

        var dialog = await DialogService.ShowAsync<EditStudentDialog>(
            Loc["StudentGrading_EditProfileTitle"],
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });

        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadStudentsAsync(showSpinner: false);
            Snackbar.Add(Loc["StudentGrading_ProfileUpdated"], Severity.Success);
        }
    }
}
