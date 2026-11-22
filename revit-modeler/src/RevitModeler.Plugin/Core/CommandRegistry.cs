using RevitMCPSDK.API.Interfaces;
using System.Collections.Generic;

namespace RevitModeler.Plugin.Core;

/// <summary>命令注册表：实现 SDK ICommandRegistry，name→IRevitCommand。</summary>
public class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, IRevitCommand> _commands = new();

    public CommandRegistry() { }

    /// <summary>整体替换式构造：热重载时一次性换成新版本的命令实例，避免逐条增删出现中间态。</summary>
    public CommandRegistry(IReadOnlyDictionary<string, IRevitCommand> commands)
    {
        foreach (var pair in commands) _commands[pair.Key] = pair.Value;
    }

    public void RegisterCommand(IRevitCommand command) => _commands[command.CommandName] = command;

    public bool TryGetCommand(string commandName, out IRevitCommand command)
        => _commands.TryGetValue(commandName, out command);

    public IEnumerable<string> GetRegisteredCommands() => _commands.Keys;

    public void ClearCommands() => _commands.Clear();
}
