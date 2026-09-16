using Microsoft.EntityFrameworkCore;
using NCBRS.District.Data;
using NCBRS.District.Services;

// Tier 2: the district node (draft 6.2, 7.2).
//
// It exists so a village post's sync does not depend on the national tier
// being reachable at that moment. Batches are accepted, held, and forwarded
// verbatim when the link returns -- the node never reinterprets one, because
// a second place that understands registration is a second place it can
// drift.
//
// Deliberately modest: a mini-PC in a district office, one SQLite file, no
// copy of the register.
var builder = WebApplication.CreateBuilder(args);

// Enums travel as readable strings, matching the central API. A device
// branching on status must not have to know that Queued is 0.
builder.Services.AddControllers()
    .AddJsonOptions(json =>
        json.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
// No Swagger here: three endpoints, and adding a package to a district box
// for a UI nobody opens on it is not worth the dependency.

builder.Services.AddDbContext<DistrictDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
                      ?? "Data Source=ncbrs-district.db"));

builder.Services.Configure<CentralApiOptions>(
    builder.Configuration.GetSection(CentralApiOptions.SectionName));
builder.Services.Configure<ForwarderOptions>(
    builder.Configuration.GetSection(ForwarderOptions.SectionName));

builder.Services.AddHttpClient<CentralApiClient>((provider, http) =>
{
    var options = provider.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<CentralApiOptions>>().Value;

    http.Timeout = options.Timeout;
});

builder.Services.AddHostedService<BatchForwarder>();

var app = builder.Build();

// The node owns this database outright -- one service, one schema, no other
// writer -- so unlike the central tier there is nothing to race with.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<DistrictDbContext>().Database.EnsureCreated();
}

// No authentication of its own: a district node sits on a district office
// network and forwards to a centre that authenticates every batch properly.
// Putting a second identity system on a box in a district office would add a
// place credentials live without adding a check the centre does not already
// make. Device enrolment (WS-B9) is what should gate this hop, and it does
// not exist yet -- see CLAUDE.md.
app.MapControllers();

app.Run();
