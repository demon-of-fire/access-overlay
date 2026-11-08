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

    public static async Task<ReadableDocument> CaptureWindowOcrAsync(IntPtr hwnd, string title = "Screen")
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
                    Source = "Screen read",
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

                // A short line is probably a button or menu option, so make it clickable.
                if (LooksLikeControl(lineText))
                {
                    blocks.Add(new ControlBlock
                    {
                        Text = lineText,
                        Role = "On-screen control",
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
                    Text = "No text detected on screen.",
                    Depth = 0
                });
            }

            return new ReadableDocument
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Screen" : title,
                Source = "Screen read",
                Blocks = blocks
            };
        }
        catch (Exception ex)
        {
            return new ReadableDocument
            {
                Title = title,
                Source = "Screen read error",
                Blocks = new List<ContentBlock>
                {
                    new ParagraphBlock { Text = $"OCR scan failed: {ex.Message}", Depth = 0 }
                }
            };
        }
    }

    /// <summary>Reads <paramref name="hwnd"/> from the screen, for apps that expose nothing to accessibility.</summary>
    public static async Task<ReadableDocument> CaptureAsync(IntPtr hwnd)
    {
        var title = "Screen";
        try
        {
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            var t = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(t)) title = t;
        }
        catch { }

        return await CaptureWindowOcrAsync(hwnd, title);
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

    private static bool LooksLikeControl(string text)
    {
        if (text.Length > 45) return false;
        if (text.EndsWith(".") || text.EndsWith("?") || text.EndsWith("!")) return false;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 6) return false;

        return text.Any(char.IsDigit) || words.Length <= 4;
    }
}
