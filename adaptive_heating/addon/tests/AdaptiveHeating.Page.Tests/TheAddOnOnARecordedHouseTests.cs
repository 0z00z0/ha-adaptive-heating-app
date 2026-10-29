using AdaptiveHeating.AddOn.Integration;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NetDaemon.Client;
using NetDaemon.HassModel;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Every consumer reads the house through the entity model, so the add-on's own composition has to
/// bring that model up and answer from it.</summary>
public sealed class TheAddOnOnARecordedHouseTests
{
	[Fact]
	public async Task The_add_on_starts_against_a_recorded_house_and_answers_from_the_model()
	{
		DirectoryInfo data = Directory.CreateTempSubdirectory("adaptive-heating-recorded-house");

		AHouseUnderTest house = new(CoreUnderTest.Thermostat(current: 19.5), CoreUnderTest.SomebodyElses());
		RunnerUnderTest runner = new();

		try
		{
			await using WebApplicationFactory<Program> addOn = new();

			await using WebApplicationFactory<Program> running = addOn.WithWebHostBuilder(host =>
			{
				host.UseSetting("AdaptiveHeating:DocumentPath", Path.Combine(data.FullName, "heating.json"));
				host.UseSetting("AdaptiveHeating:KeyRingPath", Path.Combine(data.FullName, "keys"));
				host.UseSetting("AdaptiveHeating:OptionsPath", Path.Combine(data.FullName, "options.json"));

				host.ConfigureServices(services =>
				{
					// A recorded house in place of Home Assistant: the client's runner, the model and its caches.
					services.RemoveAll<IHomeAssistantRunner>();
					services.AddSingleton<IHomeAssistantRunner>(runner);

					services.RemoveAll<IHaContext>();
					services.AddScoped<IHaContext>(_ => house);

					services.RemoveAll<IHaRegistry>();
					services.AddScoped<IHaRegistry>(_ => house);

					services.RemoveAll<ICacheManager>();
					services.AddScoped<ICacheManager>(_ => new ACacheAlreadyFilled());
				});
			});

			CoreConnection connection = running.Services.GetRequiredService<CoreConnection>();

			await using CoreUnderTest core = new();

			runner.HandOver(core);

			Assert.True(
				await AnOpenConnection.UntilAsync(() => connection.IsOpen),
				"The add-on started and never read the recorded house through the entity model.");

			// Somebody else's lamp is in the recorded house and is not one the heating reads.
			EntityState only = Assert.Single(connection.House);

			Assert.Equal(ACabin.Thermostat, only.EntityId);
			Assert.Equal(19.5, only.AttrDouble(HeatingActions.CurrentTemperatureAttribute));
		}
		finally
		{
			// The durable log is released a moment after the host goes, so the directory is not free at once.
			for (int attempt = 0; attempt < 20; attempt++)
			{
				try
				{
					data.Delete(recursive: true);
					break;
				}
				catch (IOException)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(100));
				}
			}
		}
	}
}
