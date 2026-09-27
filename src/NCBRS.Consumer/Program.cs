using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
// Everything this service serves beyond the liveness probe is reporting data.
// Reporting is a district officer's and the Ministry's to read, each within
// their own scope (ReportingScope); the DHIS2 export, a national dataset for an
// external system, is the Ministry's alone.
const string ReportingPolicy = "ncbrs-reporting";
const string ExportPolicy = "ncbrs-export";

var builder = WebApplication.CreateBuilder(args);

// The build-time OpenAPI generator runs this Program as Production and starts
// the host to read the document from it. It needs the endpoints and nothing
// that reaches a broker or enforces a deployment rule; the entry-assembly check
// is the one Microsoft documents for build-time document generation.
var generatingOpenApiDocument =
    System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

// Refused at startup outside Development unless the broker link is encrypted
// and authenticated: see KafkaOptions.SecurityProtocol.
builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName))
    .ValidateOnStart();

// Except for the build-time OpenAPI generator: see generatingOpenApiDocument.
if (!generatingOpenApiDocument)
{
    builder.Services.AddSingleton<IValidateOptions<KafkaOptions>>(
        new KafkaOptionsValidator(builder.Environment.IsDevelopment()));
}

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

// Refused, not warned: see WebClientCorsOptions.Refusal.
if (webCors.Refusal(builder.Environment.IsDevelopment()) is { } corsRefusal)
{
    throw new InvalidOperationException(corsRefusal);
}

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
// /api/dashboard/counties returned national vital statistics to an
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

// Refused, not warned: see KeycloakOptions.RequireHttpsMetadata.
if (!generatingOpenApiDocument
    && keycloak.RefusalOutsideDevelopment(builder.Environment.IsDevelopment()) is { } keycloakRefusal)
{
    throw new InvalidOperationException(keycloakRefusal);
}

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
{
    // Which county, within that, is ReportingScope's decision per request.
    authorization.AddPolicy(ReportingPolicy, policy =>
        policy.RequireRole(NcbrsRoles.DistrictOfficer, NcbrsRoles.MinistryAdmin));

    authorization.AddPolicy(ExportPolicy, policy =>
        policy.RequireRole(NcbrsRoles.MinistryAdmin));
});

// Not under the document generator. It starts the host, so this would open a
// Kafka consumer, fail for want of broker credentials, and stop the host while
// the generator was still reading from it -- a race every build ran, and lost
// on CI once.
if (!generatingOpenApiDocument)
{
    builder.Services.AddHostedService<BirthRecordDashboardConsumer>();
}

// Enums as names, matching the central API. A dashboard branching on a
// numeric 0 it has to look up is a contract bug waiting to happen.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // Numbers are numbers here, and the document should say so.
    //
    // ASP.NET's web JSON defaults set JsonNumberHandling.AllowReadingFromString,
    // so the serializer accepts "42" as well as 42 on the way in. The built-in
    // generator reports that faithfully as {"type":["integer","string"]}, and
    // because one component schema describes both directions, every integer
    // this service *returns* inherited it -- typing every count and every
    // median in the generated client as `number | string`.
    //
    // The central API keeps the leniency and corrects the document with
    // NumberSchemaTransformer, because it has request bodies and wants to be
    // forgiving about them. **This service has none: every endpoint is a GET.**
    // So the leniency buys it nothing, and the honest fix is not to be lenient
    // rather than to describe the leniency away. The document then states
    // exactly what the service does instead of understating it.
    //
    // Inert at runtime, and checkably so. AllowReadingFromString affects
    // reading, this service only writes through the HTTP JSON pipeline, and
    // the Kafka projector deserialises with JsonSerializer.Deserialize passing
    // no options -- so it uses JsonSerializerOptions.Default and never sees
    // this setting at all.
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});

// W8. Without a document these endpoints cannot be part of the generated web
// client, and the generated client is the reason React was chosen over Blazor
// (NCBRS-Web-Plan.md §2). Hand-written types for the dashboard would be
// exactly the drift that decision existed to prevent, in the part of the
// system whose numbers a Ministry acts on.
//
// The security transformer is not optional decoration: the generator infers
// nothing about authentication, so without it this document describes a
// service that needs no credentials while every reporting endpoint requires
// the policy above.
builder.Services.AddOpenApi(openApi =>
    openApi.AddOperationTransformer<ReportingSecurityTransformer>());

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
// Costs the client generator nothing, because it no longer reads this route
// at all: the document is written at build time to web/openapi by
// Microsoft.Extensions.ApiDescription.Server. This endpoint is now purely a
// developer convenience, and the build-time copy is what the client and CI
// use. The API is arranged identically, at its own /openapi/v1.json.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// This service owns the read model outright -- one writer, no other schema
// on it -- so unlike the registry there is nothing to race with.
using (var scope = app.Services.CreateScope())
{
    var readModel = scope.ServiceProvider.GetRequiredService<ReadModelDbContext>();

    readModel.Database.EnsureCreated();

    // EnsureCreated does nothing to a database that already exists, so a
    // store built by an earlier version keeps its old shape. Refused here
    // rather than discovered on the first dashboard query.
    ReadModelSchema.EnsureUsable(readModel);
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

// Every reporting endpoint resolves the caller's county first (ReportingScope):
// a district officer reads their own county only, the Ministry any or all.
app.MapGet("/api/dashboard/summary", ReportingEndpoints.SummaryAsync)
.WithName("GetDashboardSummary")
.Produces<DashboardSummary>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.Produces<ApiError>(StatusCodes.Status403Forbidden)
.RequireAuthorization(ReportingPolicy);

// The comparison across counties. A district officer gets their own row: the
// others are other counties' figures, however the request is phrased.
app.MapGet("/api/dashboard/counties", ReportingEndpoints.CountiesAsync)
.WithName("GetDashboardCounties")
.Produces<IReadOnlyList<CountySummary>>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.Produces<ApiError>(StatusCodes.Status403Forbidden)
.RequireAuthorization(ReportingPolicy);

// The headline figures bucketed by month, for charting a trend across the
// year. Same authorization and same date range as the summary; each bucket
// carries StillFilling so the client can mark the month that is not yet settled.
app.MapGet("/api/dashboard/trends", ReportingEndpoints.TrendsAsync)
.WithName("GetDashboardTrends")
.Produces<IReadOnlyList<TrendPoint>>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.Produces<ApiError>(StatusCodes.Status403Forbidden)
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
// The Ministry's: a national export to an external system, which the web
// plan's role table has always placed with the Ministry.
.RequireAuthorization(ExportPolicy);

app.MapGet("/api/dashboard/devices/silent", ReportingEndpoints.SilentDevicesAsync)
.WithName("GetSilentDevices")
.Produces<IReadOnlyList<SilentDevice>>()
.Produces<ApiError>(StatusCodes.Status400BadRequest)
.Produces<ApiError>(StatusCodes.Status403Forbidden)
.RequireAuthorization(ReportingPolicy);

app.Run();
