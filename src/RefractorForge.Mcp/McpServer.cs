using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RefractorForge.Mcp;

/// <summary>One callable tool: its name, human description, JSON-Schema for arguments, and the handler that runs it
/// (returning a text result; throwing maps to an MCP tool error).</summary>
/// <summary>What a tool call produced: text, and optionally a PNG to go with it. The implicit conversion from
/// string is what lets the great majority of tools stay plain "return a sentence" lambdas.</summary>
public readonly record struct ToolResult(string Text, byte[]? Png = null)
{
    public static implicit operator ToolResult(string text) => new(text, null);
}

public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonElement, ToolResult> Handler);

/// <summary>
/// A minimal, dependency-free Model Context Protocol server speaking JSON-RPC 2.0 over stdio (newline-delimited, the
/// MCP stdio transport). Implements the subset clients need: initialize / tools/list / tools/call / ping. Hand-rolled
/// rather than pulling the preview SDK so the project stays self-contained and always builds; clients see standard
/// MCP either way.
/// </summary>
public sealed class McpServer
{
    private readonly Dictionary<string, McpTool> _tools = new(StringComparer.Ordinal);
    private readonly string _name;
    private readonly string _version;

    public McpServer(string name, string version) { _name = name; _version = version; }

    public void Add(McpTool tool) => _tools[tool.Name] = tool;

    public void Run()
    {
        // The real stdout carries ONLY JSON-RPC; redirect Console.Out to stderr so any library Console.WriteLine
        // (e.g. LevelArchive's "skipping unreadable archive") can never corrupt the protocol stream.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        Console.SetOut(Console.Error);

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            JsonNode? req;
            try { req = JsonNode.Parse(line); } catch { continue; }   // malformed line: ignore
            if (req is null) continue;
            var resp = Handle(req);
            if (resp is not null) stdout.WriteLine(resp.ToJsonString());   // compact, no embedded newlines
        }
    }

    private JsonNode? Handle(JsonNode req)
    {
        var idNode = req["id"];                       // absent => notification (no response)
        bool isNotification = idNode is null;
        string? method = req["method"]?.GetValue<string>();
        if (method is null) return isNotification ? null : Error(idNode, -32600, "Invalid Request");

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    string pv = req["params"]?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05";
                    var result = new JsonObject
                    {
                        ["protocolVersion"] = pv,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = _name, ["version"] = _version },
                        ["instructions"] = "RefractorForge map editor for Battlefield 1942 / Vietnam. open_level a .rfa, then place_object / scatter / generate_city / set_water_level, then save_level."
                    };
                    return Ok(idNode, result);
                }
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;
                case "ping":
                    return Ok(idNode, new JsonObject());
                case "tools/list":
                {
                    var arr = new JsonArray();
                    foreach (var t in _tools.Values)
                        arr.Add(new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema.DeepClone()
                        });
                    return Ok(idNode, new JsonObject { ["tools"] = arr });
                }
                case "tools/call":
                {
                    string? name = req["params"]?["name"]?.GetValue<string>();
                    if (name is null || !_tools.TryGetValue(name, out var tool))
                        return Error(idNode, -32602, $"Unknown tool '{name}'");

                    var argsNode = req["params"]?["arguments"];
                    using var argsDoc = argsNode is null ? null : JsonDocument.Parse(argsNode.ToJsonString());
                    JsonElement args = argsDoc?.RootElement ?? default;

                    ToolResult res; bool isErr = false;
                    try { res = tool.Handler(args); }
                    catch (Exception ex) { res = $"ERROR: {ex.Message}"; isErr = true; }

                    var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = res.Text } };
                    // A tool that produced a picture attaches it, so a client that can display images shows the
                    // map rather than a paragraph describing it.
                    if (res.Png is { Length: > 0 } png)
                        content.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = Convert.ToBase64String(png),
                            ["mimeType"] = "image/png",
                        });
                    return Ok(idNode, new JsonObject { ["content"] = content, ["isError"] = isErr });
                }
                default:
                    return isNotification ? null : Error(idNode, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            return isNotification ? null : Error(idNode, -32603, ex.Message);
        }
    }

    private static JsonNode Ok(JsonNode? id, JsonNode result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonNode Error(JsonNode? id, int code, string message)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
