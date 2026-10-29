using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace Adaptive.UI;

/// <summary>Which colour a roll-call note carries.</summary>
public enum VerdictTone
{
	/// <summary>A neutral fact about the room.</summary>
	Info,

	/// <summary>Something needs a person: the amber family, used sparingly.</summary>
	Warn
}

/// <summary>One note on a roll-call row.</summary>
/// <param name="Why">What the (i) beside the note explains, or <c>null</c> where the note says it all.</param>
public sealed record Verdict(string Text, VerdictTone Tone, string? Why = null);

/// <summary>One proposed room as the roll-call draws it: the counts, the notes, and the gear it unfolds.</summary>
/// <remarks>
///     Assembled once per document, never on a tick, which would run <c>AreaEntityResolver</c> per room per
///     second. The <see cref="AreaConfig"/> is not carried, so a component cannot edit around the commit button.
/// </remarks>
/// <param name="Key">The room's identity across the board, the draft and the document.</param>
/// <param name="AreaId">The registry area id, or <c>null</c> for a room configured with explicit entities.</param>
/// <param name="Notes">The row's verdict chips, worst first. Empty means the row says <see cref="CommissioningVerdicts.ReadyWord"/>.</param>
/// <param name="Resolved">What discovery makes of the room, or <c>null</c> when it cannot resolve at all.</param>
/// <param name="Reading">The room's temperature as its thermostat last reported it, or <c>null</c>.</param>
public sealed record CommissioningRow(
	string Key,
	string? AreaId,
	string Name,
	int HeaterCount,
	int SensorCount,
	IReadOnlyList<Verdict> Notes,
	ResolvedRoom? Resolved,
	double? Reading);

/// <summary>
///     What each proposed room has to say for itself before it is switched on, and the lines under the table
///     that say once what a whole group of them has in common.
/// </summary>
/// <remarks>
///     Every note is read off the document, never re-derived. Nothing here decides whether a room would heat;
///     that is the engine's answer.
/// </remarks>
public static class CommissioningVerdicts
{
	/// <summary>The word a row with nothing to say carries.</summary>
	public const string ReadyWord = "Ready";

	/// <summary>The note a room with no temperature sensor carries, which is how the table tells the two kinds apart.</summary>
	public const string NoSensorNote = "no temperature sensor";

	/// <summary>The notes one proposed room earns, worst first.</summary>
	/// <param name="whyNotARoom">The resolver's own sentence for a room it refuses.</param>
	/// <returns>The notes, worst first. Empty means the row says <see cref="ReadyWord"/>.</returns>
	public static IReadOnlyList<Verdict> For(
		AreaConfig area,
		int heaterCount,
		int sensorCount,
		string? neighbour,
		string? whyNotARoom)
	{
		ArgumentNullException.ThrowIfNull(area);

		List<Verdict> notes = [];

		// Must come first and return: the remaining notes are about a room that runs.
		if (heaterCount == 0)
		{
			notes.Add(new Verdict("cannot be set up", VerdictTone.Warn, whyNotARoom ?? "No heater found in this room."));

			return notes;
		}

		if (sensorCount == 0)
			notes.Add(neighbour is { Length: > 0 }
				? new Verdict($"reads {neighbour}", VerdictTone.Info, NoSensorWhy)
				: new Verdict(NoSensorNote, VerdictTone.Warn, NoSensorWhy));

		return notes;
	}

	/// <summary>The line under the table about the rooms with no temperature sensor, said once for the house.</summary>
	public static string? NoSensorLine(int count) => count switch
	{
		<= 0 => null,
		1 => "1 room has no temperature sensor",
		_ => $"{count} rooms have no temperature sensor"
	};

	/// <summary>What the (i) beside <see cref="NoSensorLine"/> explains.</summary>
	public const string NoSensorWhy =
		"Such a room borrows a neighbour's reading where one is set, and otherwise is switched on or off by the mode. "
		+ "Give it a sensor in Home Assistant, or pick a neighbour on its own page.";

	/// <summary>What the commit button says, which is also the whole progress model.</summary>
	public static string CommitLabel(int picked) => picked switch
	{
		0 => "Switch on the rooms you pick",
		1 => "Switch on 1 room",
		_ => $"Switch on {picked} rooms"
	};

	/// <summary>The line under the button about the rooms left off, saying where they went.</summary>
	public static string? RestLine(int picked, int total)
	{
		int rest = total - picked;

		return rest switch
		{
			<= 0 => null,
			1 => "The other room stays off, under House",
			_ => $"The other {rest} stay off, under House"
		};
	}
}
