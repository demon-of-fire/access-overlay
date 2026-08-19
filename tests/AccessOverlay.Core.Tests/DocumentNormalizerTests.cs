using AccessOverlay.Core.Model;
using AccessOverlay.Core.Normalization;
using Xunit;

namespace AccessOverlay.Core.Tests;

public class DocumentNormalizerTests
{
    private static RawNode Text(string name) => new() { ControlType = "Text", Name = name };

    private static RawNode Pane(params RawNode[] children)
    {
        var pane = new RawNode { ControlType = "Pane" };
        pane.Children.AddRange(children);
        return pane;
    }

    [Fact]
    public void SimpleText_BecomesParagraph()
    {
        var root = new RawNode { Name = "App window" };
        root.Children.Add(Text("Hello world"));

        var doc = DocumentNormalizer.Normalize(root);

        var block = Assert.Single(doc.Blocks);
        Assert.IsType<ParagraphBlock>(block);
        Assert.Equal("Hello world", block.Text);
        Assert.Equal("App window", doc.Title);
    }

    [Fact]
    public void Heading_IsDetectedWithLevel()
    {
        var root = new RawNode { ControlType = "Document" };
        root.Children.Add(new RawNode { ControlType = "Heading", HeadingLevel = 2, Name = "Settings" });
        root.Children.Add(Text("Some body text"));

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal(2, doc.Blocks.Count);
        var heading = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.Equal(2, heading.Level);
        Assert.Equal("Settings", heading.Text);
        Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
    }

    [Fact]
    public void DuplicateParentChildText_IsSuppressed()
    {
        var root = new RawNode { Name = "Main" };
        var button = new RawNode { ControlType = "Button", Name = "Save" };
        root.Children.Add(button);
        // The tree below is the classic broken pattern: parent says "Save", child says "Save" again.
        button.Children.Add(Text("Save"));

        var doc = DocumentNormalizer.Normalize(root);

        var block = Assert.Single(doc.Blocks);
        var ctrl = Assert.IsType<ControlBlock>(block);
        Assert.Equal("Save button", ctrl.Text);
    }

    [Fact]
    public void Button_GetsRoleSuffix()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "Button", Name = "Submit" });

        var doc = DocumentNormalizer.Normalize(root);

        var ctrl = Assert.IsType<ControlBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("button", ctrl.Role);
        Assert.Equal("Submit button", ctrl.Text);
    }

    [Fact]
    public void CheckedCheckBox_AnnouncesState()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "CheckBox", Name = "Enable dark mode", ToggleState = "On" });

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal("Enable dark mode check box, checked", Assert.Single(doc.Blocks).Text);
    }

    [Fact]
    public void UncheckedCheckBox_AnnouncesNotChecked()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "CheckBox", Name = "Send email", ToggleState = "Off" });

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal("Send email check box, not checked", Assert.Single(doc.Blocks).Text);
    }

    [Fact]
    public void DisabledControl_AnnouncesUnavailable()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "Button", Name = "Delete", IsEnabled = false });

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal("Delete button, unavailable", Assert.Single(doc.Blocks).Text);
    }

    [Fact]
    public void EditWithValue_CombinesNameAndValue()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "Edit", Name = "Search", Value = "accessibility" });

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal("Search: accessibility edit field", Assert.Single(doc.Blocks).Text);
    }

