using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tickless;

/// <summary>
/// Dependency injection helpers for Tickless.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InProcessJobScheduler"/> as a singleton and as the host's
    /// <see cref="IHostedService"/>, exposing it through the <see cref="IJobScheduler"/> contract.
    /// </summary>
    /// <remarks>
    /// Register your handlers separately, before or after this call:
    /// <code>
    /// services.AddTickless();
    /// services.AddSingleton&lt;IJobHandler, MyJobHandler&gt;();
    /// </code>
    /// All registered <see cref="IJobHandler"/>s are resolved via DI and made available to the
    /// scheduler. On host startup, each handler's <see cref="IJobHandler.RecoverPendingJobsAsync"/>
    /// is invoked to rehydrate timers from domain state.
    /// </remarks>
    public static IServiceCollection AddTickless(this IServiceCollection services)
    {
        services.AddSingleton<InProcessJobScheduler>();
        services.AddSingleton<IJobScheduler>(sp => sp.GetRequiredService<InProcessJobScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<InProcessJobScheduler>());
        return services;
    }
}
