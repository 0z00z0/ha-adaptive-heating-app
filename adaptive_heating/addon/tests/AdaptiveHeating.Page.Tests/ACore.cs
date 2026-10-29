using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using NetDaemon.Client.Internal.HomeAssistant.Commands;

namespace AdaptiveHeating.Page.Tests;

/// <summary>One message as it went out, named by the action it calls or by the command it is.</summary>
internal sealed record SentCall(int Id, string Action, string Json);

/// <summary>
/// A Home Assistant that answers from a script and remembers what was sent, with the house beside it. It stands
/// in for the client's own connection and for the entity model over it.
/// </summary>
internal sealed class CoreUnderTest : IHomeAssistantConnection, IHomeAssistantHassMessages
{
	private readonly Subject<HassMessage> _messages = new();
	private readonly Subject<HassEvent> _events = new();
	private readonly Func<CoreUnderTest, SentCall, string?> _answer;

	private int _lastId;

	public CoreUnderTest(Func<CoreUnderTest, SentCall, string?>? answer = null, params EntityState[] house)
	{
		_answer = answer ?? ((_, call) => Worked(call.Id));
		House = new AHouseUnderTest(house);
	}

	/// <summary>Every action the add-on called, in order, each with the name it went out under.</summary>
	public List<SentCall> Calls { get; } = [];

	/// <summary>The house the entity model would be holding over this connection.</summary>
	public AHouseUnderTest House { get; }

	public IObservable<HassMessage> OnHassMessage => _messages;

	public Task<IObservable<HassEvent>> SubscribeToHomeAssistantEventsAsync(string? eventType, CancellationToken cancelToken) =>
		Task.FromResult<IObservable<HassEvent>>(_events);

	/// <summary>Home Assistant reporting that one entity has changed, as the model's own stream carries it.</summary>
	public void Reports(EntityState state) => House.Reports(state);

	public Task SendCommandAsync<T>(T command, CancellationToken cancelToken) where T : CommandMessage
	{
		SentCall call = Record(command, Calls);

		if (_answer(this, call) is { } answer)
			Say(answer);

		return Task.CompletedTask;
	}

	public Task<TResult?> SendCommandAndReturnResponseAsync<T, TResult>(T command, CancellationToken cancelToken)
		where T : CommandMessage => throw new NotSupportedException();

	public Task<JsonElement?> SendCommandAndReturnResponseRawAsync<T>(T command, CancellationToken cancelToken)
		where T : CommandMessage => throw new NotSupportedException();

	public Task<HassMessage?> SendCommandAndReturnHassMessageResponseAsync<T>(T command, CancellationToken cancelToken)
		where T : CommandMessage => throw new NotSupportedException();

	public Task WaitForConnectionToCloseAsync(CancellationToken cancelToken) => Task.Delay(Timeout.Infinite, cancelToken);

	public Task<T?> GetApiCallAsync<T>(string apiPath, CancellationToken cancelToken) => throw new NotSupportedException();

	public Task<T?> PostApiCallAsync<T>(string apiPath, CancellationToken cancelToken, object? data = null) =>
		throw new NotSupportedException();

	/// <summary>Puts one raw message on the stream towards the add-on.</summary>
	public void Say(string message) => _messages.OnNext(JsonSerializer.Deserialize<HassMessage>(message)!);

	/// <summary>Home Assistant going away, which is what a restart and a power cut both look like.</summary>
	public void GoAway()
	{
		_events.OnCompleted();
		_messages.OnCompleted();
	}

	public ValueTask DisposeAsync()
	{
		GoAway();

		return ValueTask.CompletedTask;
	}

