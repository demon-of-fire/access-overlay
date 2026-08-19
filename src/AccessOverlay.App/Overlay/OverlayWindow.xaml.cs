using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AccessOverlay.App.Native;
using AccessOverlay.App.Theming;
using AccessOverlay.Core.AppCapture;
using AccessOverlay.Core.Audio;
using AccessOverlay.Core.Model;
using AccessOverlay.Core.Settings;
using AccessOverlay.Core.Speech;

namespace AccessOverlay.App.Overlay;

/// <summary>
/// The always-on-top reader. Shows the normalized, reflowed, genuinely-readable
/// version of whatever the user is working in. Browse-mode style navigation:
/// H jumps between headings, Enter reads the current item, and controls
/// (buttons, links, check boxes, inputs) can be activated straight from here.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly SpeechRouter _speech;
    private ReadableDocument _doc;
    private int _themeIndex;

    private readonly List<int> _searchResults = new();
    private int _searchResultIndex = -1;

    /// <summary>Raised after a control was activated, so the source can be re-captured.</summary>
    public event EventHandler? ControlActivated;

    /// <summary>Raised when the user requests a fresh capture of the source.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the user asks for the control panel (URL bar, settings).</summary>
    public event EventHandler? ControlPanelRequested;

    public OverlayWindow(ReadableDocument doc, SpeechRouter speech)
    {
        InitializeComponent();
        _doc = doc;
        _speech = speech;

        // The reader is an overlay that only ever hides: Escape, the ✕ button
        // and external WM_CLOSE (stray Alt+F4, machine automation) must never
        // destroy it. Matches the control panel's Window_Closing behaviour.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        ThemeManager.Apply(this, App.Settings.Theme);
        _themeIndex = Math.Max(0, Array.IndexOf(ThemeManager.Names, App.Settings.Theme));
        Blocks.FontSize = App.Settings.FontSize;
        Opacity = App.Settings.Opacity;

        UpdateDocument(doc);
        SpeechStatusText.Text = $"Reading via {_speech.CurrentOutput}";
    }

    public void UpdateDocument(ReadableDocument doc)
    {
        var previouslySelected = SelectedBlock?.Text;
        _doc = doc;
        TitleText.Text = string.IsNullOrEmpty(doc.Title) ? "Untitled" : doc.Title;
        Blocks.ItemsSource = doc.Blocks;

        // UIA elements are recreated after most actions, so preserve location by
        // text rather than keeping a stale underlying automation handle.
        var restoredIndex = previouslySelected is null
            ? -1
            : doc.Blocks.ToList().FindIndex(block => block.Text == previouslySelected);
        if (restoredIndex >= 0)
            Blocks.SelectedIndex = restoredIndex;

        if (SearchPanel.Visibility == Visibility.Visible)
            PerformSearch(SearchBox.Text);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => ToggleSearch();
    private void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearch();
    private void SearchNext_Click(object sender, RoutedEventArgs e) => JumpToSearchMatch(+1);
    private void SearchPrev_Click(object sender, RoutedEventArgs e) => JumpToSearchMatch(-1);

    private void ToggleSearch()
    {
        if (SearchPanel.Visibility == Visibility.Visible)
        {
            CloseSearch();
        }
        else
        {
            SearchPanel.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
            PerformSearch(SearchBox.Text);
        }
    }

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        _searchResults.Clear();
        _searchResultIndex = -1;
        MatchCountText.Text = "0 matches";
        Blocks.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PerformSearch(SearchBox.Text);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            JumpToSearchMatch(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSearch();
        }
    }

    private void PerformSearch(string query)
    {
        _searchResults.Clear();
        _searchResultIndex = -1;

        if (string.IsNullOrWhiteSpace(query) || _doc.Blocks.Count == 0)
        {
            MatchCountText.Text = "0 matches";
            return;
        }

        var blocksList = _doc.Blocks.ToList();
        for (int i = 0; i < blocksList.Count; i++)
        {
            if (blocksList[i].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _searchResults.Add(i);
            }
        }

        MatchCountText.Text = $"{_searchResults.Count} match{(_searchResults.Count == 1 ? "" : "es")}";
        if (_searchResults.Count > 0)
        {
            _searchResultIndex = 0;
            JumpToSearchMatch(0);
        }
    }

    private void JumpToSearchMatch(int direction)
    {
        if (_searchResults.Count == 0) return;
        if (direction != 0)
        {
            _searchResultIndex = (_searchResultIndex + direction + _searchResults.Count) % _searchResults.Count;
        }
        int blockIndex = _searchResults[_searchResultIndex];
        MatchCountText.Text = $"{_searchResultIndex + 1} of {_searchResults.Count} matches";
        Blocks.SelectedIndex = blockIndex;
        ScrollToIndex(blockIndex);
        SpeakSelected();
    }

    /// <summary>Automation hook: selects a row and activates it, exactly like Enter/double-click would.</summary>
    public void SelectAndActivate(int index)
    {
        if (index < 0 || index >= Blocks.Items.Count) return;
        Blocks.SelectedIndex = index;
        ScrollToIndex(index);
        ActivateSelected();
    }

    public void ShowAndFocusFirst()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        AccessOverlay.App.Native.NativeMethods.AllowActivate(hwnd);
        Show();
        Activate();
        Blocks.Focus();
        if (Blocks.Items.Count > 0)
        {
            Blocks.SelectedIndex = 0;
            ScrollToIndex(0);
        }
    }

    /// <summary>Shows the reader without stealing keyboard focus from the page/app behind it.</summary>
    public void ShowOverlayNoActivate()
    {
        if (IsVisible) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        AccessOverlay.App.Native.NativeMethods.ShowNoActivate(hwnd);
        Show();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void FontMinus_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-2);
    private void FontPlus_Click(object sender, RoutedEventArgs e) => ChangeFontSize(+2);

    private void ChangeFontSize(int delta)
    {
        App.Settings.FontSize = Math.Clamp(App.Settings.FontSize + delta, 12, 48);
        Blocks.FontSize = App.Settings.FontSize;
        App.SettingsStore.Save(App.Settings);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _themeIndex = (_themeIndex + 1) % ThemeManager.Names.Length;
        App.Settings.Theme = ThemeManager.Names[_themeIndex];
        ThemeManager.Apply(this, App.Settings.Theme);
        App.SettingsStore.Save(App.Settings);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RequestRefresh();

    private void Speak_Click(object sender, RoutedEventArgs e) => SpeakSelected();

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void Activate_Click(object sender, RoutedEventArgs e) => ActivateSelected();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_doc.PlainText);
        }
        catch
        {
            // Clipboard can be briefly locked by other processes; ignore.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// WPF only activates a focused button with Space, never Enter. Screen-reader
    /// users press Enter, so route Enter on a focused button to its Click.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button btn)
        {
            e.Handled = true;
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
    }

    private void Controls_Click(object sender, RoutedEventArgs e) => ControlPanelRequested?.Invoke(this, EventArgs.Empty);

    private void Blocks_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!ActivateSelected()) SpeakSelected();
    }

    private void Blocks_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool shiftPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrlPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrlPressed && e.Key == Key.F)
        {
            e.Handled = true;
            ToggleSearch();
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            JumpToSearchMatch(shiftPressed ? -1 : +1);
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Key.Up)
        {
            e.Handled = true;
            _speech.Rate = Math.Min(10, _speech.Rate + 1);
            SpeechStatusText.Text = $"Speech rate: {_speech.Rate}";
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Key.Down)
        {
            e.Handled = true;
            _speech.Rate = Math.Max(-10, _speech.Rate - 1);
            SpeechStatusText.Text = $"Speech rate: {_speech.Rate}";
            return;
        }

        switch (e.Key)
        {
            case Key.H:
                e.Handled = true;
                JumpToHeading(shiftPressed ? -1 : +1);
                break;
            case Key.B when !ctrlPressed:
                e.Handled = true;
                JumpToControlBlock(shiftPressed ? -1 : +1);
                break;
            case Key.L when !ctrlPressed:
                e.Handled = true;
                JumpToControlBlock(shiftPressed ? -1 : +1);
                break;
            case Key.Enter:
                e.Handled = true;
                if (ActivateSelected()) SpeakSelected();
                break;
            case Key.F5:
                e.Handled = true;
                RequestRefresh();
                break;
            case Key.E when !ctrlPressed:
                e.Handled = true;
                EditSelected();
                break;
            case Key.S:
                e.Handled = true;
                SpeakAll();
                break;
            case Key.Escape:
                e.Handled = true;
                if (SearchPanel.Visibility == Visibility.Visible)
                    CloseSearch();
                else
                    Hide();
                break;
            case Key.OemPlus when ctrlPressed:
                e.Handled = true;
                ChangeFontSize(+2);
                break;
            case Key.OemMinus when ctrlPressed:
                e.Handled = true;
                ChangeFontSize(-2);
                break;
            case Key.C when ctrlPressed:
                e.Handled = true;
                Copy_Click(sender, e);
                break;
        }
    }

    private void JumpToControlBlock(int direction)
    {
        var start = Blocks.SelectedIndex < 0 ? 0 : Blocks.SelectedIndex;
        var count = Blocks.Items.Count;
        for (var i = 0; i < count; i++)
        {
            var idx = (start + direction + i * direction + count) % count;
            if (idx == start && i > 0) break;
            if (Blocks.Items[idx] is ControlBlock)
            {
                Blocks.SelectedIndex = idx;
                ScrollToIndex(idx);
                SpeakSelected();
                return;
            }
        }
    }

    private void RequestRefresh()
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
        SpeechStatusText.Text = "Refreshing reader…";
    }

    private bool EditSelected()
    {
        var block = SelectedBlock;
        if (block?.RuntimeTarget is not AutomationElement root)
        {
            SpeechStatusText.Text = "Select an editable field first.";
            return false;
        }

        var target = FindEditable(root);
        if (target is null)
        {
            SpeechStatusText.Text = "The selected item cannot be edited.";
            return false;
        }

        try
        {
            var valuePattern = (ValuePattern)target.GetCurrentPattern(ValuePattern.Pattern);
            if (valuePattern.Current.IsReadOnly)
            {
                SpeechStatusText.Text = "That field is read-only.";
                return false;
            }

            var isPassword = target.Current.IsPassword;
            var replacement = PromptForValue(block.Text, isPassword ? "" : valuePattern.Current.Value, isPassword);
            if (replacement is null)
                return true;

            valuePattern.SetValue(replacement);
            AfterActivated();
            return true;
        }
        catch (ElementNotAvailableException)
        {
            SpeechStatusText.Text = "That field changed. Refresh the reader and try again.";
            return false;
        }
        catch
        {
            SpeechStatusText.Text = "The source app did not accept the new value.";
            return false;
        }
    }

    private string? PromptForValue(string label, string currentValue, bool isPassword)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = isPassword ? "Set protected value" : "Edit field",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Background,
            FontFamily = FontFamily,
        };
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        Control input;
        TextBox? textBox = null;
        PasswordBox? passwordBox = null;
        if (isPassword)
        {
            passwordBox = new PasswordBox { MinWidth = 420 };
            input = passwordBox;
        }
        else
        {
            textBox = new TextBox { Text = currentValue, MinWidth = 420, MinHeight = 28, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            input = textBox;
        }
        Grid.SetRow(input, 1);
        panel.Children.Add(input);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply", IsDefault = true, MinWidth = 80 };
        apply.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        Grid.SetRow(actions, 2);
        panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => input.Focus();

        return dialog.ShowDialog() == true ? passwordBox?.Password ?? textBox?.Text : null;
    }

    private static AutomationElement? FindEditable(AutomationElement root)
    {
        try
        {
            if (root.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                return root;
            var controls = root.FindAll(TreeScope.Descendants, Automation.ControlViewCondition);
            foreach (AutomationElement candidate in controls)
            {
                if (candidate.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                    return candidate;
            }
        }
        catch { }
        return null;
    }
    private void JumpToHeading(int direction)
    {
        var start = Blocks.SelectedIndex < 0 ? 0 : Blocks.SelectedIndex;
        var count = Blocks.Items.Count;
        for (var i = 0; i < count; i++)
        {
            var idx = (start + direction + i * direction + count) % count;
            if (idx == start && i > 0) break;
            if (Blocks.Items[idx] is HeadingBlock)
            {
                Blocks.SelectedIndex = idx;
                ScrollToIndex(idx);
                return;
            }
        }
    }

    private void ScrollToIndex(int index)
    {
        if (Blocks.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement el)
            el.BringIntoView();
    }

    /// <summary>
    /// Pushes the button / follows the link / toggles the check box that this row
    /// represents, on the real page or app underneath. Returns false when the row
    /// is not interactive (then the caller just speaks it).
    /// </summary>
    private bool ActivateSelected()
    {
        var block = SelectedBlock;
        if (block is null) return false;

        // --- Game OCR Mode: activate by sending a real mouse click to screen coordinates ---
        if (block.RuntimeTarget is OcrElementTarget ocrTarget)
        {
            SpeechStatusText.Text = $"Clicking \u201c{ocrTarget.Text}\u201d in game\u2026";
            Task.Run(async () =>
            {
                await Task.Delay(200); // let overlay repaint before click
                NativeMethods.SendClickAt(ocrTarget.ScreenX, ocrTarget.ScreenY);
            });
            AfterActivated();
            return true;
        }

        // --- Standard UIA Mode ---
        if (block.RuntimeTarget is not AutomationElement el)
            return false;

        var target = FindActivatable(el) ?? el;
        try
        {
            if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                AfterActivated();
                return true;
            }
            if (target.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            {
                ((TogglePattern)toggle).Toggle();
                AfterActivated();
                return true;
            }
            if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                ((SelectionItemPattern)select).Select();
                AfterActivated();
                return true;
            }
            if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            {
                var xp = (ExpandCollapsePattern)expand;
                if (xp.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                    xp.Expand();
                else
                    xp.Collapse();
                AfterActivated();
                return true;
            }
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                return EditSelected();
            return false;
        }
        catch (ElementNotAvailableException)
        {
            SpeechStatusText.Text = "That control changed. Refresh the reader and try again.";
            return false;
        }
        catch
        {
            SpeechStatusText.Text = "Could not activate that control.";
            return false;
        }
    }

    /// <summary>A list row's invokable element is usually its descendant (e.g. &lt;li&gt; wrapping an &lt;a&gt;).</summary>
    private static AutomationElement? FindActivatable(AutomationElement root)
    {
        try
        {
            var controls = root.FindAll(TreeScope.Descendants, Automation.ControlViewCondition);
            foreach (AutomationElement candidate in controls)
            {
                if (candidate is null) continue;
                var isEnabled = true;
                try { isEnabled = candidate.Current.IsEnabled; } catch { continue; }
                if (!isEnabled) continue;
                foreach (var pattern in ActivationPatterns)
                {
                    try
                    {
                        if (candidate.TryGetCurrentPattern(pattern, out _))
                            return candidate;
                    }
                    catch { /* candidate died; try the next one */ }
                }
            }
        }
        catch
        {
            // Tree walked away underneath us; the caller falls back to the row element itself.
        }
        return null;
    }

    private static readonly AutomationPattern[] ActivationPatterns =
    {
        InvokePattern.Pattern, TogglePattern.Pattern, SelectionItemPattern.Pattern, ExpandCollapsePattern.Pattern,
    };

    private void AfterActivated()
    {
        ControlActivated?.Invoke(this, EventArgs.Empty);
        SpeechStatusText.Text = "Sent to the page/app…";
    }

    private ContentBlock? SelectedBlock =>
        Blocks.SelectedIndex >= 0 ? Blocks.Items[Blocks.SelectedIndex] as ContentBlock : null;

    private void SpeakSelected()
    {
        var block = SelectedBlock;
        if (block is null || string.IsNullOrWhiteSpace(block.Text)) return;

        // Play a spatial audio cue based on block type before reading
        Task.Run(() =>
        {
            try
            {
                switch (block)
                {
                    case HeadingBlock:  BeepSynthesizer.PlayHeadingBeep(); break;
                    case ControlBlock:  BeepSynthesizer.PlayButtonBeep();  break;
                    default:           BeepSynthesizer.PlayFieldBeep();   break;
                }
            }
            catch { }
        });

        _speech.Speak(block.Text);
        SpeechStatusText.Text = $"Reading via {_speech.CurrentOutput}";
    }

    private void SpeakAll()
    {
        if (string.IsNullOrWhiteSpace(_doc.PlainText)) return;
        _speech.Speak(_doc.PlainText);
        SpeechStatusText.Text = $"Reading via {_speech.CurrentOutput}";
    }
}
