using System.Globalization;
using Frontend.Admin.Dialogs;
using Frontend.Shared.Api;
using Frontend.Shared.Auth;
using Frontend.Shared.Models;
using Frontend.Shared.Resources;
using Frontend.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Frontend.Admin.Pages;

public partial class Management
{
    [Inject] private SubjectsApi Subjects { get; set; } = null!;
    [Inject] private GroupsApi Groups { get; set; } = null!;
    [Inject] private GradesApi Grades { get; set; } = null!;
    [Inject] private QueuesApi QueuesApi { get; set; } = null!;
    [Inject] private MeApi Me { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private AttendanceApi Attendance { get; set; } = null!;
    [Inject] private IStringLocalizer<AppStrings> Loc { get; set; } = null!;
    [Inject] private UnsavedChangesTracker Dirty { get; set; } = null!;
    [Inject] private ConfirmService Confirm { get; set; } = null!;
    [Inject] private CurrentSubject CurrentSubject { get; set; } = null!;

    // ----- Мутабельные модели строк для inline-редактирования таблиц -----
    private sealed class SubjectRow { public int Id; public string Name = ""; public bool AttendanceEnabled; }
    private sealed class GroupRow { public int Id; public string Name = ""; }
    private sealed class TaskRow { public int Id; public string Name = ""; public string Description = ""; public int MaxPoints; }
    private sealed class ComponentRow { public int Id; public string Name = ""; public int MinPoints; public int MaxPoints; }

    private sealed class QueueRow
    {
        public int Id;
        public string Name = "";
        public DateTime EventDateTime;
        public bool AllowSelfJoin;

        /// <summary>Введённая дата не разобралась — сохранять строку нельзя.</summary>
        public bool InvalidDate;

        /// <summary>Редактируемое локальное представление даты/времени.</summary>
        public string LocalDateTimeText
        {
            get => EventDateTime.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            set
            {
                InvalidDate = !DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed);
                if (!InvalidDate) EventDateTime = parsed.ToUniversalTime();
            }
        }
    }

    private List<SubjectRow> _subjects = [];
    private List<GroupRow> _groups = [];
    private List<TaskRow> _tasks = [];
    private List<ComponentRow> _components = [];
    private List<QueueRow> _queues = [];
    private bool _loading = true;
    private bool _isSuperAdmin;
    private string _section = "queues";
    private static readonly (string Id, string Label, string Icon)[] ManagementSections =
    [
        ("queues", "Management_TabQueues", Icons.Material.Filled.Queue),
        ("subjects", "Management_TabSubjects", Icons.Material.Filled.Layers),
        ("groups", "Management_TabGroups", Icons.Material.Filled.Groups),
        ("tasks", "Management_TabTasks", Icons.Material.Filled.Code),
        ("grades", "Management_TabGradeComponents", Icons.Material.Filled.Grading),
        ("visibility", "Management_VisibilityTab", Icons.Material.Filled.Visibility)
    ];

    // Видимость/архив: скрытые самим админом и запрещённые супер-админом.
    private HashSet<int> _hiddenSubjects = [];
    private HashSet<int> _hiddenGroups = [];
    private HashSet<int> _restrictedSubjects = [];
    private HashSet<int> _restrictedGroups = [];

    // Поля форм добавления.
    private string _newSubjectName = "";
    private IReadOnlyCollection<int> _newSubjectGroupIds = new HashSet<int>();
    private string _newGroupName = "";
    private IReadOnlyCollection<int> _newGroupSubjectIds = new HashSet<int>();
    private string _newTaskName = "";
    private string _newTaskDescription = "";
    private int _newTaskMaxPoints = 1;
    private string _newComponentName = "";
    private int _newComponentMin;
    private int _newComponentMax = 5;

    private int? _taskSubjectId;
    private int? _componentSubjectId;

    // Очередь — форма создания.
    private int? _queueSubjectId;
    private string _queueName = "";
    private DateTime? _queueDate = DateTime.Today;
    private TimeSpan? _queueTime = new(10, 0, 0);
    private IReadOnlyCollection<int> _autoFillGroupIds = new HashSet<int>();
    private bool _allowSelfJoin = true;

    protected override async Task OnInitializedAsync()
    {
        _isSuperAdmin = (await Authentication.GetAuthenticationStateAsync()).User.IsInRole(Roles.SuperAdmin);
        await ReloadAsync();
        if (await CurrentSubject.PickAsync(_subjects.Select(s => s.Id)) is int current)
        {
            OnQueueSubjectChanged(current);
            await OnTaskSubjectChanged(current);
            await OnComponentSubjectChanged(current);
        }
        _loading = false;
    }

