using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Who caused a thermostat's target to change.</summary>
public enum ChangeOrigin
{
	/// <summary>The add-on itself, never a reason to transition.</summary>
	Self,

	/// <summary>The thermostat's own card or a device acting on it directly, so unambiguously a human.</summary>
	PhysicalDevice,

	/// <summary>A person driving the HA app or UI.</summary>
	HaUser,

	/// <summary>Another automation, a human's proxy and configurably treated as one.</summary>
	Automation,

	/// <summary>Nothing useful could be determined.</summary>
	Unknown
}

/// <summary>Decides whether a target changed because of the add-on or because of a person.</summary>
internal sealed class OverrideDetector
{
	private readonly GlobalConfig _global;
	private readonly bool? _roomTreatsAutomationsAsManual;
	private readonly Func<string?> _ownUserId;

	// roomTreatsAutomationsAsManual null follows the house; ownUserId is read at each change, as it is learned late.
	public OverrideDetector(
		GlobalConfig global,
		bool? roomTreatsAutomationsAsManual = null,
		Func<string?>? ownUserId = null)
	{
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_roomTreatsAutomationsAsManual = roomTreatsAutomationsAsManual;
		_ownUserId = ownUserId ?? (() => null);
	}

	public ChangeOrigin Classify(StateChange change)
	{
		ArgumentNullException.ThrowIfNull(change);

		return Classify(change.New?.Context);
	}

	public ChangeOrigin Classify(Context? context)
	{
		if (context is null)
			return ChangeOrigin.Unknown;

		if (_ownUserId() is { Length: > 0 } ourUserId &&
			string.Equals(context.UserId, ourUserId, StringComparison.Ordinal))
			return ChangeOrigin.Self;

		// Checked before the user id: a script started by a person carries both, and the script set the level.
		if (context.ParentId is not null)
			return ChangeOrigin.Automation;

		// No user and no parent: the device reported it itself.
		return context.UserId is null ? ChangeOrigin.PhysicalDevice : ChangeOrigin.HaUser;
	}

	/// <summary>Whether <paramref name="origin"/> is one the area must yield to.</summary>
	public bool IsManual(ChangeOrigin origin) => origin switch
	{
		ChangeOrigin.PhysicalDevice or ChangeOrigin.HaUser => true,
		// Read at the call, not copied at construction: the house value is the live document's.
		ChangeOrigin.Automation => _roomTreatsAutomationsAsManual ?? _global.TreatAutomationsAsManual,
		_ => false
	};
}
