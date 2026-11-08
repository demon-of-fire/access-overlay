using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AccessOverlay.App.Native;
using AccessOverlay.App.Theming;
using AccessOverlay.Core.AppCapture;
using AccessOverlay.Core.Diagnostics;
using AccessOverlay.Core.Model;

namespace AccessOverlay.App.Overlay;

/// <summary>
/// The always-on-top reader. Shows the normalized, reflowed, genuinely-readable
/// version of whatever the user is working in. Browse-mode style navigation:
/// H jumps between headings, B/L between controls, and controls (buttons, links,
/// check boxes, inputs) can be activated straight from here. The app has no speech
/// of its own: the user's screen reader reads this window through focus changes
/// and the live status line.
/// </summary>
public partial class OverlayWindow : Window
{
    private const string RefreshingStatus = "Refreshing reader…";
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(5);

    private ReadableDocument _doc;
    private int _themeIndex;

    private readonly List<int> _searchResults = new();
    private int _searchResultIndex = -1;

    // Live text-input session state.
    private AutomationElement? _editTarget;
    private ValuePattern? _editValuePattern;
    private string _editOriginal = "";
    private bool _editIsPassword;
    private bool _editPushed;
    private int _editProcessId;
    private bool _editSuppressPush;
    private DispatcherTimer? _editPushTimer;
    private Task _editPushTask = Task.CompletedTask;
    private bool _uiaActionBusy;
    private Task _editCommitTask = Task.CompletedTask;

    // The last edit, until the user moves on: Esc on that row puts the old text back.
    private string? _undoOriginal;
    private int _undoIndex = -1;

    // Apps whose fields grab the foreground whenever a value is set. Pushing every
    // keystroke there would send the next keys to the app instead of the editor.
    private readonly HashSet<int> _focusStealingProcesses = new();

    /// <summary>True while the user is typing into a source edit field.</summary>
    public bool IsEditing => EditBar.Visibility == Visibility.Visible;

    /// <summary>True once the user's own window placement has been remembered.</summary>
    public bool HasSavedPosition => App.Settings.ReaderLeft is not null;

    /// <summary>Raised after a control was activated, so the source can be re-captured.</summary>
    public event EventHandler? ControlActivated;

    /// <summary>Raised when the user requests a fresh capture of the source.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the user asks for the control panel (URL bar, settings).</summary>
    public event EventHandler? ControlPanelRequested;

    /// <summary>Raised when the user deliberately dismisses the reader (Esc / close button).</summary>
    public event EventHandler? HiddenByUser;

    public OverlayWindow(ReadableDocument doc)
    {
        InitializeComponent();
        _doc = doc;

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
        RestoreReaderBounds();

        UpdateDocument(doc);
    }

