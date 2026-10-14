using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlazeWMDock.Workspaces;

/// <summary>
/// Minimal client for GlazeWM's IPC WebSocket server (ws://localhost:6123).
///
/// On connect it subscribes to the workspace, focus, window and mode events
/// and pulls the initial state. Events are coalesced: a burst (a workspace
/// switch fires several) schedules one refresh that re-runs the authoritative
/// queries, and each response is surfaced through its own event:
///   - "query monitors"  -> <see cref="MonitorsChanged"/>
///   - "query focused"   -> <see cref="FocusedChanged"/>
///   - "query tiling-direction" / "binding-modes" / "paused" -> <see cref="StateChanged"/>
/// The connection auto-reconnects, and <see cref="Reconnect"/> forces it (used
/// when the port setting changes).
///
/// (We query monitors rather than "query workspaces" because the strip needs to
/// render each monitor's workspaces separately; the flat workspace list has no
/// monitor grouping, position, or per-monitor focus.)
///
/// JSON is read with <see cref="JsonDocument"/> (no reflection), which keeps
/// the extension trim/AOT-safe — the project trims on Release.
/// </summary>
internal sealed partial class GlazeWmClient : IDisposable
{
    private const string QueryMonitors = "query monitors";
    private const string QueryFocused = "query focused";
    private const string QueryTilingDirection = "query tiling-direction";
    private const string QueryBindingModes = "query binding-modes";
    private const string QueryPaused = "query paused";

    private const string SubscribeEvents =
        "sub --events focus_changed focused_container_moved workspace_activated workspace_deactivated "
        + "workspace_updated window_managed window_unmanaged binding_modes_changed tiling_direction_changed "
        + "pause_changed";

    // Window-manager events arrive in bursts (one workspace switch is a
    // deactivate + activate + focus change). Wait this long after the first
    // event before re-querying so the burst costs one round of queries.
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(30);

    private readonly Func<int> _port;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _socket;
    private int _refreshPending;

    // Mode state is assembled from three separate query responses.
    private string[] _bindingModes = [];
    private bool _paused;
    private string _tilingDirection = string.Empty;

    /// <summary>Raised whenever a fresh monitor/workspace snapshot arrives.</summary>
    public event Action<IReadOnlyList<MonitorInfo>>? MonitorsChanged;

    /// <summary>Raised whenever a fresh focused-container snapshot arrives.</summary>
    public event Action<FocusedInfo>? FocusedChanged;

    /// <summary>Raised when binding modes, pause state or tiling direction are re-read.</summary>
    public event Action<WmState>? StateChanged;

    /// <param name="port">Read on every (re)connect, so a port change applies on <see cref="Reconnect"/>.</param>
    public GlazeWmClient(Func<int> port)
    {
        _port = port;
    }

