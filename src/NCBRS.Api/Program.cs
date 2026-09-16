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
    .AddJsonOptions(jsonOptions =>
        jsonOptions.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
builder.Services.AddSwaggerGen(swaggerOptions =>
{
    swaggerOptions.OperationFilter<TransactionHeaderOperationFilter>();

    // Lets the Swagger UI carry a bearer token, so the endpoints stay
    // explorable now that every one of them requires authentication.
    swaggerOptions.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Paste a Keycloak access token (without the \"Bearer \" prefix)."
    });

    // Swashbuckle 10 takes a factory so the requirement can reference the
    // document it is being added to.
    swaggerOptions.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("bearer", document)] = []
    });
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
    app.UseSwagger();
    app.UseSwaggerUI();

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

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
