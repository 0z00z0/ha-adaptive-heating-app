namespace Adaptive.UI;

/// <summary>How much the text behind an (i) matters, which is the colour the icon wears.</summary>
public enum InfoSeverity
{
	/// <summary>Plain help: the neutral (i).</summary>
	Neutral,

	/// <summary>Worth knowing, stops nothing: yellow.</summary>
	Warn,

	/// <summary>Needs attention: red.</summary>
	Bad
}

/// <summary>The class each design's icon takes for a severity.</summary>
public static class InfoSeverityClass
{
	public static string? Of(InfoSeverity severity) => severity switch
	{
		InfoSeverity.Warn => "warn",
		InfoSeverity.Bad => "bad",
		_ => null
	};
}
