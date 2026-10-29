using AdaptiveHeating.AddOn.Integration;

using Microsoft.Extensions.Logging;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// A house runs one half newer than the other, so an action the integration does not have has to be absent
/// rather than fatal. Anything else stops the whole of the heating over a feature nobody asked for.
/// </summary>
public sealed class AMissingActionTests
{
	[Fact]
	public async Task An_action_the_integration_does_not_have_is_not_fatal_and_the_rest_still_goes_through()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) =>
			call.Action.EndsWith(HeatingActions.WarmRoomBy, StringComparison.Ordinal)
				? CoreUnderTest.Refused(call.Id, "not_found", $"Service {call.Action} not found.")
				: CoreUnderTest.Worked(call.Id));

		ActionOutcome missing = await open.Thermostats
			.WarmRoomByAsync(ACabin.Thermostat, 21, ACabin.Typed, CancellationToken.None);

		ActionOutcome known = await open.Thermostats.ReturnToTargetAsync(ACabin.Thermostat, CancellationToken.None);

		Assert.Equal(ActionOutcome.UnknownAction, missing);
		Assert.Equal(ActionOutcome.Called, known);
	}

	[Fact]
	public async Task A_missing_action_is_still_missing_when_it_is_asked_for_again_and_is_only_said_once()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(
			(_, call) => CoreUnderTest.Refused(call.Id, "not_found", $"Service {call.Action} not found."));

		for (int attempt = 0; attempt < 3; attempt++)
		{
			ActionOutcome outcome = await open.Thermostats
				.HoldTemperatureAsync(ACabin.Thermostat, 21, TimeSpan.FromHours(2), CancellationToken.None);

			Assert.Equal(ActionOutcome.UnknownAction, outcome);
		}

		Assert.Equal(3, open.Core.Calls.Count);

		// A house on an older integration would otherwise fill its log with the same line every minute.
		Assert.Single(
			open.Log.Lines,
			line => line.Level == LogLevel.Warning
				&& line.Said.Contains(HeatingActions.HoldTemperature, StringComparison.Ordinal));
	}

	[Fact]
	public async Task Home_assistant_not_being_there_at_all_is_not_fatal_either()
	{
		AnOpenConnection never = AnOpenConnection.NotStarted();

		Assert.Equal(
			ActionOutcome.Failed,
			await never.Thermostats.ReturnToTargetAsync(ACabin.Thermostat, CancellationToken.None));
	}

	[Fact]
	public async Task A_field_home_assistant_will_not_take_reads_as_a_failure_and_not_as_a_missing_action()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) =>
			CoreUnderTest.Refused(call.Id, "invalid_format", "expected float for dictionary value @ data['temperature']"));

		Assert.Equal(
			ActionOutcome.Failed,
			await open.Thermostats.WarmRoomByAsync(ACabin.Thermostat, 21, ACabin.Typed, CancellationToken.None));
	}
}
