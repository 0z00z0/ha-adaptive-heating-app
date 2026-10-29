using System.Text.Json;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// The record goes into a calendar Home Assistant keeps itself and no other, so which integration created a
/// calendar has to be known. Every calendar's capability list looks alike, and the entity registry is what
/// tells them apart.
/// </summary>
public sealed class WhichCalendarsAreOfferedTests
{
	private const string Local = "calendar.adaptiveheating_log";
	private const string Somebody = "calendar.alex_google";

	[Fact]
	public void Every_calendar_is_offered_and_only_a_local_one_is_marked_local()
	{
		IReadOnlyList<CalendarOnOffer> offered = CalendarsInTheHouse.Offered(
			States(
				ACalendar.Entity(Local, "Heating record"),
				ACalendar.Entity(Somebody, "Alex"),
				Thermostat()),
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				[Local] = CalendarActions.LocalPlatform,
				[Somebody] = "google"
			});

		Assert.Equal(2, offered.Count);

		// Both directions: the local one is marked and the other one is not.
		Assert.True(offered.Single(calendar => calendar.EntityId == Local).Local);
		Assert.False(offered.Single(calendar => calendar.EntityId == Somebody).Local);

		// The name is read beside the choice and the entity id is what it is chosen by.
		Assert.Equal("Alex", offered[0].Name);
		Assert.Equal("Heating record", offered[1].Name);
	}

	[Fact]
	public void A_calendar_the_registry_says_nothing_about_is_offered_and_is_not_local()
	{
		// A registry read that did not go through leaves this empty, and no calendar may then take the record.
		IReadOnlyList<CalendarOnOffer> offered = CalendarsInTheHouse.Offered(
			States(ACalendar.Entity(Local, "Heating record")),
			new Dictionary<string, string>(StringComparer.Ordinal));

		Assert.False(Assert.Single(offered).Local);
	}

	[Fact]
	public void The_registry_answer_names_the_integration_behind_each_calendar()
	{
		TheCalendarsOnOffer offered = new(entityId => entityId switch
		{
			Local => Made(Local, CalendarActions.LocalPlatform),
			Somebody => Made(Somebody, "google"),
			_ => RegistryAnswer.NothingKnownAbout(entityId)
		});

		IReadOnlyList<CalendarOnOffer> both = offered.Offered(States(
			ACalendar.Entity(Local, "Heating record"),
			ACalendar.Entity(Somebody, "Alex"),
			Thermostat()));

		// Only the calendars, and each marked by the integration that made it.
		Assert.Equal(2, both.Count);
		Assert.True(both.Single(calendar => calendar.EntityId == Local).Local);
		Assert.False(both.Single(calendar => calendar.EntityId == Somebody).Local);
	}

	[Fact]
	public void A_calendar_the_registry_names_no_integration_for_is_offered_and_is_not_local()
	{
		TheCalendarsOnOffer offered = new(RegistryAnswer.NothingKnownAbout);

		IReadOnlyList<CalendarOnOffer> one = offered.Offered(States(ACalendar.Entity(Local, "Heating record")));

		Assert.False(Assert.Single(one).Local);
	}

	private static RegistryAnswer Made(string entityId, string platform) =>
		RegistryAnswer.NothingKnownAbout(entityId) with { Platform = platform };

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat() =>
		$$"""
		{ "entity_id": "{{ACabin.Thermostat}}", "state": "heat", "attributes": { "friendly_name": "Kitchen" } }
		""";
}
