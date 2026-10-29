using Adaptive.Settings;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

public sealed class TheShippedConfigurationTests
{
	// Only appsettings.json is added: no development override and no value of the test's own, so what is read
	// is what the image carries.
	[Fact]
	public void The_settings_document_is_kept_under_the_add_ons_data_folder()
	{
		IConfiguration shipped = new ConfigurationBuilder()
			.SetBasePath(AppContext.BaseDirectory)
			.AddJsonFile("appsettings.json", optional: false)
			.Build();

		string? path = shipped[HeatingConfigPath.ConfigPathKey];

		Assert.NotNull(path);
		Assert.StartsWith("/data/", path.Replace('\\', '/'), StringComparison.Ordinal);
	}
}
