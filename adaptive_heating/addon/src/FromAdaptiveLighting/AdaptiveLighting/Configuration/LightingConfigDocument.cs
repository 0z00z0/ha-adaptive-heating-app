using AdaptiveLighting.Configuration;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Adaptive.Settings;

/// <summary>What reading a document produced: the bound configuration, and whether it had to be repaired to produce it.</summary>
public sealed record DocumentReadResult(HeatingConfig Config, bool Repaired, bool MintedStableKeys = false);

/// <summary>Raised when an <c>AdaptiveHeating.yaml</c> document cannot be read or written; the message is written for the web UI.</summary>
public sealed class HeatingConfigException : Exception
{
	public HeatingConfigException(string message) : base(message)
	{
	}

	public HeatingConfigException(string message, Exception innerException) : base(message, innerException)
	{
	}

	public HeatingConfigException()
	{
	}
}

/// <summary>
///     Turns a <see cref="HeatingConfig"/> into the text of an <c>AdaptiveHeating.yaml</c> document and back. Pure
///     text in, pure text out, so the whole round trip is unit-testable.
/// </summary>
/// <remarks>
///     The only loader, so the engine and the UI cannot end up as two parsers disagreeing about one file. Comments in
///     the file do not survive a write: YamlDotNet emits a fresh document, and <see cref="Header"/> says so.
/// </remarks>
public static class HeatingConfigDocument
{
	/// <summary>The document's single top-level key: the fully qualified name of <see cref="HeatingConfig"/>.</summary>
	public const string RootKey = "Adaptive.Settings.HeatingConfig";

	private const string Header =
		"""
		# ============================================================================
		#  Adaptive Heating settings.
		# ============================================================================
		#
		#  This file may be edited by hand. Saving from the heating settings page
		#  rewrites it, and any comments added here are lost at that save.
		#
		#  The top-level key must stay exactly as written.
		# ============================================================================

		""";

	/// <summary>Serialises <paramref name="config"/> to the full text of a document, header comment included.</summary>
	/// <param name="keepUnknownKeysFrom">
	///     The document as it stands on disk, whose keys this build does not know are carried into the text
	///     written. <c>null</c> writes the known keys alone.
	/// </param>
	/// <remarks>
	///     A key nothing here reads is never dropped by a save: an older build must not destroy what a newer one
	///     wrote, and the file is the one durable record of what a person typed. Retiring a key therefore needs an
	///     explicit drop, in <see cref="Retired"/>, and not merely a property going away.
	/// </remarks>
	public static string Serialize(HeatingConfig config, string? keepUnknownKeysFrom = null)
	{
		ArgumentNullException.ThrowIfNull(config);

		Dictionary<string, HeatingConfig> document = new(StringComparer.Ordinal) { [RootKey] = config };

		// OmitNull, not OmitDefaults. On an AreaConfig null means "inherit Defaults", but an area that sets
		// Enabled: false has said something and OmitDefaults would delete it.
		string written = Rendered(document, DefaultValuesHandling.OmitNull);

		return Header + (keepUnknownKeysFrom is { Length: > 0 } standing
			? WithUnknownKeysOf(standing, written, Rendered(document, DefaultValuesHandling.Preserve))
			: written);
	}

	/// <summary>Keys this build has retired, which a save no longer carries back from the file.</summary>
	// Matched by name alone, which holds while each name is used nowhere else in the document.
	internal static readonly HashSet<string> Retired = new(StringComparer.Ordinal) { "SmoothTransitions", "BlendMinutes", "PresenceSelector" };

	private static string Rendered(Dictionary<string, HeatingConfig> document, DefaultValuesHandling defaults) =>
		new SerializerBuilder()
			.ConfigureDefaultValuesHandling(defaults)
			.Build()
			.Serialize(document);

	/// <summary>The text of <paramref name="written"/> with every key of <paramref name="standing"/> it lacks put back.</summary>
	/// <param name="everyKey">The same configuration with nothing omitted, so its keys are the ones this build writes.</param>
	/// <remarks>Returns <paramref name="written"/> unchanged where the standing text cannot be parsed.</remarks>
	private static string WithUnknownKeysOf(string standing, string written, string everyKey)
	{
		YamlStream before = new();
		YamlStream after = new();
		YamlStream shape = new();

		try
		{
			before.Load(new StringReader(standing));
			after.Load(new StringReader(written));
			shape.Load(new StringReader(everyKey));
		}
		catch (YamlException)
		{
			return written;
		}

		if (SectionOf(before) is not { } was || SectionOf(after) is not { } now || SectionOf(shape) is not { } writes)
			return written;

		PutBackMissing(was, now, writes);

		StringWriter writer = new();
		after.Save(writer, assignAnchors: false);

		return writer.ToString();
	}

