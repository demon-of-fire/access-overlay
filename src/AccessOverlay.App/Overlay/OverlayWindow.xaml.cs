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

    // Live text-input session state.
    private AutomationElement? _editTarget;
    private ValuePattern? _editValuePattern;
    private string _editOriginal = "";
    private bool _editIsPassword;
    private bool _editSuppressPush;
    private System.Windows.Threading.DispatcherTimer? _editPushTimer;

    // Auto-read the focused item on selection change, the way a screen reader
    // follows the caret. Coalesced, and suppressed while we rebuild the list.
    private System.Windows.Threading.DispatcherTimer? _selectionSpeakTimer;
    private bool _suppressSelectionSpeak;

    /// <summary>True while the user is typing into a source edit field.</summary>
    public bool IsEditing => EditBar.Visibility == Visibility.Visible;

    /// <summary>Raised after a control was activated, so the source can be re-captured.</summary>
    public event EventHandler? ControlActivated;

    /// <summary>Raised when the user requests a fresh capture of the source.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the user asks for the control panel (URL bar, settings).</summary>
    public event EventHandler? ControlPanelRequested;

    /// <summary>Raised when the user deliberately dismisses the reader (Esc / close button).</summary>
    public event EventHandler? HiddenByUser;

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
        var previousIndex = Blocks.SelectedIndex;
        _doc = doc;
        TitleText.Text = string.IsNullOrEmpty(doc.Title) ? "Untitled" : doc.Title;

        _suppressSelectionSpeak = true;
        Blocks.ItemsSource = doc.Blocks;

        // UIA elements are recreated after most actions, so preserve location by
        // text rather than keeping a stale underlying automation handle.
        var restoredIndex = previouslySelected is null
            ? -1
            : doc.Blocks.ToList().FindIndex(block => block.Text == previouslySelected);
        if (restoredIndex < 0 && previousIndex >= 0 && previousIndex < doc.Blocks.Count)
        {
            // The selected text changed (e.g. after a value edit); keep the cursor
            // in place rather than dropping it to nothing.
            restoredIndex = previousIndex;
        }
        if (restoredIndex >= 0)
            Blocks.SelectedIndex = restoredIndex;
        _suppressSelectionSpeak = false;

        if (SearchPanel.Visibility == Visibility.Visible)
            PerformSearch(SearchBox.Text);
    }

    private void Blocks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSpeak || !IsVisible) return;

        // Coalesce fast arrow-key navigation into a single read of the final item.
        _selectionSpeakTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _selectionSpeakTimer.Stop();
        _selectionSpeakTimer.Tick -= SelectionSpeakTimer_Tick;
        _selectionSpeakTimer.Tick += SelectionSpeakTimer_Tick;
        _selectionSpeakTimer.Start();
    }

    private void SelectionSpeakTimer_Tick(object? sender, EventArgs e)
    {
        _selectionSpeakTimer?.Stop();
        if (!IsVisible || IsEditing) return;
        // No spatial cue while merely navigating; the cue is reserved for deliberate reads.
        SpeakSelected(audibleCue: false);
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
            // The caller already announces the document on open; don't also read
            // the first row on top of that.
            _suppressSelectionSpeak = true;
            Blocks.SelectedIndex = 0;
            _suppressSelectionSpeak = false;
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HiddenByUser?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    /// <summary>
    /// WPF only activates a focused button with Space, never Enter. Screen-reader
    /// users press Enter, so route Enter on a focused button to its Click.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool shiftPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrlPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
        bool altPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);

        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button btn)
        {
            e.Handled = true;
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return;
        }

        if (e.Key == Key.Back && !ctrlPressed && !altPressed && !shiftPressed && BlocksHasKeyboardFocus() && CanEditSelected() && !IsEditing)
        {
            e.Handled = true;
            // Start live typing - user wants to backspace into a field.
            EditSelected("");
            return;
        }

        if (e.Key == Key.Tab)
        {
            // Let focus move normally.
            return;
        }

        // Character keys: if a writable field is selected and not already editing,
        // open the inline editor seeded with the pressed character.
        if (!e.IsRepeat && BlocksHasKeyboardFocus() && CanEditSelected() && !IsEditing && !ctrlPressed && !altPressed)
        {
            var key = e.Key;
            if (key >= Key.A && key <= Key.Z)
            {
                e.Handled = true;
                EditSelected(key.ToString().ToLowerInvariant());
                return;
            }
            if (key >= Key.D0 && key <= Key.D9)
            {
                e.Handled = true;
                EditSelected(((char)('0' + (key - Key.D0))).ToString());
                return;
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                e.Handled = true;
                EditSelected(((char)('0' + (key - Key.NumPad0))).ToString());
                return;
            }
            // Space
            if (key == Key.Space)
            {
                e.Handled = true;
                EditSelected(" ");
                return;
            }
            // Common punctuation
            if (key == Key.OemPeriod)
            {
                e.Handled = true;
                EditSelected(".");
                return;
            }
            if (key == Key.OemComma)
            {
                e.Handled = true;
                EditSelected(",");
                return;
            }
            if (key == Key.OemSemicolon)
            {
                e.Handled = true;
                EditSelected(";");
                return;
            }
            if (key == Key.OemQuotes)
            {
                e.Handled = true;
                EditSelected("\"");
                return;
            }
            if (key == Key.OemQuestion)
            {
                e.Handled = true;
                EditSelected("?");
                return;
            }
            if (key == Key.OemMinus)
            {
                e.Handled = true;
                EditSelected("-");
                return;
            }
            if (key == Key.OemPlus)
            {
                e.Handled = true;
                EditSelected("=");
                return;
            }
            if (key == Key.Divide)
            {
                e.Handled = true;
                EditSelected("/");
                return;
            }
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
        bool altPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);

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
            case Key.H when !ctrlPressed && !altPressed:
                e.Handled = true;
                JumpToHeading(shiftPressed ? -1 : +1);
                break;
            case Key.B when !ctrlPressed && !altPressed:
                e.Handled = true;
                JumpToControlBlock(shiftPressed ? -1 : +1);
                break;
            case Key.L when !ctrlPressed && !altPressed:
                e.Handled = true;
                JumpToControlBlock(shiftPressed ? -1 : +1);
                break;
            case Key.Enter:
                e.Handled = true;
                if (ActivateSelected()) SpeakSelected();
                break;
            case Key.Space when !ctrlPressed && !altPressed:
                // Space presses the focused control, the way it does everywhere else.
                if (CanEditSelected())
                {
                    // Editable field: let the window-level handler open the editor.
                    break;
                }
                if (ActivateSelected())
                {
                    e.Handled = true;
                    SpeakSelected();
                }
                break;
            case Key.R when ctrlPressed:
                e.Handled = true;
                RequestRefresh();
                break;
            case Key.F5:
                e.Handled = true;
                RequestRefresh();
                break;
            case Key.E when !ctrlPressed && !altPressed:
                e.Handled = true;
                if (IsEditing)
                    CommitEdit();
                else
                    EditSelected();
                break;
            case Key.S when !ctrlPressed && !altPressed:
                e.Handled = true;
                SpeakAll();
                break;
            case Key.Escape:
                e.Handled = true;
                if (SearchPanel.Visibility == Visibility.Visible)
                    CloseSearch();
                else if (IsEditing)
                    CancelEdit();
                else
                {
                    HiddenByUser?.Invoke(this, EventArgs.Empty);
                    Hide();
                }
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
            case Key.Left when !ctrlPressed && !altPressed && !shiftPressed:
                if (AdjustSelectedValue(-1)) e.Handled = true;
                break;
            case Key.Right when !ctrlPressed && !altPressed && !shiftPressed:
                if (AdjustSelectedValue(+1)) e.Handled = true;
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

    /// <summary>
    /// Opens the inline input bar for the selected field. Typing then flows straight
    /// through to the real control in the app or page underneath, keystroke by keystroke.
    /// </summary>
    private bool EditSelected(string? seedText = null)
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
            var valuePattern = target.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
            if (valuePattern is null)
            {
                SpeechStatusText.Text = "The selected item cannot be edited.";
                return false;
            }
            if (valuePattern.Current.IsReadOnly)
            {
                SpeechStatusText.Text = "That field is read-only.";
                return false;
            }

            var isPassword = target.Current.IsPassword;
            BeginEdit(target, valuePattern, block.Text, isPassword ? "" : valuePattern.Current.Value, isPassword, seedText);
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

    /// <summary>True when the currently selected row is a writable edit field.</summary>
    private bool CanEditSelected()
    {
        if (SelectedBlock?.RuntimeTarget is not AutomationElement root) return false;
        var target = FindEditable(root);
        if (target is null) return false;
        try
        {
            return target.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern vp && !vp.Current.IsReadOnly;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when keyboard focus is on the block list itself or one of its rows.
    /// Guards the auto-typing shortcut so it cannot steal keys meant for the
    /// search box or the inline edit bar.
    /// </summary>
    private bool BlocksHasKeyboardFocus()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return false;
        if (ReferenceEquals(focused, Blocks)) return true;
        if (focused is ListBoxItem item && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), Blocks)) return true;
        return false;
    }

    private void BeginEdit(AutomationElement target, ValuePattern valuePattern, string label, string currentValue, bool isPassword, string? seedText)
    {
        _editTarget = target;
        _editValuePattern = valuePattern;
        _editOriginal = currentValue;
        _editIsPassword = isPassword;

        var seed = seedText ?? "";
        // Match how typing behaves in a focused field: a typed character replaces
        // the existing value, while Backspace drops the last character. (Press E to
        // edit the current value in place instead.)
        var initial = seedText is null
            ? currentValue
            : seed.Length > 0
                ? seed
                : currentValue.Length > 0 ? currentValue[..^1] : "";

        _editSuppressPush = true;
        EditLabelText.Text = label;
        if (isPassword)
        {
            EditPasswordBox.Password = initial;
            EditPasswordBox.Visibility = Visibility.Visible;
            EditBox.Visibility = Visibility.Collapsed;
            EditPasswordBox.Focus();
        }
        else
        {
            EditBox.Text = initial;
            EditBox.Visibility = Visibility.Visible;
            EditPasswordBox.Visibility = Visibility.Collapsed;
            EditBox.Focus();
            EditBox.CaretIndex = EditBox.Text.Length;
        }
        _editSuppressPush = false;

        EditBar.Visibility = Visibility.Visible;
        SpeechStatusText.Text = "Typing into the field\u2026 Enter to finish, Esc to cancel.";

        if (initial != currentValue)
            QueueEditPush();
    }

    private void EditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editSuppressPush) return;
        QueueEditPush();
    }

    private void EditPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_editSuppressPush) return;
        QueueEditPush();
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelEdit();
        }
    }

    private void EditApply_Click(object sender, RoutedEventArgs e) => CommitEdit();

    private void EditCancel_Click(object sender, RoutedEventArgs e) => CancelEdit();

    /// <summary>Batches rapid keystrokes into one push, so the source keeps up while typing.</summary>
    private void QueueEditPush()
    {
        _editPushTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _editPushTimer.Stop();
        _editPushTimer.Tick -= EditPushTimer_Tick;
        _editPushTimer.Tick += EditPushTimer_Tick;
        _editPushTimer.Start();
    }

    private void EditPushTimer_Tick(object? sender, EventArgs e)
    {
        _editPushTimer?.Stop();
        PushEditValue();
    }

    private void PushEditValue()
    {
        if (_editValuePattern is null || _editTarget is null) return;
        var text = _editIsPassword ? EditPasswordBox.Password : EditBox.Text;
        try
        {
            _editValuePattern.SetValue(text);
        }
        catch (ElementNotAvailableException)
        {
            SpeechStatusText.Text = "That field changed while typing. Refresh and try again.";
        }
        catch
        {
            SpeechStatusText.Text = "The source app did not accept the new value.";
        }
    }

    private void CommitEdit()
    {
        _editPushTimer?.Stop();
        PushEditValue();
        var changed = (_editIsPassword ? EditPasswordBox.Password : EditBox.Text) != _editOriginal;
        CloseEditBar();
        SpeechStatusText.Text = changed ? "Field updated." : "No change.";
        if (changed)
            AfterActivated();
    }

    private void CancelEdit()
    {
        _editPushTimer?.Stop();
        try
        {
            _editValuePattern?.SetValue(_editOriginal);
        }
        catch { /* the field may have gone away; nothing to restore */ }
        CloseEditBar();
        SpeechStatusText.Text = "Edit cancelled.";
        Blocks.Focus();
    }

    private void CloseEditBar()
    {
        _editPushTimer?.Stop();
        EditBar.Visibility = Visibility.Collapsed;
        _editSuppressPush = true;
        EditBox.Text = "";
        EditPasswordBox.Password = "";
        _editSuppressPush = false;
        _editTarget = null;
        _editValuePattern = null;
        _editOriginal = "";
        _editIsPassword = false;
        Blocks.Focus();
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
        // ScrollIntoView works with the virtualizing panel (BringIntoView on a
        // recycled container can target the wrong item).
        if (index >= 0 && index < Blocks.Items.Count)
            Blocks.ScrollIntoView(Blocks.Items[index]);
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
            ClickThroughAt(ocrTarget.ScreenX, ocrTarget.ScreenY);
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

            // Last resort: custom-drawn controls (many launchers, embedded
            // browsers) expose no action pattern at all but do report where they
            // are on screen. Click them directly.
            if (TryClickElement(target))
            {
                AfterActivated();
                return true;
            }
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

    /// <summary>
    /// Left / Right arrows on a combo box, spinner or slider step the control's own
    /// value (the way the control behaves when it has focus in the app), instead of
    /// just moving the row selection. Returns false when the row has no steppable
    /// value, so the default list navigation still runs.
    /// </summary>
    private bool AdjustSelectedValue(int delta)
    {
        if (IsEditing) return false;
        var block = SelectedBlock;
        if (block?.RuntimeTarget is not AutomationElement root) return false;

        var target = FindValueControl(root);
        if (target is null) return false;

        try
        {
            // Sliders and spin buttons expose a numeric range directly.
            if (target.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObj))
            {
                var rv = (RangeValuePattern)rangeObj;
                var info = rv.Current;
                if (info.Minimum >= info.Maximum) return false;
                double step = info.SmallChange > 0 ? info.SmallChange : Math.Max(Math.Abs(info.Maximum - info.Minimum) / 50.0, 1.0);
                double next = Math.Clamp(info.Value + delta * step, info.Minimum, info.Maximum);
                if (Math.Abs(next - info.Value) < double.Epsilon) return false;
                rv.SetValue(next);
                AnnounceValueChanged(rv.Current.Value.ToString("0.##"));
                return true;
            }

            // Combo boxes: step through their option items.
            var items = GetSelectionItems(target);
            if (items.Count > 0)
            {
                int index = items.FindIndex(it => it.IsSelected);
                if (index < 0) return false;
                int nextIndex = Math.Clamp(index + delta, 0, items.Count - 1);
                if (nextIndex == index) return false;
                SelectItem(items[nextIndex]);
                AnnounceValueChanged(items[nextIndex].Name);
                return true;
            }

            // Collapsed combo whose options are only in the tree once expanded.
            if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandObj))
            {
                var xp = (ExpandCollapsePattern)expandObj;
                if (xp.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    xp.Expand();
                    items = GetSelectionItems(target);
                    if (items.Count > 0)
                    {
                        int index = items.FindIndex(it => it.IsSelected);
                        int nextIndex = Math.Clamp((index < 0 ? 0 : index) + delta, 0, items.Count - 1);
                        if (nextIndex != index)
                            SelectItem(items[nextIndex]);
                        xp.Collapse();
                        if (nextIndex != index)
                            AnnounceValueChanged(items[nextIndex].Name);
                        return nextIndex != index;
                    }
                    xp.Collapse();
                }
            }

            // Text-backed numeric value (e.g. an up/down control showing a number).
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj))
            {
                var vp = (ValuePattern)valueObj;
                if (!vp.Current.IsReadOnly
                    && double.TryParse(vp.Current.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    vp.SetValue((number + delta).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    AnnounceValueChanged((number + delta).ToString("0.##"));
                    return true;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            SpeechStatusText.Text = "That control changed. Refresh the reader and try again.";
        }
        catch
        {
            SpeechStatusText.Text = "That control did not accept the change.";
        }
        return false;
    }

    private void AnnounceValueChanged(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "value changed" : value;
        SpeechStatusText.Text = $"Value: {text}";
        _speech.Speak(text);
        RequestRefresh();
    }

    private static void SelectItem(SelectableItem item)
    {
        if (item.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
            ((SelectionItemPattern)pattern).Select();
    }

    private readonly record struct SelectableItem(AutomationElement Element, string Name, bool IsSelected);

    private static List<SelectableItem> GetSelectionItems(AutomationElement combo)
    {
        var items = new List<SelectableItem>();
        try
        {
            var found = combo.FindAll(TreeScope.Descendants, Automation.ControlViewCondition);
            foreach (AutomationElement candidate in found)
            {
                try
                {
                    if (!candidate.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) continue;
                    var sip = (SelectionItemPattern)pattern;
                    var name = candidate.Current.Name ?? "";
                    if (name.Length == 0) name = candidate.Current.ClassName;
                    items.Add(new SelectableItem(candidate, name, sip.Current.IsSelected));
                }
                catch { /* option died; skip it */ }
            }
        }
        catch { /* tree walked away */ }
        return items;
    }

    /// <summary>Finds the element that owns the value: the row itself or a descendant control.</summary>
    private static AutomationElement? FindValueControl(AutomationElement root)
    {
        try
        {
            if (SupportsStepping(root)) return root;
            var controls = root.FindAll(TreeScope.Descendants, Automation.ControlViewCondition);
            foreach (AutomationElement candidate in controls)
            {
                try
                {
                    if (candidate.Current.IsEnabled && SupportsStepping(candidate)) return candidate;
                }
                catch { /* candidate died; try the next one */ }
            }
        }
        catch { /* tree walked away; no value control */ }
        return null;
    }

    private static bool SupportsStepping(AutomationElement el)
    {
        foreach (var pattern in SteppingPatterns)
        {
            try
            {
                if (el.TryGetCurrentPattern(pattern, out _)) return true;
            }
            catch { /* candidate died */ }
        }
        return false;
    }

    private static readonly AutomationPattern[] SteppingPatterns =
    {
        RangeValuePattern.Pattern, ExpandCollapsePattern.Pattern, ValuePattern.Pattern,
    };

    private void AfterActivated()
    {
        ControlActivated?.Invoke(this, EventArgs.Empty);
        SpeechStatusText.Text = "Sent to the page/app…";
    }

    /// <summary>Clicks the centre of a UI element that exposes no action pattern.</summary>
    private bool TryClickElement(AutomationElement target)
    {
        try
        {
            var rect = target.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1) return false;
            int x = (int)Math.Round(rect.Left + rect.Width / 2);
            int y = (int)Math.Round(rect.Top + rect.Height / 2);
            if (x < 0 || y < 0) return false;
            ClickThroughAt(x, y);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a real mouse click at screen coordinates. If the reader itself is
    /// covering that point it briefly gets out of the way so the click reaches
    /// the app underneath, then comes back.
    /// </summary>
    private void ClickThroughAt(int x, int y)
    {
        var covered = false;
        try
        {
            if (IsVisible)
            {
                var topLeft = PointToScreen(new Point(0, 0));
                var bounds = new Rect(topLeft, new Vector(ActualWidth, ActualHeight));
                covered = bounds.Contains(new Point(x, y));
            }
        }
        catch { }

        if (covered)
        {
            var wasActive = IsActive;
            Hide();
            Task.Run(async () =>
            {
                await Task.Delay(200);
                NativeMethods.SendClickAt(x, y);
                await Task.Delay(220);
                Dispatcher.Invoke(() =>
                {
                    ShowOverlayNoActivate();
                    if (wasActive) Activate();
                });
            });
        }
        else
        {
            Task.Run(async () =>
            {
                await Task.Delay(150); // let overlay repaint before the click
                NativeMethods.SendClickAt(x, y);
            });
        }
    }

    private ContentBlock? SelectedBlock =>
        Blocks.SelectedIndex >= 0 ? Blocks.Items[Blocks.SelectedIndex] as ContentBlock : null;

    private void SpeakSelected(bool audibleCue = true)
    {
        var block = SelectedBlock;
        if (block is null || string.IsNullOrWhiteSpace(block.Text)) return;

        if (audibleCue)
        {
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
        }

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







