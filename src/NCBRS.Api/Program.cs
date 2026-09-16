using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Web;
using NCBRS.Kafka;
using FluentValidation;
using NCBRS.Middleware;
using NCBRS.Services;
using NCBRS.Validation;

var builder = WebApplication.CreateBuilder(args);

// Enums travel as readable strings ("Female", not 1), matching how the
// database stores them (see NcbrsDbContext.OnModelCreating). Registering a
// birth with the wrong sex because a caller mixed up an integer code is
// exactly the kind of error a civil registry cannot absorb.
builder.Services.AddControllers(mvcOptions =>
    {
        // Ordered ahead of MVC's ModelStateInvalidFilter (order -2000), which
        // short-circuits invalid requests before ordinary action filters run.
        // Without this, a rejected payload would come back carrying a
        // server-generated id instead of the caller's -- and a validation
        // failure is precisely the response a client needs to correlate.
        mvcOptions.Filters.Add<RequestMetaActionFilter>(order: -3000);

        // Validation first: a payload that cannot succeed is rejected without
        // opening a transaction or consuming a transaction id.
        mvcOptions.Filters.Add<FluentValidationFilter>(order: -2900);

        mvcOptions.Filters.Add<IdempotencyFilter>(order: -2800);

        mvcOptions.Filters.Add<MetaEnvelopeFilter>();
    })
    .AddJsonOptions(jsonOptions => ConfigureNcbrsJson(jsonOptions.JsonSerializerOptions));

// The same configuration again, where the OpenAPI generator reads it.
//
// Not a duplicate registration by accident. MVC serializes through
// Microsoft.AspNetCore.Mvc.JsonOptions above; the built-in OpenAPI generator
// describes types through Microsoft.AspNetCore.Http.Json.JsonOptions, and
// consults nothing else. Without this it cannot see the converter, and
// describes every enum as a bare `integer` -- so `Sex` would document as a
// number with no allowed values while the API actually sends and accepts
// "Female". A client generated from that document would type `sex: number`,
// which is the integer-code mix-up the comment above says a civil registry
// cannot absorb, made permanent in every call site.
//
// Routed through one method so the two registrations cannot drift into two
// opinions about how enums travel.
builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
    ConfigureNcbrsJson(jsonOptions.SerializerOptions));

// Field rules live in FluentValidation validators (see Validation/), run by
// FluentValidationFilter. This still handles what happens before they get a
// chance: malformed JSON, or a value that can't bind to its CLR type.
builder.Services.Configure<ApiBehaviorOptions>(apiOptions =>
    apiOptions.InvalidModelStateResponseFactory = actionContext =>
        ApiErrors.Result(ApiErrors.FromModelState(actionContext.ModelState)));

builder.Services.AddValidatorsFromAssemblyContaining<RegisterBirthRequestValidator>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentRegistrarService>();
builder.Services.AddScoped<BirthRegistrationService>();
builder.Services.AddScoped<OutcomeService>();
builder.Services.AddScoped<CertificateService>();
builder.Services.AddScoped<AmendmentService>();
builder.Services.AddScoped<CertificateRevocationService>();
builder.Services.AddScoped<CertificateRevocationRecorder>();
builder.Services.AddScoped<LateRegistrationService>();
builder.Services.AddScoped<AnnulmentService>();
builder.Services.AddScoped<MaternalStatisticsService>();
builder.Services.AddScoped<ProvisionalRecordReconciler>();
builder.Services.AddSingleton<DuplicateMatcher>();
builder.Services.AddSingleton<DevicePinHasher>();
builder.Services.AddScoped<DuplicateDetectionService>();

// W1. Searching by name is a different act from looking up a BRN -- scoped to
// the caller's district, ministry exempt, and every search audited.
builder.Services.AddScoped<RecordSearchService>();

// One place deciding which district a caller may see. Two would drift, and
// the way they drift is that one endpoint stops enforcing the boundary.
builder.Services.AddScoped<DistrictScopeResolver>();

// The signing key is loaded once and held for the process: it is the most
// sensitive secret here, and re-reading it per request would multiply the
// places it can leak.
builder.Services.Configure<CertificateSigningOptions>(
    builder.Configuration.GetSection(CertificateSigningOptions.SectionName));
builder.Services.AddSingleton<CertificateSigner>();
builder.Services.Configure<StatutoryRegistrationOptions>(
    builder.Configuration.GetSection(StatutoryRegistrationOptions.SectionName));
builder.Services.Configure<CertificateRevocationOptions>(
    builder.Configuration.GetSection(CertificateRevocationOptions.SectionName));

