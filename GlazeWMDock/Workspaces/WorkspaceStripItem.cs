using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GlazeWMDock.Configuration;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock.Workspaces;

/// <summary>
/// The single Dock band item that renders the whole workspace strip as live
/// <em>text on the bar</em> — the way the built-in Time &amp; Date and
/// Performance widgets do — instead of an icon that opens a flyout.
///
/// The Command Palette Dock renders each child item of a band inline, binding a
/// TextBlock to the item's <c>Title</c> (and a smaller one to <c>Subtitle</c>).
/// So the trick to getting dynamic text on the bar is to put the live text in
/// <c>Title</c>/<c>Subtitle</c> and mutate it in place: the toolkit's
/// <c>CommandItem</c> setters raise <c>PropChanged</c>, which the Dock observes
/// and re-renders. This is exactly the pattern the clock's <c>NowDockBand</c>
/// uses (it just assigns <c>Title = timeString; Subtitle = dateString;</c> on a
/// timer). Here the "tick" is a GlazeWM workspace/focus event instead.
///
/// On a multi-monitor setup the Command Palette Dock renders the same band on
/// every monitor — the SDK gives an extension no way to know which monitor a
/// band is painting on (<c>GetDockBands()</c> takes no monitor context) — so a
/// single strip is inevitably shared across all docks. To keep that shared strip
/// from looking like every monitor mirrors the same state, it shows each
/// monitor's workspaces as its own group, ordered left-to-right by physical
/// position. Each monitor's currently displayed workspace is the bold filled
/// circled digit; every other workspace is a plain digit. Which monitor has
/// focus is shown by the brackets: every monitor <em>except</em> the one you're
/// on is wrapped in <c>[brackets]</c>, so the un-bracketed group is where you
/// are. So <c>❸ 5 [1 2 4 ❻]</c> reads "I'm on this monitor, showing workspace 3
/// (which also has 5); the other monitor is showing 6 (and also has 1, 2, 4)."
/// Switching monitors just moves the brackets. On a single monitor there are no
/// brackets and it looks the way it always did. The focused workspace is also
/// named in the <c>Subtitle</c> (e.g. <c>Workspace 2</c>). Clicking the strip
/// opens the workspace switcher page.
/// </summary>
internal sealed partial class WorkspaceStripItem : ListItem
{
    private string[] _names;

    public WorkspaceStripItem(ICommand switcher, string[] names)
        : base(switcher)
    {
        _names = names;
        Title = string.Join(' ', names); // placeholder until the first snapshot
        Subtitle = "GlazeWM";
        Icon = new IconInfo(char.ConvertFromUtf32(0xE7F4)); // Segoe Fluent "Tiles"
    }

    /// <summary>
    /// Recompute the on-bar text from the latest monitor snapshot. Assigning
    /// Title/Subtitle raises PropChanged, so the Dock repaints the strip live.
    ///
    /// With <see cref="DockSettings.ShowOnlyActive"/> (default) only active
    /// workspaces (displayed on a monitor or holding windows) appear — matching
    /// Zebar, so unused numbers are hidden. Turned off, it's a persistent
    /// i3-style strip that also lists every configured-but-inactive workspace
    /// as a plain digit (appended after the per-monitor groups, since inactive
    /// workspaces aren't reported against any monitor by the IPC).
    /// </summary>
    public void Update(IReadOnlyList<MonitorInfo> monitors, DockSettings settings)
    {
        _names = settings.WorkspaceNames;
        var strip = new StringBuilder();
        var focusedDetail = string.Empty;
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Left-to-right by physical position so the strip mirrors the desktop
        // layout; Y then device name break ties for stacked / identical panels.
        var ordered = monitors
            .OrderBy(m => m.X)
            .ThenBy(m => m.Y)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase);

        foreach (var monitor in ordered)
        {
            var segment = new StringBuilder();

            foreach (var ws in SortWorkspaces(monitor.Workspaces))
            {
                activeNames.Add(ws.Name);

                if (segment.Length > 0)
                {
                    segment.Append(' ');
                }

                // Each monitor's currently displayed workspace gets the bold
                // filled circled digit; everything else is a plain, legible
                // digit. Which monitor has focus is shown by the brackets below
                // (the un-bracketed group is the one you're on), not by a second
                // glyph style — the thin outline circled digits were too hard to
                // read on the bar. IsDisplayed is true for exactly one workspace
                // per monitor, so this marks each monitor's current workspace the
                // same way whether or not that monitor has focus.
                segment.Append(ws.IsDisplayed
                    ? WorkspaceGlyphs.For(ws.Name, focused: true, active: true)
                    : ws.Name);

                if (ws.HasFocus)
                {
                    var label = string.IsNullOrEmpty(ws.DisplayName) ? ws.Name : ws.DisplayName;
                    focusedDetail = $"Workspace {label}";
                }
            }

            if (segment.Length == 0)
            {
                continue;
            }

            if (strip.Length > 0)
            {
                strip.Append(' ');
            }

            // Focused monitor stands alone; every other monitor is bracketed.
            if (monitor.HasFocus)
            {
                strip.Append(segment);
            }
            else
            {
                strip.Append('[').Append(segment).Append(']');
            }
        }

        // i3-style mode: list configured workspaces that aren't active anywhere.
        // They have no monitor grouping in the IPC, so trail them as plain digits.
        if (!settings.ShowOnlyActive)
        {
            foreach (var name in _names)
            {
                if (activeNames.Contains(name))
                {
                    continue;
                }

                if (strip.Length > 0)
                {
                    strip.Append(' ');
                }

                strip.Append(name);
            }
        }

        var title = strip.Length > 0 ? strip.ToString() : "no workspaces";
        if (Title != title)
        {
            Title = title;
        }

        if (Subtitle != focusedDetail)
        {
            Subtitle = focusedDetail;
        }
    }

    /// <summary>
    /// Order a monitor's workspaces by their position in the configured name
    /// list (so "1 2 3" not "3 1 2"); names outside the list sort last, by name.
    /// </summary>
    private IEnumerable<WorkspaceInfo> SortWorkspaces(IReadOnlyList<WorkspaceInfo> workspaces) =>
        workspaces
            .OrderBy(ws =>
            {
                var index = Array.IndexOf(_names, ws.Name);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(ws => ws.Name, StringComparer.OrdinalIgnoreCase);
}
