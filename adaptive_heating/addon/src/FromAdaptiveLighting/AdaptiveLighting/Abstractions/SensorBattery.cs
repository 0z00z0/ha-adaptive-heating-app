namespace AdaptiveLighting.Abstractions;

/// <summary>A motion sensor whose battery is low, with the level its device reports, if any.</summary>
public sealed record SensorBattery(string SensorId, double? LevelPct);
