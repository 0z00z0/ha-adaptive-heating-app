using AdaptiveHeating.Planner;
using Xunit;

namespace AdaptiveHeating.Planner.Tests;

public sealed class RoomSensorTests
{
	private static readonly PlannerDefaults Defaults = PlannerDefaults.Standard;
	private static readonly DateTimeOffset Now = new(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);

	private static SensorReading Fresh(string id, double temperature) =>
		new(id, temperature, Now.AddMinutes(-2), Now.AddMinutes(-2));

	[Fact]
	public void ThreeSensorsSettleADisagreementByMajorityAndTheOneSittingApartIsNamed()
	{
		RoomReading reading = RoomSensors.Read(
			[Fresh("a", 20.0), Fresh("b", 20.2), Fresh("c", 25.0)],
			Now,
			Defaults);

		Assert.Equal(20.1, reading.Temperature!.Value, 6);
		Assert.Equal(2, reading.RestingOn);
		Assert.Equal(3, reading.SensorsInTheRoom);

		SensorVerdict apart = Assert.Single(reading.LeftOut);
		Assert.Equal("c", apart.SensorId);
		Assert.Equal(SensorExclusion.FarFromTheOthers, apart.Excluded);
	}

	[Fact]
	public void TwoFreshSensorsThatDisagreeAreBothDroppedAndTheRoomLosesItsTemperature()
	{
		RoomReading reading = RoomSensors.Read([Fresh("a", 19.0), Fresh("b", 23.0)], Now, Defaults);

		Assert.False(reading.HasATemperature);
		Assert.Equal(0, reading.RestingOn);
		Assert.Equal(2, reading.LeftOut.Count());
		Assert.All(reading.LeftOut, verdict => Assert.Equal(SensorExclusion.BothOfADisagreeingPair, verdict.Excluded));
	}

	[Fact]
	public void AQuietSensorAndAStuckOneLeaveTheAverageWithoutTakingTheRoomDown()
	{
		SensorReading quiet = new("a", 20.0, Now.AddHours(-3), Now.AddHours(-3));
		SensorReading stuck = new("b", 21.0, Now.AddMinutes(-1), Now.AddHours(-9));
		RoomReading reading = RoomSensors.Read([quiet, stuck, Fresh("c", 19.5)], Now, Defaults);

		Assert.Equal(19.5, reading.Temperature!.Value, 6);
		Assert.Equal(1, reading.RestingOn);
		Assert.Equal(3, reading.SensorsInTheRoom);
		Assert.Equal(SensorExclusion.Quiet, reading.Verdicts.Single(verdict => verdict.SensorId == "a").Excluded);
		Assert.Equal(SensorExclusion.Stuck, reading.Verdicts.Single(verdict => verdict.SensorId == "b").Excluded);
	}
}
