using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eventuous.Subscriptions {
    [PublicAPI]
    public abstract class SubscriptionService : IHostedService, IHealthCheck {
        protected bool              IsRunning      { get; set; }
        protected bool              IsDropped      { get; set; }
        protected EventSubscription Subscription   { get; set; } = null!;
        protected string            SubscriptionId { get; }

        readonly ICheckpointStore        _checkpointStore;
        readonly IEventSerializer        _eventSerializer;
        readonly IEventHandler[]         _projections;
        readonly SubscriptionGapMeasure? _measure;
        readonly ILogger?                _log;
        readonly Log?                    _debugLog;

        CancellationTokenSource? _cts;
        Task?                    _measureTask;
        EventPosition?           _lastProcessed;
        ulong                    _gap;

        protected SubscriptionService(
            string                     subscriptionId,
            ICheckpointStore           checkpointStore,
            IEventSerializer           eventSerializer,
            IEnumerable<IEventHandler> eventHandlers,
            ILoggerFactory?            loggerFactory = null,
            SubscriptionGapMeasure?    measure       = null
        ) {
            _checkpointStore = Ensure.NotNull(checkpointStore, nameof(checkpointStore));
            _eventSerializer = Ensure.NotNull(eventSerializer, nameof(eventSerializer));
            SubscriptionId   = Ensure.NotEmptyString(subscriptionId, subscriptionId);
            _measure         = measure;

            _projections = Ensure.NotNull(eventHandlers, nameof(eventHandlers))
                .Where(x => x.SubscriptionId == subscriptionId)
                .ToArray();

            _log = loggerFactory?.CreateLogger($"StreamSubscription-{subscriptionId}");

            _debugLog = _log?.IsEnabled(LogLevel.Debug) == true ? _log.LogDebug : null;
        }

        public async Task StartAsync(CancellationToken cancellationToken) {
            var checkpoint = await _checkpointStore.GetLastCheckpoint(SubscriptionId, cancellationToken);

            _lastProcessed = new EventPosition(checkpoint.Position, DateTime.Now);

            Subscription = await Subscribe(checkpoint, cancellationToken);

            if (_measure != null) {
                _cts         = new CancellationTokenSource();
                _measureTask = Task.Run(() => MeasureGap(_cts.Token), _cts.Token);
            }

            IsRunning = true;

            _log?.LogInformation("Started subscription {Subscription}", SubscriptionId);
        }

        protected async Task Handler(ReceivedEvent re, CancellationToken cancellationToken) {
            _debugLog?.Invoke(
                "Subscription {Subscription} got an event {EventType}",
                SubscriptionId,
                re.EventType
            );

            _lastProcessed = GetPosition(re);

            if (re.EventType.StartsWith("$") || re.Data.IsEmpty) {
                await Store();
                return;
            }

            try {
                var contentType = string.IsNullOrWhiteSpace(re.ContentType) ? "application/json" : re.ContentType;

                if (contentType != _eventSerializer.ContentType)
                    throw new InvalidOperationException($"Unknown content type {contentType}");

                object? evt;

                try {
                    evt = _eventSerializer.Deserialize(re.Data.Span, re.EventType);
                }
                catch (Exception e) {
                    _log?.LogError(e, "Error deserializing: {Data}", Encoding.UTF8.GetString(re.Data.ToArray()));
                    throw;
                }

                if (evt != null) {
                    try {
                        _debugLog?.Invoke("Handling event {Event}", evt);
                    }
                    catch (Exception) {
                        _log?.LogWarning("Something weird with the log {Stream} {Position} {Type}",
                            re.OriginalStream, re.StreamPosition, re.EventType);
                    }

                    await Task.WhenAll(
                        _projections.Select(x => x.HandleEvent(evt, (long?) re.GlobalPosition))
                    );
                }
            }
            catch (Exception e) {
                _log?.LogWarning(e, "Error when handling the event {EventType}", re.EventType);
            }

            await Store();

            Task Store() => StoreCheckpoint(GetPosition(re), cancellationToken);

            static EventPosition GetPosition(ReceivedEvent receivedEvent)
                => new(receivedEvent.StreamPosition, receivedEvent.Created);
        }

        protected async Task StoreCheckpoint(EventPosition position, CancellationToken cancellationToken) {
            _lastProcessed = position;
            var checkpoint = new Checkpoint(SubscriptionId, position.Position);

            await _checkpointStore.StoreCheckpoint(checkpoint, cancellationToken);
        }

        protected abstract Task<EventSubscription> Subscribe(
            Checkpoint        checkpoint,
            CancellationToken cancellationToken
        );

        public async Task StopAsync(CancellationToken cancellationToken) {
            IsRunning = false;

            if (_measureTask != null) {
                _cts?.Cancel();

                try {
                    await _measureTask;
                }
                catch (OperationCanceledException) {
                    // Expected
                }
            }

            await Subscription.Stop(cancellationToken);

            _log?.LogInformation("Stopped subscription {Subscription}", SubscriptionId);
        }

        protected async Task Resubscribe(TimeSpan delay) {
            _log?.LogWarning("Resubscribing {Subscription}", SubscriptionId);

            await Task.Delay(delay);

            while (IsRunning && IsDropped) {
                try {
                    var checkpoint = new Checkpoint(SubscriptionId, _lastProcessed?.Position);

                    Subscription = await Subscribe(checkpoint, CancellationToken.None);

                    IsDropped = false;

                    _log?.LogInformation("Subscription {Subscription} restored", SubscriptionId);
                }
                catch (Exception e) {
                    _log?.LogError(e, "Unable to restart the subscription {Subscription}", SubscriptionId);

                    await Task.Delay(1000);
                }
            }
        }

        protected void Dropped(
            DropReason reason,
            Exception? exception
        ) {
            if (!IsRunning) return;

            _log?.LogWarning(
                exception,
                "Subscription {Subscription} dropped {Reason}",
                SubscriptionId,
                reason
            );

            IsDropped = true;

            Task.Run(
                () => Resubscribe(
                    reason == DropReason.Stopped ? TimeSpan.FromSeconds(10) : TimeSpan.Zero
                )
            );
        }

        async Task MeasureGap(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                var (position, created) = await GetLastEventPosition(cancellationToken);

                if (_lastProcessed?.Position != null && position != null) {
                    _gap = (ulong) position - _lastProcessed.Position.Value;

                    _measure!.PutGap(SubscriptionId, _gap, created);
                }

                await Task.Delay(1000, cancellationToken);
            }
        }

        protected abstract Task<EventPosition> GetLastEventPosition(CancellationToken cancellationToken);

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken  cancellationToken = default
        ) {
            var result = IsRunning && IsDropped
                ? HealthCheckResult.Unhealthy("Subscription dropped")
                : HealthCheckResult.Healthy();

            return Task.FromResult(result);
        }
    }

    public record EventPosition(ulong? Position, DateTime Created);
}