    private void RestoreReaderBounds()
    {
        var settings = App.Settings;
        Width = settings.ReaderWidth ?? 760;
        Height = settings.ReaderHeight ?? 640;
        if (settings.ReaderLeft is double left && settings.ReaderTop is double top)
        {
            // Ignore a position left over from a monitor that is no longer attached.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.Contains(new Point(left + 40, top + 20)))
            {
                Left = left;
                Top = top;
            }
            else
            {
                settings.ReaderLeft = settings.ReaderTop = null;
            }
        }

        LocationChanged += (_, _) => RememberBounds();
        SizeChanged += (_, _) => RememberBounds();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not false) return;
            _undoOriginal = null;
            App.SettingsStore.Save(App.Settings);
        };
    }

    private void RememberBounds()
    {
        if (!IsVisible || double.IsNaN(Left) || double.IsNaN(Top)) return;
        var settings = App.Settings;
        settings.ReaderLeft = Left;
        settings.ReaderTop = Top;
        settings.ReaderWidth = ActualWidth;
        settings.ReaderHeight = ActualHeight;
    }

    /// <summary>Shows a message and has the screen reader announce it.</summary>
    private void SetStatus(string text)
    {
        StatusLine.Text = text;
        var peer = UIElementAutomationPeer.FromElement(StatusLine) ?? UIElementAutomationPeer.CreatePeerForElement(StatusLine);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>
    /// Runs a call into another app off the UI thread, so a hung app can't freeze
    /// the reader. Returns ok=false, having reported the problem, if it fails.
    /// </summary>
    private async Task<(bool Ok, T Value)> RunUiaAsync<T>(Func<T> work, string failureStatus)
    {
        try
        {
            return (true, await Task.Run(work).WaitAsync(UiaTimeout));
        }
        catch (TimeoutException)
        {
            SetStatus("That app isn't responding.");
        }
        catch (ElementNotAvailableException)
        {
            SetStatus("That control changed. Refresh the reader and try again.");
        }
        catch
        {
            SetStatus(failureStatus);
        }
        return (false, default!);
    }

    public void UpdateDocument(ReadableDocument doc)
    {
        TitleText.Text = string.IsNullOrEmpty(doc.Title) ? "Untitled" : doc.Title;
        if (StatusLine.Text == RefreshingStatus)
            StatusLine.Text = "";

        // Rebuilding identical rows would make the screen reader re-announce the
        // focused one on every background refresh. Rows and blocks stay index-aligned,
        // so only the (possibly recreated) automation handles need replacing.
        if (Blocks.ItemsSource is not null && HasSameContent(_doc, doc))
        {
            _doc = doc;
            return;
        }

        var previouslySelected = SelectedBlock?.Text;
        var previousIndex = Blocks.SelectedIndex;
        // Replacing ItemsSource destroys the focused row, which would otherwise
        // leave the keyboard pointing at nothing until the next click.
        var hadKeyboardFocus = BlocksHasKeyboardFocus();
        _doc = doc;

        Blocks.ItemsSource = doc.Blocks;

        // UIA elements are recreated after most actions, so preserve location by
        // text rather than keeping a stale underlying automation handle. Identical
        // rows are common, so take the match closest to where the cursor was.
        var restoredIndex = FindNearestBlock(doc.Blocks, previouslySelected, previousIndex);
        if (restoredIndex < 0 && previousIndex >= 0 && doc.Blocks.Count > 0)
        {
            // The selected text changed (e.g. after a value edit); keep the cursor
            // in place rather than dropping it to nothing.
            restoredIndex = Math.Min(previousIndex, doc.Blocks.Count - 1);
        }
        if (restoredIndex >= 0)
            Blocks.SelectedIndex = restoredIndex;

        if (hadKeyboardFocus && !IsEditing)
            FocusSelectedItem();

        // A background refresh must not move the cursor.
        if (SearchPanel.Visibility == Visibility.Visible)
            PerformSearch(SearchBox.Text, jumpToFirst: false);
    }

    private static bool HasSameContent(ReadableDocument a, ReadableDocument b)
    {
        if (a.Blocks.Count != b.Blocks.Count) return false;
        for (var i = 0; i < a.Blocks.Count; i++)
        {
            if (a.Blocks[i].Text != b.Blocks[i].Text || a.Blocks[i].GetType() != b.Blocks[i].GetType())
                return false;
        }
        return true;
    }

    private static int FindNearestBlock(IReadOnlyList<ContentBlock> blocks, string? text, int around)
    {
        if (text is null) return -1;
        var best = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Text != text) continue;
            if (best < 0 || Math.Abs(i - around) < Math.Abs(best - around)) best = i;
        }
        return best;
    }

    /// <summary>
    /// Moves keyboard focus onto the selected row. Focusing only the list leaves
    /// arrow keys starting from whichever row last had focus.
    /// </summary>
    private void FocusSelectedItem()
    {
        var index = Blocks.SelectedIndex;
        if (index >= 0 && index < Blocks.Items.Count)
        {
            Blocks.ScrollIntoView(Blocks.Items[index]);
            Blocks.UpdateLayout();
            if (Blocks.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item && item.Focus())
                return;
        }
        Blocks.Focus();
    }

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
        FocusSelectedItem();
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

    private void PerformSearch(string query, bool jumpToFirst = true)
    {
        _searchResults.Clear();
        _searchResultIndex = -1;

        if (string.IsNullOrWhiteSpace(query) || _doc.Blocks.Count == 0)
        {
            MatchCountText.Text = "0 matches";
            return;
        }

        for (int i = 0; i < _doc.Blocks.Count; i++)
        {
            if (_doc.Blocks[i].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                _searchResults.Add(i);
        }

        MatchCountText.Text = $"{_searchResults.Count} match{(_searchResults.Count == 1 ? "" : "es")}";
        if (_searchResults.Count > 0)
        {
            _searchResultIndex = 0;
            if (jumpToFirst) JumpToSearchMatch(0);
        }
        else if (jumpToFirst)
        {
            SetStatus("No matches.");
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
        // Focus stays in the search box, so the screen reader needs telling.
        SetStatus($"{_searchResultIndex + 1} of {_searchResults.Count}: {_doc.Blocks[blockIndex].Text}");
    }

    /// <summary>Automation hook: selects a row and activates it, exactly like Enter/double-click would.</summary>
    public void SelectAndActivate(int index)
    {
        if (index < 0 || index >= Blocks.Items.Count) return;
        Blocks.SelectedIndex = index;
        ScrollToIndex(index);
        _ = ActivateSelectedAsync();
    }

    public void ShowAndFocusFirst()
    {
        ShowActivated = true;
        Show();
        Activate();
        if (Blocks.Items.Count > 0)
            Blocks.SelectedIndex = 0;
        FocusSelectedItem();
    }

    /// <summary>Moves the keyboard into a reader that is already showing, keeping the user's place.</summary>
    public void FocusReader()
    {
        ReclaimForeground();
        Activate();
        if (Blocks.SelectedIndex < 0 && Blocks.Items.Count > 0)
            Blocks.SelectedIndex = 0;
        FocusSelectedItem();
    }

    /// <summary>
    /// Shows the reader without stealing keyboard focus from the page/app behind it.
    /// The window stays activatable: a permanent no-activate style would leave it
    /// unable to ever receive typing.
    /// </summary>
    public void ShowOverlayNoActivate()
    {
        if (IsVisible) return;
        ShowActivated = false;
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

    private void CopyDocument()
    {
        try
        {
            Clipboard.SetText(_doc.PlainText);
            SetStatus("Copied.");
        }
        catch
        {
            SetStatus("Clipboard busy, try again.");
        }
    }

    private void Controls_Click(object sender, RoutedEventArgs e) => ControlPanelRequested?.Invoke(this, EventArgs.Empty);

    private async void Blocks_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await ActivateSelectedAsync();
    }

    /// <summary>
    /// Reader shortcuts live at window level so they keep working after a toolbar
    /// button has taken focus. Inside a text box, keys belong to the text.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // WPF only activates a focused button with Space, never Enter. Screen-reader
        // users press Enter, so route Enter on a focused button to its Click.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button btn)
        {
            e.Handled = true;
            btn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return;
        }

        if (e.Key == Key.F7)
        {
            e.Handled = true;
            DiagnosticsLogger.Enabled = !DiagnosticsLogger.Enabled;
            SetStatus(DiagnosticsLogger.Enabled ? "Debug logging on." : "Debug logging off.");
            return;
        }

        var ctrlPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
        var inTextBox = Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        if (inTextBox && !(e.Key is Key.F3 or Key.F5 || (ctrlPressed && e.Key == Key.F)))
            return;

        HandleReaderKey(e);
    }

    private void HandleEscape()
    {
        if (SearchPanel.Visibility == Visibility.Visible)
            CloseSearch();
        else if (IsEditing)
            CancelEdit();
        else if (_undoOriginal is not null && Blocks.SelectedIndex == _undoIndex)
            _ = UndoLastEditAsync();
        else
        {
            HiddenByUser?.Invoke(this, EventArgs.Empty);
            Hide();
        }
    }

    private void HandleReaderKey(KeyEventArgs e)
    {
        bool shiftPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrlPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
        bool altPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);
        bool onButton = Keyboard.FocusedElement is ButtonBase;

        // Doing anything else means the user has moved on from the last edit.
        if (e.Key is not (Key.Escape or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.Insert or Key.CapsLock))
            _undoOriginal = null;

        if (ctrlPressed && e.Key == Key.F)
        {
            e.Handled = true;
            ToggleSearch();
            return;
        }

        switch (e.Key)
        {
            case Key.F3:
                e.Handled = true;
                JumpToSearchMatch(shiftPressed ? -1 : +1);
                break;
            case Key.H when !ctrlPressed && !altPressed:
                e.Handled = true;
                JumpToHeading(shiftPressed ? -1 : +1);
                break;
            case Key.B or Key.L when !ctrlPressed && !altPressed:
                e.Handled = true;
                JumpToControlBlock(shiftPressed ? -1 : +1);
                break;
            case Key.Enter:
                e.Handled = true;
                _ = ActivateSelectedAsync();
                break;
            case Key.Space when !ctrlPressed && !altPressed && !onButton:
                e.Handled = true;
                _ = ActivateSelectedAsync();
                break;
            case Key.R when ctrlPressed:
            case Key.F5:
                e.Handled = true;
                RequestRefresh();
                break;
            case Key.E when !ctrlPressed && !altPressed:
            case Key.F2:
                e.Handled = true;
                _ = EditSelectedAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                HandleEscape();
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
                CopyDocument();
                break;
            case Key.Left when !ctrlPressed && !altPressed && !shiftPressed && !onButton:
                if (TryAdjustSelectedValue(-1)) e.Handled = true;
                break;
            case Key.Right when !ctrlPressed && !altPressed && !shiftPressed && !onButton:
                if (TryAdjustSelectedValue(+1)) e.Handled = true;
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
                FocusSelectedItem();
                return;
            }
        }
    }

    private void RequestRefresh()
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
        StatusLine.Text = RefreshingStatus;
    }

    private sealed record EditInfo(AutomationElement Target, ValuePattern? ValuePattern, bool IsPassword, string Current, int ProcessId, bool IsMultiline);

    private static EditInfo? ReadEditInfo(AutomationElement root)
    {
        var target = FindEditable(root);
        if (target is null) return null;
        // Rich text fields often have no ValuePattern; those are typed for real on commit.
        var valuePattern = target.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? (ValuePattern)pattern : null;
        var isPassword = target.Current.IsPassword;
        var current = isPassword ? "" : ReadFieldText(target, valuePattern);
        var isMultiline = target.Current.ControlType == ControlType.Document || current.Contains('\n') || current.Contains('\r');
        return new EditInfo(target, valuePattern, isPassword, current, target.Current.ProcessId, isMultiline);
    }

    /// <summary>
    /// Opens the inline input bar for the selected field, holding its current value.
    /// Typing then flows through to the real control in the app or page underneath.
    /// </summary>
    private async Task EditSelectedAsync()
    {
        if (IsEditing) return;
        var block = SelectedBlock;
        if (block?.RuntimeTarget is not AutomationElement root)
        {
            SetStatus("Select an editable field first.");
            return;
        }

        var (ok, info) = await RunUiaAsync(() => ReadEditInfo(root), "That field could not be opened.");
        if (!ok || IsEditing) return;
        if (info is null)
        {
            SetStatus("The selected item cannot be edited.");
            return;
        }
        BeginEdit(info, block.Text);
    }

    private static string ReadFieldText(AutomationElement target, ValuePattern? valuePattern)
    {
        if (valuePattern is not null) return valuePattern.Current.Value ?? "";
        if (target.TryGetCurrentPattern(TextPattern.Pattern, out var text))
            return ((TextPattern)text).DocumentRange.GetText(-1) ?? "";
        return "";
    }

    /// <summary>
    /// True when keyboard focus is on the block list itself or one of its rows.
    /// </summary>
    private bool BlocksHasKeyboardFocus()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return false;
        if (ReferenceEquals(focused, Blocks)) return true;
        if (focused is ListBoxItem item && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), Blocks)) return true;
        return false;
    }

    private void BeginEdit(EditInfo info, string label)
    {
        _editTarget = info.Target;
        _editValuePattern = info.ValuePattern;
        _editOriginal = info.Current;
        _editIsPassword = info.IsPassword;
        _editProcessId = info.ProcessId;
        _editPushed = false;
        _editPushTask = Task.CompletedTask;

        _editSuppressPush = true;
        EditLabelText.Text = label;
        // Must come before focusing: WPF refuses focus inside a collapsed parent.
        EditBar.Visibility = Visibility.Visible;
        EditPasswordBox.Visibility = info.IsPassword ? Visibility.Visible : Visibility.Collapsed;
        EditBox.Visibility = info.IsPassword ? Visibility.Collapsed : Visibility.Visible;
        if (info.IsPassword)
        {
            EditPasswordBox.Password = "";
        }
        else
        {
            EditBox.AcceptsReturn = info.IsMultiline;
            EditBox.Text = info.Current;
            EditBox.CaretIndex = EditBox.Text.Length;
        }
        _editSuppressPush = false;

        FocusEditor();
        // A double-click's mouse-up can pull focus back to the row; claim it again after.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusEditor);

        var finish = info.IsMultiline ? "Enter to finish, Shift+Enter for a new line" : "Enter to finish";
        SetStatus(CanPushLive
            ? $"Typing into the field… {finish}, Esc to cancel."
            : $"Type the text. {finish}, Esc to cancel.");
    }

    private void FocusEditor()
    {
        if (!IsEditing) return;
        Control box = _editIsPassword ? EditPasswordBox : EditBox;
        if (box.IsKeyboardFocused) return;
        if (!IsActive) Activate();
        UpdateLayout();
        box.Focus();
        Keyboard.Focus(box);
    }

    /// <summary>
    /// Passwords are sent once on commit: their old value is unreadable, so a live
    /// push could not be undone by Cancel.
    /// </summary>
    private bool CanPushLive => _editValuePattern is not null && !_editIsPassword && !_focusStealingProcesses.Contains(_editProcessId);

    /// <summary>Takes the foreground back if the source app grabbed it. Returns true when it had to.</summary>
    private bool ReclaimForeground()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || NativeMethods.GetForegroundWindow() == hwnd) return false;
        NativeMethods.ForceForeground(hwnd);
        return true;
    }

    private void EditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editSuppressPush) return;
        QueueEditPush();
    }

    private void EditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shiftPressed = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);
        // Tab leaves the field keeping the text, back on its row, where Esc undoes it.
        if (e.Key == Key.Tab || (e.Key == Key.Enter && !(shiftPressed && EditBox.AcceptsReturn)))
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
        if (!CanPushLive) return;
        if (_editPushTimer is null)
        {
            _editPushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _editPushTimer.Tick += EditPushTimer_Tick;
        }
        _editPushTimer.Stop();
        _editPushTimer.Start();
    }

    private void EditPushTimer_Tick(object? sender, EventArgs e)
    {
        _editPushTimer?.Stop();
        // One push at a time, so an older value can never land after a newer one.
        if (!_editPushTask.IsCompleted)
        {
            QueueEditPush();
            return;
        }
        _editPushTask = PushEditValueAsync();
    }

    private async Task PushEditValueAsync()
    {
        if (!CanPushLive) return;
        var valuePattern = _editValuePattern!;
        var text = EditBox.Text;
        var (ok, _) = await RunUiaAsync(() =>
        {
            valuePattern.SetValue(text);
            return true;
        }, "Live typing was refused. Press Enter to type it into the field directly.");

        if (!ok || !ReferenceEquals(valuePattern, _editValuePattern)) return;
        _editPushed = true;
        if (ReclaimForeground())
        {
            _focusStealingProcesses.Add(_editProcessId);
            FocusEditor();
            SetStatus("Keep typing, then press Enter to send it. Esc to cancel.");
        }
    }

    private void CommitEdit() => _editCommitTask = CommitEditAsync();

    private async Task CommitEditAsync()
    {
        _editPushTimer?.Stop();
        if (_editTarget is not { } target) return;

        var valuePattern = _editValuePattern;
        var isPassword = _editIsPassword;
        var original = _editOriginal;
        var text = isPassword ? EditPasswordBox.Password : EditBox.Text;
        var pendingPush = _editPushTask;
        // A password's old value is unknown, so an empty box means "left alone".
        var mustSend = text != original || _editPushed || !pendingPush.IsCompleted;

        CloseEditBar();
        if (!mustSend)
        {
            SetStatus("No change.");
            return;
        }

        // Recorded before the send so an immediate Esc undoes rather than closing the
        // reader. A password's old value is unknown, so it can't be put back.
        if (!isPassword && text != original)
        {
            _undoOriginal = original;
            _undoIndex = Blocks.SelectedIndex;
        }

        StatusLine.Text = "Sending…";
        await pendingPush;
        var (ok, accepted) = await RunUiaAsync(
            () => TrySetValue(valuePattern, text, original, isPassword),
            "The source app did not accept the new value.");
        var delivered = (ok && accepted) || await TypeWithKeyboardAsync(target, text);

        if (!delivered)
        {
            _undoOriginal = null;
            SetStatus("The field did not accept the text.");
            return;
        }
        if (IsVisible && ReclaimForeground())
            FocusSelectedItem();
        ControlActivated?.Invoke(this, EventArgs.Empty);
        SetStatus(_undoOriginal is null ? "Field updated." : "Field updated. Esc to undo.");
    }

    private async Task UndoLastEditAsync()
    {
        var original = _undoOriginal!;
        _undoOriginal = null;
        await _editCommitTask;

        // The row's element is re-read: a refresh may have replaced the one that was edited.
        if (SelectedBlock?.RuntimeTarget is not AutomationElement root) return;
        var (ok, info) = await RunUiaAsync(() => ReadEditInfo(root), "That field could not be restored.");
        if (!ok) return;
        if (info is null)
        {
            SetStatus("That field could not be restored.");
            return;
        }

        var (set, accepted) = await RunUiaAsync(
            () => TrySetValue(info.ValuePattern, original, info.Current, isPassword: false),
            "That field could not be restored.");
        if (!(set && accepted) && !await TypeWithKeyboardAsync(info.Target, original))
        {
            SetStatus("That field could not be restored.");
            return;
        }
        if (IsVisible && ReclaimForeground())
            FocusSelectedItem();
        ControlActivated?.Invoke(this, EventArgs.Empty);
        SetStatus("Change undone.");
    }

    /// <summary>
    /// Sets the value through UI Automation and confirms it landed: some controls
    /// report success yet ignore the call. Chromium applies it asynchronously, hence the wait.
    /// </summary>
    private static bool TrySetValue(ValuePattern? valuePattern, string text, string original, bool isPassword)
    {
        if (valuePattern is null) return false;
        valuePattern.SetValue(text);
        if (isPassword || text == original) return true;
        Thread.Sleep(150);
        return valuePattern.Current.Value != original;
    }

    /// <summary>
    /// Fallback for fields that refuse programmatic values: focus the real field and
    /// type into it. Keys are only sent once that field verifiably has focus, so text
    /// (possibly a password) can never land in some other window.
    /// </summary>
    private async Task<bool> TypeWithKeyboardAsync(AutomationElement target, string text)
    {
        var (ok, typed) = await RunUiaAsync(() =>
        {
            target.SetFocus();
            var focused = false;
            for (var attempt = 0; attempt < 12 && !focused; attempt++)
            {
                Thread.Sleep(50);
                focused = target.Current.HasKeyboardFocus;
            }
            if (!focused) return false;

            NativeMethods.SendSelectAll();
            Thread.Sleep(30);
            NativeMethods.SendText(text);
            Thread.Sleep(80);
            return true;
        }, "The field did not accept the text.");

        if (IsVisible) FocusReader();
        return ok && typed;
    }

    private async void CancelEdit()
    {
        _editPushTimer?.Stop();
        var valuePattern = _editValuePattern;
        var original = _editOriginal;
        var pendingPush = _editPushTask;
        var restore = _editPushed || !pendingPush.IsCompleted;

        CloseEditBar();
        SetStatus("Edit cancelled.");

        if (!restore || valuePattern is null) return;
        await pendingPush;
        await RunUiaAsync(() =>
        {
            valuePattern.SetValue(original);
            return true;
        }, "The original value could not be restored.");
        if (IsVisible && ReclaimForeground())
            FocusSelectedItem();
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
        _editPushed = false;
        FocusSelectedItem();
    }

    private static AutomationElement? FindEditable(AutomationElement root)
    {
        try
        {
            if (IsWritable(root) || UiaCaptureService.IsWritableDocument(root))
                return root;
            var controls = root.FindAll(TreeScope.Descendants, Automation.ControlViewCondition);
            foreach (AutomationElement candidate in controls)
            {
                if (IsWritable(candidate))
                    return candidate;
            }
        }
        catch { }
        return null;
    }

    private static bool IsWritable(AutomationElement element)
    {
        try
        {
            if (!element.Current.IsEnabled) return false;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                return !((ValuePattern)pattern).Current.IsReadOnly;
            return element.Current.ControlType == ControlType.Edit && element.Current.IsKeyboardFocusable;
        }
        catch
        {
            return false;
        }
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
                FocusSelectedItem();
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

    private enum ActivationKind { NotInteractive, Activated, EditField, ClickAt }

    private readonly record struct ActivationResult(ActivationKind Kind, int X = 0, int Y = 0);

    /// <summary>
    /// Pushes the button / follows the link / toggles the check box that this row
    /// represents, on the real page or app underneath.
    /// </summary>
    private async Task ActivateSelectedAsync()
    {
        var block = SelectedBlock;
        if (block is null) return;

        // Screen-read content: activate by sending a real mouse click to screen coordinates.
        if (block.RuntimeTarget is OcrElementTarget ocrTarget)
        {
            SetStatus($"Clicking “{ocrTarget.Text}”…");
            ClickThroughAt(ocrTarget.ScreenX, ocrTarget.ScreenY);
            AfterActivated();
            return;
        }

        if (block.RuntimeTarget is not AutomationElement el || _uiaActionBusy) return;

        _uiaActionBusy = true;
        try
        {
            var isControl = block is ControlBlock;
            var (ok, result) = await RunUiaAsync(() => Activate(el, isControl), "Could not activate that control.");
            if (!ok) return;

            switch (result.Kind)
            {
                case ActivationKind.EditField:
                    await EditSelectedAsync();
                    break;
                case ActivationKind.ClickAt:
                    ClickThroughAt(result.X, result.Y);
                    AfterActivated();
                    break;
                case ActivationKind.Activated:
                    AfterActivated();
                    break;
            }
        }
        finally
        {
            _uiaActionBusy = false;
        }
    }

    private static ActivationResult Activate(AutomationElement el, bool isControl)
    {
        // A field's inner "clear" button must not win over typing into the field.
        if (IsTextField(el))
            return new ActivationResult(ActivationKind.EditField);

        var target = FindActivatable(el) ?? el;
        if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return new ActivationResult(ActivationKind.Activated);
        }
        if (target.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return new ActivationResult(ActivationKind.Activated);
        }
        if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
            return new ActivationResult(ActivationKind.Activated);
        }
        if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            var xp = (ExpandCollapsePattern)expand;
            if (xp.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                xp.Expand();
            else
                xp.Collapse();
            return new ActivationResult(ActivationKind.Activated);
        }
        if (target.TryGetCurrentPattern(ValuePattern.Pattern, out _))
            return new ActivationResult(ActivationKind.EditField);

        // Last resort: custom-drawn controls (many launchers, embedded browsers)
        // expose no action pattern at all but do report where they are on screen.
        // Plain text rows have nothing to click.
        if (isControl)
        {
            var rect = target.Current.BoundingRectangle;
            if (!rect.IsEmpty && rect.Width >= 1 && rect.Height >= 1)
            {
                int x = (int)Math.Round(rect.Left + rect.Width / 2);
                int y = (int)Math.Round(rect.Top + rect.Height / 2);
                if (x >= 0 && y >= 0)
                    return new ActivationResult(ActivationKind.ClickAt, x, y);
            }
        }
        return new ActivationResult(ActivationKind.NotInteractive);
    }

    private static bool IsTextField(AutomationElement element)
    {
        try
        {
            return UiaCaptureService.IsWritableDocument(element)
                   || (element.Current.ControlType == ControlType.Edit && IsWritable(element));
        }
        catch
        {
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
    /// value (the way the control behaves when it has focus in the app). Returns
    /// false when the row can't have a steppable value, leaving the key alone.
    /// </summary>
    private bool TryAdjustSelectedValue(int delta)
    {
        if (IsEditing || _uiaActionBusy) return false;
        if (SelectedBlock is not ControlBlock { RuntimeTarget: AutomationElement root }) return false;
        _ = AdjustSelectedValueAsync(root, delta);
        return true;
    }

    private async Task AdjustSelectedValueAsync(AutomationElement root, int delta)
    {
        _uiaActionBusy = true;
        try
        {
            var (ok, value) = await RunUiaAsync(() => StepValue(root, delta), "That control did not accept the change.");
            if (!ok || value is null) return;
            SetStatus(string.IsNullOrWhiteSpace(value) ? "Value changed." : $"Value: {value}");
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _uiaActionBusy = false;
        }
    }

    /// <summary>Returns the new value's text, or null when nothing changed.</summary>
    private static string? StepValue(AutomationElement root, int delta)
    {
        var target = FindValueControl(root);
        if (target is null) return null;

        // Sliders and spin buttons expose a numeric range directly.
        if (target.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObj))
        {
            var rv = (RangeValuePattern)rangeObj;
            var info = rv.Current;
            if (info.Minimum >= info.Maximum) return null;
            double step = info.SmallChange > 0 ? info.SmallChange : Math.Max(Math.Abs(info.Maximum - info.Minimum) / 50.0, 1.0);
            double next = Math.Clamp(info.Value + delta * step, info.Minimum, info.Maximum);
            if (Math.Abs(next - info.Value) < double.Epsilon) return null;
            rv.SetValue(next);
            return rv.Current.Value.ToString("0.##");
        }

        // Combo boxes: step through their option items.
        var items = GetSelectionItems(target);
        if (items.Count > 0)
        {
            int index = items.FindIndex(it => it.IsSelected);
            if (index < 0) return null;
            int nextIndex = Math.Clamp(index + delta, 0, items.Count - 1);
            if (nextIndex == index) return null;
            SelectItem(items[nextIndex]);
            return items[nextIndex].Name;
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
                    return nextIndex != index ? items[nextIndex].Name : null;
                }
                xp.Collapse();
            }
        }

        // Text-backed numeric value (e.g. an up/down control showing a number).
        // Never a plain text box: an arrow key must not rewrite what's typed there.
        if (target.Current.ControlType != ControlType.Edit && target.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj))
        {
            var vp = (ValuePattern)valueObj;
            if (!vp.Current.IsReadOnly
                && double.TryParse(vp.Current.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                vp.SetValue((number + delta).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return (number + delta).ToString("0.##");
            }
        }
        return null;
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
        StatusLine.Text = "Sent to the page/app…";
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
                // PointToScreen returns device pixels, like the click point; ActualWidth doesn't.
                var bounds = new Rect(PointToScreen(new Point(0, 0)), PointToScreen(new Point(ActualWidth, ActualHeight)));
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
                    if (wasActive) FocusReader();
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
        Blocks.SelectedIndex >= 0 && Blocks.SelectedIndex < _doc.Blocks.Count ? _doc.Blocks[Blocks.SelectedIndex] : null;
}
