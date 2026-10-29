namespace AdaptiveHeating.Planner;

/// <summary>Why a room is given no setback tonight. Ordinals are written nowhere.</summary>
public enum NoSetback
{
	OffForThisRoom,
	NotThisMode,
	MasterSwitchOff,
	RoomSwitchedOff,
	Paused,
	Held,
	NoSensor,
	NoReading,
	NoPowerMeter,
	Fault,
	RateNotLearnt,
	LossNotLearnt,
	NoOutdoorFigure,
	HeaterTooSmall,
	NoPeriodAhead,
	TooLittleTime,
	TooLittleToGain,
}

/// <summary>Everything the setback for one room is decided from, as one pass reads it.</summary>
public sealed record NightSetbackInputs
{
	public bool SetbackOnForTheRoom { get; init; }

	/// <summary>The house-mode option in force is marked for the setback, and the room runs it.</summary>
	public bool MarkedModeInForce { get; init; }

	public bool MasterSwitchOn { get; init; } = true;

	public bool RoomEnabled { get; init; } = true;

	public bool Paused { get; init; }

	public bool Held { get; init; }

	/// <summary>The room reads its own sensor, never a neighbour's.</summary>
	public bool HasOwnSensor { get; init; }

	public bool HasAPowerMeter { get; init; }

	public bool FaultReported { get; init; }

	public double? RoomTemperature { get; init; }

	/// <summary>What the room holds tonight without a setback.</summary>
	public required double NightTemperature { get; init; }

	/// <summary>What the next period gives the room, which the restart warms it to.</summary>
	public double? PeriodTemperature { get; init; }

	public required double LowestNightTemperature { get; init; }

	public double? FrostLimit { get; init; }

	/// <summary>The lowest the room's thermostat underneath accepts; <c>null</c> for a room heated through a switch.</summary>
	public double? ThermostatMinimum { get; init; }

	/// <summary>The learnt warming rate, or <c>null</c> where no rise is measured.</summary>
	public double? WarmingRate { get; init; }

	public double? OutdoorTemperature { get; init; }

	public HeatLossEstimate? HeatLoss { get; init; }

	public TimeSpan? UntilThePeriodStarts { get; init; }
}

/// <summary>The room is given the temperature until the planned warm-up starts; saving and cooling time are the model's.</summary>
public sealed record SetbackPlan(double Temperature, TimeSpan RestartsAfter, double SavingKilowattHours, TimeSpan TimeConstant);

public sealed record SetbackVerdict(SetbackPlan? Plan, NoSetback? Reason)
{
	public static SetbackVerdict None(NoSetback reason) => new(null, reason);

	/// <summary>The thermostat's minimum where it is the floor in use, above the lowest night and the frost limit.</summary>
	public double? ThermostatFloor { get; init; }
}

