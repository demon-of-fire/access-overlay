namespace AccessOverlay.Core.Model;

/// <summary>
/// The normalized, genuinely-readable document produced from a raw accessibility tree.
/// Blocks are ordered top-to-bottom, the way a sighted person would read the screen.
/// </summary>
public sealed class ReadableDocument
{
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public IReadOnlyList<ContentBlock> Blocks { get; set; } = Array.Empty<ContentBlock>();

    public string PlainText => string.Join("\n", Blocks.Select(b => b.Text));

    public bool IsEmpty => Blocks.Count == 0;
}

public abstract class ContentBlock
{
    public required string Text { get; init; }
    public int Depth { get; init; }

    /// <summary>Opaque platform handle (UIA AutomationElement) so the row can be activated from the overlay.</summary>
    public object? RuntimeTarget { get; init; }

    /// <summary>WPF list containers announce this; the block text is what users must hear.</summary>
    public override string ToString() => Text;
}

public sealed class HeadingBlock : ContentBlock
{
    public required int Level { get; init; }
}

public sealed class ParagraphBlock : ContentBlock;

public sealed class ListItemBlock : ContentBlock;

/// <summary>A single interactive control rendered as one readable line.</summary>
public sealed class ControlBlock : ContentBlock
{
    public required string Role { get; init; }
    public string? State { get; init; }
}

/// <summary>Explicit visual separation (blank line) for readability.</summary>
public sealed class SeparatorBlock : ContentBlock;
