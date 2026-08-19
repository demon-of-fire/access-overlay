using System.Runtime.InteropServices;

namespace AccessOverlay.Core.Nvda;

/// <summary>
/// P/Invoke wrapper around NVDA's official controller client DLL
/// (nvdaControllerClient.dll, LGPL-2.1, distributed by NV Access).
/// Every function returns 0 on success, a Windows error code otherwise.
/// </summary>
public static class NvdaClient
{
    [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
    private static extern int nvdaController_testIfRunning();

    [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
    private static extern int nvdaController_speakText(string text);

    [DllImport("nvdaControllerClient.dll")]
    private static extern int nvdaController_cancelSpeech();

    [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
    private static extern int nvdaController_brailleMessage(string message);

    [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
    private static extern int nvdaController_getProcessId(out uint processId);

    [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
    private static extern int nvdaController_speakSsml(string ssml, int symbolLevel = -1, int priority = 0, bool asynchronous = true);

    /// <summary>True if NVDA is running and reachable.</summary>
    public static bool IsRunning => nvdaController_testIfRunning() == 0;

    /// <summary>Asks NVDA to speak text. Returns false if NVDA is not running.</summary>
    public static bool Speak(string text, bool interrupt = true)
    {
        if (interrupt) nvdaController_cancelSpeech();
        return nvdaController_speakText(text) == 0;
    }

    /// <summary>Asks NVDA to speak SSML. Returns false if NVDA is not running.</summary>
    public static bool SpeakSsml(string ssml, bool interrupt = true)
    {
        if (interrupt) nvdaController_cancelSpeech();
        return nvdaController_speakSsml(ssml) == 0;
    }

    public static bool CancelSpeech() => nvdaController_cancelSpeech() == 0;

    public static bool Braille(string message) => nvdaController_brailleMessage(message) == 0;

    public static uint? GetProcessId()
    {
        if (nvdaController_getProcessId(out var pid) == 0) return pid;
        return null;
    }
}
