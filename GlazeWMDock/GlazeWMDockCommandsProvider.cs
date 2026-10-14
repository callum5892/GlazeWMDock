using System;
using System.Collections.Generic;
using System.Linq;
using GlazeWMDock.Configuration;
using GlazeWMDock.Workspaces;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock;

/// <summary>
/// The extension's command provider. It:
///   - exposes a top-level "GlazeWM Workspaces" palette page,
///   - provides four Dock bands — the original workspace strip (live text),
///     clickable workspace buttons, the focused window, and mode/tiling
///     direction — each pinnable on its own from Dock settings, and
///   - exposes a settings page (port, workspaces, labels, ...).
///
/// A single <see cref="GlazeWmClient"/> feeds live state to all of them. The
/// latest snapshot of each kind is cached so a rebuilt band, or a settings
/// change, can re-render immediately without waiting for the next WM event.
/// </summary>
public partial class GlazeWMDockCommandsProvider : CommandProvider
{
    private const string ProviderId = "com.brettkinny.glazewmdock";
    private const string DockBandId = "com.brettkinny.glazewmdock.workspaces";

    private readonly DockSettings _settings = new();
    private readonly GlazeWmClient _client;
    private readonly WorkspacesListPage _page;
    private readonly ICommandItem[] _topLevel;

    // Serialises renders: WM events arrive on the WebSocket receive thread,
    // settings saves on a host COM thread.
    private readonly object _renderLock = new();

    // The bands are rebuilt if the Dock's live-update channel breaks (see
    // Render), so they aren't readonly. _bands is read on a host COM thread
    // (GetDockBands) and reassigned on the WebSocket receive thread, hence
    // volatile.
    private volatile Bands _bands;

    private IReadOnlyList<MonitorInfo> _monitors = [];
    private volatile FocusedInfo? _focused;
    private WmState _state = WmState.Empty;
    private int _port;

    public GlazeWMDockCommandsProvider()
    {
        Id = ProviderId;
        DisplayName = "GlazeWM Workspaces";
        Icon = new IconInfo(char.ConvertFromUtf32(0xE7F4)); // Segoe Fluent "Tiles"
        Settings = _settings.Settings;

        _port = _settings.Port;
        _client = new GlazeWmClient(() => _settings.Port);
        _page = new WorkspacesListPage(_client);
        _bands = CreateBands();

        _topLevel =
        [
            new CommandItem(_page)
            {
                Title = DisplayName,
                Subtitle = "Switch GlazeWM workspace",
            },
        ];

        _settings.Changed += OnSettingsChanged;
        _client.MonitorsChanged += OnMonitorsChanged;
        _client.FocusedChanged += OnFocusedChanged;
        _client.StateChanged += OnStateChanged;
        _client.Start();
        Log.Line("provider constructed; GlazeWM client started");
    }

    public override ICommandItem[] TopLevelCommands() => _topLevel;

    public override ICommandItem[]? GetDockBands() => _bands.All;

