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
using MudBlazor;
using System.Security.Claims;

namespace Frontend.Admin.Pages;

public partial class QueueDetails : IAsyncDisposable
{
    [Parameter] public int EventId { get; set; }

    [Inject] private QueuesApi Queues { get; set; } = null!;
    [Inject] private AuthService Auth { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IStringLocalizer<AppStrings> Loc { get; set; } = null!;

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

    public enum ParticipantSort { Group, Points, JoinedAt }

    protected override async Task OnInitializedAsync()
    {
        var user = (await Authentication.GetAuthenticationStateAsync()).User;
        if (Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.FindFirst("nameid")?.Value, out var adminId))
            _currentAdminId = adminId;
        await LoadAsync();
        if (_details is not null && !_disposed)
            await InitializeSignalRAsync();
    }

    private List<QueueParticipantDto> Visible => VisibleParticipants();

    private List<QueueParticipantDto> VisibleParticipants()
    {
        IEnumerable<QueueParticipantDto> items = _participants;

        if (!string.IsNullOrWhiteSpace(_filter))
        {
            var term = _filter.Trim();
            items = items.Where(p =>
                p.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.GroupName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // Статус — всегда первичный ключ сортировки (сдают → в очереди → пропущенные → завершившие),
        // выбранное поле — вторичное.
        var ordered = items.OrderBy(p => QueueStyles.StatusOrder(p.Status));
        ordered = _sort switch
        {
            ParticipantSort.Group => ordered.ThenBy(p => p.GroupName).ThenBy(p => p.LastName),
            ParticipantSort.JoinedAt => ordered.ThenBy(p => p.JoinedAt),
            _ => ordered.ThenByDescending(p => p.TotalPoints),
        };

        return ordered.ToList();
    }

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
        }
        catch (ApiException ex)
        {
            _details = null;
            _participants = [];
            Snackbar.Add(string.Format(Loc["QueueDetails_LoadErrorPrefix"], ex.Message), Severity.Error);
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

        _hub.On<QueueEntryUpdateDto>("QueueEntryUpdated", _ => InvokeAsync(LoadAsync));
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
                await InvokeAsync(LoadAsync);
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
            await LoadAsync();
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Snackbar.Add(string.Format(Loc["Common_SignalRConnectError"], ex.Message), Severity.Warning);
        }
    }

    private async Task OpenStudentAsync(QueueParticipantDto participant)
    {
        if (_details is null || !participant.CanManage)
            return;

        var checkingWarning = IsOtherTeacherChecking(participant) ? participant.CheckingByAdminName : null;
        // Другой преподаватель — предупреждение, а не запрет на приём.
        if (!IsReadOnly(participant) &&
            (participant.Status != QueueEntryStatus.Checking || participant.CheckingByAdminId != _currentAdminId))
        {
            try
            {
                await Queues.UpdateStatusAsync(EventId, participant.StudentId, new UpdateQueueStatusRequest(QueueEntryStatus.Checking));
                await LoadAsync();
                participant = _participants.FirstOrDefault(p => p.StudentId == participant.StudentId) ?? participant;
            }
            catch (ApiException ex)
            {
                Snackbar.Add(ex.Message, Severity.Error);
                await LoadAsync();
                return;
            }
        }

        if (_details is null || !participant.CanManage)
            return;

        var parameters = new DialogParameters<StudentInteractionDialog>
        {
            { x => x.StudentId, participant.StudentId },
            { x => x.StudentName, $"{participant.LastName} {participant.FirstName}" },
            { x => x.GroupName, participant.GroupName },
            { x => x.EventId, EventId },
            { x => x.SubjectId, _details.SubjectId },
            { x => x.ReadOnly, IsReadOnly(participant) },
            { x => x.StudentColor, participant.StudentColor },
            { x => x.CheckingWarningName, checkingWarning },
            { x => x.OnColorApplied, EventCallback.Factory.Create<StudentColorUpdateDto>(this, UpdateStudentColor) },
        };

        var dialog = await DialogService.ShowAsync<StudentInteractionDialog>(
            Loc["QueueDetails_WorkTitle"],
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });

        var result = await dialog.Result;
        if (result is { Canceled: false })
            await LoadAsync();
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
