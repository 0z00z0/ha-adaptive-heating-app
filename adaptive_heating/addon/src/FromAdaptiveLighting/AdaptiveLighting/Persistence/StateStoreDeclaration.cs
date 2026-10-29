namespace Adaptive.Engine;

/// <summary>When a store's file is written.</summary>
internal enum StateWritePolicy
{
	/// <summary>Written the moment the value changes, because a restart is about to need it.</summary>
	Immediate,

	/// <summary>Marked dirty on change and written by the flusher at its next interval, so a burst costs one write.</summary>
	Coalesced
}

/// <summary>What a store's file gave back at start-up.</summary>
internal enum StateRestore
{
	/// <summary>No file yet, which is a first run and never a fault.</summary>
	FirstRun,

	Restored,

	/// <summary>The main file was missing or unreadable and the backup answered instead.</summary>
	RestoredFromBackup,

	/// <summary>A file was there and was refused; the reason is in the report.</summary>
	Discarded
}

/// <summary>One line of the start-up report: what a store found, and what it did with it.</summary>
internal sealed record StateStoreRestoreReport(string Name, string Location, StateRestore Outcome, string Detail);

/// <summary>One state file, declared once: what it is called, what this build writes, and how it is written.</summary>
/// <remarks>Every engine-owned file is declared through <see cref="StateStoreRegistry"/> so nothing is written by a path nobody registered.</remarks>
internal abstract record StateStoreDeclaration(
	string Name,
	string NameSuffix,
	int Version,
	StateWritePolicy WritePolicy,
	bool KeepsBackup,
	bool InStateFolder);

/// <summary>A declaration with the reading rules for its own document shape.</summary>
/// <remarks>
///     <c>VersionOf</c> is compared against <c>Version</c>; <c>SavedAtOf</c> is refused when ahead of the clock;
///     <c>Validate</c> answers a refusal sentence, or <c>null</c> to accept.
/// </remarks>
internal sealed record StateStoreDeclaration<TDocument>(
	string Name,
	string NameSuffix,
	int Version,
	StateWritePolicy WritePolicy,
	Func<TDocument, int> VersionOf,
	Func<TDocument, DateTimeOffset> SavedAtOf,
	Func<TDocument, string?>? Validate = null,
	bool KeepsBackup = true,
	bool InStateFolder = true)
	: StateStoreDeclaration(Name, NameSuffix, Version, WritePolicy, KeepsBackup, InStateFolder)
	where TDocument : class;

/// <summary>A registered store, as the flusher and the start-up report see it.</summary>
internal interface IStateStore
{
	StateStoreDeclaration Declaration { get; }

	/// <summary>The file, or the directory where a store keeps more than one.</summary>
	string Location { get; }

	/// <summary>Whether a value is waiting to be written, which a failed write leaves true.</summary>
	bool IsDirty { get; }

	/// <summary>Writes whatever is waiting; <c>false</c> leaves the store dirty for the next interval.</summary>
	bool Flush();

	/// <summary>What the last restore found, or <c>null</c> where the store has not been read yet.</summary>
	StateStoreRestoreReport? LastRestore { get; }
}
