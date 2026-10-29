using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using NetDaemon.HassModel;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>NetDaemon's runtime normally brings the entity model up, and the add-on does not reference it.</summary>
/// <remarks>Proved here against the add-on's own composition, so the registration cannot leave without a
/// failure.</remarks>
public sealed class TheEntityModelTests
{
	[Fact]
	public async Task The_model_comes_up_in_the_add_ons_own_composition()
	{
		DirectoryInfo data = Directory.CreateTempSubdirectory("adaptive-heating-entity-model");

		try
		{
			await using WebApplicationFactory<Program> addOn = new();

			// Taken down asynchronously, or the durable log is still open when the directory goes.
			await using WebApplicationFactory<Program> running = addOn.WithWebHostBuilder(host =>
			{
				// Everything the add-on keeps on the box goes to a directory of this test's own.
				host.UseSetting("AdaptiveHeating:DocumentPath", Path.Combine(data.FullName, "heating.json"));
				host.UseSetting("AdaptiveHeating:KeyRingPath", Path.Combine(data.FullName, "keys"));
				host.UseSetting("AdaptiveHeating:OptionsPath", Path.Combine(data.FullName, "options.json"));

				// Nothing is driven and nothing reaches for Home Assistant: what is proved is the composition.
				host.ConfigureServices(services => services.RemoveAll<IHostedService>());
			});

			await using AsyncServiceScope scope = running.Services.CreateAsyncScope();

			Assert.NotNull(scope.ServiceProvider.GetService<IHaContext>());
			Assert.NotNull(scope.ServiceProvider.GetService<ICacheManager>());

			// The root provider refusing it is the add-on's own scope validation.
			Assert.Throws<InvalidOperationException>(() => running.Services.GetService<IHaContext>());
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