// WS-B9. Bound eagerly rather than through IOptions so a malformed section
// fails at startup: a deployment that silently ran with enforcement off
// would look identical to one that never had it.
var deviceEnrolment = builder.Configuration.GetSection(DeviceEnrolmentOptions.SectionName)
                          .Get<DeviceEnrolmentOptions>()
                      ?? new DeviceEnrolmentOptions();

builder.Services.AddSingleton(deviceEnrolment);
builder.Services.AddScoped<DeviceEnrolmentService>();

// F4. Thresholds follow the facility's connectivity profile -- see
// DeviceSilenceOptions for why one number across the fleet is wrong.
var deviceSilence = builder.Configuration.GetSection(DeviceSilenceOptions.SectionName)
                        .Get<DeviceSilenceOptions>()
                    ?? new DeviceSilenceOptions();

builder.Services.AddSingleton(deviceSilence);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeviceSilenceMonitor>();
builder.Services.AddHostedService<DeviceSilenceSweepService>();

// Keycloak is the identity provider for both users (registrars, district
// officers, ministry admins) and clients (device app, scanner, dashboard).
// The API is a pure resource server: it never issues or stores credentials,
// it only validates the tokens Keycloak signed.
var keycloak = builder.Configuration.GetSection(KeycloakOptions.SectionName).Get<KeycloakOptions>()
               ?? new KeycloakOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwt =>
    {
        jwt.Authority = keycloak.Authority;
        jwt.Audience = keycloak.Audience;

        // Dev only: the compose stack serves Keycloak over plain HTTP. Any
        // real deployment must leave this on -- without TLS the signing keys
        // are fetched over a channel an attacker can rewrite.
        jwt.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;

        jwt.TokenValidationParameters.ValidateIssuer = true;
        jwt.TokenValidationParameters.ValidateAudience = true;
        jwt.TokenValidationParameters.ValidateLifetime = true;

        // A village post's clock drifts while it is offline for weeks; the
        // default five minutes is too tight for a device that has just
        // reconnected.
        jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(10);

        jwt.Events = new JwtBearerEvents
        {
            // The default challenge writes an empty body. Replaced so a 401
            // carries the same enveloped error shape as everything else.
            OnChallenge = async challenge =>
            {
                challenge.HandleResponse();

                var expired = challenge.AuthenticateFailure is SecurityTokenExpiredException;

                challenge.Response.Headers.WWWAuthenticate = expired
                    ? "Bearer error=\"invalid_token\", error_description=\"The token expired\""
                    : "Bearer error=\"invalid_token\"";

                await ApiErrorWriter.WriteAsync(
                    challenge.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    "Unauthorized.",
                    "authorization",
                    expired
                        ? "The access token has expired. Request a new one and retry."
                        : "A valid bearer token is required. Send it as: Authorization: Bearer <token>.");
            }
        };
    });

// W5. The management site is served from its own origin; without this the
// browser refuses every call before the API sees one.
//
// Two choices here are deliberate. **No credentials**: the access token
// travels in the Authorization header, never a cookie, and allowing
// credentials cross-origin is the one combination that would make CSRF
// against these endpoints possible. **Correlation headers exposed**: the API
// echoes the transaction id on every response so a caller can tie its request
// to the audit trail, and a response header the browser cannot read may as
// well not be sent.
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
        .AllowAnyMethod()
        .WithExposedHeaders(
            TransactionContext.TransactionIdHeader,
            TransactionContext.ClientIdHeader);
}));

builder.Services.AddSingleton<IClaimsTransformation, KeycloakRoleClaimsTransformation>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();

