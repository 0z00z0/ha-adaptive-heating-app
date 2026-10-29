using AdaptiveHeating.Planner;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;
using SunTimes = Adaptive.Settings.SunTimes;

namespace AdaptiveHeating.Page.Settings;

/// <summary>Each room's mode temperatures: the value from the settings document, the stamp from the note.</summary>
public static class RoomTemperatures
{
	private static readonly ModeStamp NeverStamped = new(Stamp.Certain(default), ChangeOrigin.SettingsPage);

	/// <summary>What a store fills each read with: the document as it is now, the period in force, the sun today.</summary>
	public static Func<HeatingDocument, HeatingDocument> FromTheDocument(
		Func<HeatingConfig> document,
		HaCatalog catalog,
		Func<string?> optionInForce,
		TimeZoneInfo zone,
		TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(optionInForce);
		ArgumentNullException.ThrowIfNull(zone);
		ArgumentNullException.ThrowIfNull(clock);

		return held =>
		{
			HeatingConfig now = document();
			(TimeOnly? sunrise, TimeOnly? sunset) = catalog.SunTimesToday(zone);
			string? selectValue = Schedule.HomeAssistantDecides(now.Global)
				? catalog.CurrentStateOf(now.Global.PeriodSelect?.EntityId)
				: null;

			SunTimes sun = new(sunrise, sunset);
			DateTimeOffset at = clock.GetUtcNow();
			(TimePeriodConfig? period, DateTimeOffset? since) = InForce(now, sun, selectValue, at, zone);

			return Compose(held, now, period, since, optionInForce(), Next(now, sun, at, zone));
		};
	}

	/// <summary>The next period and its start, or <c>null</c> where the dropdown decides and no start can be known.</summary>
	public static PeriodSpan? Next(HeatingConfig document, SunTimes sun, DateTimeOffset now, TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (Schedule.HomeAssistantDecides(document.Global))
			return null;

		return Schedule.CalculatorFor(document.Periods, document.Global, sun, null, zone)
			.PeriodsAcross(now, now.AddDays(2))
			.FirstOrDefault(span => span.From > now);
	}

	/// <summary>The period in force and the instant it began, or a <c>null</c> start where the select named it.</summary>
	public static (TimePeriodConfig? Period, DateTimeOffset? Since) InForce(
		HeatingConfig document,
		SunTimes sun,
		string? selectValue,
		DateTimeOffset now,
		TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(document);

		PeriodInForce found = Schedule.InForceNow(document.Periods, document.Global, sun, now, selectValue, zone);

		if (found.Period is null || found.Rule is PeriodInForceRule.Select)
			return (found.Period, null);

		PeriodSpan? span = Schedule.CalculatorFor(document.Periods, document.Global, sun, null, zone)
			.PeriodsAcross(now.AddDays(-1), now)
			.LastOrDefault();

		return (found.Period, span?.From);
	}

	// optionInForce is the house-mode option in force now, by its value: its own period is the upper limit while it is Sleep.
	public static HeatingDocument Compose(
		HeatingDocument held,
		HeatingConfig document,
		TimePeriodConfig? inForce,
		DateTimeOffset? since,
		string? optionInForce,
		PeriodSpan? next = null)
	{
		ArgumentNullException.ThrowIfNull(held);
		ArgumentNullException.ThrowIfNull(document);

		HouseModeOptionConfig? option = document.Global.HouseMode?.OptionFor(optionInForce);
		TimePeriodConfig? upperLimit = option is { Kind: ModeKind.Sleep } ? HouseModeConfig.SleepClampPeriodFor(option, document.Periods) : null;

		return held with
		{
			PeriodInForce = inForce?.Name,
			PeriodKeyInForce = inForce?.Key,
			NextPeriodStartsAt = next?.From,
			NightSetbackInForce = option is { Kind: ModeKind.Sleep, NightSetback: true },
			Rooms =
			[
				.. held.Rooms.Select(room =>
				{
					AreaConfig? area = AreaOf(document, room.AreaId);

					return room with
					{
						ModeTemperatures = For(room, area, inForce, since, upperLimit),
						LevelsFromRoom = area is not null && inForce is not null ? RoomLevels.Rows([inForce], area)[0].IsOwn : null,
						NextPeriodTemperature = area is null ? null : ValueIn(area, next?.Period)
					};
				})
			]
		};
	}

	private static Dictionary<HeatingMode, Stamped<double>> For(
		RoomSlice room,
		AreaConfig? area,
		TimePeriodConfig? inForce,
		DateTimeOffset? since,
		TimePeriodConfig? upperLimit)
	{
		Dictionary<HeatingMode, Stamped<double>> values = [];

		if (area is null)
			return values;

		double? home = ValueIn(area, inForce);
		double? cap = ValueIn(area, upperLimit);

		Put(values, room, HeatingMode.Away, area.AwayTemperature, null);
		Put(values, room, HeatingMode.Boost, area.BoostTemperature, null);
		Put(values, room, HeatingMode.Home, home, since);
		Put(values, room, HeatingMode.Night, home is { } open && cap is { } limit ? Math.Min(open, limit) : home, since);

		return values;
	}

	// Through the levels editor's own rows, so the page and the engine read one period the same way.
	private static double? ValueIn(AreaConfig area, TimePeriodConfig? period) =>
		period is null ? null : RoomLevels.Rows([period], area)[0].Temperature;

	// A period beginning is a change of its own, as a schedule boundary is, so it outranks a hand change made before it.
	private static void Put(
		Dictionary<HeatingMode, Stamped<double>> values,
		RoomSlice room,
		HeatingMode mode,
		double? value,
		DateTimeOffset? since)
	{
		if (value is not { } celsius)
			return;

		ModeStamp stamped = room.ModeStamps.GetValueOrDefault(mode, NeverStamped);
		Stamp stamp = since is { } begun && begun > stamped.Stamp.SetAt
			? Stamp.Certain(begun)
			: stamped.Stamp;

		values[mode] = new Stamped<double>(celsius, stamp, stamped.Origin);
	}

	private static AreaConfig? AreaOf(HeatingConfig document, string? areaId) =>
		areaId is { Length: > 0 } ? document.Areas.FirstOrDefault(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal)) : null;
}
