using System.Reactive.Linq;

using NetDaemon.HassModel;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>Brings NetDaemon's entity model up over the add-on's own connection, once per connection.</summary>
// The caches read the client's connection as it was when they were filled, so a new one gets a scope of its own.
internal sealed class StartsTheEntityModel : BackgroundService
{
	private readonly CoreConnection _connection;
	private readonly TheHouseAsItIs _house;
	private readonly WhatIsHidden _hidden;
	private readonly Func<IReadOnlyCollection<string>> _alsoNamed;
	private readonly IServiceScopeFactory _scopes;
	private readonly ILogger<StartsTheEntityModel> _logger;
	private readonly Action _cameUp;
	private readonly Action _hasGone;

	public StartsTheEntityModel(
		CoreConnection connection,
		TheHouseAsItIs house,
		WhatIsHidden hidden,
		Func<IReadOnlyCollection<string>> alsoNamed,
		IServiceScopeFactory scopes,
		ILogger<StartsTheEntityModel> logger,
		Action? cameUp = null,
		Action? hasGone = null)
	{
		_cameUp = cameUp ?? (() => { });
		_hasGone = hasGone ?? (() => { });
		_connection = connection ?? throw new ArgumentNullException(nameof(connection));
		_house = house ?? throw new ArgumentNullException(nameof(house));
		_hidden = hidden ?? throw new ArgumentNullException(nameof(hidden));
		_alsoNamed = alsoNamed ?? throw new ArgumentNullException(nameof(alsoNamed));
		_scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	protected override async Task ExecuteAsync(CancellationToken stopping)
	{
		while (!stopping.IsCancellationRequested)
		{
			if (!await UntilAsync(() => _connection.IsLive, stopping).ConfigureAwait(false))
				return;

			try
			{
				await ReadTheHouseAsync(stopping).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception failure)
			{
				_logger.LogWarning(failure, "The entity model could not be brought up over this connection.");
			}
			finally
			{
				_house.HasGone();
				_hasGone();
			}
		}
	}

	private async Task ReadTheHouseAsync(CancellationToken stopping)
	{
		// The scoped provider implements IAsyncDisposable only, so a plain scope throws on the way out.
		await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

		IHaContext house = scope.ServiceProvider.GetRequiredService<IHaContext>();
		IHaRegistry registry = scope.ServiceProvider.GetRequiredService<IHaRegistry>();

		await scope.ServiceProvider
			.GetRequiredService<ICacheManager>()
			.InitializeAsync(stopping)
			.ConfigureAwait(false);

		await _hidden.ReadAsync(stopping).ConfigureAwait(false);

		_house.CameUp(house, registry, _hidden);
		_cameUp();

		using IDisposable watching = house
			.StateAllChanges()
			.Where(change => WhatTheHeatingReads.Reads(change, _alsoNamed()))
			.Subscribe(_ => _connection.Changed(), _ => { }, () => { });

		_logger.LogInformation(
			"The entity model is up, with {Count} entities and the registries behind them.",
			house.GetAllEntities().Count());

		// The house has been read, so a pass may now run on it.
		_connection.Changed();

		await UntilAsync(() => !_connection.IsLive, stopping).ConfigureAwait(false);
	}

	// Polled, not awaited on an event: one event covers both directions, so a missed edge would leave the model
	// down for the life of the connection.
	private static async Task<bool> UntilAsync(Func<bool> ready, CancellationToken stopping)
	{
		while (!ready())
		{
			try
			{
				await Task.Delay(TimeSpan.FromMilliseconds(100), stopping).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
		}

		return true;
	}
}
