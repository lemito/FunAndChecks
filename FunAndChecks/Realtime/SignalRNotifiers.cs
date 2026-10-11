using FunAndChecks.Application.Common.Interfaces;
using FunAndChecks.Application.Grades;
using FunAndChecks.Application.Queues;
using FunAndChecks.Application.Submissions;
using FunAndChecks.Application.Students;
using FunAndChecks.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace FunAndChecks.Realtime;

public class QueueNotifier(IHubContext<QueueHub> hubContext) : IQueueNotifier
{
    public Task QueueEntryUpdatedAsync(QueueEntryUpdateDto update, CancellationToken cancellationToken = default) =>
        hubContext.Clients
            .Group(QueueHub.GroupName(update.EventId))
            .SendAsync("QueueEntryUpdated", update, cancellationToken);

    public Task StudentColorUpdatedAsync(int eventId, StudentColorUpdateDto update, CancellationToken cancellationToken = default) =>
        hubContext.Clients
            .Group(QueueHub.GroupName(eventId))
            .SendAsync("StudentColorUpdated", update, cancellationToken);
}

public class ResultsNotifier(IHubContext<ResultsHub> hubContext) : IResultsNotifier
{
    public Task ResultUpdatedAsync(int subjectId, ResultUpdateDto update, CancellationToken cancellationToken = default) =>
        hubContext.Clients
            .Group(ResultsHub.GroupName(subjectId))
            .SendAsync("ResultUpdated", update, cancellationToken);

    public Task GradeUpdatedAsync(int subjectId, GradeUpdateDto update, CancellationToken cancellationToken = default) =>
        hubContext.Clients
            .Group(ResultsHub.GroupName(subjectId))
            .SendAsync("GradeUpdated", update, cancellationToken);

    public Task StudentColorUpdatedAsync(int subjectId, StudentColorUpdateDto update, CancellationToken cancellationToken = default) =>
        hubContext.Clients
            .Group(ResultsHub.GroupName(subjectId))
            .SendAsync("StudentColorUpdated", update, cancellationToken);
}
