using Adaptive.Settings;
using Adaptive.UI;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;

using AdaptiveLighting.Web.Presentation;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A room moved on its page takes its thermostat with it, and a refusal leaves it where it was.</summary>
public sealed class ARoomMovedToAnotherAreaTests
{
	private const string Pantry = "pantry";
	private const string Refusal = "Another room is already in that area.";

	[Fact]
	public async Task The_move_calls_the_integration_and_the_room_keeps_its_id_and_thermostat()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();
		TellsTheThermostats store = Store(open);

		SaveOutcome outcome = await store.CarryRoomAsync(ACabin.RoomId, Pantry);
		store.LinkRoom(ACabin.RoomId, Pantry);

		Assert.True(outcome.Written);
		SentCall sent = Assert.Single(open.Core.Calls, call => call.Action == "adaptive_heating.move_to_area");
		Assert.Contains($"\"area\":\"{Pantry}\"", sent.Json, StringComparison.Ordinal);
		Assert.Contains(ACabin.Thermostat, sent.Json, StringComparison.Ordinal);
		RoomSlice room = Assert.Single(store.Read().Rooms);
		Assert.Equal((ACabin.RoomId, Pantry, ACabin.Thermostat), (room.Id, room.AreaId, room.Thermostat));
	}

	[Fact]
	public async Task A_refused_move_carries_nothing_and_says_why()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(Refusing);
		TellsTheThermostats store = Store(open);

		SaveOutcome outcome = await store.CarryRoomAsync(ACabin.RoomId, Pantry);

		Assert.False(outcome.Written);
		Assert.Equal(Refusal, outcome.Message);
		Assert.Equal(ACabin.RoomId, Assert.Single(store.Read().Rooms).AreaId);
	}

	[Fact]
	public async Task A_refused_move_on_the_room_page_keeps_the_old_area_and_shows_the_refusal()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(Refusing);
		using APage page = APage.Open(Store(open));

		await page.Model.ChangeArea(Pantry);

		Assert.Equal(ACabin.RoomId, page.Model.AreaId);
		Assert.Equal(Refusal, page.Model.Failure?.Message);
		Assert.Equal(ACabin.RoomId, Assert.Single(page.Reread().Areas).AreaId);
	}

	[Fact]
	public async Task A_move_on_the_room_page_moves_the_document_entry()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();
		using APage page = APage.Open(Store(open));

		await page.Model.ChangeArea(Pantry);

		Assert.Null(page.Model.Failure);
		Assert.Equal(Pantry, Assert.Single(page.Reread().Areas).AreaId);
	}

	private static string? Refusing(CoreUnderTest core, SentCall call) =>
		call.Action.EndsWith(HeatingActions.MoveToArea, StringComparison.Ordinal)
			? CoreUnderTest.Refused(call.Id, "service_validation_error", Refusal)
			: CoreUnderTest.Worked(call.Id);

	private static TellsTheThermostats Store(AnOpenConnection open) =>
		new(new InMemorySettingsStore(ACabin.WithOneRoom()), open.Thermostats, NullLogger<TellsTheThermostats>.Instance);

	/// <summary>One room page over a settings file of its own, its store the given one.</summary>
	private sealed class APage : IDisposable
	{
		private readonly string _path;

		private APage(string path, RoomPageModel model)
		{
			_path = path;
			Model = model;
		}

		public RoomPageModel Model { get; }

		public static APage Open(ISettingsStore store)
		{
			string path = Path.Combine(ACabin.ScratchDirectory(), "heating.yaml");

			HeatingConfig document = HeatingConfig.CreateDefault();
			document.Areas = [new AreaConfig { AreaId = ACabin.RoomId, Name = "Kitchen" }];
			File.WriteAllText(path, HeatingConfigDocument.Serialize(document));

			HeatingDocumentHost host = new(
				new HeatingConfigStore(path, NullLogger<HeatingConfigStore>.Instance),
				_ => ValidationContext.None,
				() => { },
				NullLogger<HeatingDocumentHost>.Instance);
			host.Reload();

			ClockUnderTest clock = new(ACabin.Typed);

			RoomPageModel model = new(
				host,
				new HaCatalog(() => null, () => null, NullLoggerFactory.Instance),
				new ARecordedHouse([], []),
				store,
				new CertainStamps(clock),
				new NoRoomActivity(),
				TimeZoneInfo.Utc,
				NullLogger.Instance,
				work =>
				{
					work();

					return Task.CompletedTask;
				});

			model.Show(ACabin.RoomId);

			return new APage(path, model);
		}

		public HeatingConfig Reread() => new HeatingConfigStore(_path, NullLogger<HeatingConfigStore>.Instance).Load();

		public void Dispose() => Model.Dispose();
	}
}
