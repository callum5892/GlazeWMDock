using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GlazeWMDock.Configuration;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock.Workspaces;

/// <summary>
/// A Dock band with one clickable button per workspace. Clicking a button
/// focuses that workspace directly (unlike the text strip, whose single click
/// target can only open the switcher page).
///
/// The Dock has no "selected" highlight for a button, so state is carried by
/// the label: by default the focused workspace is bracketed (<c>1 [2] 6</c>)
/// and every other button is its plain name, so all buttons share one font and
/// size. Custom labels from settings are bracketed the same way and may add an
/// icon. The circled-digit glyphs remain available as a button style.
///
/// Each button's right-click menu moves the focused window to that workspace.
/// Buttons are kept and mutated in place; the item list is only replaced when
/// the set of workspaces shown changes.
/// </summary>
internal sealed partial class WorkspaceButtonsBand : WrappedDockItem
{
    public const string BandId = "com.brettkinny.glazewmdock.buttons";

    // An empty Title falls back to the command's Name ("Workspace 9"), so an
    // icon-only custom button gets a zero-width space, or a dot when focused.
    private const string IconOnlyTitle = "​"; // zero-width space
    private const string IconOnlyFocusedTitle = "•"; // bullet

    private readonly GlazeWmClient _client;
    private readonly Func<string?> _focusedWindowId;
    private readonly Dictionary<string, ListItem> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private string[] _shown = [];

    /// <param name="focusedWindowId">Id of the window "move here" should move.</param>
    public WorkspaceButtonsBand(GlazeWmClient client, Func<string?> focusedWindowId)
        : base([], BandId, "GlazeWM Workspace Buttons")
    {
        _client = client;
        _focusedWindowId = focusedWindowId;
        Icon = new IconInfo(char.ConvertFromUtf32(0xE7F4)); // Segoe Fluent "Tiles"
    }

    public void Update(IReadOnlyList<MonitorInfo> monitors, DockSettings settings)
    {
        var live = new Dictionary<string, WorkspaceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var ws in monitors.SelectMany(m => m.Workspaces))
        {
            live[ws.Name] = ws;
        }

        var configured = settings.WorkspaceNames;
        var shown = configured
            .Where(name => !settings.ShowOnlyActive || live.ContainsKey(name))
            .Concat(live.Keys
                .Where(name => !configured.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var custom = settings.CustomButtons;
        foreach (var name in shown)
        {
            var button = GetOrCreateButton(name);
            var hasLive = live.TryGetValue(name, out var ws);
            custom.TryGetValue(name, out var style);

            SetIfChanged(button, Label(name, hasLive ? ws : null, style, settings.LabelStyle), WindowCountLabel(ws, settings));
            // No icon at all (not a blank one) unless the user set one, so the
            // Dock doesn't reserve an empty icon slot in every button.
            var icon = style?.Icon;
            if (!ReferenceEquals(button.Icon, icon))
            {
                button.Icon = icon;
            }
        }

        if (!shown.SequenceEqual(_shown, StringComparer.OrdinalIgnoreCase))
        {
            _shown = shown;
            Items = shown.Select(n => (IListItem)_buttons[n]).ToArray();
        }
    }

    private ListItem GetOrCreateButton(string name)
    {
        if (_buttons.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var moveHere = new GlazeCommand(
            _client,
            $"com.brettkinny.glazewmdock.move.{name}",
            $"Move focused window to workspace {name}",
            $"move --workspace {name}",
            new IconInfo(char.ConvertFromUtf32(0xE8AB)), // Switch
            _focusedWindowId);

        var button = new ListItem(new FocusWorkspaceCommand(_client, name))
        {
            Title = name,
            MoreCommands =
            [
                new CommandContextItem(moveHere),
                new CommandContextItem(new MoveAndFollowCommand(_client, name, _focusedWindowId)),
            ],
        };

        _buttons[name] = button;
        return button;
    }

    private static string Label(string name, WorkspaceInfo? ws, WorkspaceButtonStyle? style, string labelStyle)
    {
        var focused = ws?.HasFocus == true;

        if (style is not null)
        {
            if (style.Label.Length == 0)
            {
                return focused ? IconOnlyFocusedTitle : IconOnlyTitle;
            }

            return Bracket(style.Label, focused);
        }

        return labelStyle switch
        {
            WorkspaceLabelStyle.Glyphs => WorkspaceGlyphs.For(name, focused, ws?.IsDisplayed == true),
            WorkspaceLabelStyle.DisplayName => Bracket(string.IsNullOrEmpty(ws?.DisplayName) ? name : ws.Value.DisplayName, focused),
            _ => Bracket(name, focused),
        };
    }

    private static string Bracket(string text, bool focused) => focused ? $"[{text}]" : text;

    private static string WindowCountLabel(WorkspaceInfo ws, DockSettings settings) =>
        settings.ShowWindowCounts && ws.WindowCount > 0
            ? ws.WindowCount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    // Every assignment raises PropChanged across COM to the Dock, so skip
    // no-op writes; a workspace switch then only repaints the buttons that moved.
    private static void SetIfChanged(ListItem button, string title, string subtitle)
    {
        if (button.Title != title)
        {
            button.Title = title;
        }

        if (button.Subtitle != subtitle)
        {
            button.Subtitle = subtitle;
        }
    }

    /// <summary>
    /// Moves the focused window to the workspace, then follows it there — the
    /// same pair your alt+shift+N keybinds run. The two commands are awaited in
    /// sequence so the focus can't overtake the move.
    /// </summary>
    private sealed partial class MoveAndFollowCommand : InvokableCommand
    {
        private readonly GlazeWmClient _client;
        private readonly string _workspace;
        private readonly Func<string?> _windowId;

        public MoveAndFollowCommand(GlazeWmClient client, string workspace, Func<string?> windowId)
        {
            _client = client;
            _workspace = workspace;
            _windowId = windowId;
            Id = $"com.brettkinny.glazewmdock.movefollow.{workspace}";
            Name = $"Move focused window to workspace {workspace} and follow";
            Icon = new IconInfo(char.ConvertFromUtf32(0xE72A)); // Forward
        }

        public override ICommandResult Invoke()
        {
            var windowId = _windowId();
            _ = Task.Run(async () =>
            {
                await _client.RunCommandAsync($"move --workspace {_workspace}", windowId).ConfigureAwait(false);
                await _client.RunCommandAsync($"focus --workspace {_workspace}").ConfigureAwait(false);
            });
            return CommandResult.KeepOpen();
        }
    }
}
