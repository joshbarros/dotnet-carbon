using Carbon.Shared;
using Carbon.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Carbon.Purchases;

public static class PurchasesModule
{
    public static IServiceCollection AddPurchasesModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Carbon") ?? throw new InvalidOperationException("Connection string 'Carbon' is not configured.");
        services.AddDbContext<PurchasesDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", PurchasesDbContext.SchemaName)));
        services.Configure<PurchaseOptions>(config.GetSection("Purchases"));
        services.AddScoped<PurchaseService>();
        services.AddScoped<PurchasesHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<PurchasesDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<PurchasesDbContext>>("purchases-db", tags: ["ready"]);

        messaging.AddOutbox<PurchasesDbContext>()
            .AddConsumer<PurchasesHandler>(Topology.PurchasesQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