builder.Services.AddAuthorization(authorization =>
{
    authorization.AddPolicy(NcbrsRoles.CanRegisterBirths, policy =>
        policy.RequireRole(
            NcbrsRoles.FacilityRegistrar,
            NcbrsRoles.CommunityHealthWorker,
            NcbrsRoles.DistrictOfficer,
            NcbrsRoles.MinistryAdmin));

    authorization.AddPolicy(NcbrsRoles.CanReviewDuplicates, policy =>
        policy.RequireRole(NcbrsRoles.DistrictOfficer, NcbrsRoles.MinistryAdmin));

    authorization.AddPolicy(NcbrsRoles.CanApproveAmendments, policy =>
        policy.RequireRole(NcbrsRoles.DistrictOfficer, NcbrsRoles.MinistryAdmin));

    authorization.AddPolicy(NcbrsRoles.CanApproveLateRegistrations, policy =>
        policy.RequireRole(NcbrsRoles.DistrictOfficer, NcbrsRoles.MinistryAdmin));

    // Ministry only. A district officer may correct a record or rule on a
    // duplicate; withdrawing an identity altogether is a level above that.
    authorization.AddPolicy(NcbrsRoles.CanAnnulRegistrations, policy =>
        policy.RequireRole(NcbrsRoles.MinistryAdmin));

    // The district officers who issue and collect the tablets, not the
    // facility staff holding them.
    authorization.AddPolicy(NcbrsRoles.CanEnrolDevices, policy =>
        policy.RequireRole(NcbrsRoles.DistrictOfficer, NcbrsRoles.MinistryAdmin));

    // Nothing is reachable anonymously unless it opts out explicitly.
    authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddEndpointsApiExplorer();

// The built-in generator, matching NCBRS.Consumer. Both services now emit
// OpenAPI 3.1, so one generator can produce the web client from both
// documents -- which is why React was chosen over Blazor
// (NCBRS-Web-Plan.md §2) and was not actually true while this service
// emitted 3.0.4 through Swashbuckle and the consumer emitted 3.1.1.
//
// The two transformers replace what Swashbuckle did: one describes the
// response envelope and the correlation headers, the other declares the
// bearer scheme. Neither is inferred.
builder.Services.AddOpenApi(openApi =>
{
    openApi.AddOperationTransformer<TransactionHeaderTransformer>();
    openApi.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    openApi.AddSchemaTransformer<EnumSchemaTransformer>();
    openApi.AddSchemaTransformer<NumberSchemaTransformer>();
});

// SQLite here matches the district/facility tier from the NCBRS draft
// (Section 6.2) -- a lightweight, file-based store that works fully
// offline. Swap to UseNpgsql(...) or UseSqlServer(...) for the always-on
// central tier (Section 7.1).
builder.Services.AddDbContext<NcbrsDbContext>(options =>
    NcbrsDatabase.Configure(options, builder.Configuration));

// Events are staged in the database by the request and delivered to Kafka by
// NCBRS.Relay, which is deployed separately. The API therefore holds no Kafka
// producer or consumer at all: a broker outage cannot reach a registration,
// and neither worker can take the registration API down with it.
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher>();
builder.Services.AddHostedService<IdempotencyPurgeService>();

var app = builder.Build();

// A device signs the bytes it sent, so verification needs those bytes back
// after model binding has consumed the stream. Buffering is enabled only for
// requests that actually carry a device signature -- every other request
// keeps streaming its body as before, which matters because a sync batch
// from a post offline for three weeks is the largest payload the system
// takes.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey(DeviceSignature.HeaderName))
    {
        context.Request.EnableBuffering();
    }

    await next();
});

// Early in the pipeline so it times and tags the full request/response,
// including anything a later middleware or the endpoint itself does.
// Scoped to /api -- see RequestAuditMiddleware.
app.UseMiddleware<RequestAuditMiddleware>();

if (app.Environment.IsDevelopment())
{
    // Served at /openapi/v1.json, the same path as the consumer's. Note the
    // old Swashbuckle path was /swagger/v1/swagger.json -- anything pinned to
    // that needs repointing.
    //
    // AllowAnonymous is required, not incidental. UseSwagger() was middleware
    // and ran ahead of authorization; MapOpenApi() maps an endpoint, so the
    // FallbackPolicy above catches it and the document returns 401 -- to the
    // UI, to a developer, and to the client generator. It is Development-only
    // either way, so this exposes nothing that `dotnet run` did not already.
    app.MapOpenApi().AllowAnonymous();

    // Swashbuckle's UI package, kept for the explorer itself while its
    // generator goes. It reads whatever document it is pointed at, so it is
    // now rendering the built-in generator's output: what a developer
    // explores and what a client is generated from are the same document,
    // which they were not when two generators were in play.
    app.UseSwaggerUI(ui => ui.SwaggerEndpoint("/openapi/v1.json", "NCBRS API v1"));

    // Applies any pending migrations so a dev machine is always on the
    // current schema. Other tiers should apply migrations deliberately as a
    // deployment step (`dotnet ef database update`) rather than on startup:
    // several API instances racing to migrate a shared central database is
    // a good way to corrupt it, and a civil registry's schema changes
    // warrant a human looking at them first.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NcbrsDbContext>();
    db.Database.Migrate();
}

// Before authentication, deliberately. A CORS preflight is an unauthenticated
// OPTIONS request: placed after the auth middleware it would be rejected
// without the headers the browser needs, and every call from the site would
// fail as a CORS error rather than as the 401 it actually is.
app.UseCors(WebClientCorsOptions.PolicyName);

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// How this API serializes, in one place. Applied to MVC (what the API
// actually sends) and to the OpenAPI generator (what the document says it
// sends) so the two cannot disagree -- a document that misdescribes the wire
// format is worse than no document, because a generated client trusts it.
static void ConfigureNcbrsJson(JsonSerializerOptions options) =>
    options.Converters.Add(new JsonStringEnumConverter());