	public static string Worked(int id) =>
		JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["id"] = id,
			["type"] = "result",
			["success"] = true
		});

	public static string Refused(int id, string code, string said) =>
		JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["id"] = id,
			["type"] = "result",
			["success"] = false,
			["error"] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["code"] = code,
				["message"] = said
			}
		});

	/// <summary>One thermostat of this integration, as Home Assistant reports one.</summary>
	public static EntityState Thermostat(double current = 19.5, double target = 21.0) =>
		State(
			ACabin.Thermostat,
			"heat",
			$$"""
			{
				"friendly_name": "Kitchen",
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"current_temperature": {{Invariant(current)}},
				"temperature": {{Invariant(target)}}
			}
			""");

	/// <summary>An entity of somebody else's, which every change stream carries and the heating reads none of.</summary>
	public static EntityState SomebodyElses() =>
		State("light.hall", "on", """{ "friendly_name": "Hall" }""");

	/// <summary>One state as the entity model holds it. Built through JSON: its attributes take no dictionary.</summary>
	public static EntityState State(string entityId, string state, string attributes) =>
		JsonSerializer.Deserialize<EntityState>(
			$$"""
			{ "entity_id": "{{entityId}}", "state": "{{state}}", "attributes": {{attributes}} }
			""")!;

	// A temperature written the Norwegian way is not a number to a JSON reader.
	private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

	private SentCall Record(CommandMessage command, List<SentCall> into)
	{
		// The client assigns the number as it sends, and Home Assistant refuses one that is not greater than the
		// last one on the connection.
		command.Id = ++_lastId;

		string json = command.GetJsonString();

		using JsonDocument parsed = JsonDocument.Parse(json);
		JsonElement root = parsed.RootElement;

		SentCall call = new(
			command.Id,
			root.TryGetProperty("domain", out JsonElement domain)
				? $"{domain.GetString()}.{root.GetProperty("service").GetString()}"
				: command.Type,
			json);

		into.Add(call);

		return call;
	}
}

/// <summary>Stands in for the client's own reconnecting runner: a test hands over a connection or takes it away.</summary>
internal sealed class RunnerUnderTest : IHomeAssistantRunner
{
	private readonly Subject<IHomeAssistantConnection> _opened = new();
	private readonly Subject<DisconnectReason> _closed = new();

	public IObservable<IHomeAssistantConnection> OnConnect => _opened;

	public IObservable<DisconnectReason> OnDisconnect => _closed;

	public IHomeAssistantConnection? CurrentConnection { get; private set; }

	/// <summary>The token each attempt went out on, which is how a rotated one is told from the first.</summary>
	public List<string> Tokens { get; } = [];

	public Task RunAsync(string host, int port, bool ssl, string token, TimeSpan timeout, CancellationToken cancelToken) =>
		RunAsync(host, port, ssl, token, "api/websocket", timeout, cancelToken);

	public Task RunAsync(
		string host,
		int port,
		bool ssl,
		string token,
		string websocketPath,
		TimeSpan timeout,
		CancellationToken cancelToken)
	{
		Tokens.Add(token);

		return Task.Delay(Timeout.Infinite, cancelToken);
	}

	/// <summary>A connection Home Assistant confirmed is running, which is the only kind the client hands over.</summary>
	public void HandOver(CoreUnderTest core)
	{
		CurrentConnection = core;
		_opened.OnNext(core);
	}

	/// <summary>Home Assistant answering that it is still coming up, which the client reports and then retries.</summary>
	public void TheCoreIsStarting() => _closed.OnNext(DisconnectReason.NotReady);

	public void Lost()
	{
		CurrentConnection = null;
		_closed.OnNext(DisconnectReason.Remote);
	}

	public ValueTask DisposeAsync()
	{
		_opened.OnCompleted();
		_closed.OnCompleted();

		return ValueTask.CompletedTask;
	}
}

/// <summary>A connection over a scripted Home Assistant, open and ready to be called, with the thermostats above it.</summary>
internal sealed class AnOpenConnection : IAsyncDisposable
{
	/// <summary>Stands in for what the Supervisor puts in the add-on's environment.</summary>
	public const string Token = "the-token-the-supervisor-handed-over";

	/// <summary>What the heating reads beyond the climate entities and the sun, as the add-on's form names them.</summary>
	public static readonly string[] AlsoNamed =
		[ACabin.OutdoorSensor, ACabin.SecondOutdoorSensor, ACabin.Forecast];

	private IDisposable? _watching;

	private AnOpenConnection(Func<CoreUnderTest, SentCall, string?>? answer, params EntityState[] house)
	{
		Core = new CoreUnderTest(answer, house);
		Model = new TheHouseAsItIs(() => AlsoNamed);

		Connection = new CoreConnection(
			Runner,
			() => Token,
			Model,
			TimeProvider.System,
			NullLogger<CoreConnection>.Instance);

		Api = new CoreApi(Connection, Log);

		Thermostats = new Thermostats(Api);
		Areas = new TheAreasOfTheHouse(Model);
		RoomSettings = new TellsEachRoomItsSettings(Thermostats, NullLogger<TellsEachRoomItsSettings>.Instance);
		Hidden = new WhatIsHidden(Connection, NullLogger<WhatIsHidden>.Instance);
		Changes = new ChangesAtTheThermostats(
			new AdaptiveLighting.Ha.HaStatePublisher(() => Model.Context, NullLogger.Instance),
			NullLogger<ChangesAtTheThermostats>.Instance);
	}

