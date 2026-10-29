using System.Collections.Concurrent;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Integration;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>Lighting's sorting of who changed what, for each thermostat's target, done as the change arrives.</summary>
// Per connection: the model, the own-user watch and the automation names all come and go with it.
internal sealed class ChangesAtTheThermostats : IStatePublisher
{
	private const double SameTemperature = 0.25;

	private readonly IStatePublisher _inner;
	private readonly ILogger _logger;
	private readonly ConcurrentDictionary<string, Arrival> _arrived = new(StringComparer.Ordinal);

	// Sorting only; whether an origin counts as a hand is the room's question, asked by the pass.
	private readonly OverrideDetector _sorts;

	private volatile Up? _up;
	private string? _ownUserId;

	public ChangesAtTheThermostats(IStatePublisher inner, ILogger<ChangesAtTheThermostats> logger)
	{
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_sorts = new OverrideDetector(new GlobalConfig(), ownUserId: () => OwnUserId);
	}

	/// <summary>The add-on's own Home Assistant user, or <c>null</c> until its first room event has come back.</summary>
	public string? OwnUserId => _up?.Own.UserId ?? _ownUserId;

	/// <inheritdoc/>
	public void Publish(AreaSnapshot snapshot)
	{
		if (_up is { } up)
			up.Own.Publish(snapshot);
		else
			_inner.Publish(snapshot);
	}

	public void CameUp(IHaContext house)
	{
		ArgumentNullException.ThrowIfNull(house);

		HasGone();

		OwnUser own = new(house, _inner, _logger);
		ChangeOriginNames names = new(house, _logger);
		IDisposable watching = house.StateAllChanges().SubscribeSafe(Arrived, _logger);

		_up = new Up(own, names, watching);
	}

	public void HasGone()
	{
		if (_up is not { } up)
			return;

		_up = null;
		_ownUserId = up.Own.UserId ?? _ownUserId;
		up.Watching.Dispose();
		up.Names.Dispose();
		up.Own.Dispose();
	}

	/// <summary>Remembers who moved a thermostat's target, sorted from that change's own context.</summary>
	internal void Arrived(StateChange change)
	{
		ArgumentNullException.ThrowIfNull(change);

		if (change.New is not { } now
			|| now.AttrString(HeatingActions.IntegrationVersionAttribute) is not { Length: > 0 }
			|| now.AttrDouble(HeatingActions.TargetTemperatureAttribute) is not { } target
			|| change.Old.AttrDouble(HeatingActions.TargetTemperatureAttribute) == target)
			return;

		_arrived[now.EntityId] = new Arrival(target, now.Context, _sorts.Classify(change));
	}

	/// <summary>Who set the target a thermostat holds: the change seen arriving, else the context its state stands in.</summary>
	public Who WhoSet(string entityId, double holding, Context? standing)
	{
		(ChangeOrigin origin, Context? context) =
			_arrived.TryGetValue(entityId, out Arrival? seen) && Math.Abs(seen.Target - holding) <= SameTemperature
				? (seen.Origin, seen.Context)
				: (_sorts.Classify(standing), standing);

		return new Who(
			origin,
			context,
			_up?.Names.Describe(origin, context) ?? Unnamed(origin),
			origin is ChangeOrigin.Automation ? _up?.Names.AutomationEntityId(context) : null);
	}

	// The connection's names are gone with it; the kind of origin is still known.
	private static string? Unnamed(ChangeOrigin origin) => origin switch
	{
		ChangeOrigin.Self => ChangeOriginNames.ByTheEngine,
		ChangeOrigin.PhysicalDevice => ChangeOriginNames.UnknownSource,
		ChangeOrigin.Automation => ChangeOriginNames.ByAnAutomation,
		ChangeOrigin.HaUser => ChangeOriginNames.ByAHomeAssistantUser,
		_ => null
	};

	/// <summary>The words for a Home Assistant user: the person carrying that user id now, named when shown.</summary>
	public string PersonWords(string userId) =>
		_up?.Names.Describe(ChangeOrigin.HaUser, new Context { UserId = userId }) ?? ChangeOriginNames.ByAHomeAssistantUser;

	/// <summary>The words for an automation: the entity called what it is called now.</summary>
	public string AutomationWords(string entityId) =>
		_up?.Names.AutomationWords(entityId) ?? ChangeOriginNames.ByAnAutomation;

	/// <summary>The origin, the context it was read from, lighting's words for it where it has any, and the automation's entity id.</summary>
	public sealed record Who(ChangeOrigin Origin, Context? Context, string? Words, string? AutomationId)
	{
		/// <summary>The user id of a person's change; <c>null</c> for every other origin.</summary>
		public string? UserId => Origin is ChangeOrigin.HaUser && Context?.UserId is { Length: > 0 } id ? id : null;

		/// <summary>What is kept: no words for a person or a known automation, who are kept by id and named when shown.</summary>
		public string? KeptWords => UserId is null && AutomationId is null ? Words : null;
	}

	private sealed record Arrival(double Target, Context? Context, ChangeOrigin Origin);

	private sealed record Up(OwnUser Own, ChangeOriginNames Names, IDisposable Watching);
}
