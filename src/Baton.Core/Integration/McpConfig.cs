using System.Text;
using System.Text.Json;

namespace Baton.Core.Integration;

/// <summary>The text the user pastes into the MCP servers file of Claude Code or Claude Desktop to add Baton.</summary>
public static class McpConfig
{
    public const string ServerName = "baton";

    /// <summary>{"mcpServers":{"baton":{"command":"…\batonctl.exe","args":["mcp"]}}}, indented; the path is escaped as JSON needs.</summary>
    public static string Json(string batonctlPath)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("mcpServers");
            writer.WriteStartObject(ServerName);
            writer.WriteString("command", batonctlPath);
            writer.WriteStartArray("args");
            writer.WriteStringValue("mcp");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n");
    }
}
