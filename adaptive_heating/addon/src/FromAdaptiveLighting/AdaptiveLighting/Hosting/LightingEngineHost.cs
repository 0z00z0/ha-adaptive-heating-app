using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace Adaptive.Settings;

/// <summary>How a call to <see cref="HeatingDocumentHost.Save"/> ended.</summary>
public enum SaveStatus
{
	/// <summary>Written, and a pass over the rooms asked for.</summary>
	Saved,

	/// <summary>Refused: the document has errors that make it unrunnable. Nothing was written.</summary>
	Rejected,

	/// <summary>Refused: the file changed while the page sat open, and saving would revert that. Nothing was written.</summary>
	/// <remarks>A retry cannot clear it, so a page offers a reload.</remarks>
	Conflicted,

	/// <summary>Written, but the file system failed afterwards and the document does not read back.</summary>
	Failed
}

/// <summary>What the host's write step does with a document the engine cannot run.</summary>
internal enum InvalidDocument
{
	/// <summary>Refuse the write and leave the file as it was.</summary>
	Refuse,

	/// <summary>Write it, and hand the errors back to be reported.</summary>
	WriteAnyway
}

/// <summary>What one pass through the host's write step did.</summary>
/// <param name="Written">Whether the bytes reached the disk.</param>
/// <param name="Validation">The document as validated after normalisation, whether or not it was written.</param>
internal sealed record ConfigWriteResult(bool Written, ValidationResult Validation);

/// <summary>The outcome of a save. <c>Message</c> is one sentence for the operator.</summary>
public sealed record SaveResult(SaveStatus Status, ValidationResult Validation, string Message)
{
	/// <summary>Whether the document reached disk.</summary>
	public bool Written => Status is SaveStatus.Saved;
}

/// <summary>What one writer was shown, and how to take that same reading off a freshly read document.</summary>
/// <remarks>
///     The scope belongs to the writer — one area for a room's page, the whole document for the settings editor —
///     and the comparison belongs to the host, so no writer can skip it.
/// </remarks>
public sealed record StampCheck(string Expected, Func<HeatingConfig, string> Of);

/// <summary>The one way the heating document reaches the disk.</summary>
/// <remarks>
///     A bad document never throws out of here: the caller is either a hosted service whose death would take the
///     Home Assistant connection with it, or a Razor component rendering a page. The host reports itself faulted
///     instead.
/// </remarks>
public sealed class HeatingDocumentHost
{
	private const string Conflict =
		"The settings file was changed somewhere else while this page was open, so nothing was written — saving "
		+ "now would have reverted that change. Reload to see it as it now stands, then make the change again.";

	private readonly HeatingConfigStore _store;
	private readonly Func<HeatingConfig, ValidationContext> _houseFacts;
	private readonly Action _askForAPass;
	private readonly ILogger<HeatingDocumentHost> _logger;

	// NoLogLevelControl where the host provides none, so the document's level is then ignored.
	private readonly ILogLevelControl _logLevel;

	// Two browser tabs saving must not interleave a read with a write.
	private readonly Lock _gate = new();

	// Replayed, because the recorder may subscribe after the first read. One is enough: nothing precedes the start.
	private readonly ReplaySubject<EngineNotice> _notices = new(bufferSize: 1);

	private bool _startNoticed;

	public HeatingDocumentHost(
		HeatingConfigStore store,
		Func<HeatingConfig, ValidationContext> houseFacts,
		Action askForAPass,
		ILogger<HeatingDocumentHost> logger,
		ILogLevelControl? logLevel = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(houseFacts);
		ArgumentNullException.ThrowIfNull(askForAPass);
		ArgumentNullException.ThrowIfNull(logger);

		_store = store;
		_houseFacts = houseFacts;
		_askForAPass = askForAPass;
		_logger = logger;
		_logLevel = logLevel ?? NoLogLevelControl.Instance;
	}

	/// <summary>The add-on's built-in enable switch, the gate when the document names none.</summary>
	public string? DefaultGateEntity => AddOnSwitch.EntityId;

	/// <summary>The house-wide things the add-on did to itself: it started, or a save was taken up.</summary>
	public IObservable<EngineNotice> Notices => _notices;

	/// <summary>The heaters more than one room commands, as the last pass over the rooms found them.</summary>
	public IReadOnlyList<SuspectLight> SharedLights { get; private set; } = [];