[Fact]
    public void PasswordValue_IsNeverIncludedInReadableText()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode
        {
            ControlType = "Edit",
            Name = "Account password",
            Value = "do-not-expose-this",
            IsPassword = true,
        });

        var doc = DocumentNormalizer.Normalize(root);

        var text = Assert.Single(doc.Blocks).Text;
        Assert.Equal("Account password (protected) edit field", text);
        Assert.DoesNotContain("do-not-expose-this", text);
    }
    [Fact]
    public void UnnamedControl_FallsBackToAutomationId()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "Button", AutomationId = "btnSave42" });

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal("btnSave42 button", Assert.Single(doc.Blocks).Text);
    }

    [Fact]
    public void OffscreenNodes_AreSkipped()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "Text", Name = "Visible text", IsOffscreen = false });
        root.Children.Add(new RawNode { ControlType = "Text", Name = "Hidden in a menu", IsOffscreen = true });

        var doc = DocumentNormalizer.Normalize(root);

        var block = Assert.Single(doc.Blocks);
        Assert.Equal("Visible text", block.Text);
    }

    [Fact]
    public void Whitespace_IsCollapsed()
    {
        var root = new RawNode();
        root.Children.Add(Text("  This   has\t messy  whitespace   "));

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal("This has messy whitespace", Assert.Single(doc.Blocks).Text);
    }

    [Fact]
    public void ConsecutiveDuplicateBlocks_AreSuppressed()
    {
        var root = new RawNode();
        root.Children.Add(Text("Loading..."));
        root.Children.Add(Text("Loading..."));
        root.Children.Add(Text("Ready"));

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("Loading...", doc.Blocks[0].Text);
        Assert.Equal("Ready", doc.Blocks[1].Text);
    }

    [Fact]
    public void ListItems_BecomeListItemBlocks()
    {
        var root = new RawNode();
        var list = new RawNode { ControlType = "List" };
        list.Children.Add(new RawNode { ControlType = "ListItem", Name = "First item" });
        list.Children.Add(new RawNode { ControlType = "ListItem", Name = "Second item" });
        root.Children.Add(list);

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal(2, doc.Blocks.Count);
        Assert.IsType<ListItemBlock>(doc.Blocks[0]);
        Assert.Equal("First item", doc.Blocks[0].Text);
        Assert.Equal("Second item", doc.Blocks[1].Text);
    }

    [Fact]
    public void EmptyContainers_ProduceNoBlocks()
    {
        var root = new RawNode();
        root.Children.Add(Pane(Pane()));
        root.Children.Add(new RawNode { ControlType = "Group", Name = "Untitled group" });

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Empty(doc.Blocks);
    }

    [Fact]
    public void NestedContent_IsIndented()
    {
        var root = new RawNode();
        var group = new RawNode { ControlType = "Group", Name = "Account" };
        group.Children.Add(Text("Profile details"));
        root.Children.Add(group);

        var doc = DocumentNormalizer.Normalize(root);

        var block = Assert.Single(doc.Blocks);
        Assert.Equal(1, block.Depth);
    }

    [Fact]
    public void DeepNesting_IsCapped()
    {
        var deep = Text("Bottom");
        for (var i = 0; i < 30; i++)
            deep = Pane(deep);

        var doc = DocumentNormalizer.Normalize(deep);

        var block = Assert.Single(doc.Blocks);
        Assert.True(block.Depth <= 6, $"Depth {block.Depth} exceeded cap");
    }

    [Fact]
    public void LongControlValue_IsTruncated()
    {
        var root = new RawNode();
        root.Children.Add(new RawNode { ControlType = "Edit", Name = "Notes", Value = new string('x', 500) });

        var doc = DocumentNormalizer.Normalize(root);

        var block = Assert.Single(doc.Blocks);
        Assert.Contains("…", block.Text);
        Assert.True(block.Text.Length < 280);
    }

    [Fact]
    public void EmbeddedLineBreaks_ProduceSeparateParagraphs()
    {
        var root = new RawNode();
        root.Children.Add(Text("First paragraph here.\r\n\r\nSecond paragraph here.\r\n\r\nThird one."));

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal(3, doc.Blocks.Count);
        Assert.Equal("First paragraph here.", doc.Blocks[0].Text);
        Assert.Equal("Second paragraph here.", doc.Blocks[1].Text);
        Assert.Equal("Third one.", doc.Blocks[2].Text);
    }

    [Fact]
    public void LongSingleLineText_IsSplitAtSentenceBoundaries()
    {
        var filler = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu" };
        var text = string.Join(" ", filler.Select(f => $"Sentence number {f} is a moderately long sentence used to pad out this text block. "));
        var root = new RawNode();
        root.Children.Add(Text(text));

        var doc = DocumentNormalizer.Normalize(root);

        Assert.True(doc.Blocks.Count > 4, $"expected splitting, got {doc.Blocks.Count} blocks");
        Assert.All(doc.Blocks, b => Assert.True(b.Text.Length <= 420, $"block too long: {b.Text.Length}"));
    }

    [Fact]
    public void WindowTree_ProducesReadingOrder()
    {
        var root = new RawNode { Name = "Mail - Inbox", ControlType = "Window" };
        root.Children.Add(new RawNode { ControlType = "Heading", HeadingLevel = 1, Name = "Inbox" });
        var list = new RawNode { ControlType = "List" };
        list.Children.Add(new RawNode { ControlType = "ListItem", Name = "Alice: meeting notes" });
        list.Children.Add(new RawNode { ControlType = "ListItem", Name = "Bob: lunch plans" });
        root.Children.Add(list);
        root.Children.Add(new RawNode { ControlType = "Button", Name = "Compose" });
        root.Children.Add(new RawNode { ControlType = "Edit", Name = "Search mail" });

        var doc = DocumentNormalizer.Normalize(root);

        Assert.Equal("Mail - Inbox", doc.Title);
        Assert.Equal(5, doc.Blocks.Count);
        Assert.Equal("Inbox", doc.Blocks[0].Text);
        Assert.IsType<ListItemBlock>(doc.Blocks[1]);
        Assert.IsType<ListItemBlock>(doc.Blocks[2]);
        Assert.Equal("Compose button", doc.Blocks[3].Text);
        Assert.Equal("Search mail edit field", doc.Blocks[4].Text);
    }
}
