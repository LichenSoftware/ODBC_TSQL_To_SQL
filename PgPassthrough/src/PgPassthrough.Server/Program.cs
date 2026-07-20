using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;
using PgPassthrough.Execution;
using PgPassthrough.Server;
using PgPassthrough.Tds;
using PgPassthrough.Tds.Protocol;
using PgPassthrough.Translation;

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

        // PostgreSQL backend (Npgsql DataSource with connection pooling)
        services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ServerConfiguration>>().Value;
            var backend = config.Backend;
            var connStr = new NpgsqlConnectionStringBuilder
            {
                Host = backend.Host,
                Port = backend.Port,
                Database = backend.Database,
                Username = backend.Username,
                Password = backend.Password,
                MinPoolSize = backend.MinPoolSize,
                MaxPoolSize = backend.MaxPoolSize,
                Timeout = backend.ConnectionTimeoutSeconds,
                CommandTimeout = backend.CommandTimeoutSeconds,
                SslMode = backend.SslMode ? SslMode.Require : SslMode.Disable
            };
            return NpgsqlDataSource.Create(connStr.ToString());
        });

        // Execution engine
        services.AddSingleton<IExecutionEngine>(sp =>
        {
            var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
            var config = sp.GetRequiredService<IOptions<ServerConfiguration>>().Value;
            var logger = sp.GetRequiredService<ILogger<NpgsqlExecutionEngine>>();
            return new NpgsqlExecutionEngine(dataSource, config.Backend, logger);
        });

        // SQL translator
        services.AddSingleton<ISqlTranslator, TSqlToPgTranslator>();

        // Procedure mapping store (custom translations from schema conversion session)
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ProcedureMappingStore>>();
            var mappingFile = Path.Combine(AppContext.BaseDirectory, "procedure-mappings.json");
            return new ProcedureMappingStore(mappingFile, logger);
        });

        // Query handler — the real pipeline
        services.AddSingleton<IQueryHandler, PipelineQueryHandler>();

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
