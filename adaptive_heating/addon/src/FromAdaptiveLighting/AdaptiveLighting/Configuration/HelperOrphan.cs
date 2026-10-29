namespace Adaptive.Settings;

/// <summary>What to call a stored row whose Home Assistant dropdown no longer offers its value.</summary>
/// <remarks>A rename and a removal are indistinguishable from here, so no wording claims which one happened.</remarks>
public static class HelperOrphan
{
	/// <summary>The badge on a row the helper no longer offers.</summary>
	public const string Badge = "not in the helper";

	/// <summary>The badge's title, and what the row means wherever there is room to say it.</summary>
	public const string Explanation =
		"Home Assistant's dropdown no longer offers this value. It was renamed or removed — from here the two "
		+ "look the same.";

	/// <summary>Opens a warning about one: <c>'Natt' is no longer one of the helper's options</c>.</summary>
	public static string NoLongerOffered(string? value) =>
		$"'{value?.Trim()}' is no longer one of the helper's options";
}
