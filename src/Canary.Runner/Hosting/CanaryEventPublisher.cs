using Microsoft.Extensions.DependencyInjection;
using RadiCorder.Logics.Domain.AppEvent;
using RadiCorder.Logics.Domain.Notification;
using RadiCorder.Logics.Domain.Recording;
using RadiCorder.Logics.Domain.Reserve;
using RadiCorder.Logics.Logics.ProgramScheduleLogic;
using RadiCorder.Logics.Logics.RecordedRadioLogic;
using RadiCorder.Logics.Models;

namespace Canary.Runner.Hosting;

/// <summary>
/// UI向け通知の受け口。Canaryの結果は各チェックのログとstatus.jsonに出力する。
/// </summary>
internal sealed class CanaryEventPublisher : IAppToastEventPublisher, IAppOperationEventPublisher,
    INotificationEventPublisher, IRecordingStateEventPublisher, IReserveScheduleEventPublisher,
    IProgramUpdateStatusPublisher, IRecordedDuplicateDetectionStatusPublisher
{
    public ValueTask PublishAsync(AppToastEvent payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask PublishAsync(AppOperationEvent payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask PublishAsync(NotificationChangedEvent payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask PublishAsync(RecordingStateChangedEvent payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask PublishAsync(ReserveScheduleChangedEvent payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask PublishAsync(ProgramUpdateStatusSnapshot status, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask PublishAsync(RecordedDuplicateDetectionStatusEntry status, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

internal static class CanaryEventPublisherRegistration
{
    internal static IServiceCollection AddCanaryEventPublishers(this IServiceCollection services)
    {
        services.AddSingleton<CanaryEventPublisher>();
        services.AddSingleton<IAppToastEventPublisher>(provider => provider.GetRequiredService<CanaryEventPublisher>());
        services.AddSingleton<IAppOperationEventPublisher>(provider => provider.GetRequiredService<CanaryEventPublisher>());
        services.AddSingleton<INotificationEventPublisher>(provider => provider.GetRequiredService<CanaryEventPublisher>());
        services.AddSingleton<IRecordingStateEventPublisher>(provider => provider.GetRequiredService<CanaryEventPublisher>());
        services.AddSingleton<IReserveScheduleEventPublisher>(provider => provider.GetRequiredService<CanaryEventPublisher>());
        services.AddSingleton<IProgramUpdateStatusPublisher>(provider => provider.GetRequiredService<CanaryEventPublisher>());
        services.AddSingleton<IRecordedDuplicateDetectionStatusPublisher>(provider => provider.GetRequiredService<CanaryEventPublisher>());
        return services;
    }
}
