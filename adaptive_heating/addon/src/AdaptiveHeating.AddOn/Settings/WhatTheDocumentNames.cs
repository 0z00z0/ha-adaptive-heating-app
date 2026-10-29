using Adaptive.Settings;

using AdaptiveHeating.AddOn.Integration;

using NetDaemon.HassModel;

namespace AdaptiveHeating.AddOn.Settings;

/// <summary>The entities the document points at, and what the document is checked against.</summary>
internal static class WhatTheDocumentNames
{
	// Every domain a house-wide setting can name. The validator asks the house for these and nothing else, so an
	// id in a domain nobody can choose would read as unknown rather than as unchecked.
	private static readonly string[] Domains =
		["input_select", "input_boolean", "sensor", "weather", "calendar", "switch", "climate", "sun"];

	/// <summary>Every entity id the document names, which is what the connection has to be watching.</summary>
	public static IReadOnlyCollection<string> Entities(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		HashSet<string> named = new(StringComparer.Ordinal);

		foreach (string? entityId in new[]
			{
				global.HouseMode?.EntityId,
				global.PeriodSelect?.EntityId,
				global.OutdoorSensor,
				global.ForecastEntity,
				global.GateEntity,
				global.ArrivalCalendar,
				global.RecordCalendar,
				global.SunEntity
			})
			if (entityId is { Length: > 0 })
				named.Add(entityId);

		return named;
	}

	/// <summary>What the house knows, or nothing known while the entity model's caches are not filled.</summary>
	public static ValidationContext CheckedAgainst(TheHouseAsItIs house, HeatingConfig document)
	{
		ArgumentNullException.ThrowIfNull(house);
		ArgumentNullException.ThrowIfNull(document);

		return new ValidationContext
		{
			// Null and empty mean different things: null leaves every referential check unrun, empty would fail
			// each one. Only null says the question was never put.
			KnownEntityIds = house.IsUp ? [.. Domains.SelectMany(house.EntityIdsInDomain)] : null,
			KnownAreaIds = house.Areas?.AreaIds,
			OurOwnEntityIds = house.IsUp ? OurOwn(house) : null,
			LivePeriodSelectOptions = ReadSelectOptions(house.Context, document.Global.PeriodSelect?.EntityId),
			KnownLabelIds = house.Registry is { } registry ? [.. registry.KnownLabels().Select(label => label.Id)] : null,
			DefaultGateEntity = AddOnSwitch.EntityId
		};
	}

	/// <summary>The live <c>options</c> of a select, or <c>null</c> when there is no select, no connection, or no readable attribute.</summary>
	private static IReadOnlyCollection<string>? ReadSelectOptions(IHaContext? ha, string? entityId)
	{
		if (ha is null || entityId is not { Length: > 0 })
			return null;

		try
		{
			IReadOnlyList<string> options = ha.GetState(entityId).AttrStringList("options");

			return options.Count > 0 ? options : null;
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection to HA completes.
			return null;
		}
	}

	/// <summary>This integration's own thermostats, which are rooms and never a room's heater.</summary>
	private static IReadOnlyCollection<string> OurOwn(TheHouseAsItIs house) =>
		[.. house.EntityIdsInDomain(HeatingActions.ClimateDomain)
			.Where(id => string.Equals(house.About(id).Platform, HeatingActions.Domain, StringComparison.OrdinalIgnoreCase))];
}
