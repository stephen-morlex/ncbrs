using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Kafka;

// The producer side, deployed on its own.
//
// Kept separate from the API because the two fail and scale differently: a
// broker outage stalls delivery here without touching registrations, and the
// relay leases work so exactly one instance publishes a given message no
// matter how many API replicas are running.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<NcbrsDbContext>(options =>
    NcbrsDatabase.Configure(options, builder.Configuration));

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddSingleton<KafkaOutboxTransport>();
builder.Services.AddHostedService<OutboxRelay>();

var host = builder.Build();

// Deliberately no migration on startup: schema changes belong to a
// deliberate deployment step, and three services racing to migrate one
// database is a good way to corrupt it.
host.Run();
