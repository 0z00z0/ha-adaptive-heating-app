using AdaptiveLighting.Abstractions;

using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>Files the add-on's house-wide notices in the activity record: it started, or a save was taken up.</summary>
// In process: a save is not something a room reported, and one row per room for a single save would be noise.
public sealed class EngineNoticeRecorder : IHostedService, IDisposable
{
	private readonly HeatingDocumentHost _engine;
	private readonly Action<EngineNotice> _activity;
	private readonly ILogger<EngineNoticeRecorder> _logger;

	private IDisposable? _subscription;

	/// <param name="activity">Where a notice is filed: the add-on's own record of what it did.</param>
	public EngineNoticeRecorder(HeatingDocumentHost engine, Action<EngineNotice> activity, ILogger<EngineNoticeRecorder> logger)
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_activity = activity ?? throw new ArgumentNullException(nameof(activity));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_subscription = _engine.Notices.SubscribeSafe(Record, _logger);
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_subscription?.Dispose();
		_subscription = null;
	}

	private void Record(EngineNotice notice) => _activity(notice);
}
