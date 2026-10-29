using System.Reactive.Concurrency;

using Adaptive.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace Adaptive.Engine;

/// <summary>Registers the last-seen cache.</summary>
public static class LastSeenServiceCollectionExtensions
{
	/// <summary>Adds <see cref="IEntityLastSeen"/> and the hosted service that keeps it current.</summary>
	/// <remarks>
	///     Must be called after <see cref="HeatingConfigStore"/> is registered, which <c>AddLightingWeb</c> does: the
	///     cache file location is derived from the configuration document's resolved path, never configured.
	/// </remarks>
	public static IServiceCollection AddEntityLastSeen(this IServiceCollection services, LastSeenOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(services);

		LastSeenOptions resolved = options ?? new LastSeenOptions();

		services.AddSingleton(provider => new LastSeenStore(
			provider.GetRequiredService<HeatingConfigStore>().FilePath,
			provider.GetRequiredService<ILogger<LastSeenStore>>(),
			provider.GetService<StateStoreRegistry>()));

		// Registered as itself so the interface and the hosted service resolve to one instance, which the record needs
		// to accumulate from process start.
		services.AddSingleton(provider => new LastSeenService(
			provider.GetRequiredService<LastSeenStore>(),
			resolved,
			// The host's scheduler when it registers one, so tests and hosts share a clock.
			provider.GetService<IScheduler>() ?? DefaultScheduler.Instance,
			provider.GetRequiredService<ILoggerFactory>()));

		services.AddSingleton<IEntityLastSeen>(provider => provider.GetRequiredService<LastSeenService>());
		services.AddHostedService(provider => provider.GetRequiredService<LastSeenService>());

		return services;
	}
}
