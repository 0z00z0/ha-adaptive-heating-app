using Adaptive.Settings;

namespace Adaptive.Engine;

/// <summary>Proposes rooms from the Home Assistant area registry, once, on a house that has none.</summary>
/// <remarks>The rules live in <see cref="AreaSetupService"/>; the timing and the once-only part live here.</remarks>
public sealed class AreaDiscoveryScheduler
{
	private readonly HeatingDocumentHost _host;
	private readonly ILogger<AreaDiscoveryScheduler> _logger;

	// Once per connection. The scan runs from the ordinary pass over the rooms, which runs every minute, so
	// without this a house whose Areas hold no labelled heater would propose nothing once a minute for ever.
	private int _scannedOnConnection = -1;

	public AreaDiscoveryScheduler(HeatingDocumentHost host, ILogger<AreaDiscoveryScheduler> logger)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Scans where the document has no room and has never been scanned, and writes what it proposes.</summary>
	/// <param name="timesOpened">Which connection this is, so one connection gets one scan.</param>
	/// <returns>The rooms proposed and written, empty where nothing was.</returns>
	// Never throws: the caller is a hosted service whose death would take the Home Assistant connection with it.
	/// <param name="ha">The house's entities, which name the people the first scan seeds.</param>
	public IReadOnlyList<string> ScanIfNeeded(int timesOpened, IAreaRegistry registry, AreaEntityResolver resolver, IHaContext? ha = null)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);

		if (_scannedOnConnection == timesOpened)
			return [];

		try
		{
			return Scan(timesOpened, registry, resolver, ha);
		}
		catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogWarning(
				failure,
				"Room discovery failed and was abandoned; the document is unchanged and the rooms are proposed again on the next connection.");

			return [];
		}
	}

	private IReadOnlyList<string> Scan(int timesOpened, IAreaRegistry registry, AreaEntityResolver resolver, IHaContext? ha)
	{
		// Re-read, never the document a caller is holding: a save may have landed since.
		HeatingConfig config = _host.Store.Load();

		if (config.Global.AreasAutoDiscovered || config.Areas.Count > 0)
		{
			_scannedOnConnection = timesOpened;

			return [];
		}

		// Empty scope: this path only runs on a document with no rooms, so the plan is entirely NewAreas.
		SetupPlan plan = AreaSetupService.Plan(config, registry, resolver, []);

		if (plan.NewAreas.Count == 0)
		{
			// The flag stays unset, so the next connection looks again.
			_scannedOnConnection = timesOpened;

			_logger.LogInformation(
				"No Home Assistant area holds a heater carrying the discovery label yet. Label one heater per room, then restart to look again.");

			return [];
		}

		AreaSetupService.Apply(config, plan);
		config.Global.AreasAutoDiscovered = true;

		// First setup only, never a re-run: an emptied list must stay empty next start.
		IReadOnlyList<string> seeded = ha is null ? [] : AreaSetupService.SeedPersons(config, ha);

		SaveResult saved = _host.Save(config);

		if (!saved.Written)
		{
			_logger.LogWarning(
				"The {Count} rooms discovered were not written: {Message}", plan.NewAreas.Count, saved.Message);

			return [];
		}

		_scannedOnConnection = timesOpened;

		IReadOnlyList<string> proposed = [.. plan.NewAreas.Select(area => area.AreaId!)];

		_logger.LogInformation(
			"Discovered {Count} rooms from the area registry ({Rooms}), all switched off. Switch the ones to run on under Rooms.",
			proposed.Count, string.Join(", ", proposed.Select(Described)));

		if (seeded.Count > 0)
			_logger.LogInformation(
				"The house will show who is home from {Count} people ({Persons}); the house-mode dropdown is what "
				+ "decides whether the house is away. Change who under Configuration → House.",
				seeded.Count, string.Join(", ", seeded));

		return proposed;
	}

	private static string Described(string areaId) =>
		AreaAutoDiscovery.RoleOf(areaId) is { } role ? $"{areaId} ({role})" : areaId;
}
