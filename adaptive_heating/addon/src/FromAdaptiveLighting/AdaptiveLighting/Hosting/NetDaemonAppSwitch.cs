using System.Text;

namespace Adaptive.Settings;

/// <summary>Derives the entity id of the switch the gate falls back to when the document names none.</summary>
/// <remarks>
///     An <c>input_boolean</c> named after the add-on, so the heating can be switched off from a dashboard without
///     stopping the add-on. Nothing creates it: a household that wants the switch adds the helper, and until then
///     the validator warns and the engine runs ungated.
/// </remarks>
public static class AddOnSwitch
{
	private const string Prefix = "input_boolean.";

	/// <summary>The switch's entity id for this add-on.</summary>
	public static string EntityId { get; } = EntityIdForName("Adaptive heating");

	/// <summary>The switch entity id for an application called <paramref name="name"/>.</summary>
	/// <remarks>A string overload so the slug can be asserted without an application to read a name from.</remarks>
	public static string EntityIdForName(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		return Prefix + Slug(name);
	}

	/// <summary>
	///     A '.' becomes '_', a '_' is inserted before an uppercase following a lowercase or digit, everything is
	///     lowercased, and runs of '_' collapse.
	/// </summary>
	private static string Slug(string typeName)
	{
		StringBuilder builder = new(typeName.Length + 8);

		foreach (char c in typeName)
		{
			if (c == '.' || c == ' ')
			{
				Append(builder, '_');
				continue;
			}

			if (char.IsUpper(c) && builder.Length > 0)
			{
				char previous = builder[^1];
				if (char.IsLower(previous) || char.IsDigit(previous))
					builder.Append('_');
			}

			Append(builder, char.ToLowerInvariant(c));
		}

		return builder.ToString().Trim('_');

		// Never two underscores in a row: a segment ending in an uppercase would otherwise double up.
		static void Append(StringBuilder builder, char c)
		{
			if (c == '_' && builder.Length > 0 && builder[^1] == '_')
				return;

			builder.Append(c);
		}
	}
}
