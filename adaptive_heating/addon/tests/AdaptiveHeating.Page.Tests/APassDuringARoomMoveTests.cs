using System.Text.Json;

using Adaptive.Settings;
using Adaptive.UI;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Web.Presentation;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A pass over the rooms that lands in the middle of a move on the room page costs the room nothing it has learnt.</summary>
public sealed class APassDuringARoomMoveTests
{
	public enum Moment
	{
		WhileTheIntegrationMovesTheThermostat,
		WhenTheRoomIsLinkedToItsNewArea,
		AfterAnotherTabSavedTheRoom
	}

	private const string RoomId = "room_001";
	private const string Kitchen = "kitchen";
	private const string Pantry = "pantry";
	private const int Measured = 47;

	private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

	// Long enough for a pass that is not held back to finish on a loaded machine.
	private static readonly TimeSpan PassGetsThisLong = TimeSpan.FromSeconds(1);

	[Theory]
	[InlineData(Moment.WhileTheIntegrationMovesTheThermostat)]
	[InlineData(Moment.WhenTheRoomIsLinkedToItsNewArea)]
	[InlineData(Moment.AfterAnotherTabSavedTheRoom)]
	public async Task The_room_keeps_what_it_has_learnt_and_ends_up_in_its_new_area(Moment moment)
	{
		AHouse? house = null;
		Task pass = Task.CompletedTask;

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((core, call) =>
		{
			if (call.Action.EndsWith(HeatingActions.MoveToArea, StringComparison.Ordinal))
			{
				if (moment is Moment.WhileTheIntegrationMovesTheThermostat)
					pass = house!.PassDuring();
				else if (moment is Moment.AfterAnotherTabSavedTheRoom)
					house!.AnotherTabRenamesTheRoom();
			}

			return CoreUnderTest.Worked(call.Id);
		});

		house = AHouse.Over(open);

		bool linked = false;
		house.Store.Changed += () =>
		{
			if (moment is Moment.WhenTheRoomIsLinkedToItsNewArea && !linked)
			{
				linked = true;
				pass = house.PassDuring();
			}
		};

		await house.Page.ChangeArea(Pantry);
		await pass;

		// The pass a minute later, with the card in the new area.
		await house.PassAsync();

		if (moment is Moment.AfterAnotherTabSavedTheRoom)
			Assert.Equal(SaveStatus.Conflicted, house.Page.Failure?.Status);
		else
			Assert.Null(house.Page.Failure);

		Assert.Equal(Pantry, Assert.Single(house.Host.Store.Load().Areas).AreaId);
		RoomSlice room = Assert.Single(house.Store.Read().Rooms);
		Assert.Equal((RoomId, Pantry), (room.Id, room.AreaId));
		Assert.Equal(Measured, room.Learnt.WarmingRate.MeasurementCount);
	}

	private sealed class AHouse
	{
		private readonly ARecordedHouse _registry = new([ARecordedHouse.Area(Kitchen), ARecordedHouse.Area(Pantry)], []);
		private readonly RoomsMovedInHomeAssistant _follower;

		private AHouse(TellsTheThermostats store, HeatingDocumentHost host, RoomPageModel page)
		{
			Store = store;
			Host = host;
			Page = page;
			_follower = new RoomsMovedInHomeAssistant(store, host, new AReporter().Reports, NullLogger<RoomsMovedInHomeAssistant>.Instance);
		}

		public TellsTheThermostats Store { get; }

		public HeatingDocumentHost Host { get; }

		public RoomPageModel Page { get; }

		public static AHouse Over(AnOpenConnection open)
		{
			RoomLearntNumbers starting = RoomLearntNumbers.Starting(PlannerDefaults.Standard);
			RoomSlice kitchen = ACabin.WithOneRoom().Rooms[0] with
			{
				Id = RoomId,
				AreaId = Kitchen,
				Learnt = starting with { WarmingRate = starting.WarmingRate with { SystemValue = 1.35, MeasurementCount = Measured } }
			};

			TellsTheThermostats store = new(
				new InMemorySettingsStore(ACabin.WithOneRoom() with { Rooms = [kitchen] }),
				open.Thermostats,
				NullLogger<TellsTheThermostats>.Instance);

			string path = Path.Combine(ACabin.ScratchDirectory(), "heating.yaml");
			HeatingConfig document = HeatingConfig.CreateDefault();
			document.Areas = [new AreaConfig { AreaId = Kitchen, Name = "Kitchen" }];
			File.WriteAllText(path, HeatingConfigDocument.Serialize(document));

			HeatingDocumentHost host = new(
				new HeatingConfigStore(path, NullLogger<HeatingConfigStore>.Instance),
				_ => ValidationContext.None,
				() => { },
				NullLogger<HeatingDocumentHost>.Instance);
			host.Reload();

			RoomPageModel page = new(
				host,
				new HaCatalog(() => null, () => null, NullLoggerFactory.Instance),
				new ARecordedHouse([], []),
				store,
				new CertainStamps(new ClockUnderTest(ACabin.Typed)),
				new NoRoomActivity(),
				TimeZoneInfo.Utc,
				NullLogger.Instance,
				work =>
				{
					work();

					return Task.CompletedTask;
				});

			page.Show(Kitchen);

			return new AHouse(store, host, page);
		}

		/// <summary>A pass on a thread of its own, given a moment to run before the move carries on.</summary>
		public Task PassDuring()
		{
			Task pass = Task.Run(PassAsync);
			pass.Wait(PassGetsThisLong);

			return pass;
		}

		/// <summary>What one pass does with the rooms, with the thermostat's card already in the new area.</summary>
		public async Task PassAsync()
		{
			IReadOnlyList<EntityState> states = [Card()];

			await _follower.FollowAsync(states, Now, CancellationToken.None);
			RoomsFromTheAreas.MergeInto(Store, Host, states, _registry, _registry.Resolver());
		}

		public void AnotherTabRenamesTheRoom()
		{
			HeatingConfig other = Host.Store.Load();
			other.Areas[0].Name = "Kitchen and pantry";
			Assert.True(Host.Save(other).Written);
		}

		private static EntityState Card() =>
			JsonSerializer.Deserialize<EntityState>(
				$$"""
				{
					"entity_id": "{{ACabin.Thermostat}}",
					"state": "heat",
					"attributes": { "room_id": "{{RoomId}}", "area_id": "{{Pantry}}", "integration_version": "0.1.0", "vocabulary_version": 2 },
					"context": { "id": "01JCARD", "parent_id": null, "user_id": null }
				}
				""")!;
	}
}