    /// <summary>Starts the background connect/receive/reconnect loop.</summary>
    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Drops the current connection; the loop reconnects, re-reading the port.</summary>
    public void Reconnect()
    {
        try
        {
            _socket?.Abort();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Sends a WM command, e.g. "focus --workspace 3". With
    /// <paramref name="subjectId"/> the command targets that container
    /// (<c>command --id &lt;id&gt; ...</c>) rather than whatever is focused when
    /// it arrives — clicking the Dock or opening the palette can move focus, so
    /// window actions pin the window they were built for.
    /// </summary>
    public async Task RunCommandAsync(string command, string? subjectId = null)
    {
        try
        {
            var message = string.IsNullOrEmpty(subjectId)
                ? $"command {command}"
                : $"command --id {subjectId} {command}";
            await SendAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Best effort. If the socket is down, the reconnect loop recovers.
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                _socket = socket;
                var uri = new Uri($"ws://127.0.0.1:{_port()}");
                await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                Log.Line($"IPC connected to {uri}");

                await SendAsync(SubscribeEvents).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);

                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
                Log.Line("IPC receive loop ended (socket closed); will reconnect");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Connection failed, dropped, or was aborted by Reconnect(); fall
                // through to the retry delay.
                Log.Line($"IPC connect/receive failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _socket = null;
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            HandleMessage(sb.ToString());
        }
    }

    /// <summary>Re-runs every state query; each response raises its own event.</summary>
    private async Task RefreshAsync()
    {
        await SendAsync(QueryMonitors).ConfigureAwait(false);
        await SendAsync(QueryFocused).ConfigureAwait(false);
        await SendAsync(QueryTilingDirection).ConfigureAwait(false);
        await SendAsync(QueryBindingModes).ConfigureAwait(false);
        await SendAsync(QueryPaused).ConfigureAwait(false);
    }

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 1)
        {
            return; // a refresh is already queued for this burst
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RefreshDelay, _cts.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _refreshPending, 0);
                await RefreshAsync().ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref _refreshPending, 0);
            }
        });
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("messageType", out var typeEl))
            {
                return;
            }

            var messageType = typeEl.GetString();

            if (messageType == "event_subscription")
            {
                // Something changed; ask for the authoritative state.
                ScheduleRefresh();
                return;
            }

            if (messageType != "client_response"
                || !root.TryGetProperty("clientMessage", out var cm)
                || !root.TryGetProperty("success", out var ok)
                || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data))
            {
                return;
            }

            switch (cm.GetString())
            {
                case QueryMonitors:
                    HandleMonitors(data);
                    break;
                case QueryFocused:
                    HandleFocused(data);
                    break;
                case QueryTilingDirection:
                    _tilingDirection = data.ValueKind == JsonValueKind.Object ? GetString(data, "tilingDirection") : string.Empty;
                    break;
                case QueryBindingModes:
                    _bindingModes = ParseBindingModes(data);
                    break;
                case QueryPaused:
                    // Last query of a refresh round, so publish the assembled state once.
                    _paused = data.ValueKind == JsonValueKind.True;
                    StateChanged?.Invoke(new WmState(_bindingModes, _paused, _tilingDirection));
                    break;
            }
        }
        catch
        {
            // Ignore malformed or unexpected frames.
        }
    }

    private void HandleMonitors(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("monitors", out var monArray)
            || monArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var list = new List<MonitorInfo>(monArray.GetArrayLength());
        foreach (var mon in monArray.EnumerateArray())
        {
            list.Add(ParseMonitor(mon));
        }

        MonitorsChanged?.Invoke(list);
    }

    private void HandleFocused(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("focused", out var f)
            || f.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var isWindow = string.Equals(GetString(f, "type"), "window", StringComparison.OrdinalIgnoreCase);
        var state = f.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
            ? GetString(st, "type")
            : string.Empty;

        FocusedChanged?.Invoke(new FocusedInfo(
            IsWindow: isWindow,
            Id: GetString(f, "id"),
            Title: isWindow ? GetString(f, "title") : FirstNonEmpty(GetString(f, "displayName"), GetString(f, "name")),
            ProcessName: GetString(f, "processName"),
            State: state));
    }

    private static string[] ParseBindingModes(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("bindingModes", out var modes)
            || modes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return modes.EnumerateArray()
            .Select(m => m.ValueKind == JsonValueKind.Object
                ? GetString(m, "name")
                : string.Empty)
            .Where(n => n.Length > 0)
            .ToArray();
    }

    private static MonitorInfo ParseMonitor(JsonElement mon)
    {
        var workspaces = new List<WorkspaceInfo>();
        if (mon.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (string.Equals(GetString(child, "type"), "workspace", StringComparison.OrdinalIgnoreCase))
                {
                    workspaces.Add(ParseWorkspace(child));
                }
            }
        }

        return new MonitorInfo(
            DeviceName: GetString(mon, "deviceName"),
            DevicePath: GetString(mon, "devicePath"),
            X: GetInt(mon, "x"),
            Y: GetInt(mon, "y"),
            HasFocus: GetBool(mon, "hasFocus"),
            Workspaces: workspaces);
    }

    private static WorkspaceInfo ParseWorkspace(JsonElement ws)
    {
        var name = GetString(ws, "name");
        var displayName = GetString(ws, "displayName");
        return new WorkspaceInfo(
            Name: string.IsNullOrEmpty(name) ? displayName : name,
            DisplayName: string.IsNullOrEmpty(displayName) ? name : displayName,
            HasFocus: GetBool(ws, "hasFocus"),
            IsDisplayed: GetBool(ws, "isDisplayed"),
            WindowCount: CountWindows(ws));
    }

    private static int CountWindows(JsonElement container)
    {
        var count = 0;
        if (container.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (string.Equals(GetString(child, "type"), "window", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }

                count += CountWindows(child);
            }
        }

        return count;
    }

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;

    private async Task SendAsync(string message)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, _cts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _socket?.Abort();
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
        _sendLock.Dispose();
    }
}
