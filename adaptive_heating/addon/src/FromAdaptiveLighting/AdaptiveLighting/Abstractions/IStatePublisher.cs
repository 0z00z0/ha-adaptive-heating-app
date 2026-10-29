namespace AdaptiveLighting.Abstractions;

public interface IStatePublisher
{
	/// <summary>Publishes <paramref name="snapshot"/>. Must not throw: it is called from inside the area's lock.</summary>
	void Publish(AreaSnapshot snapshot);
}
