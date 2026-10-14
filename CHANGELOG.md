# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Versioning note: the Microsoft Store reserves the 4th version field (it must stay
`0`), so releases bump the 3rd field (`1.0.0`, `1.0.1`, `1.0.2`, and so on),
which maps to package versions `1.0.0.0`, `1.0.1.0`, `1.0.2.0`.

## [Unreleased]

- Preparing the first public / Microsoft Store release: MIT license, public docs,
  community-health files, and package version restructured to `1.0.0.0`.

## [1.1.1] - fork: dock improvements

Package versions `1.1.0.0` / `1.1.1.0`, sideloaded under the fork's self-signed
`CN=GlazeWMDock Dev` identity rather than the Store identity.

### Added
- **GlazeWM Workspace Buttons** band: one clickable button per workspace that
  focuses it directly, with the focused workspace in `[brackets]`. Right-click a
  button to move the focused window there, or to move it and follow.
- Custom workspace buttons: per-workspace labels and icons (emoji or Segoe
  Fluent glyph code) set as `name = label | icon` lines in settings.
- **GlazeWM Focused Window** band: the focused window's title, process and
  state icon. Click or right-click it for window actions (toggle floating,
  tiling or fullscreen, minimize, close, move to a workspace), which target the
  window by id (`command --id`).
- **GlazeWM Mode & Tiling Direction** band: shows the tiling direction (click to
  flip it), the active binding mode (click to exit it), or PAUSED (click to
  resume). Right-click for toggle pause, redraw and reload config.
- Settings page in Command Palette: IPC port, workspace list, show only active
  workspaces, button style, custom buttons, window counts and focused-title
  length. Saving applies immediately; a port change reconnects.

### Changed
- The IPC client also subscribes to window, mode, tiling-direction and pause
  events, and coalesces each burst of events into one round of queries.
- The workspace strip reads its workspace list and show-only-active option from
  settings instead of compile-time constants.
- A failed live update rebuilds every band, not just the strip, so all bands
  recover after Command Palette's UI process restarts.

## [1.0.1] - certification fix (pending)

### Fixed
- Launching the executable directly (Start menu tile, or its AUMID) no longer
  exits instantly with no window. This project is a `WinExe` with no console, so
  the old no-arguments path wrote to a console that did not exist and terminated
  within milliseconds. Store certification launches the app's entry point and
  watches it, so that was indistinguishable from a crash — it is why the
  `1.0.0.0` submission was rejected under policy 10.1.2 ("The product crashes at
  launch"). Direct launches now show a dialog explaining that this package is a
  Command Palette extension, listing the PowerToys and GlazeWM prerequisites,
  and exit gracefully when dismissed.

## [1.0.0] - rejected at certification, never published

### Added
- Dock band: the GlazeWM workspace strip rendered as live text on the Command
  Palette Dock (filled glyph = focused, outline = active, plain = empty), with
  the focused workspace detailed in the subtitle.
- Multi-monitor grouping: the strip now groups workspaces by monitor (ordered
  left-to-right by physical position). Each monitor's currently displayed
  workspace is the bold filled circled digit; every other workspace is a plain
  digit. Focus is shown by brackets: every monitor except the one you're on is
  bracketed, so the un-bracketed group is where you are (e.g. `❸ 5 [1 2 4 ❻]`).
  The Dock API shares one band across all monitors, so this replaces the previous
  behavior where every monitor's dock showed an identical, focus-only strip.
- Top-level "GlazeWM Workspaces" palette page to switch workspaces from the
  palette (type / arrow / Enter).
- Live updates via GlazeWM v3's IPC WebSocket (`ws://127.0.0.1:6123`): subscribes
  to workspace/focus events and re-queries on each change, with auto-reconnect.
- Two display modes via the `ShowOnlyActive` constant (active-only, Zebar-like;
  or all workspaces, i3-style).
- Best-effort local diagnostic log under the app's `LocalState` folder
  (connection status and exception types only; rotated at 512 KB).

### Fixed
- Dock strip and switcher freezing after CmdPal.UI restarts on monitor hot-plug
  (a stale COM subscriber was retained; now guarded, with the dock band rebuilt).
