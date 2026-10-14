using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock.Workspaces;

/// <summary>
/// A Dock band for GlazeWM's mode state. Normally it shows the tiling
/// direction new windows will be inserted in (↔ horizontal / ↕ vertical) and
/// clicking flips it. While a binding mode such as resize is on it shows the
/// mode's name in capitals and clicking exits the mode; while GlazeWM is
/// paused it shows PAUSED and clicking resumes.
///
/// Right-click offers toggle tiling direction, toggle pause, redraw and
/// reload config.
/// </summary>
internal sealed partial class ModeBand : WrappedDockItem
{
    public const string BandId = "com.brettkinny.glazewmdock.mode";

    private static readonly IconInfo DirectionIcon = new(char.ConvertFromUtf32(0xE7F4)); // Tiles
    private static readonly IconInfo ModeIcon = new(char.ConvertFromUtf32(0xE765)); // KeyboardClassic
    private static readonly IconInfo PausedIcon = new(char.ConvertFromUtf32(0xE769)); // Pause

    private readonly ListItem _item;
    private volatile WmState _state = WmState.Empty;

    public ModeBand(GlazeWmClient client)
        : base([], BandId, "GlazeWM Mode & Tiling Direction")
    {
        var primary = new GlazeCommand(
            client,
            "com.brettkinny.glazewmdock.mode.primary",
            "Toggle tiling direction / exit mode",
            PrimaryCommand);

        _item = new ListItem(primary)
        {
            Title = "↔", // ↔
            Subtitle = string.Empty,
            Icon = DirectionIcon,
            MoreCommands =
            [
                new CommandContextItem(new GlazeCommand(client, "com.brettkinny.glazewmdock.mode.direction", "Toggle tiling direction", "toggle-tiling-direction", DirectionIcon)),
                new CommandContextItem(new GlazeCommand(client, "com.brettkinny.glazewmdock.mode.pause", "Toggle pause", "wm-toggle-pause", PausedIcon)),
                new CommandContextItem(new GlazeCommand(client, "com.brettkinny.glazewmdock.mode.redraw", "Redraw all windows", "wm-redraw", new IconInfo(char.ConvertFromUtf32(0xE72C)))),
                new CommandContextItem(new GlazeCommand(client, "com.brettkinny.glazewmdock.mode.reload", "Reload GlazeWM config", "wm-reload-config", new IconInfo(char.ConvertFromUtf32(0xE777)))),
            ],
        };

        Icon = DirectionIcon;
        Items = [_item];
    }

    public void Update(WmState state)
    {
        _state = state;

        string title;
        string subtitle;
        IconInfo icon;

        if (state.Paused)
        {
            (title, subtitle, icon) = ("PAUSED", "Click to resume", PausedIcon);
        }
        else if (state.BindingModes.Length > 0)
        {
            title = string.Join(' ', state.BindingModes.Select(m => m.ToUpperInvariant()));
            (subtitle, icon) = ("Click to exit", ModeIcon);
        }
        else
        {
            var vertical = string.Equals(state.TilingDirection, "vertical", StringComparison.OrdinalIgnoreCase);
            title = vertical ? "↕" : "↔"; // ↕ / ↔
            subtitle = vertical ? "Vertical" : "Horizontal";
            icon = DirectionIcon;
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

    // What a click does depends on the state the band is showing right now.
    private string PrimaryCommand()
    {
        var state = _state;
        if (state.Paused)
        {
            return "wm-toggle-pause";
        }

        return state.BindingModes.Length > 0
            ? $"wm-disable-binding-mode --name {state.BindingModes[0]}"
            : "toggle-tiling-direction";
    }
}
