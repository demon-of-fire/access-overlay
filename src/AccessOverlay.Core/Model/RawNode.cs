namespace AccessOverlay.Core.Model;

/// <summary>
/// A lightweight, neutral accessibility-tree node. Filled by the UIA reader,
/// consumed by the normalizer. Kept UIA-free so the normalizer is unit-testable.
/// </summary>
public sealed class RawNode
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string HelpText { get; set; } = "";
    public string AutomationId { get; set; } = "";

    /// <summary>UIA control type programmatic name, e.g. "Button", "Document", "Text".</summary>
    public string ControlType { get; set; } = "";

    public string LocalizedControlType { get; set; } = "";

    /// <summary>1-9 for headings, 0 for not a heading.</summary>
    public int HeadingLevel { get; set; }

    public bool IsOffscreen { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsKeyboardFocusable { get; set; }

    /// <summary>"On" / "Off" / "Indeterminate" for toggles, empty otherwise.</summary>
    public string ToggleState { get; set; } = "";

    public bool IsSelected { get; set; }
    public bool IsReadOnly { get; set; }

    /// <summary>True when the underlying control contains a protected password value.</summary>
    public bool IsPassword { get; set; }

    /// <summary>
    /// Opaque handle to the underlying platform element (e.g. a UIA AutomationElement).
    /// Not touched by the normalizer; carried through so controls can be activated later.
    /// </summary>
    public object? RuntimeTarget { get; set; }

    public List<RawNode> Children { get; } = new();

    /// <summary>Best available readable text for this node itself.</summary>
    public string OwnText =>
        string.Join(" ", new[] { Name, Value, HelpText }
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0));
}
