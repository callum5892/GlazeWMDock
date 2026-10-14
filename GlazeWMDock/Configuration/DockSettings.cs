using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock.Configuration;

/// <summary>
/// User-editable settings, shown on the extension's page in Command Palette
/// settings and persisted to <c>LocalState\settings.json</c>. Replaces the
/// compile-time constants (port, workspace names, show-only-active) the
/// extension used to need a rebuild to change.
///
/// <see cref="Changed"/> fires after the user saves the form; the provider
/// re-renders every band and the IPC client reconnects if the port moved.
/// </summary>
internal sealed class DockSettings : JsonSettingsManager
{
    public const int DefaultPort = 6123;
    private const int DefaultTitleLength = 40;

    private readonly TextSetting _port = new(
        "port",
        "GlazeWM IPC port",
        "The port GlazeWM's IPC WebSocket listens on (6123 unless you've changed it).",
        DefaultPort.ToString(CultureInfo.InvariantCulture));

    private readonly TextSetting _workspaces = new(
        "workspaces",
        "Workspaces",
        "Comma-separated workspace names, in the order to show them. Should match the workspaces block in your GlazeWM config.yaml.",
        "1, 2, 3, 4, 5, 6, 7, 8, 9, 10");

    private readonly ToggleSetting _showOnlyActive = new(
        "showOnlyActive",
        "Show only active workspaces",
        "Hide workspaces that are empty and not displayed. Turn off for a fixed i3-style row of every workspace above.",
        true);

    private readonly ChoiceSetSetting _labelStyle = new(
        "labelStyle",
        "Workspace button style",
        "How workspace buttons are labelled when no custom label is set below.",
        [
            new ChoiceSetSetting.Choice("Workspace name, focused in [brackets]", WorkspaceLabelStyle.Name),
            new ChoiceSetSetting.Choice("Display name from GlazeWM config, focused in [brackets]", WorkspaceLabelStyle.DisplayName),
            new ChoiceSetSetting.Choice("Circled digits (filled = focused, outline = shown on another monitor)", WorkspaceLabelStyle.Glyphs),
        ]);

    private readonly TextSetting _customLabels = new(
        "customLabels",
        "Custom workspace buttons",
        "One per line: name = label | icon. The focused workspace's label is shown in [brackets]. The icon is optional and can be an emoji or a Segoe Fluent glyph code. Examples:  1 = Main | E80F   3 = Code   9 = | 🎵",
        string.Empty)
    {
        Multiline = true,
        Placeholder = "1 = Main | E80F\n2 = Web | E774\n3 = Code",
    };

    private readonly ToggleSetting _showWindowCounts = new(
        "showWindowCounts",
        "Show window counts",
        "Show the number of windows under each workspace button.",
        false);

    private readonly TextSetting _titleLength = new(
        "titleLength",
        "Focused window title length",
        "Maximum characters of the focused window's title shown on the Dock.",
        DefaultTitleLength.ToString(CultureInfo.InvariantCulture));

    public DockSettings()
    {
        FilePath = SettingsJsonPath();

        Settings.Add(_port);
        Settings.Add(_workspaces);
        Settings.Add(_showOnlyActive);
        Settings.Add(_labelStyle);
        Settings.Add(_customLabels);
        Settings.Add(_showWindowCounts);
        Settings.Add(_titleLength);

        LoadSettings();
        CustomButtons = WorkspaceButtonStyle.ParseAll(_customLabels.Value);

        Settings.SettingsChanged += (_, _) =>
        {
            CustomButtons = WorkspaceButtonStyle.ParseAll(_customLabels.Value);
            SaveSettings();
            Log.Line("settings saved");
            Changed?.Invoke();
        };
    }

    /// <summary>Raised after the user saves new settings.</summary>
    public event Action? Changed;

    public int Port =>
        int.TryParse(_port.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536
            ? p
            : DefaultPort;

    public string[] WorkspaceNames
    {
        get
        {
            var names = (_workspaces.Value ?? string.Empty)
                .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return names.Length > 0 ? names : ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"];
        }
    }

    public bool ShowOnlyActive => _showOnlyActive.Value;

    public string LabelStyle => _labelStyle.Value ?? WorkspaceLabelStyle.Name;

    public bool ShowWindowCounts => _showWindowCounts.Value;

    public int TitleLength =>
        int.TryParse(_titleLength.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 5
            ? n
            : DefaultTitleLength;

    /// <summary>
    /// Parsed custom buttons, cached between saves so each band update reuses
    /// the same <see cref="IconInfo"/> instances instead of re-sending icons.
    /// </summary>
    public IReadOnlyDictionary<string, WorkspaceButtonStyle> CustomButtons { get; private set; } =
        new Dictionary<string, WorkspaceButtonStyle>();

    private static string SettingsJsonPath()
    {
        var directory = Utilities.BaseSettingsPath("GlazeWMDock");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}

/// <summary>Values of the "Workspace button style" choice.</summary>
internal static class WorkspaceLabelStyle
{
    public const string Glyphs = "glyphs";
    public const string Name = "name";
    public const string DisplayName = "displayName";
}
