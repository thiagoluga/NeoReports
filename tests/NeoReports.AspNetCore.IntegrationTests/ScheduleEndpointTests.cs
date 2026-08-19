using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Building;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Scheduling;
using NeoReports.Formats.Csv;
using NeoReports.Jobs.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// ADR D41: <c>PUT/DELETE /reports/{name}/schedule</c>, the schedule fields on
/// <c>GET /reports/{name}</c>, and the <c>Scheduling</c> capability flag.
/// </summary>
public class ScheduleEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void AddScheduledCodeFirstReport(IServiceCollection services)
    {
        services.AddReport<Sale>("sales", b => b
            .From(new InMemorySource(rows: 10, pageSize: 10))
            .Column(v => v.Id, "ID")
            .To(Csv())
            .Schedule("0 6 * * 1"));
        services.AddNeoReportsInMemoryJobs();
        services.AddInMemoryScheduling();
    }

    private static void AddUnscheduledCodeFirstReport(IServiceCollection services)
    {
        services.AddReport<Sale>("sales", b => b
            .From(new InMemorySource(rows: 10, pageSize: 10))
            .Column(v => v.Id, "ID")
            .To(Csv()));
        services.AddNeoReportsInMemoryJobs();
        services.AddInMemoryScheduling();
    }

    /// <summary>
    /// A code-first report is under no naming constraint, so a dot is legal — but an override store
    /// keys by name and only accepts <c>DynamicReportName.Pattern</c>. This is the combination that
    /// used to reach the store unguarded.
    /// </summary>
    private static void AddCodeFirstReportWithAnUnstorableName(IServiceCollection services)
    {
        services.AddReport<Sale>("sales.daily", b => b
            .From(new InMemorySource(rows: 10, pageSize: 10))
            .Column(v => v.Id, "ID")
            .To(Csv()));
        services.AddNeoReportsInMemoryJobs();
        services.AddInMemoryScheduling();
    }

    [Fact]
    public async Task Setting_a_schedule_for_a_name_no_override_store_can_key_is_refused_not_a_500()
    {
        using var host = await TestApp.StartAsync(AddCodeFirstReportWithAnUnstorableName);
        var client = host.GetTestClient();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/reports/sales.daily/schedule", new { cron = "0 6 * * 1" }, Json);

        // The report exists and the host has a scheduler; what cannot be done is persisting an
        // override under this name. That is the resource's state, not a malformed request — and it
        // must not be the ArgumentException-turned-500 it used to be.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // Read through the parsed body, not the raw JSON: the pattern contains a backslash (it is
        // anchored with \z, since $ also matches before a trailing newline), and JSON doubles it on
        // the wire — a substring check against the raw text compares two different encodings.
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("error").GetString()!.ShouldContain(DynamicReportName.Pattern);
    }

    [Fact]
    public async Task Clearing_a_schedule_for_a_name_no_override_store_can_key_is_refused_not_a_500()
    {
        using var host = await TestApp.StartAsync(AddCodeFirstReportWithAnUnstorableName);
        var client = host.GetTestClient();

        HttpResponseMessage response = await client.DeleteAsync("/api/reports/sales.daily/schedule");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Capabilities_reports_scheduling_true_when_a_recurring_scheduler_is_registered()
    {
        using var host = await TestApp.StartAsync(); // default TestApp always calls AddNeoReportsInMemoryJobs
        var client = host.GetTestClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/capabilities", Json);
        capabilities.GetProperty("scheduling").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Detail_reflects_a_declared_schedule_with_a_real_next_run()
    {
        using var host = await TestApp.StartAsync(AddScheduledCodeFirstReport);
        var client = host.GetTestClient();

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("scheduleCron").GetString().ShouldBe("0 6 * * 1");
        detail.GetProperty("scheduleOverridden").GetBoolean().ShouldBeFalse();
        detail.GetProperty("nextRunAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Detail_shows_no_schedule_for_an_unscheduled_report()
    {
        using var host = await TestApp.StartAsync(AddUnscheduledCodeFirstReport);
        var client = host.GetTestClient();

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("scheduleCron").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("scheduleOverridden").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Set_schedule_returns_404_for_an_unknown_report()
    {
        using var host = await TestApp.StartAsync(AddUnscheduledCodeFirstReport);
        var client = host.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/reports/does-not-exist/schedule", new { cron = "0 6 * * 1" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Set_schedule_returns_400_for_a_missing_cron()
    {
        using var host = await TestApp.StartAsync(AddUnscheduledCodeFirstReport);
        var client = host.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/reports/sales/schedule", new { }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Set_schedule_returns_400_for_an_invalid_cron()
    {
        using var host = await TestApp.StartAsync(AddUnscheduledCodeFirstReport);
        var client = host.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/reports/sales/schedule", new { cron = "garbage" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Set_schedule_returns_409_when_no_override_store_is_registered()
    {
        // Default TestApp registers a recurring scheduler (AddNeoReportsInMemoryJobs) but never
        // calls AddScheduling/AddInMemoryScheduling — the override store is what's missing here.
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/reports/sales/schedule", new { cron = "0 6 * * 1" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Set_schedule_on_an_unscheduled_report_activates_it_for_both_origins()
    {
        using var host = await TestApp.StartAsync(AddUnscheduledCodeFirstReport);
        var client = host.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/reports/sales/schedule", new { cron = "*/5 * * * *" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("scheduleCron").GetString().ShouldBe("*/5 * * * *");
        detail.GetProperty("scheduleOverridden").GetBoolean().ShouldBeTrue();
        detail.GetProperty("nextRunAt").ValueKind.ShouldNotBe(JsonValueKind.Null);

        var scheduler = host.Services.GetRequiredService<IRecurringReportScheduler>();
        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldContain("sales");
    }

    [Fact]
    public async Task Clear_schedule_on_an_override_only_report_removes_it_entirely()
    {
        using var host = await TestApp.StartAsync(AddUnscheduledCodeFirstReport);
        var client = host.GetTestClient();

        await client.PutAsJsonAsync("/api/reports/sales/schedule", new { cron = "*/5 * * * *" }, Json);

        var response = await client.DeleteAsync("/api/reports/sales/schedule");
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("scheduleCron").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("scheduleOverridden").GetBoolean().ShouldBeFalse();

        var overrides = host.Services.GetRequiredService<IScheduleOverrideStore>();
        (await overrides.GetAsync("sales", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Clear_schedule_on_a_declared_schedule_tombstones_it_rather_than_reactivating()
    {
        using var host = await TestApp.StartAsync(AddScheduledCodeFirstReport);
        var client = host.GetTestClient();

        var response = await client.DeleteAsync("/api/reports/sales/schedule");
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("scheduleCron").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("scheduleOverridden").GetBoolean().ShouldBeTrue();

        var scheduler = host.Services.GetRequiredService<IRecurringReportScheduler>();
        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldNotContain("sales");

        var overrides = host.Services.GetRequiredService<IScheduleOverrideStore>();
        var entry = await overrides.GetAsync("sales", CancellationToken.None);
        entry.ShouldNotBeNull();
        entry!.Cron.ShouldBeNull();
    }

    [Fact]
    public async Task Clear_schedule_returns_404_for_an_unknown_report()
    {
        using var host = await TestApp.StartAsync(AddUnscheduledCodeFirstReport);
        var client = host.GetTestClient();

        var response = await client.DeleteAsync("/api/reports/does-not-exist/schedule");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dynamic_report_with_a_schedule_field_registers_it_immediately_on_creation()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddDynamicReports(o => o.Directory = Path.Join(Path.GetTempPath(), "nr-d41-" + Guid.NewGuid().ToString("N")));
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>()));
            services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
            services.AddNeoReportsInMemoryJobs();
            services.AddInMemoryScheduling();
        });
        var client = host.GetTestClient();

        const string document = """
        {
          "name": "dyn-scheduled",
          "source": { "type": "inmemory" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "csv" } ],
          "schedule": { "cron": "0 6 * * 1" }
        }
        """;

        var created = await client.PostAsync("/api/reports", JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(document)));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var scheduler = host.Services.GetRequiredService<IRecurringReportScheduler>();
        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldContain("dyn-scheduled");

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/dyn-scheduled", Json);
        detail.GetProperty("scheduleCron").GetString().ShouldBe("0 6 * * 1");
    }

    [Fact]
    public async Task Deleting_a_dynamic_report_removes_its_recurring_registration()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddDynamicReports(o => o.Directory = Path.Join(Path.GetTempPath(), "nr-d41-" + Guid.NewGuid().ToString("N")));
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>()));
            services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
            services.AddNeoReportsInMemoryJobs();
            services.AddInMemoryScheduling();
        });
        var client = host.GetTestClient();

        const string document = """
        {
          "name": "dyn-scheduled",
          "source": { "type": "inmemory" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "csv" } ],
          "schedule": { "cron": "0 6 * * 1" }
        }
        """;
        await client.PostAsync("/api/reports", JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(document)));

        var deleted = await client.DeleteAsync("/api/reports/dyn-scheduled");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var scheduler = host.Services.GetRequiredService<IRecurringReportScheduler>();
        (await scheduler.ListRegisteredNamesAsync(CancellationToken.None)).ShouldNotContain("dyn-scheduled");
    }
}
