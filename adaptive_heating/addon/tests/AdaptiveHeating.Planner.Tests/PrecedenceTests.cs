using AdaptiveHeating.Planner;
using Xunit;

namespace AdaptiveHeating.Planner.Tests;

public sealed class PrecedenceTests
{
	private static readonly DateTimeOffset Morning = new(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);

	private static Stamped<double> FromThePage(double temperature, DateTimeOffset at) =>
		new(temperature, Stamp.Certain(at), ChangeOrigin.SettingsPage);

	private static Stamped<double> FromTheCard(double temperature, DateTimeOffset at) =>
		new(temperature, Stamp.Certain(at), ChangeOrigin.Thermostat);

	[Fact]
	public void AHandChangeStandsUntilALaterModeTemperatureReplacesItAndARunningWarmUpWinsOutright()
	{
		RoomTargetCandidates afterTheHandChange = new()
		{
			ModeTemperature = FromThePage(21.0, Morning.AddHours(1)),
			HandChange = FromTheCard(23.0, Morning.AddHours(2)),
		};

		ResolvedTarget standing = Precedence.Resolve(afterTheHandChange);
		Assert.Equal(23.0, standing.Temperature, 6);
		Assert.Equal(TargetSource.HandChange, standing.Source);
		Assert.Equal(ChangeOrigin.Thermostat, standing.Origin);

		RoomTargetCandidates afterTheNextBoundary = afterTheHandChange with
		{
			ModeTemperature = FromThePage(19.0, Morning.AddHours(3)),
		};

		ResolvedTarget replaced = Precedence.Resolve(afterTheNextBoundary);
		Assert.Equal(19.0, replaced.Temperature, 6);
		Assert.Equal(TargetSource.ModeTemperature, replaced.Source);
		Assert.Equal(ChangeOrigin.SettingsPage, replaced.Origin);

		RoomTargetCandidates whileWarmingUp = afterTheNextBoundary with
		{
			WarmUp = FromThePage(21.0, Morning),
		};

		ResolvedTarget warming = Precedence.Resolve(whileWarmingUp);
		Assert.Equal(21.0, warming.Temperature, 6);
		Assert.Equal(TargetSource.WarmUp, warming.Source);
	}

	[Fact]
	public void ATemperatureBelowTheAwayModesTemperatureIsAcceptedAndReachesTheRoom()
	{
		RoomTargetCandidates candidates = new()
		{
			ModeTemperature = FromThePage(4.0, Morning),
			HandChange = FromTheCard(2.0, Morning.AddMinutes(5)),
		};

		ResolvedTarget resolved = Precedence.Resolve(candidates);
		Assert.Equal(2.0, resolved.Temperature, 6);
		Assert.Equal(TargetSource.HandChange, resolved.Source);
	}
}
