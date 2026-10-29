using System.Globalization;

namespace AdaptiveHeating.Planner;

/// <summary>
/// The household's own clock: the one zone every wall time is decided on and shown in.
/// </summary>
/// <remarks>
///     One of these is built where the zone is read, and the same one reaches the planner, the log and every time
///     the settings page draws. A second read anywhere is what puts two clocks in one process.
///     <para>
///         Nothing here reads an environment or a file, so a test names the zone it means. A stored instant is
///         never converted: <see cref="Wall"/> is a rendering of one, and the instant itself stays as it was
///         recorded.
///     </para>
/// </remarks>
public sealed class TheHouseholdClock
{
	private TheHouseholdClock(TimeZoneInfo zone, string? named, bool resolved)
	{
		Zone = zone;
		Named = named;
		Resolved = resolved;
	}

	/// <summary>The zone in use. Where <see cref="Resolved"/> is false this is the stand-in, not the household's.</summary>
	public TimeZoneInfo Zone { get; }

	/// <summary>The zone name this was given, or <c>null</c> where none was given at all.</summary>
	public string? Named { get; }

	/// <summary>Whether <see cref="Named"/> resolved. False means every wall time is wrong until it is put right.</summary>
	public bool Resolved { get; }

	/// <summary>A clock on a zone the caller names, which is what a host with no Supervisor under it has.</summary>
	public static TheHouseholdClock Of(TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(zone);

		return new TheHouseholdClock(zone, zone.Id, resolved: true);
	}

	/// <summary>The clock for a zone named by its IANA id, falling back to <paramref name="whenUnresolved"/>.</summary>
	/// <param name="named">The zone id, or <c>null</c> where nothing named one.</param>
	/// <param name="whenUnresolved">The zone to run on meanwhile. The result reports itself unresolved either way.</param>
	public static TheHouseholdClock From(string? named, TimeZoneInfo whenUnresolved)
	{
		ArgumentNullException.ThrowIfNull(whenUnresolved);

		if (named is not { Length: > 0 } wanted)
			return new TheHouseholdClock(whenUnresolved, null, resolved: false);

		try
		{
			return new TheHouseholdClock(TimeZoneInfo.FindSystemTimeZoneById(wanted), wanted, resolved: true);
		}
		// An absent zone database and an id nobody knows both arrive as the first of these, and data found but
		// unreadable as the second. Never a silent fall back: the caller reports an unresolved clock.
		catch (Exception failure) when (failure is TimeZoneNotFoundException or InvalidTimeZoneException)
		{
			return new TheHouseholdClock(whenUnresolved, wanted, resolved: false);
		}
	}

	/// <summary>The same instant read off the household's wall clock.</summary>
	public DateTimeOffset Wall(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);
}
