using System.Globalization;

using AdaptiveHeating.Planner;
using Xunit;
using Xunit.Abstractions;

namespace AdaptiveHeating.Planner.Tests;

public sealed class NightSetbackTests(ITestOutputHelper output)
{
	private static readonly PlannerDefaults Defaults = PlannerDefaults.Standard;

	// The starting numbers: 0.5 °C an hour, and the holding term's 0.005 of a 1000 W heater per degree.
	private static readonly NightSetbackInputs LearntDefaults = Room(rate: 0.5, loss: 5.0, heater: 1000.0);

	private static readonly NightSetbackInputs Slow = Room(rate: 0.4, loss: 25.0, heater: 800.0);

	private static readonly NightSetbackInputs Fast = Room(rate: 2.0, loss: 30.0, heater: 1500.0);

	private static NightSetbackInputs Room(double rate, double loss, double heater) =>
		new()
		{
			SetbackOnForTheRoom = true,
			MarkedModeInForce = true,
			HasOwnSensor = true,
			HasAPowerMeter = true,
			RoomTemperature = 19.0,
			NightTemperature = 19.0,
			PeriodTemperature = 19.0,
			LowestNightTemperature = 16.0,
			FrostLimit = 10.0,
			WarmingRate = rate,
			OutdoorTemperature = -5.0,
			HeatLoss = new HeatLossEstimate(loss, heater),
			UntilThePeriodStarts = TimeSpan.FromHours(7.5),
		};

	[Fact]
	public void EachRoomIsSetBackNoLowerThanItsFloorAndIsBackByThePeriodStart()
	{
		output.WriteLine("| Room | Night to morning | Setback | Restart after | Saving | Cooling time | Reason |");

		foreach ((string name, NightSetbackInputs room) in new[] { ("learnt defaults", LearntDefaults), ("slow", Slow), ("fast", Fast) })
		{
			foreach (double morning in new[] { 19.0, 21.0 })
			{
				NightSetbackInputs inputs = room with { PeriodTemperature = morning };
				SetbackVerdict verdict = NightSetback.Plan(inputs, Defaults);
				output.WriteLine(Row(name, inputs, verdict));

				if (verdict.Plan is { } plan)
				{
					Assert.True(plan.Temperature >= 16.0);
					Assert.True(plan.Temperature <= 18.5);
					Assert.True(WarmUp.Plan(plan.Temperature, morning, TimeSpan.FromHours(7.5) - plan.RestartsAfter, inputs.WarmingRate!.Value, Defaults).ReachesItInTime);
					Assert.True(plan.SavingKilowattHours > 0.0);
				}
			}
		}

		Assert.NotNull(NightSetback.Plan(Fast, Defaults).Plan);
		Assert.NotNull(NightSetback.Plan(Slow, Defaults).Plan);
	}

	[Fact]
	public void ASetbackNeverFallsBelowTheLowestNightTemperatureOrTheFrostLimit()
	{
		// The fast room left free would cool far below either.
		SetbackPlan lowest = NightSetback.Plan(Fast with { UntilThePeriodStarts = TimeSpan.FromHours(12) }, Defaults).Plan!;
		Assert.Equal(16.0, lowest.Temperature);

		SetbackPlan frost = NightSetback.Plan(Fast with { UntilThePeriodStarts = TimeSpan.FromHours(12), LowestNightTemperature = 12.0, FrostLimit = 17.0 }, Defaults).Plan!;
		Assert.Equal(17.0, frost.Temperature);
	}

	[Fact]
	public void ASetbackNeverFallsBelowTheThermostatsOwnMinimumAndSaysSo()
	{
		NightSetbackInputs longNight = Fast with { UntilThePeriodStarts = TimeSpan.FromHours(12), LowestNightTemperature = 12.0, FrostLimit = 5.0 };

		SetbackVerdict above = NightSetback.Plan(longNight with { ThermostatMinimum = 14.0 }, Defaults);
		SetbackVerdict below = NightSetback.Plan(longNight with { ThermostatMinimum = 7.0 }, Defaults);

		Assert.Equal(14.0, above.Plan!.Temperature);
		Assert.Equal(14.0, above.ThermostatFloor);
		Assert.Equal(12.0, below.Plan!.Temperature);
		Assert.Null(below.ThermostatFloor);
	}

