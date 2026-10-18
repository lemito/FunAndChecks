using System.Text.Json;
using Frontend.Admin.Dialogs;
using Frontend.Shared.Api;
using Frontend.Shared.Models;
using Frontend.Shared.Resources;
using Frontend.Shared.Services;
using Frontend.Shared.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MudBlazor;
using System.Security.Claims;

namespace Frontend.Admin.Pages;

public partial class QueueDetails : IAsyncDisposable
{
    /// <summary>Пачка событий очереди склеивается в одно перечитывание.</summary>
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    [Parameter] public int EventId { get; set; }

    [Inject] private QueuesApi Queues { get; set; } = null!;
    [Inject] private AuthService Auth { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IStringLocalizer<AppStrings> Loc { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private QueueDetailsDto? _details;
    private List<QueueParticipantDto> _participants = [];
    private bool _loading = true;
    private HubConnection? _hub;
    private Guid? _currentAdminId;
    private bool _disposed;
    private long _colorRevision;
    private readonly Dictionary<Guid, (long Revision, string? Color)> _latestColorChanges = [];

    private string? _filter;
    private ParticipantSort _sort = ParticipantSort.Points;
    // Группы, которые этот преподаватель скрыл в этой очереди (хранится в браузере).
    private readonly HashSet<string> _hiddenGroups = new(StringComparer.Ordinal);
    private bool _dialogOpen, _reloadScheduled;
    private string? _loadError;

    public enum ParticipantSort { Group, Points, JoinedAt }

    private string HiddenGroupsKey => $"fc-queue-hidden-groups-{EventId}";

    protected override async Task OnInitializedAsync()
    {
        var user = (await Authentication.GetAuthenticationStateAsync()).User;
        if (Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.FindFirst("nameid")?.Value, out var adminId))
            _currentAdminId = adminId;
        await RestoreHiddenGroupsAsync();
        await LoadAsync();
        if (_details is not null && !_disposed)
            await InitializeSignalRAsync();
    }

    private List<QueueParticipantDto> Visible => VisibleParticipants();

    /// <summary>Все группы очереди с числом участников — для переключателей видимости.</summary>
    private List<(string Group, int Count)> Groups => _participants.GroupBy(p => p.GroupName)
        .OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();

    private IEnumerable<QueueParticipantDto> InVisibleGroups => _participants.Where(p => !_hiddenGroups.Contains(p.GroupName));

    private List<QueueParticipantDto> VisibleParticipants()
    {
        var items = InVisibleGroups;

        if (!string.IsNullOrWhiteSpace(_filter))
        {
            var term = _filter.Trim();
            items = items.Where(p =>
                p.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.GroupName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // Статус — всегда первичный ключ сортировки (сдают → в очереди → пропущенные → завершившие),
        // выбранное поле — вторичное, время записи — последний ключ при равенстве.
        var ordered = items.OrderBy(p => QueueStyles.StatusOrder(p.Status));
        ordered = _sort switch
        {
            ParticipantSort.Group => ordered.ThenBy(p => p.GroupName).ThenBy(p => p.LastName),
            ParticipantSort.JoinedAt => ordered.ThenBy(p => p.JoinedAt),
            _ => ordered.ThenByDescending(p => p.TotalPoints).ThenBy(p => p.JoinedAt),
        };

        return ordered.ToList();
    }

    /// <summary>Следующий ожидающий в видимых группах: больше баллов — раньше, при равенстве — кто раньше записался.</summary>
    private QueueParticipantDto? NextWaiting() => InVisibleGroups
        .Where(p => p.Status == QueueEntryStatus.Waiting && p.CanManage)
        .OrderByDescending(p => p.TotalPoints).ThenBy(p => p.JoinedAt)
        .FirstOrDefault();

    private async Task LoadAsync()
    {
        if (_disposed)
            return;
        try
        {
            var eventId = EventId;
            var colorRevision = _colorRevision;
            var details = await Queues.GetDetailsAsync(eventId);
            if (_disposed || EventId != eventId)
                return;
            _participants = details.Participants.Select(participant =>
                _latestColorChanges.TryGetValue(participant.StudentId, out var change) && change.Revision > colorRevision
                    ? participant with { StudentColor = change.Color }
                    : participant).ToList();
            _details = details with { Participants = _participants };
            _loadError = null;
        }
        catch (ApiException ex)
        {
            if (_details is null)
            {
                _participants = [];
                Snackbar.Add(string.Format(Loc["QueueDetails_LoadErrorPrefix"], ex.Message), Severity.Error);
            }
            else
            {
                // Не стираем очередь из-за одной ошибки: показываем последний список и пробуем снова.
                _loadError = ex.Message;
                _ = RetryLaterAsync();
            }
        }
        finally
        {
            if (!_disposed)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task RetryLaterAsync()
    {
        await Task.Delay(RetryDelay);
        if (!_disposed && _loadError is not null && !_dialogOpen)
            await InvokeAsync(LoadAsync);
    }

    /// <summary>
    /// Реакция на событие очереди. Пока открыт диалог студента, список под ним не трогаем —
    /// он перечитается целиком при возврате; иначе склеиваем пачку событий в одно перечитывание.
    /// </summary>
    private async Task OnQueueChangedAsync()
    {
        if (_disposed) return;
        if (_dialogOpen || _reloadScheduled) return;
        _reloadScheduled = true;
        try
        {
            await Task.Delay(ReloadDelay);
            if (!_dialogOpen) await LoadAsync();
        }
        finally { _reloadScheduled = false; }
    }

    private async Task InitializeSignalRAsync()
    {
        if (_disposed)
            return;
        _hub = new HubConnectionBuilder()
            .WithUrl(Nav.ToAbsoluteUri("/apiHub/queueHub"), options =>
                options.AccessTokenProvider = async () => await Auth.GetTokenAsync())
            .WithAutomaticReconnect()
            .Build();

        var hub = _hub;
        var eventId = EventId;

        _hub.On<QueueEntryUpdateDto>("QueueEntryUpdated", _ => InvokeAsync(OnQueueChangedAsync));
        _hub.On<StudentColorUpdateDto>("StudentColorUpdated", update => InvokeAsync(() => UpdateStudentColor(update)));
        _hub.Reconnecting += _ => InvokeAsync(() => { if (!_disposed) StateHasChanged(); });
        _hub.Closed += _ => InvokeAsync(() => { if (!_disposed) StateHasChanged(); });

        // После переподключения переподписываемся, иначе обновления перестают приходить.
        _hub.Reconnected += async _ =>
        {
            try
            {
                if (_disposed || !ReferenceEquals(_hub, hub) || EventId != eventId)
                    return;
                await hub.InvokeAsync("SubscribeToQueue", eventId);
                await InvokeAsync(OnQueueChangedAsync);
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Snackbar.Add(string.Format(Loc["Common_SignalRReconnectError"], ex.Message), Severity.Error);
            }
        };

        try
        {
            await hub.StartAsync();
            if (_disposed)
                return;
            await hub.InvokeAsync("SubscribeToQueue", eventId);
            // Изменения между первой загрузкой и подпиской не должны потеряться.
            await LoadAsync();
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Snackbar.Add(string.Format(Loc["Common_SignalRConnectError"], ex.Message), Severity.Warning);
        }
    }

    private async Task TakeNextAsync()
    {
        // Перед выбором — свежий список: «следующий» должен быть актуальным, а не из устаревшей картинки.
        await LoadAsync();
        var next = NextWaiting();
        if (next is null)
        {
            Snackbar.Add(Loc["QueueDetails_NoNext"], Severity.Info);
            return;
        }
        await OpenStudentAsync(next, take: true);
    }

    /// <summary>
    /// Открывает студента. Ожидающего или проверяемого берёт на проверку; завершённого или пропущенного —
    /// только показывает, без смены статуса (берётся явной кнопкой «Взять на проверку»).
    /// </summary>
    private async Task OpenStudentAsync(QueueParticipantDto participant, bool take = false)
    {
        if (_details is null || !participant.CanManage)
            return;

        var viewOnly = !take && participant.Status is QueueEntryStatus.Finished or QueueEntryStatus.Skipped;
        var checkingWarning = IsOtherTeacherChecking(participant) ? participant.CheckingByAdminName : null;
        // Другой преподаватель — предупреждение, а не запрет на приём.
        if (!viewOnly && !IsReadOnly(participant) &&
            (participant.Status != QueueEntryStatus.Checking || participant.CheckingByAdminId != _currentAdminId))
        {
            try
            {
                await Queues.UpdateStatusAsync(EventId, participant.StudentId, new UpdateQueueStatusRequest(QueueEntryStatus.Checking));
                participant = participant with { Status = QueueEntryStatus.Checking, CheckingByAdminId = _currentAdminId };
            }
            catch (ApiException ex)
            {
                Snackbar.Add(ex.Message, Severity.Error);
                await LoadAsync();
                return;
            }
        }

        var parameters = new DialogParameters<StudentInteractionDialog>
        {
            { x => x.StudentId, participant.StudentId },
            { x => x.StudentName, $"{participant.LastName} {participant.FirstName}" },
            { x => x.GroupName, participant.GroupName },
            { x => x.EventId, EventId },
            { x => x.SubjectId, _details.SubjectId },
            { x => x.ReadOnly, viewOnly || IsReadOnly(participant) },
            { x => x.CanTake, viewOnly && !IsReadOnly(participant) },
            { x => x.StudentColor, participant.StudentColor },
            { x => x.CheckingWarningName, checkingWarning },
            { x => x.OnColorApplied, EventCallback.Factory.Create<StudentColorUpdateDto>(this, UpdateStudentColor) },
        };

        _dialogOpen = true;
        DialogResult? result;
        try
        {
            var dialog = await DialogService.ShowAsync<StudentInteractionDialog>(
                Loc["QueueDetails_WorkTitle"],
                parameters,
                new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
            result = await dialog.Result;
        }
        finally
        {
            _dialogOpen = false;
        }

        // Возврат к списку — всегда полное перечитывание: так надёжнее, чем собирать картину из событий.
        await LoadAsync();
        if (result is { Canceled: false, Data: string action } && !_disposed)
        {
            if (action == StudentDialogResults.Take)
            {
                var fresh = _participants.FirstOrDefault(p => p.StudentId == participant.StudentId);
                if (fresh is not null) await OpenStudentAsync(fresh, take: true);
            }
            else if (action == StudentDialogResults.Next)
            {
                var next = NextWaiting();
                if (next is null) Snackbar.Add(Loc["QueueDetails_NoNext"], Severity.Info);
                else await OpenStudentAsync(next, take: true);
            }
        }
    }

    /// <summary>Счётчик статуса: при скрытых группах — «видимые / всего».</summary>
    private string StatCount(QueueEntryStatus status)
    {
        var total = _participants.Count(p => p.Status == status);
        var visible = InVisibleGroups.Count(p => p.Status == status);
        return visible == total ? total.ToString() : $"{visible} / {total}";
    }

    private async Task ToggleGroupAsync(string group)
    {
        if (!_hiddenGroups.Remove(group)) _hiddenGroups.Add(group);
        await SaveHiddenGroupsAsync();
    }

    private async Task ShowAllGroupsAsync()
    {
        _hiddenGroups.Clear();
        await SaveHiddenGroupsAsync();
    }

    private async Task RestoreHiddenGroupsAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", HiddenGroupsKey);
            if (!string.IsNullOrEmpty(json) && JsonSerializer.Deserialize<string[]>(json) is { } groups)
                _hiddenGroups.UnionWith(groups);
        }
        catch (Exception ex) when (ex is JSException or JsonException) { /* Нет настройки — показываем всех. */ }
    }

    private async Task SaveHiddenGroupsAsync()
    {
        try
        {
            if (_hiddenGroups.Count == 0) await JS.InvokeVoidAsync("localStorage.removeItem", HiddenGroupsKey);
            else await JS.InvokeVoidAsync("localStorage.setItem", HiddenGroupsKey, JsonSerializer.Serialize(_hiddenGroups));
        }
        catch (JSException) { /* Настройка останется до перезагрузки страницы. */ }
    }

    private static Color StatusColor(QueueEntryStatus status) => status switch
    {
        QueueEntryStatus.Checking => Color.Info,
        QueueEntryStatus.Skipped => Color.Error,
        QueueEntryStatus.Waiting => Color.Warning,
        QueueEntryStatus.Finished => Color.Success,
        _ => Color.Default,
    };

    private void UpdateStudentColor(StudentColorUpdateDto update)
    {
        if (_disposed)
            return;
        _latestColorChanges[update.StudentId] = (++_colorRevision, update.Color);
        if (!_participants.Any(participant => participant.StudentId == update.StudentId && participant.StudentColor != update.Color))
            return;

        _participants = _participants.Select(participant => participant.StudentId == update.StudentId
            ? participant with { StudentColor = update.Color }
            : participant).ToList();
        if (_details is not null)
            _details = _details with { Participants = _participants };
        StateHasChanged();
    }

    private bool IsReadOnly(QueueParticipantDto participant) => _currentAdminId is null;

    private bool IsOtherTeacherChecking(QueueParticipantDto participant) =>
        participant.Status == QueueEntryStatus.Checking &&
        participant.CheckingByAdminId is not null && participant.CheckingByAdminId != _currentAdminId;

    private static string QueueClass(QueueEntryStatus status) => status switch
    {
        QueueEntryStatus.Checking => "fc-queue-checking",
        QueueEntryStatus.Skipped => "fc-queue-skipped",
        QueueEntryStatus.Finished => "fc-queue-finished",
        _ => "fc-queue-waiting",
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_hub is not null)
        {
            try
            {
                if (_hub.State == HubConnectionState.Connected)
                    await _hub.InvokeAsync("UnsubscribeFromQueue", EventId);
            }
            catch
            {
                // соединение могло уже закрыться
            }

            await _hub.DisposeAsync();
        }
    }
}
