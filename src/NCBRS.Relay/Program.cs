using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCBRS.Data;
using NCBRS.Kafka;

// The producer side, deployed on its own.
//
// Kept separate from the API because the two fail and scale differently: a
// broker outage stalls delivery here without touching registrations, and the
// relay leases work so exactly one instance publishes a given message no
// matter how many API replicas are running.
var builder = Host.CreateApplicationBuilder(args);

// Refused, not warned: see NcbrsDatabase.RefusalOutsideDevelopment.
if (NcbrsDatabase.RefusalOutsideDevelopment(builder.Configuration, builder.Environment.IsDevelopment()) is { } databaseRefusal)
{
    throw new InvalidOperationException(databaseRefusal);
}

builder.Services.AddDbContext<NcbrsDbContext>(options =>
    NcbrsDatabase.Configure(options, builder.Configuration));

// Refused at startup outside Development unless the broker link is encrypted
// and authenticated: see KafkaOptions.SecurityProtocol.
builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<KafkaOptions>>(
    new KafkaOptionsValidator(builder.Environment.IsDevelopment()));

builder.Services.AddSingleton<KafkaOutboxTransport>();
builder.Services.AddHostedService<OutboxRelay>();

var host = builder.Build();

// Deliberately no migration on startup: schema changes belong to a
// deliberate deployment step, and three services racing to migrate one
// database is a good way to corrupt it.
host.Run();
