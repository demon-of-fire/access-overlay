using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using AccessOverlay.Core.Diagnostics;
using AccessOverlay.Core.Model;

namespace AccessOverlay.Core.AppCapture;

public sealed record OcrElementTarget(int ScreenX, int ScreenY, string Text, double Width, double Height);

public static class OcrCaptureService
{
    private static OcrEngine? _ocrEngine;

    private static OcrEngine GetEngine()
    {
        return _ocrEngine ??= OcrEngine.TryCreateFromUserProfileLanguages() 
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
    }

    public static async Task<ReadableDocument> CaptureWindowOcrAsync(IntPtr hwnd, string title = "Game Menu", float minConfidence = 0.70f, bool gameMode = false)
    {
        try
        {
            Rectangle bounds;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
            {
                bounds = new Rectangle(rect.Left, rect.Top, Math.Max(100, rect.Right - rect.Left), Math.Max(100, rect.Bottom - rect.Top));
            }
            else
            {
                bounds = new Rectangle(0, 0, (int)System.Windows.SystemParameters.PrimaryScreenWidth, (int)System.Windows.SystemParameters.PrimaryScreenHeight);
            }

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var engine = GetEngine();
            if (engine is null)
            {
                return new ReadableDocument
                {
                    Title = title,
                    Source = "OCR Game Reader",
                    Blocks = new List<ContentBlock>
                    {
                        new ParagraphBlock { Text = "Windows OCR engine unavailable.", Depth = 0 }
                    }
                };
            }

            var ocrResult = await engine.RecognizeAsync(softwareBitmap);
            DiagnosticsLogger.Log($"OCR capture started for '{title}' hwnd=0x{hwnd.ToInt64():X} bounds={bounds.Width}x{bounds.Height}");
            var blocks = new List<ContentBlock>();

            foreach (var line in ocrResult.Lines)
            {
                string lineText = line.Text.Trim();
                if (string.IsNullOrWhiteSpace(lineText))
                    continue;

                // Skip extremely noisy OCR lines that contain too few letters.
                var letterCount = lineText.Count(char.IsLetterOrDigit);
                if (letterCount < 2 || letterCount < lineText.Length / 3)
                {
                    DiagnosticsLogger.Log($"Skipping OCR line: '{lineText}' (letterCount={letterCount})");
                    continue;
                }

                // Calculate center screen coordinate for clicking
                double centerX = bounds.Left;
                double centerY = bounds.Top;
                double totalWidth = 0;
                double totalHeight = 0;

                if (line.Words.Count > 0)
                {
                    var firstWord = line.Words[0].BoundingRect;
                    var lastWord = line.Words[^1].BoundingRect;

                    double minX = firstWord.Left;
                    double minY = line.Words.Min(w => w.BoundingRect.Top);
                    double maxX = lastWord.Right;
                    double maxY = line.Words.Max(w => w.BoundingRect.Bottom);

                    totalWidth = maxX - minX;
                    totalHeight = maxY - minY;
                    centerX = bounds.Left + minX + (totalWidth / 2.0);
                    centerY = bounds.Top + minY + (totalHeight / 2.0);
                }

                var target = new OcrElementTarget(
                    (int)Math.Round(centerX),
                    (int)Math.Round(centerY),
                    lineText,
                    totalWidth,
                    totalHeight
                );

                    // If line looks like a button / menu option or short text, make it an interactive ControlBlock
                if (LooksLikeGameMenuOption(lineText, true))
                {
                    blocks.Add(new ControlBlock
                    {
                        Text = lineText,
                        Role = "Game Menu Option",
                        State = "Clickable",
                        Depth = 0,
                        RuntimeTarget = target
                    });
                }
                else
                {
                    blocks.Add(new ParagraphBlock
                    {
                        Text = lineText,
                        Depth = 0,
                        RuntimeTarget = target
                    });
                }
            }

            if (blocks.Count == 0)
            {
                blocks.Add(new ParagraphBlock
                {
                    Text = "No game text or menu items detected on screen.",
                    Depth = 0
                });
            }

            return new ReadableDocument
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Game Menu OCR" : title,
                Source = "Game Reader",
                Blocks = blocks
            };
        }
        catch (Exception ex)
        {
            return new ReadableDocument
            {
                Title = title,
                Source = "Game Reader Error",
                Blocks = new List<ContentBlock>
                {
                    new ParagraphBlock { Text = $"OCR scan failed: {ex.Message}", Depth = 0 }
                }
            };
        }
    }

    /// <summary>
    /// Public API used by MainWindow. Captures <paramref name="hwnd"/> via OCR.
    /// <paramref name="minConfidence"/> (0–1) is used to discard low-confidence words
    /// (useful for Frostbite where texture aliasing produces garbled characters).
    /// </summary>
    public static async Task<ReadableDocument> CaptureAsync(IntPtr hwnd, float minConfidence = 0.70f, bool gameMode = false)
    {
        // Get window title for the overlay header
        var title = "Game Menu";
        try
        {
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            var t = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(t)) title = t;
        }
        catch { }

        return await CaptureWindowOcrAsync(hwnd, title, minConfidence, gameMode);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private static bool LooksLikeGameMenuOption(string text, bool gameMode)
    {
        if (text.Length > 45) return false;
        if (text.EndsWith(".") || text.EndsWith("?") || text.EndsWith("!")) return false;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 6) return false;

        if (text.Any(char.IsDigit)) return true;
        if (text.StartsWith("Play ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Start ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Continue ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Resume ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Exit ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Back ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (gameMode)
        {
            if (text.ToUpperInvariant() == text && text.Any(char.IsLetter))
                return true;
            if (words.Length <= 4 && text.Length >= 3)
                return true;
        }

        return words.Length <= 4;
    }
}
