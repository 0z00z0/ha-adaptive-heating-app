using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Serilog.Events;

namespace AdaptiveLighting.NetDaemon;

/// <summary>The one place a runtime value becomes text in the durable log.</summary>
/// <remarks>
///     <see cref="DurableLogFormatter"/> renders the template itself, so nothing reaches the file except through here.
///     A property whose name reads as a credential is replaced whole, nested values included. A value shaped like one
///     has that part replaced: a JWT, a <c>password=</c> pair, a URI's user info, a long opaque mixed-case run.
///     Entity ids and paths survive both, being lower case.
/// </remarks>
internal static partial class LoggedValue
{
	/// <summary>What a rejected value is written as, kept in the line so a reader knows something was dropped.</summary>
	public const string Hidden = "***";

	/// <summary>How much of one property value a line will carry.</summary>
	public const int MaxValueLength = 256;

	private const int MaxDepth = 3;
	private const string Truncated = "...";

	/// <summary>Renders one of a log event's named properties.</summary>
	public static string Of(string name, LogEventPropertyValue? value) => Of(name, value, depth: 0);

	/// <summary>Puts one runtime string on a single line with its credentials removed, and caps its length.</summary>
	/// <remarks>
	///     Also applied to the message template's literal text: a template is normally a compile-time constant, but
	///     <c>ILogger.Log(someString)</c> compiles, and that string is runtime data like any other.
	/// </remarks>
	public static string Text(string? text, int maxLength = MaxValueLength)
	{
		if (text is null)
			return "null";

		StringBuilder flattened = new(text.Length);

		foreach (char character in text)
			flattened.Append(char.IsControl(character) ? ' ' : character);

		string guarded = OpaqueRun().Replace(
			JsonWebToken().Replace(
				CredentialPair().Replace(
					UriUserInfo().Replace(flattened.ToString(), $"$1{Hidden}@"),
					$"$1={Hidden}"),
				Hidden),
			Hidden);

		return Cap(guarded, maxLength);
	}

	private static string Of(string name, LogEventPropertyValue? value, int depth)
	{
		if (value is null)
			return "null";

		if (name.Length > 0 && CredentialName().IsMatch(name))
			return Hidden;

		if (depth > MaxDepth)
			return Truncated;

		return value switch
		{
			ScalarValue scalar => Scalar(scalar.Value),
			SequenceValue sequence =>
				"[" + string.Join(", ", sequence.Elements.Select(element => Of(name, element, depth + 1))) + "]",
			StructureValue structure =>
				"{" + string.Join(", ", structure.Properties.Select(
					property => property.Name + "=" + Of(property.Name, property.Value, depth + 1))) + "}",
			DictionaryValue dictionary =>
				"{" + string.Join(", ", dictionary.Elements.Select(
					pair => Scalar(pair.Key.Value) + "=" + Of(pair.Key.Value as string ?? string.Empty, pair.Value, depth + 1))) + "}",
			_ => Text(value.ToString())
		};
	}

	// bool, char and an enum have no room to hide anything; everything else formattable is written invariantly, so
	// a decimal comma or a local date format never reaches the file.
	private static string Scalar(object? raw) =>
		raw switch
		{
			null => "null",
			string text => Text(text),
			bool or char or Enum => raw.ToString() ?? string.Empty,
			IFormattable formattable => Text(formattable.ToString(null, CultureInfo.InvariantCulture)),
			_ => Text(raw.ToString())
		};

	private static string Cap(string text, int maxLength)
	{
		if (maxLength <= 0 || text.Length <= maxLength)
			return text;

		int cut = maxLength;

		// Never split a surrogate pair; a lone half encodes as U+FFFD and reads as corruption.
		if (char.IsHighSurrogate(text[cut - 1]))
			cut--;

		return string.Concat(text.AsSpan(0, cut), Truncated);
	}

	[GeneratedRegex(
		"token|password|passwd|pwd|secret|credential|api[_-]?key|connectionstring",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CredentialName();

	// The optional quote after the key is what covers JSON: "password": "hunter2" puts a closing quote between
	// the key and the separator, which YAML and an ini file do not.
	[GeneratedRegex(
		@"(token|password|passwd|pwd|secret|credential|api[_-]?key)[""']?\s*[=:]\s*(""[^""]*""|'[^']*'|[^\s,;&""']+)",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CredentialPair();

	/// <remarks>A Home Assistant long-lived access token is a JWT, and its header always starts <c>eyJ</c>.</remarks>
	[GeneratedRegex(
		@"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}",
		RegexOptions.CultureInvariant)]
	private static partial Regex JsonWebToken();

	/// <remarks>Covers a Samba credential written as a URL, <c>smb://user:pass@nas</c>.</remarks>
	[GeneratedRegex(
		@"([A-Za-z][A-Za-z0-9+.\-]*://)[^\s/@]+:[^\s/@]*@",
		RegexOptions.CultureInvariant)]
	private static partial Regex UriUserInfo();

	// The alphabet omits / \ : and . so the run breaks at a path or URL separator, and keeps _ and -, so the upper
	// case requirement is what spares a 32+ character entity id or GUID area id, both of which are lower case. The
	// cost is an all lower case secret of the same shape, and a classic-base64 secret containing a slash.
	[GeneratedRegex(
		"(?<![A-Za-z0-9+=_-])(?=[A-Za-z0-9+=_-]*[a-z])(?=[A-Za-z0-9+=_-]*[A-Z])(?=[A-Za-z0-9+=_-]*[0-9])"
		+ "[A-Za-z0-9+=_-]{32,}(?![A-Za-z0-9+=_-])",
		RegexOptions.CultureInvariant)]
	private static partial Regex OpaqueRun();
}