    private async Task ReloadAsync()
    {
        try
        {
            _subjects = (await Subjects.GetAllAsync()).Select(s => new SubjectRow { Id = s.Id, Name = s.Name, AttendanceEnabled = s.AttendanceEnabled }).ToList();
            _groups = (await Groups.GetAllAsync()).Select(g => new GroupRow { Id = g.Id, Name = g.Name }).ToList();

            var access = await Me.GetMyAccessAsync();
            _hiddenSubjects = [.. access.HiddenSubjectIds];
            _hiddenGroups = [.. access.HiddenGroupIds];
            _restrictedSubjects = [.. access.RestrictedSubjectIds];
            _restrictedGroups = [.. access.RestrictedGroupIds];

            await ReloadQueuesAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    private async Task ReloadQueuesAsync()
    {
        try
        {
            _queues = (await QueuesApi.GetAllAsync())
                .Select(q => new QueueRow { Id = q.Id, Name = q.Name, EventDateTime = q.EventDateTime, AllowSelfJoin = q.AllowSelfJoin })
                .ToList();
        }
        catch (ApiException ex)
        {
            // Вызывается и как after() успешных операций — сбой перечитки не должен
            // выглядеть ошибкой самой операции.
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    // ----- Очереди -----
    private void OnQueueSubjectChanged(int? subjectId)
    {
        _queueSubjectId = subjectId;
        _ = CurrentSubject.SetAsync(subjectId);
        var name = _subjects.FirstOrDefault(s => s.Id == subjectId)?.Name;
        if (name is not null)
            _queueName = $"{name} {(_queueDate ?? DateTime.Today).ToString("d", CultureInfo.CurrentCulture)}";
    }

    private async Task CreateQueueAsync()
    {
        if (_queueSubjectId is null) { Snackbar.Add(Loc["Management_SelectSubjectWarning"], Severity.Warning); return; }
        if (_queueDate is null || _queueTime is null) { Snackbar.Add(Loc["Management_DateTimeRequired"], Severity.Warning); return; }

        var when = DateTime.SpecifyKind(_queueDate.Value.Date + _queueTime.Value, DateTimeKind.Local).ToUniversalTime();
        var name = string.IsNullOrWhiteSpace(_queueName)
            ? $"{_subjects.FirstOrDefault(s => s.Id == _queueSubjectId)?.Name} {_queueDate.Value.ToString("d", CultureInfo.CurrentCulture)}"
            : _queueName.Trim();

        var autoFill = _autoFillGroupIds.Count > 0 ? _autoFillGroupIds.ToList() : null;

        try
        {
            await QueuesApi.CreateAsync(new CreateQueueEventRequest(name, when, _queueSubjectId.Value, _allowSelfJoin, autoFill));
            Snackbar.Add(Loc["Management_QueueCreated"], Severity.Success);
            await ReloadQueuesAsync();
            Dirty.MarkClean();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    // RowEditCommit в MudTable — синхронный Action<object>; запускаем сохранение фоном.
    private void OnQueueCommit(object element)
    {
        if (element is QueueRow { InvalidDate: true } invalid)
        {
            invalid.InvalidDate = false;
            Snackbar.Add(Loc["Management_InvalidDate", CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern + " " +
                CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern], Severity.Warning);
            _ = ReloadQueuesAsync();
            return;
        }
        _ = RunAsync(
        () => QueuesApi.UpdateAsync(((QueueRow)element).Id, new UpdateQueueEventRequest(((QueueRow)element).Name, ((QueueRow)element).EventDateTime)),
        Loc["Management_QueueUpdated"], () => Task.CompletedTask, ReloadQueuesAsync);
    }

    private async Task DeleteQueueAsync(QueueRow row)
    {
        if (await Confirm.ConfirmDeleteAsync(row.Name)) await RunAsync(() => QueuesApi.DeleteAsync(row.Id), Loc["Management_QueueDeleted"], ReloadQueuesAsync);
    }

    // ----- Предметы -----
    private async Task SetAttendanceEnabledAsync(SubjectRow row, bool enabled)
    {
        try
        {
            await Attendance.SetEnabledAsync(row.Id, enabled);
            row.AttendanceEnabled = enabled;
            Snackbar.Add(enabled ? "Учёт посещаемости включён." : "Учёт выключен, история сохранена.", Severity.Success);
        }
        catch (ApiException ex) { Snackbar.Add(ex.Message, Severity.Error); }
    }

    private async Task CreateSubjectAsync()
    {
        if (string.IsNullOrWhiteSpace(_newSubjectName)) return;

        await RunAsync(
            async () =>
            {
                var subject = await Subjects.CreateAsync(new CreateSubjectRequest(_newSubjectName.Trim()));
                if (_isSuperAdmin)
                    foreach (var groupId in _newSubjectGroupIds)
                        await Groups.LinkSubjectAsync(groupId, subject.Id);
            },
            Loc[_isSuperAdmin ? "Management_SubjectCreated" : "Management_SubjectCreatedPendingAccess"],
            async () => { _newSubjectName = ""; _newSubjectGroupIds = new HashSet<int>(); await ReloadAsync(); });
    }

    private void OnSubjectCommit(object element) => _ = RunAsync(
        () => Subjects.UpdateAsync(((SubjectRow)element).Id, new UpdateSubjectRequest(((SubjectRow)element).Name)),
        Loc["Common_Saved"], () => Task.CompletedTask, ReloadAsync);

    private async Task DeleteSubjectAsync(SubjectRow row)
    {
        if (await Confirm.ConfirmDeleteAsync(row.Name)) await RunAsync(() => Subjects.DeleteAsync(row.Id), Loc["Management_SubjectDeleted"], ReloadAsync);
    }

    // ----- Группы -----
    private async Task CreateGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(_newGroupName)) return;

        await RunAsync(
            async () =>
            {
                var group = await Groups.CreateAsync(new CreateGroupRequest(_newGroupName.Trim()));
                foreach (var subjectId in _newGroupSubjectIds)
                    await Groups.LinkSubjectAsync(group.Id, subjectId);
            },
            Loc["Management_GroupCreated"],
            async () => { _newGroupName = ""; _newGroupSubjectIds = new HashSet<int>(); await ReloadAsync(); });
    }

    private void OnGroupCommit(object element) => _ = RunAsync(
        () => Groups.UpdateAsync(((GroupRow)element).Id, new UpdateGroupRequest(((GroupRow)element).Name)),
        Loc["Common_Saved"], () => Task.CompletedTask, ReloadAsync);

    private async Task DeleteGroupAsync(GroupRow row)
    {
        if (await Confirm.ConfirmDeleteAsync(row.Name)) await RunAsync(() => Groups.DeleteAsync(row.Id), Loc["Management_GroupDeleted"], ReloadAsync);
    }

    // ----- Задачи -----
    private async Task OnTaskSubjectChanged(int? subjectId)
    {
        _taskSubjectId = subjectId;
        await CurrentSubject.SetAsync(subjectId);
        if (subjectId is null)
        {
            _tasks = [];
            return;
        }

        try
        {
            _tasks = (await Subjects.GetTasksAsync(subjectId.Value))
                .Select(t => new TaskRow { Id = t.Id, Name = t.Name, Description = t.Description, MaxPoints = t.MaxPoints })
                .ToList();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    private MudTextField<string>? _taskNameField;
    private string? _bulkTasks;
    private int? _copyFromSubjectId;
    private bool _bulkBusy;

    private async Task CreateTaskAsync()
    {
        if (_taskSubjectId is null || string.IsNullOrWhiteSpace(_newTaskName)) return;
        await RunAsync(
            () => Subjects.CreateTaskAsync(_taskSubjectId.Value, new CreateTaskRequest(_newTaskName.Trim(), _newTaskDescription?.Trim() ?? "", _newTaskMaxPoints)),
            Loc["Management_TaskCreated"],
            async () => { _newTaskName = ""; _newTaskDescription = ""; await OnTaskSubjectChanged(_taskSubjectId); });
        // Баллы оставляем прежними: соседние задачи обычно стоят одинаково.
        if (_taskNameField is not null) await _taskNameField.FocusAsync();
    }

    /// <summary>Строки вида «название;баллы[;описание]» — сразу пачка задач. Ошибка в строке останавливает ввод до записи.</summary>
    private async Task AddBulkTasksAsync()
    {
        if (_taskSubjectId is not int subjectId || string.IsNullOrWhiteSpace(_bulkTasks)) return;
        var requests = new List<CreateTaskRequest>();
        var lines = _bulkTasks.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Split(lines[i].Contains('\t') ? '\t' : ';', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out var points) || points is < 0 or > 1000)
            {
                Snackbar.Add(Loc["Management_TasksBulkInvalid", i + 1], Severity.Warning);
                return;
            }
            requests.Add(new CreateTaskRequest(parts[0], parts.Length > 2 ? parts[2] : "", points));
        }
        if (await CreateTasksAsync(subjectId, requests) is int created && created > 0)
        {
            _bulkTasks = null;
            Snackbar.Add(Loc["Management_TasksBulkAdded", created], Severity.Success);
        }
    }

    /// <summary>Копирует задачи другого предмета, пропуская совпадающие по названию.</summary>
    private async Task CopyTasksFromAsync(int? sourceId)
    {
        _copyFromSubjectId = null;
        if (_taskSubjectId is not int subjectId || sourceId is not int source) return;
        try
        {
            var existing = _tasks.Select(t => t.Name.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var requests = (await Subjects.GetTasksAsync(source))
                .Where(t => !existing.Contains(t.Name.Trim()))
                .Select(t => new CreateTaskRequest(t.Name, t.Description, t.MaxPoints)).ToList();
            var created = await CreateTasksAsync(subjectId, requests) ?? 0;
            Snackbar.Add(Loc["Management_TasksCopied", created], Severity.Success);
        }
        catch (ApiException ex) { Snackbar.Add(ex.Message, Severity.Error); }
    }

    private async Task<int?> CreateTasksAsync(int subjectId, IReadOnlyList<CreateTaskRequest> requests)
    {
        _bulkBusy = true;
        var created = 0;
        try
        {
            foreach (var request in requests)
            {
                await Subjects.CreateTaskAsync(subjectId, request);
                created++;
            }
            return created;
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
            return created;
        }
        finally
        {
            _bulkBusy = false;
            await OnTaskSubjectChanged(_taskSubjectId);
        }
    }

    private void OnTaskCommit(object element)
    {
        var row = (TaskRow)element;
        _ = RunAsync(() => Subjects.UpdateTaskAsync(row.Id, new UpdateTaskRequest(row.Name, row.Description, row.MaxPoints)),
            Loc["Common_Saved"], () => Task.CompletedTask, () => OnTaskSubjectChanged(_taskSubjectId));
    }

    private async Task DeleteTaskAsync(TaskRow row)
    {
        if (await Confirm.ConfirmDeleteAsync(row.Name)) await RunAsync(() => Subjects.DeleteTaskAsync(row.Id), Loc["Management_TaskDeleted"], () => OnTaskSubjectChanged(_taskSubjectId));
    }

    // ----- Оценочные колонки -----
    private async Task OnComponentSubjectChanged(int? subjectId)
    {
        _componentSubjectId = subjectId;
        await CurrentSubject.SetAsync(subjectId);
        if (subjectId is null)
        {
            _components = [];
            return;
        }

        try
        {
            _components = (await Subjects.GetGradeComponentsAsync(subjectId.Value))
                .Select(c => new ComponentRow { Id = c.Id, Name = c.Name, MinPoints = c.MinPoints, MaxPoints = c.MaxPoints })
                .ToList();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    private async Task CreateComponentAsync()
    {
        if (_componentSubjectId is null || string.IsNullOrWhiteSpace(_newComponentName)) return;
        await RunAsync(
            () => Subjects.CreateGradeComponentAsync(_componentSubjectId.Value, new CreateGradeComponentRequest(_newComponentName.Trim(), _newComponentMin, _newComponentMax)),
            Loc["Management_ComponentCreated"],
            async () => { _newComponentName = ""; _newComponentMin = 0; _newComponentMax = 5; await OnComponentSubjectChanged(_componentSubjectId); });
    }

    private void OnComponentCommit(object element)
    {
        var row = (ComponentRow)element;
        _ = RunAsync(() => Subjects.UpdateGradeComponentAsync(row.Id, new UpdateGradeComponentRequest(row.Name, row.MinPoints, row.MaxPoints)),
            Loc["Common_Saved"], () => Task.CompletedTask, () => OnComponentSubjectChanged(_componentSubjectId));
    }

    private async Task DeleteComponentAsync(ComponentRow row)
    {
        if (await Confirm.ConfirmDeleteAsync(row.Name)) await RunAsync(() => Grades.DeleteComponentAsync(row.Id), Loc["Management_ComponentDeleted"], () => OnComponentSubjectChanged(_componentSubjectId));
    }

    // ----- Доступ (связи группа↔предмет) -----
    private async Task OpenSubjectAccessAsync(SubjectRow subject)
    {
        List<int> current;
        try
        {
            current = await Groups.GetGroupIdsForSubjectAsync(subject.Id);
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
            return;
        }

        var options = _groups.Select(g => new EntityLinkDialog.LinkOption(g.Id, g.Name)).ToList();
        var selected = await ShowAccessDialogAsync(string.Format(Loc["Management_SubjectAccessTitle"], subject.Name), options, current);
        if (selected is null)
            return;

        await ApplyAccessDiffAsync(current, selected,
            groupId => Groups.LinkSubjectAsync(groupId, subject.Id),
            groupId => Groups.UnlinkSubjectAsync(groupId, subject.Id));
    }

    private async Task OpenGroupAccessAsync(GroupRow group)
    {
        List<int> current;
        try
        {
            current = await Groups.GetSubjectIdsAsync(group.Id);
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
            return;
        }

        var options = AvailableSubjects.Select(s => new EntityLinkDialog.LinkOption(s.Id, s.Name)).ToList();
        var editableIds = options.Select(option => option.Id).ToHashSet();
        current = current.Where(editableIds.Contains).ToList();
        var selected = await ShowAccessDialogAsync(string.Format(Loc["Management_GroupAccessTitle"], group.Name), options, current);
        if (selected is null)
            return;

        await ApplyAccessDiffAsync(current, selected,
            subjectId => Groups.LinkSubjectAsync(group.Id, subjectId),
            subjectId => Groups.UnlinkSubjectAsync(group.Id, subjectId));
    }

    private async Task<HashSet<int>?> ShowAccessDialogAsync(string title, List<EntityLinkDialog.LinkOption> options, List<int> current)
    {
        var parameters = new DialogParameters<EntityLinkDialog>
        {
            { x => x.Title, title },
            { x => x.Options, options },
            { x => x.SelectedIds, current },
        };
        var dialog = await DialogService.ShowAsync<EntityLinkDialog>(title, parameters);
        var result = await dialog.Result;
        return result is { Canceled: false, Data: HashSet<int> set } ? set : null;
    }

    private async Task ApplyAccessDiffAsync(List<int> current, HashSet<int> selected, Func<int, Task> link, Func<int, Task> unlink)
    {
        try
        {
            foreach (var id in selected.Where(id => !current.Contains(id)))
                await link(id);
            foreach (var id in current.Where(id => !selected.Contains(id)))
                await unlink(id);
            Snackbar.Add(Loc["Management_AccessUpdated"], Severity.Success);
            Dirty.MarkClean();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }

    // ----- Видимость / архив (скрытие самим админом) -----
    private bool IsSubjectArchived(int id) => _hiddenSubjects.Contains(id);
    private bool IsSubjectRestricted(int id) => _restrictedSubjects.Contains(id);
    private bool CanUseSubject(int id) => !IsSubjectRestricted(id) && !IsSubjectArchived(id);
    private IEnumerable<SubjectRow> AvailableSubjects => _subjects.Where(subject => CanUseSubject(subject.Id));
    private bool IsGroupArchived(int id) => _hiddenGroups.Contains(id);
    private bool IsGroupRestricted(int id) => _restrictedGroups.Contains(id);

    private async Task SetSubjectArchivedAsync(int subjectId, bool hidden)
    {
        await RunAsync(
            () => Me.SetSubjectHiddenAsync(subjectId, hidden),
            hidden ? Loc["Management_SubjectArchived"] : Loc["Management_SubjectRestored"],
            () => { if (hidden) _hiddenSubjects.Add(subjectId); else _hiddenSubjects.Remove(subjectId); return Task.CompletedTask; });
    }

    private async Task SetGroupArchivedAsync(int groupId, bool hidden)
    {
        await RunAsync(
            () => Me.SetGroupHiddenAsync(groupId, hidden),
            hidden ? Loc["Management_GroupArchived"] : Loc["Management_GroupRestored"],
            () => { if (hidden) _hiddenGroups.Add(groupId); else _hiddenGroups.Remove(groupId); return Task.CompletedTask; });
    }

    private async Task RunAsync(Func<Task> action, string success, Func<Task> after, Func<Task>? onError = null)
    {
        try
        {
            await action();
            await after();
            Dirty.MarkClean();
            Snackbar.Add(success, Severity.Success);
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
            if (onError != null)
                await onError();
        }
        finally
        {
            StateHasChanged();
        }
    }
}
