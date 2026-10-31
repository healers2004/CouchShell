using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CouchShell.Platform;

namespace CouchShell.Services;

public enum UiSound { Move, Select, Back, Startup }

// Plays the interface sounds through one audio stream that stays open. A new sound replaces the one
// playing, which suits quick repeated moves. All audio calls happen on their own thread, so they
// can't hold up the UI, however fast someone moves.
public sealed class UiSounds : IDisposable
{
    private const int HeaderSize = 44;  // the bytes before the samples in the .wav files we generate
    private static readonly string[] Names =
    [
        "soft-move", "soft-select", "soft-back",
        "click-move", "click-select", "click-back",
        "chime-move", "chime-select", "chime-back",
        "startup",
    ];

    // Ticks closer together than this are a blur anyway, and restarting audio has a cost.
    private static readonly TimeSpan MinMoveGap = TimeSpan.FromMilliseconds(45);
    // Reopening after a quiet spell picks up a change of output device, like plugging in the TV.
    private static readonly TimeSpan ReopenAfter = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, WindowsNative.WaveClip> _clips = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private IntPtr _device;
    private string? _next;  // only the newest request matters
    private TimeSpan _lastMove = TimeSpan.FromHours(-1);
    private TimeSpan _lastPlayed = TimeSpan.FromHours(-1);
    private volatile bool _stopping;

    public UiSounds()
    {
        foreach (string name in Names)
        {
            try
            {
                using Stream stream = Application.GetResourceStream(new Uri($"Assets/Sounds/{name}.wav", UriKind.Relative)).Stream;
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                _clips[name] = WindowsNative.CreateWaveClip(bytes.ToArray().AsSpan(HeaderSize));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
            {
                AppLog.Write($"Could not load the {name} sound: {e.Message}");  // silence, not a crash
            }
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "UI sounds" };
        _thread.Start();
    }

    public void Play(UiSound sound, string pack)
    {
        if (sound == UiSound.Move)
        {
            if (_clock.Elapsed - _lastMove < MinMoveGap) return;
            _lastMove = _clock.Elapsed;
        }
        Volatile.Write(ref _next, sound == UiSound.Startup ? "startup" : $"{pack}-{sound.ToString().ToLowerInvariant()}");
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void Run()
    {
        while (true)
        {
            _wake.Wait();
            if (_stopping) break;
            string? name = Interlocked.Exchange(ref _next, null);
            if (name == null || !_clips.TryGetValue(name, out WindowsNative.WaveClip? clip)) continue;

            try
            {
                if (_clock.Elapsed - _lastPlayed > ReopenAfter) CloseDevice();
                if (!PlayOn(clip))
                {
                    // The device may have gone away, e.g. a TV switched off. Try once with a fresh one.
                    CloseDevice();
                    PlayOn(clip);
                }
                _lastPlayed = _clock.Elapsed;
            }
            catch (Exception e) when (e is ExternalException or DllNotFoundException)
            {
                AppLog.Write($"Could not play a sound: {e.Message}");
            }
        }
        CloseDevice();
    }

    private bool PlayOn(WindowsNative.WaveClip clip)
    {
        if (_device == IntPtr.Zero && !OpenDevice()) return false;
        return WindowsNative.PlayWaveClip(_device, clip);
    }

    private bool OpenDevice()
    {
        _device = WindowsNative.OpenWaveDevice();
        if (_device == IntPtr.Zero) return false;
        foreach (WindowsNative.WaveClip clip in _clips.Values) WindowsNative.PrepareWaveClip(_device, clip);
        return true;
    }

    private void CloseDevice()
    {
        if (_device == IntPtr.Zero) return;
        WindowsNative.CloseWaveDevice(_device, _clips.Values);
        _device = IntPtr.Zero;
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Release();
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