	/// <summary>What the pass over the rooms found shared, for the House tab to show.</summary>
	public void FoundSharedLights(IReadOnlyList<SuspectLight> shared) => SharedLights = shared ?? [];

	/// <summary>Whether the document's log level takes effect; <c>false</c> where the host sets it.</summary>
	public bool CanSetLogLevel => _logLevel.IsSupported;

	/// <summary>The document's own file, which is what the page names and what every write goes through.</summary>
	public HeatingConfigStore Store => _store;

	/// <summary>Why nothing is running, or <c>null</c> while the document is readable.</summary>
	public string? Fault { get; private set; }

	/// <summary>The document as last validated, which is what the page renders beneath the save bar.</summary>
	public ValidationResult LastValidation { get; private set; } = new();

	/// <summary>The errors the load path's own rewrite went out with, or <c>null</c> where it carried none.</summary>
	public ValidationResult? WrittenWithErrors { get; private set; }

	/// <summary>The document as last read or written, which is where every consumer reads a house-wide setting.</summary>
	public HeatingConfig Current { get; private set; } = HeatingConfig.CreateDefault();

	/// <summary>Reads the current document from disk, reporting an unreadable one as a visible fault.</summary>
	/// <remarks>Never throws.</remarks>
	public SaveResult Reload()
	{
		lock (_gate)
		{
			DocumentReadResult read;

			try
			{
				read = _store.Read();
			}
			catch (HeatingConfigException exception)
			{
				Fault = exception.Message;
				_logger.LogError(exception, "Could not load the heating configuration from {Path}.", _store.FilePath);

				ValidationResult unreadable = new();
				unreadable.AddError(exception.Message);
				LastValidation = unreadable;

				return new SaveResult(SaveStatus.Failed, unreadable, "The configuration file could not be read.");
			}

			if (read.Repaired || read.MintedStableKeys)
				RewriteRepaired(read.Config);

			Fault = null;
			Current = read.Config;
			LastValidation = Validate(read.Config);
			ApplyLogLevel(read.Config.Global);

			if (!_startNoticed)
			{
				_startNoticed = true;
				_notices.OnNext(new EngineNotice(EngineNoticeKind.Started, DateTimeOffset.Now));
			}

			return new SaveResult(SaveStatus.Saved, LastValidation, "Loaded.");
		}
	}

	/// <summary>The person's write: refuse a stale stamp, refuse a document the engine cannot run, then ask for a pass.</summary>
	/// <remarks>
	///     Normalisation and validation belong to <see cref="NormaliseValidateAndWrite"/>, so every writer gets
	///     them. A document-level error costs the save and nothing reaches the disk.
	/// </remarks>
	public SaveResult Save(HeatingConfig config, StampCheck? against = null)
	{
		ArgumentNullException.ThrowIfNull(config);

		lock (_gate)
		{
			if (against is not null && StaleStamp(against) is { } conflicted)
				return conflicted;

			ConfigWriteResult write;

			try
			{
				write = NormaliseValidateAndWrite(config, InvalidDocument.Refuse);
			}
			catch (HeatingConfigException exception)
			{
				// Already normalised and validated before the write threw, so asking again is the way back to what
				// the refusal check saw.
				ValidationResult attempted = Validate(config);
				LastValidation = attempted;
				_logger.LogError(exception, "Could not write the heating configuration.");

				return new SaveResult(SaveStatus.Failed, attempted, exception.Message);
			}

			if (!write.Written)
			{
				LastValidation = write.Validation;
				_logger.LogWarning(
					"Refused to save the heating configuration: {Count} document-level errors.",
					write.Validation.Errors.Count);

				return new SaveResult(
					SaveStatus.Rejected,
					write.Validation,
					"Not saved. The file on disk is untouched.");
			}

			// Re-read, never the in-memory object: a save is reported successful only once the bytes on disk parse
			// back into a document the engine accepts, which is what matters after a restart.
			HeatingConfig written;

			try
			{
				written = _store.Read().Config;
			}
			catch (HeatingConfigException exception)
			{
				Fault = $"The settings file was written but does not read back. {exception.Message}";
				_logger.LogError(exception, "The heating configuration at {Path} does not read back after the write.", _store.FilePath);

				ValidationResult unreadable = new();
				unreadable.AddError(exception.Message);
				LastValidation = unreadable;

				return new SaveResult(SaveStatus.Failed, unreadable, "The file was written but does not read back.");
			}

			Fault = null;
			Current = written;
			LastValidation = Validate(written);
			ApplyLogLevel(written.Global);

			_askForAPass();
			_notices.OnNext(new EngineNotice(EngineNoticeKind.SettingsSaved, DateTimeOffset.Now));

			return new SaveResult(SaveStatus.Saved, LastValidation, "Saved.");
		}
	}

