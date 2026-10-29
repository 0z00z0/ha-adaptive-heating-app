namespace Adaptive.Settings;

/// <summary>The settings editor's write: the whole document, refused by the host when the file has moved on.</summary>
/// <remarks>
///     That page edits a whole draft, with no smaller part to scope a check to, so it takes a whole-document
///     token. The room page's, which is per area, is <see cref="RoomWrite"/>.
/// </remarks>
public static class DocumentWrite
{
	/// <summary>The token the editor carries while it is open.</summary>
	public static string Open(HeatingConfig document) => ConfigStamp.OfDocument(document);

	/// <summary>Writes the whole draft, or refuses it where the file no longer holds what the page was shown.</summary>
	public static SaveResult Save(HeatingDocumentHost host, string stamp, HeatingConfig draft)
	{
		ArgumentNullException.ThrowIfNull(host);

		return host.Save(draft, new StampCheck(stamp, ConfigStamp.OfDocument));
	}
}
