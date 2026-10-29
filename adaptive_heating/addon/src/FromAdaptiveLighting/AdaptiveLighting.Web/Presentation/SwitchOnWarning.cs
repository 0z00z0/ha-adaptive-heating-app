namespace AdaptiveLighting.Web.Presentation;

/// <summary>A heater the switch-on note names, with the reason it is named.</summary>
public sealed record SuspectHeater(string EntityId, string Name, string Reason);

/// <summary>One heater the room would command, as the switch-on note judges it.</summary>
public sealed record HeaterUnderReview(string EntityId, string Name, bool Answering);

/// <summary>What a room's switch has to say for itself the moment it is turned on.</summary>
public sealed record SwitchOnNote(
	string Lead,
	IReadOnlyList<SuspectHeater> Suspicious,
	IReadOnlyList<string> Others,
	string? Advice)
{
	/// <summary>Whether this note is a warning or a quiet remark: the amber edge, or the neutral one.</summary>
	// A room with no reading is worth the amber edge too: it is switched, not held at a temperature.
	public bool IsWarning => Suspicious.Count > 0 || Advice is not null;

	/// <summary>The heaters that were not flagged, as one sentence, or <c>null</c> when every heater was flagged.</summary>
	public string? OthersLine => Others.Count switch
	{
		0 => null,
		_ when Suspicious.Count == 0 => $"They are {Join(Others)}.",
		_ => $"The rest answer: {Join(Others)}."
	};

	/// <summary>"a", "a and b", "a, b and c".</summary>
	private static string Join(IReadOnlyList<string> names) => names.Count switch
	{
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		_ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
	};
}

/// <summary>The note a room shows when somebody switches it on: what "on" now reaches, and what of it is wrong.</summary>
/// <remarks>Advisory and after the fact. The switch works first and the note appears under it. The page owns dismissal.</remarks>
public static class SwitchOnWarning
{
	/// <summary>The note for a room that was just switched on, or <c>null</c> when there is nothing worth saying.</summary>
	/// <param name="hasAReading">Whether the room reads a temperature, its own or a neighbour's.</param>
	public static SwitchOnNote? For(string roomName, IReadOnlyList<HeaterUnderReview> commanded, bool hasAReading)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(roomName);
		ArgumentNullException.ThrowIfNull(commanded);

		List<SuspectHeater> suspicious = [];

		foreach (HeaterUnderReview heater in commanded)
			if (!heater.Answering)
				suspicious.Add(new SuspectHeater(heater.EntityId, heater.Name, "not answering in Home Assistant"));

		if (commanded.Count <= 1 && suspicious.Count == 0 && hasAReading)
			return null;

		HashSet<string> flagged = new(suspicious.Select(suspect => suspect.EntityId), StringComparer.Ordinal);

		return new SwitchOnNote(
			Lead(roomName, commanded.Count, suspicious.Count, hasAReading),
			suspicious,
			[.. commanded.Where(heater => !flagged.Contains(heater.EntityId)).Select(heater => heater.Name)],
			hasAReading ? null : Advice(roomName));
	}

	/// <summary>The first line: what the switch just did, and how much of it deserves a second look.</summary>
	private static string Lead(string roomName, int heaterCount, int suspectCount, bool hasAReading)
	{
		string commands = $"{roomName} on · {Count(heaterCount, "heater")}";

		if (!hasAReading)
			commands = $"{commands}, no reading";

		if (suspectCount == 0)
			return commands;

		if (suspectCount == heaterCount)
			return heaterCount == 1 ? $"{commands}, not answering" : $"{commands}, none answering";

		return $"{commands}, {suspectCount} not answering";
	}

	private static string Advice(string roomName) =>
		$"No temperature sensor in {roomName}, so the mode switches its heaters on and off. "
			+ "Name a neighbour under Heaters and sensors to heat on its reading instead.";

	private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