	/// <summary>The value node of this document's own top-level key, matched the way <see cref="Deserialize"/> matches it.</summary>
	private static YamlNode? SectionOf(YamlStream stream) =>
		stream.Documents
			.Select(document => document.RootNode)
			.OfType<YamlMappingNode>()
			.SelectMany(mapping => mapping.Children)
			.Where(child => child.Key is YamlScalarNode { Value: { Length: > 0 } key }
				&& (string.Equals(key, RootKey, StringComparison.OrdinalIgnoreCase)
					|| key.EndsWith("." + nameof(HeatingConfig), StringComparison.OrdinalIgnoreCase)))
			.Select(child => child.Value)
			.FirstOrDefault();

	/// <summary>Copies every key of <paramref name="was"/> that this build does not write into <paramref name="now"/>, recursively.</summary>
	/// <param name="writes">The matching node of the nothing-omitted render; a key absent from it is one nothing here writes.</param>
	/// <remarks>
	///     Ordinal, because YamlDotNet binds ordinally: a key differing only in case binds to nothing and so is one
	///     this build does not know. List entries are paired by their own id and never by position, so reordering a
	///     list cannot move one entry's unknown key onto another.
	/// </remarks>
	private static void PutBackMissing(YamlNode was, YamlNode now, YamlNode? writes)
	{
		switch (was, now)
		{
			case (YamlMappingNode old, YamlMappingNode fresh):
				foreach (KeyValuePair<YamlNode, YamlNode> child in old.Children)
				{
					if (child.Key is not YamlScalarNode { Value: { Length: > 0 } key })
						continue;

					YamlNode? emitted = writes is YamlMappingNode shape ? Child(shape, key) : null;

					if (Child(fresh, key) is { } kept)
						PutBackMissing(child.Value, kept, emitted);
					else if (emitted is null && !Retired.Contains(key))
						fresh.Children.Add(new YamlScalarNode(key), child.Value);
				}

				break;

			case (YamlSequenceNode old, YamlSequenceNode fresh):
				foreach (YamlNode entry in old.Children)
					if (IdOf(entry) is { } id && fresh.Children.FirstOrDefault(candidate => IdOf(candidate) == id) is { } same)
						PutBackMissing(entry, same, EntryOf(writes, id));

				break;
		}
	}

	private static YamlNode? EntryOf(YamlNode? writes, string id) =>
		writes is YamlSequenceNode sequence
			? sequence.Children.FirstOrDefault(candidate => IdOf(candidate) == id)
			: null;

	private static YamlNode? Child(YamlMappingNode mapping, string key) =>
		mapping.Children
			.Where(child => child.Key is YamlScalarNode scalar
				&& string.Equals(scalar.Value, key, StringComparison.Ordinal))
			.Select(child => child.Value)
			.FirstOrDefault();

	/// <summary>What a list entry is identified by, or <c>null</c> where it carries no id.</summary>
	private static string? IdOf(YamlNode entry) =>
		entry is not YamlMappingNode mapping
			? null
			: (Child(mapping, nameof(AreaConfig.AreaId)) ?? Child(mapping, nameof(TimePeriodConfig.Id))) is YamlScalarNode { Value: { Length: > 0 } id }
				? id
				: null;

