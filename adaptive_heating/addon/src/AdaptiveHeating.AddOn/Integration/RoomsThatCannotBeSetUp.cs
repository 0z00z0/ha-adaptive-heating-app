using System.Net;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.Page.Settings;

using AdaptiveLighting.Engine;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>Tells a person which switched-on rooms could not be set up, and when the settings file has errors.</summary>
/// <remarks>Lighting's orchestrator set-up report and its host's notifications, run from the pass over the rooms.</remarks>
internal sealed class RoomsThatCannotBeSetUp
{
	public const string SetupFailureTitle = "Adaptive Heating: rooms that could not be set up";

	public const string InvalidConfigTitle = "Adaptive Heating: the settings file has errors";

	public const string ForcedWriteTitle = "Adaptive Heating: the settings file was written with errors";

	private readonly IAreaSetupMemory? _memory;
	private readonly ITellsAPerson _person;
	private readonly ILogger<RoomsThatCannotBeSetUp> _logger;

	// What the file's card last said, and on which connection: a core restart clears every card.
	private string? _fileToldAs;
	private int _fileToldOn = -1;

	public RoomsThatCannotBeSetUp(IAreaSetupMemory? memory, ITellsAPerson person, ILogger<RoomsThatCannotBeSetUp> logger)
	{
		_memory = memory;
		_person = person ?? throw new ArgumentNullException(nameof(person));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Every switched-on room the resolver refuses, keyed by its room id, or by its area id where it has none.</summary>
	public static IReadOnlyList<AreaSetupFault> Faults(HeatingConfig document, HeatingDocument held, AreaEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(held);
		ArgumentNullException.ThrowIfNull(resolver);

		List<AreaSetupFault> faults = [];

		foreach (AreaConfig area in document.Areas)
		{
			if (resolver.TryResolve(area, document.Defaults, out _, out string? error))
				continue;

			// The exclude label and a room switched off are both the owner's act, so neither is a fault.
			if (resolver.IsExcludedArea(area.AreaId) || !area.Effective(document.Defaults).Enabled)
				continue;

			faults.Add(new AreaSetupFault(
				area.AreaId is { Length: > 0 } areaId ? held.RoomIn(areaId)?.Id ?? areaId : area.DisplayName,
				area.DisplayName,
				error ?? "It could not be set up."));
		}

		return faults;
	}

	/// <summary>Raises one card naming every standing fault, the first time each is seen.</summary>
	public async Task ReportAsync(HeatingConfig document, HeatingDocument held, AreaEntityResolver resolver, CancellationToken token)
	{
		IReadOnlyList<AreaSetupFault> failures = Faults(document, held, resolver);

		// Recorded even when nothing failed: forgetting a room that resolves is what lets a later fault notify.
		IReadOnlyList<AreaSetupFault> unreported = _memory?.Record(failures) ?? failures;

		if (unreported.Count == 0)
			return;

		foreach (AreaSetupFault failure in unreported)
			_logger.LogError("Room {Room} could not be set up: {Error}", failure.Area, failure.Problem);

		string body = string.Join("", failures.Select(failure =>
			$"<li>{WebUtility.HtmlEncode(failure.Area)}: {WebUtility.HtmlEncode(failure.Problem)}</li>"));

		await _person.NotifyAsync(
			"rooms_not_set_up",
			null,
			SetupFailureTitle,
			$"{failures.Count} of {document.ManagedAreaCount} rooms are switched on but could not be set up, so they are "
			+ $"not being managed:<ul>{body}</ul>",
			token).ConfigureAwait(false);
	}

	/// <summary>Raises the settings file's card while it has errors, once per connection and once per change.</summary>
	public async Task ReportTheFileAsync(HeatingDocumentHost host, int connection, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(host);

		(string Kind, string Title, string Message)? card = host.Fault is { Length: > 0 } || !host.LastValidation.IsValid
			? ("settings_file_has_errors", InvalidConfigTitle, host.LastValidation.ToHtml())
			: host.WrittenWithErrors is { } written
				? ("settings_file_written_with_errors", ForcedWriteTitle, written.ToHtml())
				: null;

		string? said = card is { } shown ? shown.Title + shown.Message : null;

		if (said is null || (string.Equals(said, _fileToldAs, StringComparison.Ordinal) && _fileToldOn == connection))
		{
			_fileToldAs = said;

			return;
		}

		if (await _person.NotifyAsync(card!.Value.Kind, null, card.Value.Title, card.Value.Message, token).ConfigureAwait(false))
		{
			_fileToldAs = said;
			_fileToldOn = connection;
		}
	}
}
