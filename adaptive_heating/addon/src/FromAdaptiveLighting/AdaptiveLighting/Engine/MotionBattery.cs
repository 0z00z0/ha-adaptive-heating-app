using System.Globalization;

using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Engine;

/// <summary>The battery entities on a motion sensor's own device. At least one of the two is set.</summary>
public sealed record MotionBattery(string Sensor, string? LowEntity, string? LevelEntity)
{
	/// <summary>The level at or below which a battery counts as low when its device has no low flag.</summary>
	public const double LowAtPct = 20;

	/// <summary>The sensor as a low battery, or <c>null</c> while its battery is fine or cannot be read.</summary>
	// The device's own low flag decides where it has one. Unavailable or unknown reads as fine.
	public SensorBattery? LowFrom(string? lowState, string? levelState)
	{
		double? level = double.TryParse(levelState, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
			? parsed
			: null;

		bool low = LowEntity is not null
			? string.Equals(lowState, "on", StringComparison.OrdinalIgnoreCase)
			: level <= LowAtPct;

		return low ? new SensorBattery(Sensor, level) : null;
	}
}