	/// <summary>Parses the text of a configuration document.</summary>
	/// <exception cref="HeatingConfigException">The text is not YAML, or carries no <see cref="RootKey"/> section.</exception>
	public static DocumentReadResult Deserialize(string yaml, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(yaml);

		// IgnoreUnmatchedProperties, so a key this build does not know is silence and not an error: a stale key
		// cannot brick the page that exists to remove it, and a key a newer build wrote survives being read here.
		IDeserializer deserializer = new DeserializerBuilder()
			.IgnoreUnmatchedProperties()
			.Build();

		Dictionary<string, HeatingConfig?>? document;

		try
		{
			document = deserializer.Deserialize<Dictionary<string, HeatingConfig?>>(yaml);
		}
		catch (YamlException exception)
		{
			throw new HeatingConfigException(
				$"The configuration file is not valid YAML: {exception.Message}", exception);
		}

		if (document is null || document.Count == 0)
			throw new HeatingConfigException("The configuration file is empty.");

		// The fallback matches any key naming HeatingConfig whatever namespace produced it, so renaming the
		// namespace does not orphan every file on disk. Reading is forgiving, writing is not: Serialize always
		// emits RootKey.
		string? match =
			document.Keys.FirstOrDefault(key => string.Equals(key, RootKey, StringComparison.OrdinalIgnoreCase))
			?? document.Keys.FirstOrDefault(key => key.EndsWith("." + nameof(HeatingConfig), StringComparison.OrdinalIgnoreCase));

		if (match is null)
			throw new HeatingConfigException(
				$"The configuration file has no '{RootKey}' section. It has: {string.Join(", ", document.Keys)}.");

		// A present-but-empty section parses to null, meaning all defaults and no areas.
		HeatingConfig config = document[match] ?? new HeatingConfig();

		bool repaired = RepairStructuralNulls(config, logger);

		// After the repair, so the migration walks lists it can rely on.
		bool mintedStableKeys = StableKeyMigration.Apply(config);

		if (mintedStableKeys)
			logger?.LogInformation(
				"The configuration document referred to periods and house modes by name. Each has been given an id "
				+ "and every reference repointed at it, so renaming one no longer breaks what pointed at it.");

		return new DocumentReadResult(config, repaired, mintedStableKeys);
	}

	/// <summary>Puts back the collections and sub-objects the model says are never <c>null</c>.</summary>
	/// <remarks>
	///     A bare <c>Areas:</c> line is valid YAML for "this key is null", and YamlDotNet assigns that null over the
	///     property initialiser. Unrepaired it throws out of the host's reload, which is documented never to throw.
	///     Blank list elements are dropped, so a stray <c>-</c> does not become a nameless room.
	/// </remarks>
	private static bool RepairStructuralNulls(HeatingConfig config, ILogger? logger)
	{
		bool repaired = config.Global is null || config.Defaults is null;

		config.Global ??= new GlobalConfig();
		config.Defaults ??= new AreaSettings();

		GlobalConfig global = config.Global;

		repaired |= NullSafeList(config.Periods, out List<TimePeriodConfig> periods);
		config.Periods = periods;

		repaired |= NullSafeList(config.Areas, out List<AreaConfig> areas);
		config.Areas = areas;

		repaired |= NullSafeList(global.Persons, out List<string> persons);
		global.Persons = persons;

		if (global.HouseMode is { } houseMode)
		{
			repaired |= NullSafeList(houseMode.Options, out List<HouseModeOptionConfig> options);
			houseMode.Options = options;

			foreach (HouseModeOptionConfig option in options)
			{
				repaired |= NullSafeList(option.ActivateWhileOn, out List<string> activateWhileOn);
				option.ActivateWhileOn = activateWhileOn;

				repaired |= NullSafeList(option.ResetPresenceSensors, out List<string> resetPresenceSensors);
				option.ResetPresenceSensors = resetPresenceSensors;
			}
		}

		if (global.PeriodSelect is { } periodSelect)
		{
			repaired |= NullSafeList(periodSelect.Options, out List<PeriodSelectOptionConfig> periodOptions);
			periodSelect.Options = periodOptions;
		}

		// An area's entity lists are nullable by design (null means "let discovery find them"), so only their
		// contents are repaired, never their absence.
		foreach (AreaConfig area in areas)
			repaired |= DropBlanks(area.ExcludeEntities);

		if (repaired)
			logger?.LogWarning(
				"The configuration document has empty sections or blank list entries — a key with nothing under it, "
				+ "or a bare '-'. They have been read as absent so the rest of the file still loads; the next save "
				+ "writes the document without them.");

		return repaired;
	}

	/// <summary>Whether <paramref name="list"/> needed repair, and the list to use instead: never null, never holding a null.</summary>
	private static bool NullSafeList<T>(List<T>? list, out List<T> repaired) where T : class
	{
		if (list is null)
		{
			repaired = [];
			return true;
		}

		repaired = list;

		return DropBlanks(list);
	}

	/// <summary>Removes any null element from <paramref name="list"/>, in place. <c>true</c> when one was there.</summary>
	private static bool DropBlanks<T>(List<T>? list) where T : class =>
		list is not null && list.RemoveAll(item => item is null) > 0;
}
