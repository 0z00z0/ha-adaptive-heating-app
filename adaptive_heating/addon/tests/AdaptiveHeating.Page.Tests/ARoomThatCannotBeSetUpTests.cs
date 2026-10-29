using Adaptive.Engine;
using Adaptive.Settings;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Persistence;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A switched-on room that cannot be set up is told to a person once, and a moved room keeps what it learnt.</summary>
public sealed class ARoomThatCannotBeSetUpTests
{
	private static readonly ARecordedHouse House = new(
		[ARecordedHouse.Area("stue"), ARecordedHouse.Area("loft"), ARecordedHouse.Area("garasje"), ARecordedHouse.Area("attic")],
		[ARecordedHouse.Heater("switch.stue", "stue"), ARecordedHouse.Heater("switch.garasje", "garasje")]);

	[Fact]
	public async Task A_room_that_cannot_be_set_up_is_told_once_and_not_again_after_a_restart()
	{
		DirectoryInfo data = Directory.CreateTempSubdirectory("adaptive-heating-setup-faults");
		string documentPath = Path.Combine(data.FullName, "heating.yaml");
		ACardCollector cards = new();

		await Reporter(documentPath, cards).ReportAsync(Document(loftEnabled: true), HeatingDocument.NothingHeardYet(), House.Resolver(), CancellationToken.None);

		ACard card = Assert.Single(cards.Raised);
		Assert.Equal(RoomsThatCannotBeSetUp.SetupFailureTitle, card.Title);
		Assert.Contains("loft", card.Message, StringComparison.Ordinal);

		// A new reporter over the same note is a restart.
		await Reporter(documentPath, cards).ReportAsync(Document(loftEnabled: true), HeatingDocument.NothingHeardYet(), House.Resolver(), CancellationToken.None);

		Assert.Single(cards.Raised);
	}

	[Fact]
	public void A_room_switched_off_is_no_fault()
	{
		Assert.Empty(RoomsThatCannotBeSetUp.Faults(Document(loftEnabled: false), HeatingDocument.NothingHeardYet(), House.Resolver()));
	}

	[Fact]
	public void A_fault_is_kept_under_the_room_id_and_under_the_area_where_the_room_has_none()
	{
		HeatingDocument held = new(
			[new RoomSlice("room_002", "Loft", RoomSensorSettings.Standard(PlannerDefaults.Standard), RoomLearntNumbers.Starting(PlannerDefaults.Standard)) { AreaId = "loft" }],
			HeatingDocument.NothingHeardYet().Versions);

		HeatingConfig document = Document(loftEnabled: true);
		document.Areas.Add(new AreaConfig { AreaId = "attic", Enabled = true });

		Assert.Equal(["room_002", "attic"], RoomsThatCannotBeSetUp.Faults(document, held, House.Resolver()).Select(fault => fault.Key));
	}

	[Fact]
	public void A_room_moved_to_another_area_keeps_its_id_and_what_it_has_learnt()
	{
		RoomLearntNumbers starting = RoomLearntNumbers.Starting(PlannerDefaults.Standard);
		RoomLearntNumbers learnt = starting with { WarmingRate = starting.WarmingRate with { SystemValue = 1.35, MeasurementCount = 47 } };
		HeatingDocument held = new(
			[new RoomSlice("room_001", "Living room", RoomSensorSettings.Standard(PlannerDefaults.Standard), learnt) { AreaId = "stue" }],
			HeatingDocument.NothingHeardYet().Versions);

		HeatingConfig moved = HeatingConfig.CreateDefault();
		moved.Areas.Add(new AreaConfig { AreaId = "garasje", Enabled = true });

		// The card names the same room in its new area; nothing was carried beforehand.
		MergedRooms merged = RoomsFromTheAreas.Merge(
			held, moved, new Dictionary<string, string> { ["garasje"] = "room_001" }, House, House.Resolver(), PlannerDefaults.Standard);

		RoomSlice room = Assert.Single(merged.Document.Rooms);
		Assert.Equal(("room_001", "garasje"), (room.Id, room.AreaId));
		Assert.Equal(47, room.Learnt.WarmingRate.MeasurementCount);
	}

	private static RoomsThatCannotBeSetUp Reporter(string documentPath, ACardCollector cards) =>
		new(
			new AreaSetupMemoryStore(documentPath, NullLogger<AreaSetupMemoryStore>.Instance),
			cards,
			NullLogger<RoomsThatCannotBeSetUp>.Instance);

	private static HeatingConfig Document(bool loftEnabled)
	{
		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Areas.Add(new AreaConfig { AreaId = "stue", Enabled = true });
		document.Areas.Add(new AreaConfig { AreaId = "loft", Enabled = loftEnabled });

		return document;
	}
}
