using Adaptive.Engine;
using Adaptive.Settings;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A settings document in a directory of its own, behind the one write path.</summary>
internal sealed class AHostedDocument
{
	/// <summary>The weather entity a runnable document names, so the warm-up source has one.</summary>
	public const string Forecast = "weather.cabin";

	private AHostedDocument(HeatingDocumentHost host) => Host = host;

	public HeatingDocumentHost Host { get; }

	/// <summary>The smallest document the engine can run, with a forecast entity named.</summary>
	public static AHostedDocument Runnable()
	{
		HeatingConfig config = HeatingConfig.CreateDefault();
		config.Global.ForecastEntity = Forecast;

		return Holding(HeatingConfigDocument.Serialize(config));
	}

	// Nothing known, so every referential check is skipped and a test may name an entity the house has not got.
	public static AHostedDocument Holding(string yaml) => Holding(yaml, _ => ValidationContext.None);

	/// <summary>A document checked against what <paramref name="houseFacts"/> says Home Assistant holds.</summary>
	public static AHostedDocument Holding(string yaml, Func<HeatingConfig, ValidationContext> houseFacts)
	{
		ArgumentNullException.ThrowIfNull(houseFacts);

		string path = Path.Combine(ACabin.ScratchDirectory(), "AdaptiveHeating.yaml");
		File.WriteAllText(path, yaml);

		HeatingConfigStore store = new(path, NullLogger<HeatingConfigStore>.Instance);

		return new AHostedDocument(new HeatingDocumentHost(
			store,
			houseFacts,
			() => { },
			NullLogger<HeatingDocumentHost>.Instance));
	}
}
