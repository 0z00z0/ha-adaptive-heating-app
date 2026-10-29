using System.Text;

namespace Adaptive.Settings;

/// <summary>A configuration problem scoped to one area: the area is skipped and the rest of the house keeps working.</summary>
public sealed record AreaError(string AreaName, string Message);

/// <summary>The outcome of <see cref="ConfigValidator.Validate"/>.</summary>
/// <remarks>
///     Document-level <see cref="Errors"/> are fatal; <see cref="AreaErrors"/> are not, because an entity renamed in
///     HA must not black out the whole house.
/// </remarks>
public sealed class ValidationResult
{
	private readonly List<string> _errors = [];
	private readonly List<AreaError> _areaErrors = [];
	private readonly List<string> _warnings = [];

	/// <summary>Document-level errors. Non-empty means the configuration cannot be run at all.</summary>
	public IReadOnlyList<string> Errors => _errors;

	/// <summary>Area-level errors. Each names one area that will be skipped.</summary>
	public IReadOnlyList<AreaError> AreaErrors => _areaErrors;

	/// <summary>Non-blocking warnings. Rendered and logged, but they never refuse a save or stop the engine.</summary>
	public IReadOnlyList<string> Warnings => _warnings;

	/// <summary>Whether the document can be run. Area errors and warnings do not make a document invalid.</summary>
	public bool IsValid => _errors.Count == 0;

	public void AddError(string message) => _errors.Add(message);

	public void AddAreaError(string areaName, string message) => _areaErrors.Add(new AreaError(areaName, message));

	public void AddWarning(string message) => _warnings.Add(message);

	public override string ToString()
	{
		StringBuilder text = new();

		foreach (string error in _errors)
			text.Append("- ").AppendLine(error);

		foreach (AreaError areaError in _areaErrors)
			text.Append("- [").Append(areaError.AreaName).Append("] ").AppendLine(areaError.Message);

		if (_warnings.Count > 0)
		{
			text.AppendLine("Warnings:");
			foreach (string warning in _warnings)
				text.Append("- ").AppendLine(warning);
		}

		return text.Length == 0 ? "No configuration problems." : text.ToString();
	}

	public string ToHtml()
	{
		StringBuilder text = new("<ul>");

		foreach (string error in _errors)
			text.Append("<li>").Append(Escape(error)).Append("</li>");

		foreach (AreaError areaError in _areaErrors)
			text.Append("<li><b>").Append(Escape(areaError.AreaName)).Append("</b>: ").Append(Escape(areaError.Message)).Append("</li>");

		text.Append("</ul>");

		if (_warnings.Count > 0)
		{
			text.Append("<p><b>Warnings</b></p><ul>");
			foreach (string warning in _warnings)
				text.Append("<li>").Append(Escape(warning)).Append("</li>");
			text.Append("</ul>");
		}

		return text.ToString();
	}

	private static string Escape(string text) =>
		text.Replace("&", "&amp;", StringComparison.Ordinal)
			.Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal);
}
