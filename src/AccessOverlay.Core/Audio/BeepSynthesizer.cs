using System.Media;
using System.Runtime.InteropServices;

namespace AccessOverlay.Core.Audio;

public static class BeepSynthesizer
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    public static void PlayHeadingBeep()
    {
        try
        {
            Console.Beep(880, 70); // Crisp high tone for headings
        }
        catch
        {
            MessageBeep(0x00000040);
        }
    }

    public static void PlayButtonBeep()
    {
        try
        {
            Console.Beep(520, 60); // Mid confirmation tone for buttons/links
        }
        catch
        {
            MessageBeep(0x00000000);
        }
    }

    public static void PlayFieldBeep()
    {
        try
        {
            Console.Beep(420, 50); // Soft tone for input fields
        }
        catch
        {
            MessageBeep(0x00000000);
        }
    }
}
