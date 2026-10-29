using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A number saved on the settings page has to reach the room's thermostat, or the page writes into itself.</summary>
public sealed class TellsTheThermostatsTests
{
	[Fact]
	public async Task A_warming_rate_set_by_hand_reaches_the_rate_action_against_that_rooms_thermostat()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		TellsTheThermostats store = Store(open);

		Assert.True(store.SetLearntByHand(ACabin.RoomId, LearntKind.WarmingRate, 1.75).Written);

		IReadOnlyList<ActionOutcome> sent = await store.SendWhatIsWaitingAsync(CancellationToken.None);

		Assert.Equal([ActionOutcome.Called], sent);

		SentCall called = Assert.Single(open.Core.Calls);

		Assert.Equal($"{HeatingActions.Domain}.{HeatingActions.SetWarmingRate}", called.Action);
		Assert.Contains("\"degrees_per_hour\":1.75", called.Json, StringComparison.Ordinal);
		Assert.Contains($"\"entity_id\":[\"{ACabin.Thermostat}\"]", called.Json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_band_set_by_hand_reaches_the_regulation_action()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		TellsTheThermostats store = Store(open);

		Assert.True(store.SetLearntByHand(ACabin.RoomId, LearntKind.Band, 0.8).Written);
		await store.SendWhatIsWaitingAsync(CancellationToken.None);

		SentCall called = Assert.Single(open.Core.Calls);

		Assert.Equal($"{HeatingActions.Domain}.{HeatingActions.SetRegulation}", called.Action);
		Assert.Contains("\"band\":0.8", called.Json, StringComparison.Ordinal);

		// Only the field that changed is sent. The rest stay as the loop has them.
		Assert.DoesNotContain("cycle_length", called.Json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_room_the_integration_has_not_reported_is_saved_and_nothing_is_sent()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		HeatingDocument held = ACabin.WithOneRoom();
		TellsTheThermostats store = new(
			new InMemorySettingsStore(held with { Rooms = [held.Rooms[0] with { Thermostat = null }] }),
			open.Thermostats,
			NullLogger<TellsTheThermostats>.Instance);

		Assert.True(store.SetLearntByHand(ACabin.RoomId, LearntKind.WarmingRate, 1.2).Written);

		Assert.Empty(await store.SendWhatIsWaitingAsync(CancellationToken.None));
		Assert.Empty(open.Core.Calls);
		Assert.Equal(1.2, store.Read().Rooms[0].Learnt.WarmingRate.Value);
	}

	private static TellsTheThermostats Store(AnOpenConnection open) =>
		new(new InMemorySettingsStore(ACabin.WithOneRoom()), open.Thermostats, NullLogger<TellsTheThermostats>.Instance);
}
