using System.Text;
using AccessOverlay.Core.Model;

namespace AccessOverlay.Core.Normalization;

/// <summary>
/// Turns a raw accessibility tree into a linear, semantically-flagged readable document.
/// Handles the classic ways UIA trees are broken: duplicated text, empty wrappers,
/// offscreen junk, unnamed controls, and text splattered across many tiny nodes.
/// </summary>
public static class DocumentNormalizer
{
    private const int MaxDepth = 6;
    private const int MaxControlValueChars = 240;
    private const int MaxParagraphChars = 400;

    private static readonly HashSet<string> ControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "SplitButton", "Link", "Edit", "ComboBox", "CheckBox", "RadioButton",
        "Slider", "Spinner", "TabItem", "MenuItem", "TreeItem", "Hyperlink",
        "Calendar", "ProgressBar", "ScrollBar", "Thumb", "DataItem", "Header", "HeaderItem",
        "StatusBar", "ToolTip",
    };

    private static readonly HashSet<string> ContainerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Group", "Pane", "Window", "Tab", "Custom", "List", "Tree", "Table", "DataGrid",
        "MenuBar", "Menu", "ToolBar", "TitleBar", "Panel", "SplitContainer", "Selection",
    };

    private static readonly Dictionary<string, string> RoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Button"] = "button",
        ["SplitButton"] = "split button",
        ["Link"] = "link",
        ["Hyperlink"] = "link",
        ["Edit"] = "edit field",
        ["ComboBox"] = "combo box",
        ["CheckBox"] = "check box",
        ["RadioButton"] = "radio button",
        ["Slider"] = "slider",
        ["Spinner"] = "spin box",
        ["TabItem"] = "tab",
        ["MenuItem"] = "menu item",
        ["TreeItem"] = "tree item",
        ["ProgressBar"] = "progress bar",
        ["Calendar"] = "calendar",
        ["StatusBar"] = "status bar",
        ["HeaderItem"] = "column header",
        ["DataItem"] = "item",
        ["ToolTip"] = "tooltip",
    };

    public static ReadableDocument Normalize(RawNode root, string? title = null, string? source = null)
    {
        var blocks = new List<ContentBlock>();
        Walk(root, parentText: "", depth: 0, blocks);

        var titleText = Clean(title)
            ?? (Clean(root.Name) is { Length: > 0 } t ? t : null)
            ?? "Untitled";

        return new ReadableDocument
        {
            Title = titleText,
            Source = Clean(source) ?? "Unknown source",
            Blocks = blocks,
        };
    }

    private static void Walk(RawNode node, string parentText, int depth, List<ContentBlock> blocks)
    {
        if (node.IsOffscreen) return;

        var text = Clean(node.OwnText) ?? "";

        if (node.ControlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase))
        {
            var itemText = text;
            if (itemText.Length == 0)
            {
                var first = node.Children.FirstOrDefault(c => Clean(c.OwnText) is { Length: > 0 });
                itemText = first is null ? "" : Clean(first.OwnText) ?? "";
            }
            if (itemText.Length > 0 && !IsDuplicateOfParent(itemText, parentText))
                Push(blocks, new ListItemBlock
                {
                    Text = itemText,
                    Depth = Math.Min(depth, MaxDepth),
                    RuntimeTarget = node.RuntimeTarget,
                });
            return;
        }

        if (ControlTypes.Contains(node.ControlType))
        {
            EmitControl(node, text, depth, blocks);
            return;
        }

        if (node.HeadingLevel is >= 1 and <= 9)
        {
            if (text.Length > 0 && !IsDuplicateOfParent(text, parentText))
            {
                Push(blocks, new HeadingBlock { Text = text, Level = node.HeadingLevel, Depth = Math.Min(depth, MaxDepth) });
                parentText = text;
            }
            // fall through: some headings wrap real content below them
        }

        var childDepth = ContainerTypes.Contains(node.ControlType) ? depth + 1 : depth;
        childDepth = Math.Min(childDepth, MaxDepth);

        var emittedAny = false;
        foreach (var child in node.Children)
        {
            // Suppress the classic "parent and child both announce the same text" duplication.
            if (child.OwnText.Length > 0 && ContainsText(parentText, child.OwnText) && !IsControl(child))
                continue;

            var before = blocks.Count;
            Walk(child, parentText, childDepth, blocks);
            if (blocks.Count > before) emittedAny = true;
        }

        if (!emittedAny && !ContainerTypes.Contains(node.ControlType) && !IsDuplicateOfParent(text, parentText))
        {
            EmitParagraphs(node.OwnText, depth, blocks);
        }
    }

    /// <summary>
    /// Emits free text as paragraphs. A line that ends with sentence punctuation (or a
    /// blank line) starts a new paragraph; hard-wrapped lines are merged back together.
    /// </summary>
    private static void EmitParagraphs(string rawText, int depth, List<ContentBlock> blocks)
    {
        var clampedDepth = Math.Min(depth, MaxDepth);
        var piece = new StringBuilder();
        void Flush()
        {
            if (piece.Length == 0) return;
            EmitParagraph(piece.ToString(), clampedDepth, blocks);
            piece.Clear();
        }

        foreach (var line in rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var clean = Clean(line);
            if (clean is null || clean.Length == 0)
            {
                Flush();
                continue;
            }
            if (piece.Length > 0) piece.Append(' ');
            piece.Append(clean);
            if (EndsSentence(clean))
                Flush();
        }
        Flush();
    }

    private static bool EndsSentence(string text)
    {
        var last = text[^1];
        return last is '.' or '!' or '?' or ':' or ';' or '”' or '"';
    }

    private static void EmitParagraph(string text, int depth, List<ContentBlock> blocks)
    {
        if (text.Length <= MaxParagraphChars)
        {
            Push(blocks, new ParagraphBlock { Text = text, Depth = depth });
            return;
        }

        // Oversized single paragraphs (minified pages, log dumps) get split at sentence boundaries.
        var piece = new StringBuilder(text.Length);
        foreach (var sentence in SplitSentences(text))
        {
            if (piece.Length > 0 && piece.Length + sentence.Length > MaxParagraphChars)
            {
                Push(blocks, new ParagraphBlock { Text = piece.ToString(), Depth = depth });
                piece.Clear();
            }
            piece.Append(sentence);
        }
        if (piece.Length > 0)
            Push(blocks, new ParagraphBlock { Text = piece.ToString(), Depth = depth });
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if ((text[i] == '.' || text[i] == '!' || text[i] == '?') && char.IsWhiteSpace(text[i + 1]))
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    private static void EmitControl(RawNode node, string text, int depth, List<ContentBlock> blocks)
    {
        var name = Clean(node.Name) ?? "";
        if (name.Length == 0)
            name = Clean(node.AutomationId) ?? "";
        if (name.Length == 0)
            name = Clean(node.LocalizedControlType) ?? "unnamed control";

        var role = RoleNames.TryGetValue(node.ControlType, out var r) ? r : Clean(node.LocalizedControlType) ?? node.ControlType;

        var value = node.IsPassword ? "" : Clean(node.Value) ?? "";
        if (node.IsPassword)
            name += " (protected)";
        if (value.Length > 0 && !string.Equals(value, name, StringComparison.Ordinal) && !name.Contains(value, StringComparison.Ordinal))
        {
            if (value.Length > MaxControlValueChars)
                value = value[..MaxControlValueChars] + "…";
            name += ": " + value;
        }

        var state = "";
        if (node.ControlType.Equals("CheckBox", StringComparison.OrdinalIgnoreCase) || node.ToggleState.Length > 0)
        {
            state = node.ToggleState.ToLowerInvariant() switch
            {
                "on" => ", checked",
                "off" => ", not checked",
                "indeterminate" => ", partially checked",
                _ => "",
            };
        }
        else if (node.IsSelected)
        {
            state = ", selected";
        }
        else if (node.ControlType.Equals("RadioButton", StringComparison.OrdinalIgnoreCase))
        {
            state = ", not selected";
        }

        if (!node.IsEnabled)
            state += ", unavailable";

        var line = string.Concat(name, " ", role, state);
        Push(blocks, new ControlBlock
        {
            Text = line.Trim(),
            Role = role,
            State = state.TrimStart(',', ' '),
            Depth = Math.Min(depth, MaxDepth),
            RuntimeTarget = node.RuntimeTarget,
        });
    }

    private static void Push(List<ContentBlock> blocks, ContentBlock block)
    {
        if (block.Text.Length == 0) return;
        var last = blocks.Count > 0 ? blocks[^1] : null;
        if (last is not null
            && last is not HeadingBlock
            && block is not HeadingBlock
            && string.Equals(last.Text, block.Text, StringComparison.Ordinal))
            return;
        blocks.Add(block);
    }

    private static bool IsControl(RawNode node) => ControlTypes.Contains(node.ControlType);

    private static bool IsDuplicateOfParent(string text, string parentText)
    {
        if (parentText.Length == 0) return false;
        if (string.Equals(text, parentText, StringComparison.Ordinal)) return true;
        if (text.Length > 3 && parentText.Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool ContainsText(string container, string candidate)
    {
        if (candidate.Length <= 3) return false;
        return container.Contains(candidate, StringComparison.Ordinal);
    }

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            sb.Append(c);
            space = false;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
