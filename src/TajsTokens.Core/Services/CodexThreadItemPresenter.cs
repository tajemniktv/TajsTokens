// Taj's Tokens | CodexThreadItemPresenter.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text;
using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>
///     Converts observed Codex thread-item JSON into a readable, local-only presentation. This is
///     deliberately tolerant: the native item and JSON are always retained when a newer or malformed
///     shape cannot be understood.
/// </summary>
public static class CodexThreadItemPresenter
{
    private const int MaxDisplayedCharacters = 80_000;

    public static CodexThreadItemPresentation Present(CodexThreadItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ItemJson))
        {
            return Unknown(item, "Empty item payload");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(item.ItemJson);
            JsonElement root = document.RootElement;
            string type = StringProperty(root, "type") ?? item.ItemType;
            return NormalizeType(type) switch
            {
                "usermessage" => Message(item, root, true),
                "agentmessage" => Message(item, root, false),
                "reasoning" => Reasoning(item, root),
                "commandexecution" => Command(item, root),
                "filechange" => FileChange(item, root),
                "mcptoolcall" or "dynamictoolcall" or "functioncalloutput" => Tool(item, root, type),
                "collabagenttoolcall" or "subagentactivity" => Collaboration(item, root, type),
                "plan" => Plan(item, root),
                "contextcompaction" or "websearch" or "imageview" or "imagegeneration" or
                    "sleep" or "enteredreviewmode" or "exitedreviewmode" or "hookprompt" =>
                    SystemEvent(item, root, type),
                _ => Unknown(item, $"Unsupported native item type: {type}"),
            };
        }
        catch (JsonException)
        {
            return Unknown(item, "The source item contains malformed JSON");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return Unknown(item, $"The source item could not be presented: {exception.Message}");
        }
    }

    public static IReadOnlyList<CodexThreadItemPresentation> PresentMany(
        IEnumerable<CodexThreadItem> items)
    {
        return items.Select(Present).ToArray();
    }

    private static CodexThreadItemPresentation Message(
        CodexThreadItem item,
        JsonElement root,
        bool isUser)
    {
        string? body = isUser
            ? UserContent(root)
            : StringProperty(root, "text");
        string heading = isUser ? "You" : "Codex";
        var facts = new List<CodexThreadItemFact>();
        AddStringFact(facts, "Phase", root, "phase");
        AddStringFact(facts, "Delivery", root, "delivery");
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.Message,
            heading,
            Trim(body),
            facts,
            true,
            !string.IsNullOrWhiteSpace(body));
    }

    private static CodexThreadItemPresentation Reasoning(CodexThreadItem item, JsonElement root)
    {
        List<string> summary = StringArray(root, "summary");
        List<string> content = StringArray(root, "content");
        string body = summary.Count > 0 ? string.Join(Environment.NewLine, summary) : string.Join(Environment.NewLine, content);
        var facts = new List<CodexThreadItemFact>();
        if (summary.Count > 0) facts.Add(new CodexThreadItemFact("Summary sections", summary.Count.ToString()));
        if (content.Count > 0) facts.Add(new CodexThreadItemFact("Raw content sections", content.Count.ToString()));
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.Reasoning,
            "Reasoning",
            Trim(body),
            facts,
            false,
            body.Length > 0);
    }

    private static CodexThreadItemPresentation Command(CodexThreadItem item, JsonElement root)
    {
        var facts = new List<CodexThreadItemFact>();
        AddStringFact(facts, "Status", root, "status");
        AddStringFact(facts, "Working directory", root, "cwd");
        AddStringFact(facts, "Exit code", root, "exitCode");
        AddStringFact(facts, "Duration", root, "durationMs", " ms");
        AddStringFact(facts, "Source", root, "source");
        AddStringFact(facts, "Process", root, "processId");
        string? command = StringProperty(root, "command");
        string? output = StringProperty(root, "aggregatedOutput");
        string? body = string.IsNullOrWhiteSpace(output)
            ? command
            : string.IsNullOrWhiteSpace(command)
                ? output
                : command + Environment.NewLine + Environment.NewLine + output;
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.CommandExecution,
            "Command",
            Trim(body),
            facts,
            false,
            !string.IsNullOrWhiteSpace(body));
    }

    private static CodexThreadItemPresentation FileChange(CodexThreadItem item, JsonElement root)
    {
        var facts = new List<CodexThreadItemFact>();
        AddStringFact(facts, "Status", root, "status");
        if (root.TryGetProperty("changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array)
        {
            facts.Add(new CodexThreadItemFact("Files", changes.GetArrayLength().ToString()));
            string?[] paths = changes.EnumerateArray()
                .Select(change => StringProperty(change, "path") ?? StringProperty(change, "filePath"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(30)
                .ToArray();
            if (paths.Length > 0)
            {
                facts.Add(new CodexThreadItemFact("Paths", string.Join(", ", paths)));
            }
        }

        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.FileChange,
            "File changes",
            null,
            facts,
            false,
            true);
    }

    private static CodexThreadItemPresentation Tool(CodexThreadItem item, JsonElement root, string type)
    {
        var facts = new List<CodexThreadItemFact>();
        AddStringFact(facts, "Status", root, "status");
        AddStringFact(facts, "Tool", root, "tool");
        AddStringFact(facts, "Server", root, "server");
        AddStringFact(facts, "Namespace", root, "namespace");
        AddStringFact(facts, "Name", root, "name");
        AddStringFact(facts, "Duration", root, "durationMs", " ms");
        AddJsonFact(facts, "Arguments", root, "arguments");
        AddJsonFact(facts, "Result", root, "result");
        AddJsonFact(facts, "Error", root, "error");
        string? body = StringProperty(root, "output") ?? StringProperty(root, "result") ?? StringProperty(root, "error");
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.ToolCall,
            type,
            Trim(body),
            facts,
            false,
            true);
    }

    private static CodexThreadItemPresentation Collaboration(CodexThreadItem item, JsonElement root, string type)
    {
        var facts = new List<CodexThreadItemFact>();
        AddStringFact(facts, "Kind", root, "kind");
        AddStringFact(facts, "Status", root, "status");
        AddStringFact(facts, "Sender", root, "senderThreadId");
        AddStringFact(facts, "Agent path", root, "agentPath");
        string? linked = StringProperty(root, "agentThreadId");
        if (root.TryGetProperty("receiverThreadIds", out JsonElement receivers) && receivers.ValueKind == JsonValueKind.Array)
        {
            string?[] ids = receivers.EnumerateArray().Select(value => value.GetString()).Where(value => value is not null).ToArray();
            if (ids.Length > 0)
            {
                linked ??= ids[0];
                facts.Add(new CodexThreadItemFact("Receivers", string.Join(", ", ids)));
            }
        }
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.Collaboration,
            type,
            StringProperty(root, "prompt"),
            facts,
            false,
            true,
            linked);
    }

    private static CodexThreadItemPresentation Plan(CodexThreadItem item, JsonElement root)
    {
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.Plan,
            "Plan",
            Trim(StringProperty(root, "text")),
            Array.Empty<CodexThreadItemFact>(),
            true,
            true);
    }

    private static CodexThreadItemPresentation SystemEvent(CodexThreadItem item, JsonElement root, string type)
    {
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.SystemEvent,
            type,
            null,
            Array.Empty<CodexThreadItemFact>(),
            false,
            false);
    }

    private static CodexThreadItemPresentation Unknown(CodexThreadItem item, string message)
    {
        return new CodexThreadItemPresentation(
            item,
            CodexThreadItemPresentationKind.Unknown,
            "Unknown Codex item",
            message,
            new[] { new CodexThreadItemFact("Native type", item.ItemType) },
            false,
            true);
    }

    private static string UserContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return StringProperty(root, "text") ?? string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonElement part in content.EnumerateArray())
        {
            string? type = StringProperty(part, "type");
            string? text = StringProperty(part, "text");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) && text is not null)
            {
                if (builder.Length > 0) builder.AppendLine();
                builder.Append(text);
            }
            else if (!string.IsNullOrWhiteSpace(type))
            {
                if (builder.Length > 0) builder.AppendLine();
                builder.Append('[').Append(type).Append(']');
            }
        }
        return builder.ToString();
    }

    private static List<string> StringArray(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(item => item is not null).Cast<string>().ToList()
            : [];
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : value.ToString()
            : null;
    }

    private static void AddStringFact(
        ICollection<CodexThreadItemFact> facts,
        string label,
        JsonElement root,
        string property,
        string suffix = "")
    {
        string? value = StringProperty(root, property);
        if (!string.IsNullOrWhiteSpace(value)) facts.Add(new CodexThreadItemFact(label, value + suffix));
    }

    private static void AddJsonFact(
        ICollection<CodexThreadItemFact> facts,
        string label,
        JsonElement root,
        string property)
    {
        if (root.TryGetProperty(property, out JsonElement value) && value.ValueKind is not JsonValueKind.Null)
        {
            string? text = Trim(value.ToString());
            if (!string.IsNullOrWhiteSpace(text)) facts.Add(new CodexThreadItemFact(label, text));
        }
    }

    private static string NormalizeType(string type)
    {
        return new string(type.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        value = value.Trim();
        return value.Length <= MaxDisplayedCharacters ? value : value[..MaxDisplayedCharacters] + "\n… [display truncated]";
    }
}