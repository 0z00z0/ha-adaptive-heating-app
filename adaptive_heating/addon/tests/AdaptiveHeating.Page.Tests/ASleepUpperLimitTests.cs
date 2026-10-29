using Adaptive.Settings;

using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A sleep option names the period that is its upper limit; one that finds none is refused, as lighting refuses it.</summary>
public sealed class ASleepUpperLimitTests
{
	[Fact]
	public void A_sleep_option_pointing_at_no_period_is_refused()
	{
		SaveResult saved = Save(ModeKind.Sleep);

		Assert.Equal((SaveStatus.Rejected, "Not saved. The file on disk is untouched."), (saved.Status, saved.Message));
		Assert.Contains(saved.Validation.Errors, error => error.Contains("ClampPeriodId 'gone' matches no configured period", StringComparison.Ordinal));
	}

	[Fact]
	public void The_same_pointer_on_another_kind_is_saved_and_warned_inert()
	{
		SaveResult saved = Save(ModeKind.Away);

		Assert.True(saved.Written);
		Assert.Contains(saved.Validation.Warnings, warning => warning.Contains("has a ClampPeriodId but its kind is Away; it is inert", StringComparison.Ordinal));
	}

	[Fact]
	public void A_sleep_option_with_no_period_to_hold_the_rooms_at_is_refused()
	{
		// No clamp, no period switching to it, and no period named night: the chain finds nothing.
		SaveResult saved = Save(ModeKind.Sleep, clamp: null, periodName: "Evening");

		Assert.Equal(SaveStatus.Rejected, saved.Status);
		Assert.Contains(saved.Validation.Errors, error => error.Contains("Sleep option 'Asleep' has no period to hold the rooms at or below", StringComparison.Ordinal));
	}

	[Fact]
	public void The_upper_limit_is_the_period_of_the_sleep_option_in_force_and_not_the_first()
	{
		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Periods =
		[
			new TimePeriodConfig { Id = "evening", Name = "Evening", Start = "00:00", Temperature = 21 },
			new TimePeriodConfig { Id = "late", Name = "Late", Start = "22:00", Temperature = 15 },
			new TimePeriodConfig { Id = "early", Name = "Early", Start = "05:00", Temperature = 18 }
		];
		document.Areas = [new AreaConfig { AreaId = ACabin.RoomId }];
		document.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.house_mode",
			Options =
			[
				new HouseModeOptionConfig { Value = "Asleep", Kind = ModeKind.Sleep, ClampPeriodId = "late" },
				new HouseModeOptionConfig { Value = "Napping", Kind = ModeKind.Sleep, ClampPeriodId = "early" }
			]
		};

		HeatingDocument composed = RoomTemperatures.Compose(ACabin.WithOneRoom(), document, document.Periods[0], since: null, optionInForce: "Napping");

		// 21 held at or below the napping option's 18; the first option's period would give 15.
		Assert.Equal(18, composed.Rooms[0].ModeTemperatures[HeatingMode.Night].Value);
	}

	private static SaveResult Save(ModeKind kind, string? clamp = "gone", string periodName = "Night")
	{
		string path = Path.Combine(ACabin.ScratchDirectory(), "heating.yaml");

		HeatingDocumentHost host = new(
			new HeatingConfigStore(path, NullLogger<HeatingConfigStore>.Instance),
			_ => ValidationContext.None,
			() => { },
			NullLogger<HeatingDocumentHost>.Instance);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Periods = [new TimePeriodConfig { Id = periodName.ToLowerInvariant(), Name = periodName, Start = "23:00", Temperature = 16 }];
		document.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.house_mode",
			Options =
			[
				new HouseModeOptionConfig { Value = "Home", Kind = ModeKind.Normal },
				new HouseModeOptionConfig { Value = "Asleep", Kind = kind, ClampPeriodId = clamp }
			]
		};

		return host.Save(document);
	}
}
