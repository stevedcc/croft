namespace Croft.Web.E2E;

/// <summary>
/// What a page's events produced so far, safe to read while the page is still raising them.
/// Playwright raises <c>Request</c>, <c>Response</c>, <c>Console</c> and <c>WebSocket</c> on its own
/// thread, so a test that enumerates a plain list the handler appends to (an interactive page's
/// circuit and socket requests arrive after the test thinks the page is quiet) throws
/// "Collection was modified". <see cref="Add"/> and <see cref="Snapshot"/> take the same lock;
/// every assertion enumerates a snapshot.
/// </summary>
public sealed class EventLog<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _items = [];
    private Action? _unsubscribe;

    internal EventLog(Action unsubscribe) => _unsubscribe = unsubscribe;

    /// <summary>Records one item.</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            _items.Add(item);
        }
    }

    /// <summary>A copy of everything recorded so far; later events do not change it.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }

    /// <summary>Unsubscribes the handler; the log keeps what it has.</summary>
    public void Stop() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}

/// <summary>Factories for the logs the tests keep: each subscribes to the page and returns the log.</summary>
public static class EventLog
{
    /// <summary>The URL of every request the page makes that passes <paramref name="filter"/>.</summary>
    public static EventLog<string> RequestUrls(IPage page, Func<IRequest, bool>? filter = null)
    {
        EventLog<string>? log = null;
        void OnRequest(object? _, IRequest request)
        {
            if (filter is null || filter(request))
            {
                log!.Add(request.Url);
            }
        }

        log = new EventLog<string>(() => page.Request -= OnRequest);
        page.Request += OnRequest;
        return log;
    }

    /// <summary>Every response the page receives.</summary>
    public static EventLog<IResponse> Responses(IPage page)
    {
        EventLog<IResponse>? log = null;
        void OnResponse(object? _, IResponse response) => log!.Add(response);
        log = new EventLog<IResponse>(() => page.Response -= OnResponse);
        page.Response += OnResponse;
        return log;
    }

    /// <summary>The URL of every WebSocket the page opens.</summary>
    public static EventLog<string> WebSocketUrls(IPage page)
    {
        EventLog<string>? log = null;
        void OnSocket(object? _, IWebSocket socket) => log!.Add(socket.Url);
        log = new EventLog<string>(() => page.WebSocket -= OnSocket);
        page.WebSocket += OnSocket;
        return log;
    }

    /// <summary>The text of every console message of one of the given <paramref name="types"/>.</summary>
    public static EventLog<string> ConsoleTexts(IPage page, params string[] types)
    {
        EventLog<string>? log = null;
        void OnConsole(object? _, IConsoleMessage message)
        {
            if (types.Contains(message.Type))
            {
                log!.Add(message.Text);
            }
        }

        log = new EventLog<string>(() => page.Console -= OnConsole);
        page.Console += OnConsole;
        return log;
    }

    /// <summary>The URL of every WebSocket the page opens and every request to <c>/_blazor</c>: what a circuit
    /// asks for. A page that must open none leaves this empty.</summary>
    public static EventLog<string> CircuitTraffic(IPage page)
    {
        EventLog<string>? log = null;
        void OnSocket(object? _, IWebSocket socket) => log!.Add(socket.Url);
        void OnRequest(object? _, IRequest request)
        {
            if (request.Url.Contains("/_blazor", StringComparison.Ordinal))
            {
                log!.Add(request.Url);
            }
        }

        log = new EventLog<string>(() =>
        {
            page.WebSocket -= OnSocket;
            page.Request -= OnRequest;
        });
        page.WebSocket += OnSocket;
        page.Request += OnRequest;
        return log;
    }
}
