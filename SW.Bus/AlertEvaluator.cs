using System;
using System.Collections.Generic;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus;

/// <summary>
/// Default alert evaluator that derives <see cref="DashboardAlert"/> objects from
/// live <see cref="ConsumerHealthView"/> snapshots using configurable thresholds
/// stored in <see cref="BusOptions"/>.
/// Replace this with a custom <see cref="IAlertEvaluator"/> registration to apply
/// organisation-specific SLA thresholds.
/// </summary>
internal sealed class AlertEvaluator : IAlertEvaluator
{
    private readonly BusOptions busOptions;

    public AlertEvaluator(BusOptions busOptions) => this.busOptions = busOptions;

    /// <inheritdoc />
    public IReadOnlyList<DashboardAlert> Evaluate(ConsumerHealthView[] consumers)
    {
        var alerts = new List<DashboardAlert>();
        var now = DateTime.UtcNow;

        foreach (var c in consumers)
        {
            // ── Consumer disconnected ─────────────────────────────────────────
            if (c.TotalNodes == 0)
                alerts.Add(new DashboardAlert(
                    AlertSeverity.Critical,
                    "Consumer Disconnected",
                    $"No active nodes for {c.Name} / {c.MessageName}. Queue '{c.QueueName}' is not being processed.",
                    c.QueueName, c.Name, now));

            // ── Dead-letter backlog ───────────────────────────────────────────
            if (c.FailedCount > 0)
            {
                var sev = c.FailedCount > busOptions.AlertDeadLetterCriticalThreshold
                    ? AlertSeverity.Critical
                    : AlertSeverity.Warning;
                alerts.Add(new DashboardAlert(
                    sev,
                    "Dead-Letter Backlog",
                    $"{c.FailedCount:N0} messages in dead-letter queue for {c.Name} / {c.MessageName}.",
                    c.QueueName, c.Name, now));
            }

            // ── Retry backlog ─────────────────────────────────────────────────
            if (c.RetryCount > busOptions.AlertRetryWarningThreshold)
            {
                var sev = c.RetryCount > busOptions.AlertRetryCriticalThreshold
                    ? AlertSeverity.Critical
                    : AlertSeverity.Warning;
                alerts.Add(new DashboardAlert(
                    sev,
                    "Retry Backlog",
                    $"{c.RetryCount:N0} messages in retry queue for {c.Name} / {c.MessageName}.",
                    c.QueueName, c.Name, now));
            }

            // ── Queue backpressure ────────────────────────────────────────────
            if (c.IsBackpressured)
                alerts.Add(new DashboardAlert(
                    AlertSeverity.Warning,
                    "Queue Backpressure",
                    $"Queue depth ({c.QueueCount:N0}) exceeds threshold ({busOptions.QueueBackpressureThreshold:N0}) for {c.Name} / {c.MessageName}.",
                    c.QueueName, c.Name, now));

            // ── Publish / Ack imbalance ───────────────────────────────────────
            if (c.IncomingRate > 1.0 && c.AckRate < 0.01)
                alerts.Add(new DashboardAlert(
                    AlertSeverity.Warning,
                    "Publish / Ack Imbalance",
                    $"Messages are arriving at {c.IncomingRate:F1} msg/s but ack rate is effectively zero for {c.Name} / {c.MessageName}.",
                    c.QueueName, c.Name, now));
        }

        return alerts;
    }
}

