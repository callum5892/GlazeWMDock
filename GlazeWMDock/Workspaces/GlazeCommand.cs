using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock.Workspaces;

/// <summary>
/// Runs a GlazeWM command over IPC when invoked, optionally targeting a
/// specific container (<c>command --id</c>). Used for the Dock buttons, their
/// right-click menus and the window-actions page.
///
/// Dock buttons return <see cref="CommandResult.KeepOpen"/> (there's no palette
/// to dismiss); palette pages pass <c>dismiss: true</c> so the palette closes
/// and focus returns to the window being acted on.
/// </summary>
internal sealed partial class GlazeCommand : InvokableCommand
{
    private readonly GlazeWmClient _client;
    private readonly Func<string> _command;
    private readonly Func<string?>? _subjectId;
    private readonly bool _dismiss;

    public GlazeCommand(
        GlazeWmClient client,
        string id,
        string name,
        string command,
        IconInfo? icon = null,
        Func<string?>? subjectId = null,
        bool dismiss = false)
        : this(client, id, name, () => command, icon, subjectId, dismiss)
    {
    }

    /// <param name="command">Evaluated on each invoke, for buttons whose action depends on live state.</param>
    public GlazeCommand(
        GlazeWmClient client,
        string id,
        string name,
        Func<string> command,
        IconInfo? icon = null,
        Func<string?>? subjectId = null,
        bool dismiss = false)
    {
        _client = client;
        _command = command;
        _subjectId = subjectId;
        _dismiss = dismiss;

        // A non-empty, stable Id is required for items that live on the Dock.
        Id = id;
        Name = name;
        if (icon is not null)
        {
            Icon = icon;
        }
    }

    public override ICommandResult Invoke()
    {
        var command = _command();
        if (!string.IsNullOrEmpty(command))
        {
            _ = _client.RunCommandAsync(command, _subjectId?.Invoke());
        }

        return _dismiss ? CommandResult.Dismiss() : CommandResult.KeepOpen();
    }
}
