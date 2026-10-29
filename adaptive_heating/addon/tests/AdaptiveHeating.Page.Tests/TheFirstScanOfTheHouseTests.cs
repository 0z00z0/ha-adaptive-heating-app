using Adaptive.Engine;
using Adaptive.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>The scan that proposes rooms on a house that has none, and writes them under their area ids.</summary>
public sealed class TheFirstScanOfTheHouseTests
{
	[Fact]
	public void The_scan_writes_a_room_per_qualifying_Area_keyed_by_its_area_id_and_switched_off()
	{
		ARecordedHouse house = AHouseOfThree();
		AHostedDocument document = ADocumentWithNoRooms();
		AreaDiscoveryScheduler scanning = Scanner(document);

		Assert.Equal(["bad", "stue"], scanning.ScanIfNeeded(1, house, house.Resolver()).Order(StringComparer.Ordinal));

		HeatingConfig written = document.Host.Store.Load();

		Assert.Equal(["bad", "stue"], written.Areas.Select(area => area.AreaId).Order(StringComparer.Ordinal));
		Assert.All(written.Areas, area => Assert.False(area.Enabled));

		// Nothing a person has not typed: a proposal carries the key and the switch and no number.
		Assert.All(written.Areas, area => Assert.Null(area.AwayTemperature));
		Assert.All(written.Areas, area => Assert.Null(area.Heaters));

		Assert.True(written.Global.AreasAutoDiscovered);
	}

	[Fact]
	public void One_connection_gets_one_scan()
	{
		ARecordedHouse house = AHouseOfThree();
		AreaDiscoveryScheduler scanning = Scanner(ADocumentWithNoRooms());

		Assert.NotEmpty(scanning.ScanIfNeeded(1, house, house.Resolver()));
		Assert.Empty(scanning.ScanIfNeeded(1, house, house.Resolver()));
	}

	[Fact]
	public void A_document_that_has_been_scanned_is_never_scanned_again()
	{
		ARecordedHouse house = AHouseOfThree();
		AHostedDocument document = ADocumentWithNoRooms();

		Assert.NotEmpty(Scanner(document).ScanIfNeeded(1, house, house.Resolver()));

		// A household that removes every room must find them still gone, whatever a later connection reads.
		HeatingConfig emptied = document.Host.Store.Load();
		emptied.Areas.Clear();
		Assert.True(document.Host.Save(emptied).Written);

		Assert.Empty(Scanner(document).ScanIfNeeded(2, house, house.Resolver()));
		Assert.Empty(document.Host.Store.Load().Areas);
	}

	[Fact]
	public void A_house_with_nothing_labelled_is_looked_at_again_on_the_next_connection()
	{
		ARecordedHouse bare = new(
			[ARecordedHouse.Area("stue")],
			[new RecordedEntity("switch.stue", "stue", [], ARecordedHouse.Theirs)]);

		AHostedDocument document = ADocumentWithNoRooms();
		AreaDiscoveryScheduler scanning = Scanner(document);

		Assert.Empty(scanning.ScanIfNeeded(1, bare, bare.Resolver()));
		Assert.False(document.Host.Store.Load().Global.AreasAutoDiscovered);

		ARecordedHouse labelled = AHouseOfThree();

		Assert.NotEmpty(scanning.ScanIfNeeded(2, labelled, labelled.Resolver()));
	}

	/// <summary>Two Areas holding a labelled heater, and one holding only this integration's own thermostat.</summary>
	private static ARecordedHouse AHouseOfThree() =>
		new(
			[ARecordedHouse.Area("stue"), ARecordedHouse.Area("bad"), ARecordedHouse.Area("loft")],
			[
				ARecordedHouse.Heater("switch.stue", "stue"),
				ARecordedHouse.Heater("climate.bad", "bad"),
				ARecordedHouse.OurThermostat("climate.loft", "loft")
			]);

	private static AHostedDocument ADocumentWithNoRooms() => AHostedDocument.Runnable();

	private static AreaDiscoveryScheduler Scanner(AHostedDocument document) =>
		new(document.Host, NullLogger<AreaDiscoveryScheduler>.Instance);
}
