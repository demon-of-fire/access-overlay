# Access Overlay

A universal reader overlay for Windows that works alongside NVDA. Captures any foreground app or website and shows it as a clean, navigable reader window that NVDA reads like any other app.

## Features

- **App Capture (Ctrl+Alt+A)**: Captures the active foreground window and normalizes UI elements into readable document hierarchy.
- **Reader keys**: H heading, B/L control, Enter or Space to press or edit, F2 to edit, left/right to change a combo or slider, Ctrl+F to find, F5 to refresh, Ctrl+C to copy, F7 to toggle debug logging, Esc to close. Ctrl+Alt+A moves into the reader when it is showing behind another app.
- **Type into fields**: Press Enter on a field to edit it from the reader; text goes to the real app or page. Tab leaves the field, and Esc then undoes the change.
- **Accessible Web Browser**: Built-in WebView2 browser with article text extraction and automatic read-on-load options.
- **Screen-read fallback**: Apps that expose nothing to accessibility tools are read from the screen, with clickable lines.
- **Customizable Overlay Reader**: Floating, customizable window with dynamic font resizing, contrast themes, and opacity control.

## Requirements

- Windows 10/11 (64-bit)
- .NET 8.0 Runtime
- NVDA (or another screen reader) for speech

## License

MIT License
