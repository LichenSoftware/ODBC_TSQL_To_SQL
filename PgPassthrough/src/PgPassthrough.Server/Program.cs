using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;
using PgPassthrough.Server;
using PgPassthrough.Tds;
using PgPassthrough.Tds.Protocol;

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureServices((ctx, services) =>
    {
        // Configuration
        services.Configure<ServerConfiguration>(
            ctx.Configuration.GetSection(ServerConfiguration.SectionName));
        services.Configure<TdsServerOptions>(
            ctx.Configuration.GetSection(TdsServerOptions.SectionName));

        // TDS protocol layer
        services.AddSingleton<ICredentialValidator, ConfiguredCredentialValidator>();
        services.AddSingleton<TdsListener>();

        // Query handler — replaced in Phase 6 with the real pipeline
        services.AddSingleton<IQueryHandler, StubQueryHandler>();

        // Hosted service that owns TdsListener lifetime
        services.AddHostedService<TdsServerService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddDebug();
    })
    .Build();

await host.RunAsync();
