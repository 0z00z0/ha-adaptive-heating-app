using AdaptiveHeating.AddOn.Integration;

using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Lighting's command log: a file per thermostat, a line per command, only while it is switched on.</summary>
public sealed class EveryThermostatCommandIsLoggedTests
{
	[Fact]
	public async Task A_command_lands_in_its_thermostats_file_while_the_log_is_on_and_nowhere_while_it_is_off()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();
		string directory = ACabin.ScratchDirectory();
		CommandLog log = new(directory, NullLogger<CommandLog>.Instance);
		bool on = false;
		Thermostats thermostats = new(open.Api, () => on ? log : null, () => new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero));

		await thermostats.SetTemperatureAsync(ACabin.Thermostat, 20.5, CancellationToken.None);
		string file = Path.Combine(directory, CommandLog.SafeName(ACabin.Thermostat) + ".log");

		Assert.False(File.Exists(file));

		on = true;
		await thermostats.SetTemperatureAsync(ACabin.Thermostat, 21.5, CancellationToken.None);

		Assert.Equal(
			[$"# {ACabin.Thermostat}", "2026-10-26T07:00:00.000+00:00 action=climate.set_temperature temperature=21.5 outcome=called"],
			File.ReadAllLines(file));
	}
}