    private void OnMonitorsChanged(IReadOnlyList<MonitorInfo> monitors)
    {
        _monitors = monitors;

        // The switcher page lists every workspace across all monitors, so flatten
        // the per-monitor tree back into a single list for it.
        var workspaces = monitors.SelectMany(m => m.Workspaces).ToList();

        // Update the palette page FIRST and guard it independently. The page
        // re-pulls GetItems() every time it's opened, so it self-heals even
        // when no live notification reaches the host — but only if this call
        // actually runs.
        try
        {
            _page.SetSnapshot(workspaces);
        }
        catch (Exception ex)
        {
            // Stale/dead host proxy; ignore and keep pumping events.
            Log.Line($"page update failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }

        Render(b =>
        {
            b.Strip.Update(monitors, _settings);
            b.Buttons.Update(monitors, _settings);
        });
    }

    private void OnFocusedChanged(FocusedInfo focused)
    {
        // Command Palette's own window isn't a GlazeWM window worth showing
        // (or targeting with window actions); keep the last real one.
        if (focused.IsWindow && focused.ProcessName.StartsWith("Microsoft.CmdPal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _focused = focused;
        Render(b => b.Focused.Update(focused, _settings));
    }

    private void OnStateChanged(WmState state)
    {
        _state = state;
        Render(b => b.Mode.Update(state));
    }

    private void OnSettingsChanged()
    {
        var port = _settings.Port;
        if (port != _port)
        {
            _port = port;
            Log.Line($"port changed to {port}; reconnecting");
            _client.Reconnect();
        }

        Render(RenderAll);
    }

    /// <summary>
    /// Applies an update to the live bands. Updating assigns Title/Subtitle,
    /// which raises PropChanged and marshals to CmdPal.UI over COM. That UI
    /// process restarts on a monitor hot-plug while this singleton extension
    /// keeps running, which leaves a dead subscriber on the items; raising into
    /// it throws. An unguarded throw here used to unwind into GlazeWmClient's
    /// swallowing catch and permanently freeze every future update until a
    /// manual reload. Now we catch it and rebuild the bands so the host can
    /// re-bind.
    /// </summary>
    private void Render(Action<Bands> update)
    {
        lock (_renderLock)
        {
            try
            {
                update(_bands);
            }
            catch (Exception ex)
            {
                Log.Line($"band update failed (host proxy likely dead): {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message} — rebuilding dock bands");
                RebuildDockBands();
            }
        }
    }

    private void RenderAll(Bands b)
    {
        b.Strip.Update(_monitors, _settings);
        b.Buttons.Update(_monitors, _settings);
        if (_focused is { } focused)
        {
            b.Focused.Update(focused, _settings);
        }

        b.Mode.Update(_state);
    }

    /// <summary>
    /// Replaces every band with fresh items (which have no dead COM
    /// subscribers) seeded with the latest snapshots, then asks the host to
    /// re-pull <see cref="GetDockBands"/> so it re-subscribes to the new items.
    /// This is how the Dock recovers its live state after CmdPal.UI restarts
    /// without a manual reload. If the host is unreachable right now the next
    /// event (or a reload) retries; fresh items have no subscribers so they
    /// won't throw again, so this can't loop.
    /// </summary>
    private void RebuildDockBands()
    {
        try
        {
            var bands = CreateBands();
            RenderAll(bands);
            _bands = bands;
            RaiseItemsChanged(_topLevel.Length);
            Log.Line("dock bands rebuilt; RaiseItemsChanged sent so host re-binds");
        }
        catch (Exception ex)
        {
            // Host still unreachable; leave state as-is and let the next event retry.
            Log.Line($"dock band rebuild failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private Bands CreateBands()
    {
        var strip = new WorkspaceStripItem(_page, _settings.WorkspaceNames);

        // The 3rd arg is the band's label in the Dock picker / Settings → Bands
        // (NOT the on-bar text — that's the strip item's live Title). Keep it
        // distinct from the top-level palette command so the two are easy to
        // tell apart when pinning.
        return new Bands(
            new WrappedDockItem([strip], DockBandId, "GlazeWM Workspace Strip"),
            strip,
            new WorkspaceButtonsBand(_client, FocusedWindowId),
            new FocusedWindowBand(_client, () => _settings, () => _focused),
            new ModeBand(_client));
    }

    private string? FocusedWindowId() => _focused is { IsWindow: true } f ? f.Id : null;

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _client.MonitorsChanged -= OnMonitorsChanged;
        _client.FocusedChanged -= OnFocusedChanged;
        _client.StateChanged -= OnStateChanged;
        _client.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>One generation of Dock bands; replaced wholesale on rebuild.</summary>
    private sealed record Bands(
        WrappedDockItem StripBand,
        WorkspaceStripItem Strip,
        WorkspaceButtonsBand Buttons,
        FocusedWindowBand Focused,
        ModeBand Mode)
    {
        public ICommandItem[] All { get; } = [StripBand, Buttons, Focused, Mode];
    }
}
