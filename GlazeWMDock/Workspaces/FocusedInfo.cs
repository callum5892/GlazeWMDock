namespace GlazeWMDock.Workspaces;

/// <summary>
/// The container GlazeWM reports as focused ("query focused"): either a window
/// or, when the focused workspace is empty, the workspace itself.
/// </summary>
/// <param name="IsWindow">True for a window; false for an empty workspace.</param>
/// <param name="Id">The container id, usable as <c>command --id</c>.</param>
/// <param name="Title">Window title, or the workspace's display name.</param>
/// <param name="ProcessName">Window process name (e.g. <c>Code</c>); empty for a workspace.</param>
/// <param name="State">Window state type: tiling, floating, fullscreen or minimized.</param>
internal sealed record FocusedInfo(
    bool IsWindow,
    string Id,
    string Title,
    string ProcessName,
    string State);

/// <summary>
/// The window manager's mode state shown on the mode band.
/// </summary>
/// <param name="BindingModes">Names of active binding modes (e.g. "resize"), as used by wm-disable-binding-mode.</param>
/// <param name="Paused">Whether GlazeWM is paused (wm-toggle-pause).</param>
/// <param name="TilingDirection">"horizontal" or "vertical" for the focused container.</param>
internal sealed record WmState(
    string[] BindingModes,
    bool Paused,
    string TilingDirection)
{
    public static readonly WmState Empty = new([], false, string.Empty);
}