	public RunnerUnderTest Runner { get; } = new();

	public CoreUnderTest Core { get; }

	public CoreConnection Connection { get; }

	/// <summary>The entity model's reading of the house, brought up over this connection.</summary>
	public TheHouseAsItIs Model { get; }

	public WhatIsHidden Hidden { get; }

	/// <summary>Who changed each thermostat, sorted as the change arrives over this connection's model.</summary>
	public ChangesAtTheThermostats Changes { get; }

	public CoreApi Api { get; }

	public Thermostats Thermostats { get; }

	/// <summary>The Areas as the engine asks them, over the entity model this connection brought up.</summary>
	public TheAreasOfTheHouse Areas { get; }

	public TellsEachRoomItsSettings RoomSettings { get; }

	public LogUnderTest<CoreApi> Log { get; } = new();

	/// <summary>Starts the connection, hands over a Home Assistant and waits until the add-on can use it.</summary>
	public static async Task<AnOpenConnection> StartAsync(
		Func<CoreUnderTest, SentCall, string?>? answer = null,
		params EntityState[] house)
	{
		AnOpenConnection open = await StartingAsync(answer, house).ConfigureAwait(false);

		open.Runner.HandOver(open.Core);

		if (!await UntilAsync(() => open.Connection.IsLive).ConfigureAwait(false))
			throw new InvalidOperationException("The connection under test never became usable.");

		await open.TheModelComesUpAsync().ConfigureAwait(false);

		return open;
	}

	/// <summary>What StartsTheEntityModel does once the connection is live: read the house and watch it.</summary>
	// The two registry probes are left to the tests about them, so no other test counts their messages.
	public Task TheModelComesUpAsync()
	{
		Model.CameUp(Core.House, Core.House, Hidden);
		Changes.CameUp(Core.House);

		_watching?.Dispose();
		_watching = Core.House
			.StateAllChanges()
			.Where(change => WhatTheHeatingReads.Reads(change, AlsoNamed))
			.Subscribe(_ => Connection.Changed(), _ => { }, () => { });

		Connection.Changed();

		return Task.CompletedTask;
	}

	/// <summary>Starts the connection and hands over nothing, which is a core that has not finished starting.</summary>
	public static async Task<AnOpenConnection> StartingAsync(
		Func<CoreUnderTest, SentCall, string?>? answer = null,
		params EntityState[] house)
	{
		AnOpenConnection open = new(answer, house);

		await open.Connection.StartAsync(CancellationToken.None).ConfigureAwait(false);

		if (!await UntilAsync(() => open.Runner.Tokens.Count == 1).ConfigureAwait(false))
			throw new InvalidOperationException("The connection under test never tried to open.");

		return open;
	}

	/// <summary>A connection that was never started, which is what a call made while the add-on is alone meets.</summary>
	public static AnOpenConnection NotStarted() => new(answer: null);

	/// <summary>Waits for something a background task does. False where it never happened, so a test can assert on it.</summary>
	public static async Task<bool> UntilAsync(Func<bool> ready)
	{
		DateTime giveUp = DateTime.UtcNow.AddSeconds(10);

		while (!ready())
		{
			if (DateTime.UtcNow > giveUp)
				return false;

			await Task.Delay(1).ConfigureAwait(false);
		}

		return true;
	}

	public async ValueTask DisposeAsync()
	{
		_watching?.Dispose();
		Changes.HasGone();

		await Connection.StopAsync(CancellationToken.None).ConfigureAwait(false);

		Connection.Dispose();

		await Core.DisposeAsync().ConfigureAwait(false);
		await Runner.DisposeAsync().ConfigureAwait(false);
	}
}

/// <summary>A logger that keeps what was written, so a test can tell one line from none.</summary>
internal sealed class LogUnderTest<T> : ILogger<T>
{
	public List<(LogLevel Level, string Said)> Lines { get; } = [];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter) =>
		Lines.Add((logLevel, formatter(state, exception)));
}
