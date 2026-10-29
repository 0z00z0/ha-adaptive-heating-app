using System.Text.RegularExpressions;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The validator's messages as a page shows them: the settings file's key names read as words.</summary>
/// <remarks>The engine's wording is kept for the log; only the key names change, never the sentence.</remarks>
public static partial class ValidationWording
{
	/// <summary><c>Global.ExcludeLabel is …</c> reads as <c>Exclude label is …</c>.</summary>
	public static string Plain(string message)
	{
		ArgumentNullException.ThrowIfNull(message);

		string keyed = DottedKey().Replace(message, match => match.Groups["owner"].Value is "HouseMode" or "PeriodSelect"
			? $"{Words(match.Groups["owner"].Value)} {Words(match.Groups["key"].Value)}"
			: Words(match.Groups["key"].Value));
		string plain = BracketedKey().Replace(keyed, match => Words(match.Groups["key"].Value));

		return plain.Length > 0 ? char.ToUpper(plain[0], CultureInfo.CurrentCulture) + plain[1..] : plain;
	}

	// "ExcludeLabel" -> "exclude label"; an acronym run such as "HA" stays whole.
	private static string Words(string pascal) =>
		SplitPoint().Replace(pascal, " ").ToLower(CultureInfo.CurrentCulture);

	[GeneratedRegex(@"\b(?<owner>Global|Defaults|HouseMode|PeriodSelect)\.(?<key>[A-Z][A-Za-z]+)")]
	private static partial Regex DottedKey();

	[GeneratedRegex(@"\[(?<key>PeriodSelect|HouseMode|Global|Defaults)\]")]
	private static partial Regex BracketedKey();

	[GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
	private static partial Regex SplitPoint();
}

/// <summary>The one-line headlines over the validator's lists; the lists themselves sit behind an (i).</summary>
public static class ValidationHeadline
{
	public static string Errors(int count) => $"{count} to fix before saving";

	public static string SkippedRooms(int count) => count == 1 ? "1 room skipped" : $"{count} rooms skipped";

	public static string Warnings(int count) => $"{count} worth knowing";
}
