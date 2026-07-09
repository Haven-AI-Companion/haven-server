using System.Text.Json;
using System.Text.Json.Serialization;

namespace AshServer.Plugins;

public class PluginManifest
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("version")]     public string Version     { get; set; } = "1.0.0";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("enabled")]     public bool   Enabled     { get; set; } = true;
    [JsonPropertyName("builtin")]     public bool   Builtin     { get; set; } = false;
    [JsonPropertyName("config")]      public Dictionary<string, JsonElement>? Config { get; set; } = [];
    [JsonPropertyName("tools")]       public List<PluginTool> Tools { get; set; } = [];

    [JsonIgnore] public string DirectoryPath { get; set; } = "";
}

public class PluginTool
{
    [JsonPropertyName("name")]        public string      Name        { get; set; } = "";
    [JsonPropertyName("description")] public string      Description { get; set; } = "";
    [JsonPropertyName("parameters")]  public JsonElement Parameters  { get; set; }
    [JsonPropertyName("handler")]     public ToolHandler Handler     { get; set; } = new();
}

public class ToolHandler
{
    /// <summary>Type of handler: "http", "process", or "builtin"</summary>
    [JsonPropertyName("type")]    public string   Type    { get; set; } = "http";
    [JsonPropertyName("url")]     public string   Url     { get; set; } = "";
    [JsonPropertyName("command")] public string   Command { get; set; } = "";
    [JsonPropertyName("args")]    public string[] Args    { get; set; } = [];
}
