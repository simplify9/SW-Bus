using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 8 — Heavy/slow consumer (500–2000 ms), prefetch 1.
/// Simulates generating a full business report: aggregating data across
/// multiple DB tables, rendering a PDF, uploading to storage.
/// Prefetch 1 prevents the host from being overwhelmed by concurrent reports.
/// Only one report processes at a time per node.
/// Never fails — reports always finish, just slowly.
/// </summary>
public class ReportingConsumer : IConsume<DailyReportMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<ReportingConsumer> logger;

    public ReportingConsumer(ILogger<ReportingConsumer> logger)
        => this.logger = logger;

    public async Task Process(DailyReportMessage message)
    {
        logger.LogInformation(
            "Report generation started: [{Type}] for {Date:yyyy-MM-dd} requested by {User}",
            message.ReportType, message.ReportDate, message.RequestedBy);

        // Simulate 3 sequential phases with realistic delays
        await Task.Delay(Rng.Next(100, 500));    // Phase 1: data aggregation
        await Task.Delay(Rng.Next(200, 800));    // Phase 2: PDF rendering
        await Task.Delay(Rng.Next(100, 600));    // Phase 3: upload to storage

        logger.LogInformation(
            "Report {ReportId} [{Type}] for {Date:yyyy-MM-dd} completed.",
            message.ReportId, message.ReportType, message.ReportDate);
    }
}

