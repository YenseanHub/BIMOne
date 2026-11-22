using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitModeler.Plugin.Configuration;

/// <summary>command.json 的命令声明（与仓库根 command.json 字段对齐）。</summary>
public class CommandDescriptor
{
    [JsonProperty("commandName")]
    public string CommandName { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; }

    [JsonProperty("assemblyPath")]
    public string AssemblyPath { get; set; }

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;
}

public class CommandManifest
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("commands")]
    public List<CommandDescriptor> Commands { get; set; } = new();
}