	/// <summary>Asks for a pass over the rooms, which resolves every room from Home Assistant afresh.</summary>
	public void AskForAPass() => _askForAPass();

	/// <summary>Runs <paramref name="step"/> behind the gate every save takes, so no write lands inside it.</summary>
	public void Exclusively(Action step)
	{
		ArgumentNullException.ThrowIfNull(step);

		lock (_gate)
			step();
	}

	/// <summary>Validates <paramref name="config"/> against what Home Assistant currently knows, without saving anything.</summary>
	/// <remarks>Referential checks are skipped where Home Assistant is not connected.</remarks>
	public ValidationResult Validate(HeatingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		return ConfigValidator.Validate(config, _houseFacts(config));
	}

	// Unset or unrecognised puts back the level the host started with.
	private void ApplyLogLevel(GlobalConfig global)
	{
		if (!_logLevel.IsSupported)
			return;

		if (LogLevelSetting.Read(global.LogLevel) is { } level)
			_logLevel.Apply(level);
		else
			_logLevel.RestoreInitial();
	}

	/// <summary>The refusal, where the file has moved on since the writer read it, or <c>null</c> where it has not.</summary>
	private SaveResult? StaleStamp(StampCheck against)
	{
		HeatingConfig onDisk;

		try
		{
			onDisk = _store.Read().Config;
		}
		catch (HeatingConfigException)
		{
			// A file that will not parse has nothing worth protecting, and this save is how it gets repaired.
			return null;
		}

		if (string.Equals(against.Of(onDisk), against.Expected, StringComparison.Ordinal))
			return null;

		_logger.LogWarning("Refused to save the heating configuration: the file has changed since the page read it.");

		return new SaveResult(SaveStatus.Conflicted, LastValidation, Conflict);
	}

	/// <summary>Writes a document the reader had to repair back out, so the next load finds it clean.</summary>
	/// <remarks>Once, on load, before anything runs, and with the errors reported rather than refusing the write.</remarks>
	private void RewriteRepaired(HeatingConfig config)
	{
		try
		{
			ConfigWriteResult write = NormaliseValidateAndWrite(config, InvalidDocument.WriteAnyway);

			if (!write.Validation.IsValid)
			{
				WrittenWithErrors = write.Validation;
				_logger.LogError(
					"The configuration file was rewritten in the current schema, but the document it holds has errors. It was written all the same:{NewLine}{Validation}",
					Environment.NewLine, write.Validation);
			}

			_logger.LogInformation(
				"The configuration file has been rewritten in its current shape. The file as it was is at {Backup}.",
				_store.BackupPath);
		}
		catch (HeatingConfigException exception)
		{
			_logger.LogWarning(
				exception,
				"Could not rewrite {Path}. The engine is running on it either way; the repair runs again on the next start.",
				_store.FilePath);
		}
	}

	/// <summary>The one way a document reaches the disk: normalise it, validate it, then refuse it or write it.</summary>
	/// <remarks>
	///     Both writers come through here, so neither can skip either step. <paramref name="onInvalid"/> refuses a
	///     person's save of a document the engine cannot run, while the load path's own rewrite goes out and carries
	///     its errors back. Warnings never affect a write.
	/// </remarks>
	/// <exception cref="HeatingConfigException">The file could not be written.</exception>
	private ConfigWriteResult NormaliseValidateAndWrite(HeatingConfig config, InvalidDocument onInvalid)
	{
		// In place, so the caller's own object carries the drops: the page looks at it afterwards.
		ConfigNormalizer.Normalize(config);

		ValidationResult validation = Validate(config);

		if (!validation.IsValid && onInvalid is InvalidDocument.Refuse)
			return new ConfigWriteResult(Written: false, validation);

		_store.Write(config);

		return new ConfigWriteResult(Written: true, validation);
	}
}
