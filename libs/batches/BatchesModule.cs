using Carbon.Shared;
using Carbon.Shared.Contracts;
using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Carbon.Batches;

public static class BatchesModule
{
    public static IServiceCollection AddBatchesModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Carbon") ?? throw new InvalidOperationException("Connection string 'Carbon' is not configured.");
        services.AddDbContext<BatchesDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", BatchesDbContext.SchemaName)));
        services.AddScoped<BatchesHandler>();
        services.AddScoped<IBatchCatalog, BatchCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<BatchesDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<BatchesDbContext>>("tonnes-db", tags: ["ready"]);

        messaging.AddOutbox<BatchesDbContext>()
            .AddConsumer<BatchesHandler>(Topology.BatchesQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
