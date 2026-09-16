using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Services;
using NCBRS.Kafka;

// The consumer side, deployed on its own.
//
// Statistics aggregation scales with reporting demand rather than with
// registration traffic, and a consumer crash must never be able to stop a
// village post registering a birth.
//
// It both builds the projection and serves queries over it, because it is
// the one process that owns that store. Putting the dashboard endpoints on
// the registration API instead would have reporting load land on the service
// that registers births -- the exact thing plan F1 exists to prevent -- and
// a second process writing this schema would undo the single-writer property
// the projection relies on.
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));

// The projection's own store, separate from the registry. Draft 6.4.1 keeps
// the API the single writer to the system of record; a consumer writing back
// into it would make the register partly a function of its own event stream.
builder.Services.AddDbContext<ReadModelDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ReadModel")
                      ?? "Data Source=ncbrs-readmodel.db"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<BirthRecordProjector>();
builder.Services.AddScoped<DashboardQueryService>();

// E4. Data element UIDs and the org-unit map are instance-specific, so they
// are configuration; the suppression threshold is too, but only upward.
var dhis2 = builder.Configuration.GetSection(Dhis2ExportOptions.SectionName)
                .Get<Dhis2ExportOptions>()
            ?? new Dhis2ExportOptions();

builder.Services.AddSingleton(dhis2);
builder.Services.AddScoped<Dhis2ExportService>();
builder.Services.AddHostedService<BirthRecordDashboardConsumer>();

// Enums as names, matching the central API. A dashboard branching on a
// numeric 0 it has to look up is a contract bug waiting to happen.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// This service owns the read model outright -- one writer, no other schema
// on it -- so unlike the registry there is nothing to race with.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ReadModelDbContext>().Database.EnsureCreated();
}

// Whether the projection is keeping up. A dashboard served from a consumer
// that stalled three days ago looks exactly like a dashboard of a country
// where nothing happened, so this is the first thing to check and not an
// afterthought.
app.MapGet("/health", async (ReadModelDbContext db, CancellationToken cancellationToken) =>
{
    var lastEvent = await db.ProcessedEvents
        .OrderByDescending(processed => processed.ProcessedAtUtc)
        .Select(processed => (DateTime?)processed.ProcessedAtUtc)
        .FirstOrDefaultAsync(cancellationToken);

    return Results.Ok(new
    {
        status = "ok",
        lastEventProcessedAtUtc = lastEvent,
        registrations = await db.RegistrationFacts.CountAsync(cancellationToken),

        // Events waiting on a registration that has not arrived. Persistently
        // non-zero means the projection is missing registrations, which no
        // total on the dashboard would reveal on its own.
        heldAwaitingRegistration = await db.PendingEvents.CountAsync(cancellationToken)
    });
});

app.MapGet("/api/dashboard/summary", async (
    DateTime? from,
    DateTime? to,
    string? districtId,
    DashboardQueryService dashboard,
    CancellationToken cancellationToken) =>
{
    var (fromUtc, toUtc) = Range(from, to);

    return toUtc <= fromUtc
        ? Results.BadRequest(new { error = "'to' must be after 'from'." })
        : Results.Ok(await dashboard.SummaryAsync(fromUtc, toUtc, districtId, cancellationToken));
});

app.MapGet("/api/dashboard/districts", async (
    DateTime? from,
    DateTime? to,
    DashboardQueryService dashboard,
    CancellationToken cancellationToken) =>
{
    var (fromUtc, toUtc) = Range(from, to);

    return toUtc <= fromUtc
        ? Results.BadRequest(new { error = "'to' must be after 'from'." })
        : Results.Ok(await dashboard.DistrictsAsync(fromUtc, toUtc, cancellationToken));
});

// E4. Anonymised aggregate only: the unit of this payload is a
// district-month, never a person. See Dhis2ExportService for the three
// suppression rules and why aggregation alone is not anonymity.
app.MapGet("/api/exports/dhis2", async (
    string period,
    Dhis2ExportService exports,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await exports.ExportAsync(period, cancellationToken));
    }
    catch (ArgumentException invalid)
    {
        return Results.BadRequest(new { error = invalid.Message });
    }
});

app.MapGet("/api/dashboard/devices/silent", async (
    int? silentForDays,
    string? districtId,
    DashboardQueryService dashboard,
    CancellationToken cancellationToken) =>
{
    var days = silentForDays ?? 7;

    return days < 1
        ? Results.BadRequest(new { error = "'silentForDays' must be at least 1." })
        : Results.Ok(await dashboard.SilentDevicesAsync(days, districtId, cancellationToken));
});

app.Run();

// Defaults to the last full year of births. An unbounded default would scan
// the whole projection to answer a casual page load.
static (DateTime FromUtc, DateTime ToUtc) Range(DateTime? from, DateTime? to)
{
    var toUtc = AsUtc(to ?? DateTime.UtcNow.Date.AddDays(1));

    return (AsUtc(from ?? toUtc.AddYears(-1)), toUtc);
}

// "?from=2026-09-01" parses with no kind, and ToUniversalTime would read
// that as local time and shift it by the server's offset -- so a birth just
// after midnight would fall outside a query for its own month, differently
// depending on where the server happens to run. A date on the wire is UTC.
static DateTime AsUtc(DateTime value) => value.Kind switch
{
    DateTimeKind.Utc => value,
    DateTimeKind.Local => value.ToUniversalTime(),
    _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
};
