using System.Reactive.Concurrency;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveHeating.Page.Tests;

/// <summary>The driving pass with everything under it, so a test names only what it is about.</summary>
internal sealed class ADrivenCabin
{
	private ADrivenCabin(
		DrivesTheRooms driver,
		TheArrivals arrivals,
		RecordsTheWarmUps record,
		Calendars calendars,
		Action saves)
	{
		_saves = saves;
		Driver = driver;
		Arrivals = arrivals;
		Record = record;
		Calendars = calendars;
	}

	private readonly Action _saves;

	public DrivesTheRooms Driver { get; }

	/// <summary>A save from the settings page: the same settings, read back as a new document.</summary>
	public void SavesTheDocument() => _saves();

	/// <summary>The arrivals as the last reading left them, which is how a test sees every one that was read.</summary>
	public TheArrivals Arrivals { get; }

	/// <summary>The warm-ups awaiting the record, which is how a test counts what is still to be written.</summary>
	public RecordsTheWarmUps Record { get; }

	public Calendars Calendars { get; }

	/// <param name="note">Where the warm-ups awaiting the record are kept. Nothing keeps them where it is null.</param>
	/// <param name="household">The household's clock. UTC where a test is not about a zone, so a wall time is the instant.</param>
	/// <param name="startsAt">What the house-mode rules read as now when they start, which clamps the selector's own stamp.</param>
	/// <param name="global">Sets whatever else the house's own settings carry, the House tab's choices.</param>
	/// <param name="defaults">What every room starts with, the House tab's defaults fold.</param>
	/// <param name="rooms">Adds the rooms' own settings, the room page's choices.</param>
	public static ADrivenCabin Over(
		AnOpenConnection open,
		ISettingsStore store,
		ReportsWhatHappened reports,
		OutdoorChoice? outdoor = null,
		IWarmUpNote? note = null,
		Func<string, string>? nameOf = null,
		TheHouseholdClock? household = null,
		HouseModeConfig? houseMode = null,
		DateTimeOffset? startsAt = null,
		Action<GlobalConfig>? global = null,
		AdaptiveLighting.Abstractions.IStatePublisher? publisher = null,
		ILogger<DrivesTheRooms>? logger = null,
		AreaSettings? defaults = null,
		params AreaConfig[] rooms)
	{
		ArgumentNullException.ThrowIfNull(open);

		Calendars calendars = new(open.Api);
		TheArrivals arrivals = new(calendars, reports, NullLogger<TheArrivals>.Instance);
		RecordsTheWarmUps record = new(note, calendars, reports, nameOf ?? (roomId => roomId));

		OutdoorChoice chosen = outdoor ?? OutdoorChoice.Nothing;

		HeatingConfig Read()
		{
			GlobalConfig house = new()
			{
				HouseMode = houseMode,
				OutdoorSensor = chosen.Sensors.FirstOrDefault(),
				ForecastEntity = chosen.Forecast
			};

			global?.Invoke(house);

			return new HeatingConfig { Global = house, Defaults = defaults ?? new AreaSettings(), Areas = [.. rooms] };
		}

		HeatingConfig document = Read();

		TheHouseholdClock clock = household ?? TheHouseholdClock.Of(TimeZoneInfo.Utc);

		TheHouseMode houseModeRules = new(
			() => open.Model.Context,
			() => document,
			new HistoricalScheduler(startsAt ?? ACabin.Typed),
			defaultGate: null,
			NullLoggerFactory.Instance,
			() => new SunTimes(null, null),
			lastPeriod: null,
			clock.Zone);

		DrivesTheRooms driver = new(
			store,
			open.Thermostats,
			open.Api,
			PlannerDefaults.Standard,
			clock,
			() => document,
			open.Changes,
			reports,
			arrivals,
			record,
			houseModeRules,
			logger ?? NullLogger<DrivesTheRooms>.Instance,
			publisher: publisher);

		return new ADrivenCabin(driver, arrivals, record, calendars, () => document = Read());
	}
}
