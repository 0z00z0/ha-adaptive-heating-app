using AdaptiveHeating.Planner;
using Xunit;

namespace AdaptiveHeating.Planner.Tests;

public sealed class LearningTests
{
	private static readonly PlannerDefaults Defaults = PlannerDefaults.Standard;
	private static readonly DateTimeOffset At = new(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);

	[Fact]
	public void WithNothingMeasuredEveryNumberSitsAtItsCautiousStartingValueAndSaysSo()
	{
		LearntNumber rate = LearntNumbers.WarmingRate(0.6);
		LearntNumber holdingTerm = LearntNumbers.OutdoorHoldingTerm(Defaults);
		LearntNumber band = LearntNumbers.Band(Defaults);

		Assert.Equal(0.6, rate.Value, 6);
		Assert.False(rate.Measured);
		Assert.Equal(0.005, holdingTerm.Value, 6);
		Assert.False(holdingTerm.Measured);
		Assert.Equal(0.5, band.Value, 6);
		Assert.False(band.Measured);

		WarmingRateEstimate estimate = WarmingRate.Estimate([], 0.6, outdoorTemperature: -10.0, At, Defaults);
		Assert.Equal(0.6, estimate.DegreesPerHour, 6);
		Assert.Equal(0, estimate.MeasurementCount);
		Assert.False(estimate.Measured);
	}

	[Fact]
	public void ARoomLosingHeatFasterIsBelievedAtOnceAndOneNeedingLessOnlyAfterFiveAgree()
	{
		LearntNumber term = LearntNumbers.OutdoorHoldingTerm(Defaults);

		term = term.Observe(0.009, At, Defaults);
		Assert.Equal(0.009, term.Value, 6);

		for (int agreement = 1; agreement < Defaults.AgreementsBeforeRelaxing; agreement++)
		{
			term = term.Observe(0.004, At, Defaults);
			Assert.Equal(0.009, term.Value, 6);
		}

		term = term.Observe(0.004, At, Defaults);
		Assert.Equal(0.004, term.Value, 6);
		Assert.Equal(6, term.MeasurementCount);
	}

	[Fact]
	public void TheWarmingRateErrsSlowAndTheBandErrsNarrowAndNeverWidensPastItsCeiling()
	{
		LearntNumber rate = LearntNumbers.WarmingRate(1.0).Observe(0.7, At, Defaults);
		Assert.Equal(0.7, rate.Value, 6);
		for (int agreement = 1; agreement < Defaults.AgreementsBeforeRelaxing; agreement++)
		{
			rate = rate.Observe(1.5, At, Defaults);
			Assert.Equal(0.7, rate.Value, 6);
		}

		Assert.Equal(1.5, rate.Observe(1.5, At, Defaults).Value, 6);

		LearntNumber band = LearntNumbers.Band(Defaults).Observe(0.3, At, Defaults);
		Assert.Equal(0.3, band.Value, 6);
		for (int agreement = 0; agreement < Defaults.AgreementsBeforeRelaxing; agreement++)
		{
			band = band.Observe(2.0, At, Defaults);
		}

		Assert.Equal(Defaults.BandCeiling, band.Value, 6);
	}

	[Fact]
	public void ARiseWhoseHeaterDrewNoPowerIsThrownAwayAndTheSameRiseWithPowerIsKept()
	{
		// A room that fails to warm because its own switch is off would otherwise teach itself a false rate,
		// and the wrong number would stay after somebody switched the heater back on.
		RiseOutcome drewNothing = ARiseOf(powerWatts: 0.0, hasAMeter: true);
		Assert.Equal(RiseVerdict.NoPowerDrawn, drewNothing.Verdict);
		Assert.Null(drewNothing.Measurement);

		RiseOutcome drewPower = ARiseOf(powerWatts: 425.0, hasAMeter: true);
		Assert.Equal(RiseVerdict.Recorded, drewPower.Verdict);
		Assert.NotNull(drewPower.Measurement);
		Assert.Equal(2.0, drewPower.Measurement.DegreesPerHour, 6);
		Assert.Equal(-5.0, drewPower.Measurement.OutdoorTemperature);

		// A room with no meter is ordinary rather than a fault, and its rises count.
		Assert.Equal(RiseVerdict.Recorded, ARiseOf(powerWatts: null, hasAMeter: false).Verdict);

		// A rise nothing heated is the weather's, and a rate read too high starts the next warm-up late.
		Assert.Equal(RiseVerdict.HeaterNeverRan, ARiseOf(null, false, heaterCommandedOn: false).Verdict);
	}

	/// <summary>Two degrees in one hour at five below, sampled every ten minutes.</summary>
	private static RiseOutcome ARiseOf(double? powerWatts, bool hasAMeter, bool heaterCommandedOn = true)
	{
		WarmingRateWatcher watcher = new(Defaults);
		RiseOutcome outcome = RiseOutcome.Nothing;

		for (int minutes = 0; minutes <= 60; minutes += 10)
		{
			outcome = watcher.Observe(new RoomObservation(
				At.AddMinutes(minutes),
				RoomTemperature: 18.0 + (2.0 * minutes / 60.0),
				Target: 20.0,
				heaterCommandedOn,
				WarmUpRunning: false,
				OutdoorTemperature: -5.0,
				powerWatts,
				hasAMeter));
		}

		return outcome;
	}

	[Fact]
	public void AValueSetByHandLocksTheNumberAndClearingItCarriesOnFromTheMeasurements()
	{
		LearntNumber band = LearntNumbers.Band(Defaults).Observe(0.3, At, Defaults);

		band = band.SetByHand(0.8, At);
		Assert.Equal(0.8, band.Value, 6);
		Assert.True(band.LockedByAPerson);
		Assert.Equal(0.8, band.Observe(0.2, At, Defaults).Value, 6);

		band = band.ClearTheSetValue();
		Assert.False(band.LockedByAPerson);
		Assert.Equal(0.3, band.Value, 6);

		band = band.Reset(At);
		Assert.Equal(Defaults.BandStartingValue, band.Value, 6);
		Assert.Equal(0, band.MeasurementCount);
	}
}