	[Fact]
	public void AShortNightSetsBackOnlyAsFarAsTheWarmUpCanRecoverByThePeriodStart()
	{
		NightSetbackInputs shortNight = Slow with { UntilThePeriodStarts = TimeSpan.FromHours(4) };

		SetbackPlan plan = NightSetback.Plan(shortNight, Defaults).Plan!;

		Assert.True(plan.Temperature > 16.0);
		Assert.True(WarmUp.Plan(plan.Temperature, 19.0, TimeSpan.FromHours(4) - plan.RestartsAfter, 0.4, Defaults).ReachesItInTime);
	}

	[Fact]
	public void ARoomThatCannotWarmFromWhereItIsByThePeriodStartIsNotSetBack() =>
		Assert.Equal(
			NoSetback.TooLittleTime,
			NightSetback.Plan(Slow with { RoomTemperature = 15.0, UntilThePeriodStarts = TimeSpan.FromHours(2) }, Defaults).Reason);

	[Fact]
	public void EveryGateBreachedGivesNoSetbackAndSaysWhich()
	{
		(NightSetbackInputs Breached, NoSetback Reason)[] cases =
		[
			(Fast with { SetbackOnForTheRoom = false }, NoSetback.OffForThisRoom),
			(Fast with { MarkedModeInForce = false }, NoSetback.NotThisMode),
			(Fast with { MasterSwitchOn = false }, NoSetback.MasterSwitchOff),
			(Fast with { RoomEnabled = false }, NoSetback.RoomSwitchedOff),
			(Fast with { Paused = true }, NoSetback.Paused),
			(Fast with { Held = true }, NoSetback.Held),
			(Fast with { HasOwnSensor = false }, NoSetback.NoSensor),
			(Fast with { RoomTemperature = null }, NoSetback.NoReading),
			(Fast with { HasAPowerMeter = false }, NoSetback.NoPowerMeter),
			(Fast with { FaultReported = true }, NoSetback.Fault),
			(Fast with { WarmingRate = null }, NoSetback.RateNotLearnt),
			(Fast with { HeatLoss = null }, NoSetback.LossNotLearnt),
			(Fast with { OutdoorTemperature = null }, NoSetback.NoOutdoorFigure),
			(Fast with { HeatLoss = new HeatLossEstimate(30.0, 500.0) }, NoSetback.HeaterTooSmall),
			(Fast with { UntilThePeriodStarts = null }, NoSetback.NoPeriodAhead),
			(Fast with { LowestNightTemperature = 18.6 }, NoSetback.TooLittleToGain),
		];

		Assert.NotNull(NightSetback.Plan(Fast, Defaults).Plan);

		foreach ((NightSetbackInputs breached, NoSetback reason) in cases)
		{
			SetbackVerdict verdict = NightSetback.Plan(breached, Defaults);
			output.WriteLine(Row("fast, " + reason, breached, verdict));
			Assert.Null(verdict.Plan);
			Assert.Equal(reason, verdict.Reason);
		}
	}

	[Fact]
	public void ASettledHourReadAgainstTheMeterGivesTheLossPerDegreeAndTheHeaterDraw()
	{
		HeatLossWatcher watcher = new(Defaults);
		DateTimeOffset start = new(2026, 1, 15, 1, 0, 0, TimeSpan.Zero);
		HeatLossMeasurement? measured = null;

		// A 1000 W heater on for a third of every fifteen minutes holds 20 °C at 0 °C outside: 333 W over 20 degrees.
		for (int minute = 0; minute <= 61; minute++)
		{
			bool on = minute % 15 < 5;
			measured ??= watcher.Observe(new RoomObservation(start.AddMinutes(minute), 20.0, 20.0, on, false, 0.0, on ? 1000.0 : 0.0, true));
		}

		Assert.NotNull(measured);
		Assert.Equal(1000.0, measured.HeaterWatts, 6);
		Assert.InRange(measured.WattsPerDegree, 16.0, 17.5);
	}

	private static string Row(string name, NightSetbackInputs inputs, SetbackVerdict verdict) =>
		verdict.Plan is { } plan
			? string.Create(CultureInfo.InvariantCulture,
				$"| {name} | {inputs.NightTemperature:0.0} to {inputs.PeriodTemperature:0.0} °C | {plan.Temperature:0.0} °C | {plan.RestartsAfter.TotalHours:0.00} h | {plan.SavingKilowattHours:0.00} kWh | {plan.TimeConstant.TotalHours:0} h | |")
			: string.Create(CultureInfo.InvariantCulture,
				$"| {name} | {inputs.NightTemperature:0.0} to {inputs.PeriodTemperature:0.0} °C | none | | | | {verdict.Reason} |");
}
