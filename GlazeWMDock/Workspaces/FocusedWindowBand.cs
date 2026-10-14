using System;
using System.Collections.Generic;
using GlazeWMDock.Configuration;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock.Workspaces;

/// <summary>
/// A Dock band showing the focused window's title (and process name below
/// it), with an icon for its state: tiling, floating, fullscreen or minimized.
/// When the focused workspace is empty it shows the workspace instead.
///
/// Clicking it opens <see cref="WindowActionsPage"/>; right-clicking offers
/// the same actions inline. Every action targets the window by id, so it acts
/// on the window shown here even though clicking the Dock may move focus.
///
/// GlazeWM has no title-changed event, so the title refreshes on the next
/// focus/window event rather than live as a tab changes.
/// </summary>
internal sealed partial class FocusedWindowBand : WrappedDockItem
{
    public const string BandId = "com.brettkinny.glazewmdock.focused";

    private static readonly IconInfo TilingIcon = new(char.ConvertFromUtf32(0xE7F4)); // Tiles
    private static readonly IconInfo FloatingIcon = new(char.ConvertFromUtf32(0xE8A7)); // OpenInNewWindow
    private static readonly IconInfo FullscreenIcon = new(char.ConvertFromUtf32(0xE740)); // FullScreen
    private static readonly IconInfo MinimizedIcon = new(char.ConvertFromUtf32(0xE921)); // ChromeMinimize
    private static readonly IconInfo WorkspaceIcon = new(char.ConvertFromUtf32(0xE8A9)); // ViewAll

    private readonly ListItem _item;
    private readonly WindowActionsPage _page;

    public FocusedWindowBand(GlazeWmClient client, Func<DockSettings> settings, Func<FocusedInfo?> focused)
        : base([], BandId, "GlazeWM Focused Window")
    {
        _page = new WindowActionsPage(client, settings, focused);
        _item = new ListItem(_page)
        {
            Title = "GlazeWM",
            Subtitle = string.Empty,
            Icon = TilingIcon,
            MoreCommands = _page.ContextItems(),
        };

        Icon = FloatingIcon;
        Items = [_item];
    }

    public void Update(FocusedInfo focused, DockSettings settings)
    {
        string title;
        string subtitle;
        IconInfo icon;

        if (focused.IsWindow)
        {
            title = Truncate(focused.Title.Length > 0 ? focused.Title : focused.ProcessName, settings.TitleLength);
            subtitle = focused.ProcessName;
            icon = focused.State switch
            {
                "floating" => FloatingIcon,
                "fullscreen" => FullscreenIcon,
                "minimized" => MinimizedIcon,
                _ => TilingIcon,
            };
        }
        else
        {
            title = $"Workspace {focused.Title}";
            subtitle = "No windows";
            icon = WorkspaceIcon;
        }

        if (_item.Title != title)
        {
            _item.Title = title;
        }

        if (_item.Subtitle != subtitle)
        {
            _item.Subtitle = subtitle;
        }

        if (!ReferenceEquals(_item.Icon, icon))
        {
            _item.Icon = icon;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…"); // …
}

/// <summary>
/// Actions for the window shown on the focused-window band: float/tile,
/// fullscreen, minimize, close, and move to each configured workspace.
/// Built against the window id at the moment the page opens.
/// </summary>
internal sealed partial class WindowActionsPage : ListPage
{
    private readonly GlazeWmClient _client;
    private readonly Func<DockSettings> _settings;
    private readonly Func<FocusedInfo?> _focused;

    public WindowActionsPage(GlazeWmClient client, Func<DockSettings> settings, Func<FocusedInfo?> focused)
    {
        _client = client;
        _settings = settings;
        _focused = focused;
        Id = "com.brettkinny.glazewmdock.windowactions";
        Name = "Window actions";
        Title = "GlazeWM Window Actions";
        Icon = new IconInfo(char.ConvertFromUtf32(0xE8A7));
        PlaceholderText = "Act on the focused window...";
    }

    public override IListItem[] GetItems()
    {
        var focused = _focused();
        if (focused is null || !focused.IsWindow)
        {
            return [new ListItem(new NoOpCommand()) { Title = "No focused window", Subtitle = "Focus a window managed by GlazeWM first" }];
        }

        var items = new List<IListItem>();
        foreach (var command in Actions(focused.Id, dismiss: true))
        {
            items.Add(new ListItem(command) { Title = command.Name, Subtitle = focused.Title });
        }

        foreach (var ws in _settings().WorkspaceNames)
        {
            var move = new GlazeCommand(
                _client,
                $"com.brettkinny.glazewmdock.window.move.{ws}",
                $"Move to workspace {ws}",
                $"move --workspace {ws}",
                new IconInfo(char.ConvertFromUtf32(0xE8AB)),
                () => focused.Id,
                dismiss: true);
            items.Add(new ListItem(move) { Title = move.Name, Subtitle = focused.Title });
        }

        return [.. items];
    }

    /// <summary>The same actions as a right-click menu on the Dock item.</summary>
    public IContextItem[] ContextItems()
    {
        var items = new List<IContextItem>();
        foreach (var command in Actions(null, dismiss: false))
        {
            items.Add(new CommandContextItem(command));
        }

        return [.. items];
    }

    /// <param name="windowId">Fixed target, or null to resolve the focused window at invoke time.</param>
    private IEnumerable<GlazeCommand> Actions(string? windowId, bool dismiss)
    {
        Func<string?> target = windowId is null ? () => _focused() is { IsWindow: true } f ? f.Id : null : () => windowId;

        // Without a window to target these would act on whatever GlazeWM has
        // focused when the command lands, so they no-op instead.
        Func<string> Guard(string command) => () => target() is null ? string.Empty : command;

        yield return new GlazeCommand(_client, "com.brettkinny.glazewmdock.window.float", "Toggle floating", Guard("toggle-floating --centered"), new IconInfo(char.ConvertFromUtf32(0xE8A7)), target, dismiss);
        yield return new GlazeCommand(_client, "com.brettkinny.glazewmdock.window.tile", "Toggle tiling", Guard("toggle-tiling"), new IconInfo(char.ConvertFromUtf32(0xE7F4)), target, dismiss);
        yield return new GlazeCommand(_client, "com.brettkinny.glazewmdock.window.fullscreen", "Toggle fullscreen", Guard("toggle-fullscreen"), new IconInfo(char.ConvertFromUtf32(0xE740)), target, dismiss);
        yield return new GlazeCommand(_client, "com.brettkinny.glazewmdock.window.minimize", "Minimize", Guard("toggle-minimized"), new IconInfo(char.ConvertFromUtf32(0xE921)), target, dismiss);
        yield return new GlazeCommand(_client, "com.brettkinny.glazewmdock.window.close", "Close window", Guard("close"), new IconInfo(char.ConvertFromUtf32(0xE8BB)), target, dismiss);
    }
}
