namespace AdaptiveLighting.Abstractions;

/// <summary>Changes the process's minimum log level while it runs, where the host's logging allows it.</summary>
public interface ILogLevelControl
{
	/// <summary>Whether the level can be changed here; <c>false</c> leaves it to the host.</summary>
	bool IsSupported { get; }

	/// <summary>Sets the minimum level for everything outside the Microsoft namespaces.</summary>
	void Apply(LogLevel level);

	/// <summary>Puts back the level the host started with.</summary>
	void RestoreInitial();
}

/// <summary>For a host that provides no control: nothing changes, and the page says the host sets the level.</summary>
public sealed class NoLogLevelControl : ILogLevelControl
{
	public static NoLogLevelControl Instance { get; } = new();

	public bool IsSupported => false;

	public void Apply(LogLevel level)
	{
	}

	public void RestoreInitial()
	{
	}
}