/// <summary>
/// The deepest setback that still has the room back at the next period's temperature at its start, on a
/// first-order room: loss linear in indoor minus outdoor, the heater's full power from its meter.
/// </summary>
public static class NightSetback
{
	private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);

	public static SetbackVerdict Plan(NightSetbackInputs inputs, PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(inputs);
		ArgumentNullException.ThrowIfNull(defaults);

		if (Refused(inputs) is { } refused)
			return SetbackVerdict.None(refused);

		double room = inputs.RoomTemperature!.Value;
		double night = inputs.NightTemperature;
		double wanted = inputs.PeriodTemperature!.Value;
		double outdoor = inputs.OutdoorTemperature!.Value;
		double rate = inputs.WarmingRate!.Value;
		double loss = inputs.HeatLoss!.WattsPerDegree;
		TimeSpan until = inputs.UntilThePeriodStarts!.Value;
		double settings = Math.Max(inputs.LowestNightTemperature, inputs.FrostLimit ?? double.MinValue);
		double floor = Math.Max(settings, inputs.ThermostatMinimum ?? double.MinValue);
		double? thermostatFloor = floor > settings ? floor : null;

		return Deepest(inputs.HeatLoss!, defaults, room, night, wanted, outdoor, rate, loss, until, floor) with { ThermostatFloor = thermostatFloor };
	}

	private static SetbackVerdict Deepest(
		HeatLossEstimate heatLoss, PlannerDefaults defaults, double room, double night, double wanted, double outdoor,
		double rate, double loss, TimeSpan until, double floor)
	{
		if (floor > night - defaults.SmallestSetback)
			return SetbackVerdict.None(NoSetback.TooLittleToGain);

		// The capacity follows from the rate: a rise at full power gains what the heater gives beyond the loss.
		double capacity = (heatLoss.HeaterWatts - (loss * (wanted - outdoor))) / rate;
		if (capacity <= 0.0)
			return SetbackVerdict.None(NoSetback.HeaterTooSmall);

		double tau = capacity / loss;

		if (!WarmUp.Plan(room, wanted, until, rate, defaults).ReachesItInTime)
			return SetbackVerdict.None(NoSetback.TooLittleTime);

		double exact = Lowest(room, floor, wanted, outdoor, tau, rate, until, defaults);
		double setback = Math.Ceiling(exact / defaults.SetbackStep) * defaults.SetbackStep;

		if (setback > night - defaults.SmallestSetback)
			return SetbackVerdict.None(NoSetback.TooLittleToGain);

		// Rounding up only shortens the warm-up, so the restart still fits.
		TimeSpan restartsAfter = until - WarmUp.Plan(setback, wanted, until, rate, defaults).StartsBeforeTheDeadline;
		double saving = Saving(room, night, wanted, setback, restartsAfter, until, outdoor, tau, rate, loss);

		// Nothing is left to save at the restart itself, and the setback still stands until then.
		return saving < 0.0
			? SetbackVerdict.None(NoSetback.TooLittleToGain)
			: new SetbackVerdict(new SetbackPlan(setback, restartsAfter, saving, TimeSpan.FromHours(tau)), null);
	}

	private static NoSetback? Refused(NightSetbackInputs inputs) =>
		inputs switch
		{
			{ SetbackOnForTheRoom: false } => NoSetback.OffForThisRoom,
			{ MarkedModeInForce: false } => NoSetback.NotThisMode,
			{ MasterSwitchOn: false } => NoSetback.MasterSwitchOff,
			{ RoomEnabled: false } => NoSetback.RoomSwitchedOff,
			{ Paused: true } => NoSetback.Paused,
			{ Held: true } => NoSetback.Held,
			{ HasOwnSensor: false } => NoSetback.NoSensor,
			{ RoomTemperature: null } => NoSetback.NoReading,
			{ HasAPowerMeter: false } => NoSetback.NoPowerMeter,
			{ FaultReported: true } => NoSetback.Fault,
			{ WarmingRate: not > 0.0 } => NoSetback.RateNotLearnt,
			{ HeatLoss: null } or { HeatLoss.WattsPerDegree: not > 0.0 } => NoSetback.LossNotLearnt,
			{ OutdoorTemperature: null } => NoSetback.NoOutdoorFigure,
			{ PeriodTemperature: null } or { UntilThePeriodStarts: null } => NoSetback.NoPeriodAhead,
			{ UntilThePeriodStarts: { } until } when until <= TimeSpan.Zero => NoSetback.NoPeriodAhead,
			_ => null
		};

	// Cooling time plus warm-up time grows as the setback deepens, so the deepest one that fits is found by halving.
	private static double Lowest(
		double room, double floor, double wanted, double outdoor, double tau, double rate, TimeSpan until, PlannerDefaults defaults)
	{
		if (Fits(floor))
			return floor;

		double low = floor;
		double high = room;

		for (int round = 0; round < 60; round++)
		{
			double middle = (low + high) / 2.0;

			if (Fits(middle))
				high = middle;
			else
				low = middle;
		}

		return high;

		bool Fits(double setback)
		{
			double cooling = setback >= room ? 0.0
				: setback <= outdoor ? double.PositiveInfinity
				: tau * Math.Log((room - outdoor) / (setback - outdoor));

			return cooling + WarmUp.Plan(setback, wanted, until, rate, defaults).PlannedDuration.TotalHours <= until.TotalHours;
		}
	}

	// Against holding the night temperature to the deadline and warming to the period's afterwards, so both paths end alike.
	private static double Saving(
		double room, double night, double wanted, double setback, TimeSpan restartsAfter, TimeSpan until,
		double outdoor, double tau, double rate, double loss)
	{
		TimeSpan window = until + TimeSpan.FromHours(Math.Max(0.0, wanted - night) / rate) + Step;
		double held = room;
		double set = room;
		double degreeHours = 0.0;
		double hours = Step.TotalHours;

		for (TimeSpan at = TimeSpan.Zero; at < window; at += Step)
		{
			held = Towards(held, at < until ? night : wanted);
			set = Towards(set, at < restartsAfter ? setback : wanted);
			degreeHours += (held - set) * hours;
		}

		return loss * degreeHours / 1000.0;

		double Towards(double temperature, double target) =>
			temperature > target
				? Math.Max(target, outdoor + ((temperature - outdoor) * Math.Exp(-hours / tau)))
				: Math.Min(target, temperature + (rate * hours));
	}
}
