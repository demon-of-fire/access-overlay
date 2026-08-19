using System.Speech.Synthesis;
using AccessOverlay.Core.Nvda;

namespace AccessOverlay.Core.Speech;

/// <summary>
/// Routes corrected text to the best available output: NVDA when it is running,
/// the Windows SAPI5 synthesizer as a fallback. This is how the overlay "reaches
/// into NVDA" and makes sure users hear the *fixed* content, not the broken tree.
/// </summary>
public sealed class SpeechRouter : IDisposable
{
    private readonly SpeechSynthesizer _sapi = new();
    private bool _disposed;

    public event Action<string>? OutputChanged;

    /// <summary>Description of the currently active speech output, e.g. "NVDA" or "Windows SAPI5".</summary>
    public string CurrentOutput { get; private set; } = "None";
    public bool NvdaAvailable => NvdaClient.IsRunning;

    public int Rate
    {
        get => _sapi.Rate;
        set => _sapi.Rate = Math.Clamp(value, -10, 10);
    }

    public int Volume
    {
        get => _sapi.Volume;
        set => _sapi.Volume = Math.Clamp(value, 0, 100);
    }

    public void Speak(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (NvdaClient.IsRunning && NvdaClient.Speak(text, interrupt))
        {
            SetOutput("NVDA");
            return;
        }
        if (interrupt) _sapi.SpeakAsyncCancelAll();
        _sapi.SpeakAsync(text);
        SetOutput("Windows SAPI5");
    }

    public void SpeakSsml(string ssml, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(ssml)) return;
        if (NvdaClient.IsRunning && NvdaClient.SpeakSsml(ssml, interrupt))
        {
            SetOutput("NVDA");
            return;
        }
        var plain = System.Security.SecurityElement.Escape(ssml);
        if (interrupt) _sapi.SpeakAsyncCancelAll();
        _sapi.SpeakAsync(plain);
        SetOutput("Windows SAPI5");
    }

    public void Cancel()
    {
        if (NvdaClient.IsRunning) NvdaClient.CancelSpeech();
        _sapi.SpeakAsyncCancelAll();
    }

    public void RefreshStatus()
    {
        SetOutput(NvdaClient.IsRunning ? "NVDA" : "Windows SAPI5");
    }

    private void SetOutput(string name)
    {
        if (CurrentOutput == name) return;
        CurrentOutput = name;
        OutputChanged?.Invoke(name);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sapi.Dispose();
    }
}
