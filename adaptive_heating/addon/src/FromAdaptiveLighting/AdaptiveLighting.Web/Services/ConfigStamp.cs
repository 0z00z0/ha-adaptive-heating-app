using System.Security.Cryptography;
using System.Text;

namespace Adaptive.Settings;

/// <summary>
///     A token over the part of the document a page is allowed to write, taken when the page loads and compared
///     against the file at save time.
/// </summary>
/// <remarks>
///     Equal tokens mean nothing else has written that part since, so the page's copy can be applied to a freshly
///     read document without reverting another write. Scoped per area for the room page, per document for the
///     settings editor.
/// </remarks>
public static class ConfigStamp
{
	/// <summary>The token for an area slot no area fills. Never collides: a real token is hex.</summary>
	private const string Absent = "absent";

	/// <summary>The token for one area slot, keyed by area id.</summary>
	public static string OfArea(HeatingConfig document, string? areaId)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (areaId is not { Length: > 0 })
			return Absent;

		AreaConfig? area = document.Areas.FirstOrDefault(
			candidate => string.Equals(candidate.AreaId, areaId, StringComparison.Ordinal));

		return area is null ? Absent : Of(new HeatingConfig { Areas = [area] });
	}

	public static string OfDocument(HeatingConfig document)
	{
		ArgumentNullException.ThrowIfNull(document);

		return Of(document);
	}

	// Through the document serialiser, so a property added to the schema is covered without a change here.
	private static string Of(HeatingConfig fragment) =>
		Convert.ToHexStringLower(
			SHA256.HashData(Encoding.UTF8.GetBytes(HeatingConfigDocument.Serialize(fragment))));
}
