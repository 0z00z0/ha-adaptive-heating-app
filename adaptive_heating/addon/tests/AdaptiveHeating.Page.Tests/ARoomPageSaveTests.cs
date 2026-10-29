using Adaptive.Settings;

using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Web.Presentation;
using Adaptive.UI;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A temperature set on a room's page lands in the settings document, and its stamp in the note.</summary>
public sealed class ARoomPageSaveTests
{
	private const string Evening = "evening";

	private static readonly DateTimeOffset Saved = ACabin.Typed.AddHours(2);

	[Fact]
	public async Task A_temperature_set_on_the_room_page_is_in_the_document_after_its_store_restarts()
	{
		using ARoomPage page = ARoomPage.Open();

		await page.Set(22.5);

		// A fresh store over the same file, which is what the add-on reads after a restart.
		HeatingConfig reread = new HeatingConfigStore(page.Path, NullLogger<HeatingConfigStore>.Instance).Load();
		AreaConfig kitchen = Assert.Single(reread.Areas, area => area.AreaId == ACabin.RoomId);

		Assert.Equal(22.5, Assert.Single(kitchen.PeriodTemperatures, row => row.PeriodId == Evening).Temperature);
	}

	[Fact]
	public async Task A_page_save_stamps_the_note_and_a_later_hand_change_at_the_thermostat_wins()
	{
		using ARoomPage page = ARoomPage.Open();

		await page.Set(22.5);

		RoomSlice room = page.Store.Read().Rooms[0];

		// The value is the document's and the stamp the note's; nothing else about the room moved.
		Assert.Equal(Saved, room.ModeStamps[HeatingMode.Home].Stamp.SetAt);
		Assert.Equal(22.5, room.ModeTemperatures[HeatingMode.Home].Value);
		Assert.Equal(Saved, room.ModeTemperatures[HeatingMode.Home].Stamp.SetAt);
		Assert.Equal(ACabin.Typed, room.ModeStamps[HeatingMode.Away].Stamp.SetAt);

		ResolvedTarget earlier = Precedence.Resolve(new RoomTargetCandidates
		{
			ModeTemperature = room.ModeTemperatures[HeatingMode.Home],
			HandChange = new Stamped<double>(18, Stamp.Certain(Saved.AddMinutes(-10)), ChangeOrigin.Thermostat)
		});

		ResolvedTarget later = Precedence.Resolve(new RoomTargetCandidates
		{
			ModeTemperature = room.ModeTemperatures[HeatingMode.Home],
			HandChange = new Stamped<double>(18, Stamp.Certain(Saved.AddMinutes(10)), ChangeOrigin.Thermostat)
		});

		Assert.Equal((22.5, TargetSource.ModeTemperature), (earlier.Temperature, earlier.Source));
		Assert.Equal((18.0, TargetSource.HandChange), (later.Temperature, later.Source));
	}

	/// <summary>One room page over a settings file of its own, with one period in force all day.</summary>
	private sealed class ARoomPage : IDisposable
	{
		private readonly RoomPageModel _model;

		private ARoomPage(string path, InMemorySettingsStore store, RoomPageModel model)
		{
			Path = path;
			Store = store;
			_model = model;
		}

		public string Path { get; }

		public InMemorySettingsStore Store { get; }

		public static ARoomPage Open()
		{
			string path = System.IO.Path.Combine(ACabin.ScratchDirectory(), "heating.yaml");

			HeatingConfig document = HeatingConfig.CreateDefault();
			document.Periods = [new TimePeriodConfig { Id = Evening, Name = "Evening", Start = "00:00", Temperature = 20 }];
			document.Areas = [new AreaConfig { AreaId = ACabin.RoomId, Name = "Kitchen", AwayTemperature = 8 }];
			File.WriteAllText(path, HeatingConfigDocument.Serialize(document));

			HeatingDocumentHost host = new(
				new HeatingConfigStore(path, NullLogger<HeatingConfigStore>.Instance),
				_ => ValidationContext.None,
				() => { },
				NullLogger<HeatingDocumentHost>.Instance);
			host.Reload();

			HaCatalog nowhere = new(() => null, () => null, NullLoggerFactory.Instance);
			ClockUnderTest clock = new(Saved);

			InMemorySettingsStore store = new(
				ACabin.WithOneRoom(),
				RoomTemperatures.FromTheDocument(() => host.Current, nowhere, () => null, TimeZoneInfo.Utc, clock));

			RoomPageModel model = new(
				host,
				nowhere,
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

			return new ARoomPage(path, store, model);
		}

		/// <summary>What the levels editor does when its slider lands on a stop, then the save the timer would make.</summary>
		public async Task Set(double celsius)
		{
			RoomLevels.SetTemperature(_model.Area!, Evening, celsius);
			await _model.MarkDirty();
			_model.Commit();

			Assert.Equal(RoomSaveState.Saved, _model.SaveState);
		}

		public void Dispose() => _model.Dispose();
	}
}
