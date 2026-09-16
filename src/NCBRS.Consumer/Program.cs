using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NCBRS.Kafka;
using NCBRS.Middleware;
using NCBRS.Services;
using NCBRS.Web;

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
// The consumer has one policy: everything it serves beyond the liveness
// probe is reporting data, and reporting data is a district officer's and
// the Ministry's to read.
const string ReportingPolicy = "ncbrs-reporting";

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

// W5. The dashboard and export endpoints are called from the same site as the
// API, so the same origins apply — shared through WebClientCorsOptions rather
// than configured twice from memory. No credentials, for the same reason as
// the API: the token travels in the Authorization header, not a cookie.
var webCors = WebClientCorsOptions.From(builder.Configuration);

builder.Services.AddCors(cors => cors.AddPolicy(WebClientCorsOptions.PolicyName, policy =>
{
    if (webCors.AllowedOrigins.Length == 0)
    {
        return;
    }

    policy
        .WithOrigins(webCors.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod();
}));
// W9. These endpoints had no authentication at all: GET
// /api/dashboard/districts returned national vital statistics to an
// anonymous caller — including districts with a single live birth, which is
// precisely the small-cell disclosure the DHIS2 export goes to lengths to
// suppress. The export withheld that district; the dashboard handed it over.
//
// Same realm, same audience and the same role names as the registration API,
// shared through NCBRS.Core rather than restated here. A second service with
// its own idea of who a district officer is would drift, and the way it
// drifts is that one of them stops enforcing.
var keycloak = builder.Configuration.GetSection(KeycloakOptions.SectionName).Get<KeycloakOptions>()
               ?? new KeycloakOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwt =>
    {
        jwt.Authority = keycloak.Authority;
        jwt.Audience = keycloak.Audience;
        jwt.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;

        jwt.TokenValidationParameters.ValidateIssuer = true;
        jwt.TokenValidationParameters.ValidateAudience = true;
        jwt.TokenValidationParameters.ValidateLifetime = true;

        jwt.Events = new JwtBearerEvents
        {
            // Keycloak nests realm roles in a JSON claim that ASP.NET's role
            // machinery cannot read. Without this, RequireRole matches
            // nothing and every authorised caller is refused — and a policy
            // that matches nothing looks exactly like one that works.
            OnTokenValidated = context =>
            {
                if (context.Principal is not null)
                {
                    KeycloakRealmRoles.Apply(context.Principal);
                }

                return Task.CompletedTask;
            }
        };
    });

// The dashboard is a district officer's and the Ministry's view. A facility
// registrar's work is a record at a time, and national figures are not theirs
// to read — the web plan's role table says as much.
builder.Services.AddAuthorization(authorization =>
    authorization.AddPolicy(ReportingPolicy, policy =>
        policy.RequireRole(NcbrsRoles.DistrictOfficer, NcbrsRoles.MinistryAdmin)));

builder.Services.AddHostedService<BirthRecordDashboardConsumer>();

// Enums as names, matching the central API. A dashboard branching on a
// numeric 0 it has to look up is a contract bug waiting to happen.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// W8. Without a document these endpoints cannot be part of the generated web
// client, and the generated client is the reason React was chosen over Blazor
// (NCBRS-Web-Plan.md §2). Hand-written types for the dashboard would be
// exactly the drift that decision existed to prevent, in the part of the
// system whose numbers a Ministry acts on.
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseCors(WebClientCorsOptions.PolicyName);

// After CORS, before the endpoints: a preflight is an unauthenticated
// OPTIONS and must not be challenged.
app.UseAuthentication();
app.UseAuthorization();

// Served at /openapi/v1.json, and Development only — matching the
// registration API, which gates Swagger the same way. A published document is
// a map of the whole surface, and handing one to unauthenticated callers
// gives away more than it helps.
//
// The client generator runs the service in Development, so this costs it
// nothing. Note the API publishes its own document at
// /swagger/v1/swagger.json: two services, two documents, and the generator
// reads both.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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

    return Results.Ok(new ProjectionHealth(
        "ok",
        lastEvent,
        await db.RegistrationFacts.CountAsync(cancellationToken),

        // Events waiting on a registration that has not arrived. Persistently
        // non-zero means the projection is missing registrations, which no
        // total on the dashboard would reveal on its own.
        await db.PendingEvents.CountAsync(cancellationToken)));
})
.WithName("GetProjectionHealth")
.Produces<ProjectionHealth>();

app.MapGet("/api/dashboard/summary", async (
    DateTime? from,
    DateTime? to,
    string? districtId,
    DashboardQueryService dashboard,
    CancellationToken cancellationToken) =>
{
    var (fromUtc, toUtc) = Range(from, to);

    return toUtc <= fromUtc
        ? Results.BadRequest(new ApiError("'to' must be after 'from'."))
        : Results.Ok(await dashboard.SummaryAsync(fromUtc, toUtc, districtId, cancellationToken));
})
.WithName("GetDashboardSummary")
.Produces<DashboardSummary>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.RequireAuthorization(ReportingPolicy);

app.MapGet("/api/dashboard/districts", async (
    DateTime? from,
    DateTime? to,
    DashboardQueryService dashboard,
    CancellationToken cancellationToken) =>
{
    var (fromUtc, toUtc) = Range(from, to);

    return toUtc <= fromUtc
        ? Results.BadRequest(new ApiError("'to' must be after 'from'."))
        : Results.Ok(await dashboard.DistrictsAsync(fromUtc, toUtc, cancellationToken));
})
.WithName("GetDashboardDistricts")
.Produces<IReadOnlyList<DistrictSummary>>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.RequireAuthorization(ReportingPolicy);

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
        return Results.BadRequest(new ApiError(invalid.Message));
    }
})
.WithName("GetDhis2Export")
.Produces<Dhis2Export>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.RequireAuthorization(ReportingPolicy);

app.MapGet("/api/dashboard/devices/silent", async (
    int? silentForDays,
    string? districtId,
    DashboardQueryService dashboard,
    CancellationToken cancellationToken) =>
{
    var days = silentForDays ?? 7;

    return days < 1
        ? Results.BadRequest(new ApiError("'silentForDays' must be at least 1."))
        : Results.Ok(await dashboard.SilentDevicesAsync(days, districtId, cancellationToken));
})
.WithName("GetSilentDevices")
.Produces<IReadOnlyList<SilentDevice>>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.RequireAuthorization(ReportingPolicy);

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
