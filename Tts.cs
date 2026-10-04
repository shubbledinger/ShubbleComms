using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal interface ITts
{
    string[] Voices { get; }
    WaveFormat Format { get; }
    Action<byte[]>? OnSamples { get; set; }     // trimmed MONO PCM at the render rate
    void SetVoice(string name);
    void SetRate(int rate);
    void SetPitch(int percent);                 // -50..+50
    void SetShout(int percent);                 // 0..50 — pitch+gain of ALL-CAPS words (no speed)
    void SetOutputRate(int samplesPerSecond);
    void Speak(string text);
    void Preload(string text);                  // render into cache now, don't play — soundboard
}

// Shared DSP over 16-bit mono PCM: shout detection, message segmentation,
// resampling, the WSOLA time-stretch (decouples pitch from speed), gain, and
// silence trimming. Used by both engines and the soundboard.
internal static class Pcm
{
    // "shouting" = at least two letters, all of them uppercase. Single letters
    // ("I", "A") never shout, or stream mode would scream every "I".
    public static bool IsShout(string s)
    {
        int letters = 0;
        foreach (var ch in s)
            if (char.IsLetter(ch))
            {
                if (char.IsLower(ch)) return false;
                letters++;
            }
        return letters >= 2;
    }

    // Split a message into runs of normal / shouted words — per-word shouting:
    //   "you are SUCH a fucking IDIOT"
    //     -> [("you are", false), ("SUCH", true), ("a fucking", false), ("IDIOT", true)]
    // Consecutive same-mode words merge into one segment (keeps prosody, fewer
    // renders). A message that's caps all over collapses into ONE shouted piece,
    // so full-sentence shouting still sounds like one sentence.
    public static List<(string Text, bool Shout)> Segment(string text)
    {
        if (IsShout(text)) return new List<(string Text, bool Shout)> { (text, true) };

        var segs = new List<(string Text, bool Shout)>();
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool sh = IsShout(w);
            if (segs.Count > 0 && segs[^1].Shout == sh)
                segs[^1] = (segs[^1].Text + " " + w, sh);
            else
                segs.Add((w, sh));
        }
        return segs;
    }

    // concatenate rendered segments with a short silence between them, so the
    // voice-character switch reads as emphasis rather than a glitch
    public static byte[] Join(IReadOnlyList<byte[]> parts, int gapSamples)
    {
        if (parts.Count == 1) return parts[0];
        int gap = Math.Max(0, gapSamples) * 2;
        long total = gap * (parts.Count - 1L);
        foreach (var p in parts) total += p.Length;
        var res = new byte[total];
        int pos = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            Array.Copy(parts[i], 0, res, pos, parts[i].Length);
            pos += parts[i].Length + gap;
        }
        return res;
    }

    // Linear resample. ratio = in/out; 1.15 returns 1/1.15 as many samples, which
    // played back at the same rate is +15% speed AND +15% pitch — the two are
    // coupled here; Stretch is what decouples them.
    public static byte[] ResampleLinear(byte[] pcm, double ratio)
    {
        if (pcm.Length < 4 || Math.Abs(ratio - 1.0) < 0.0001) return pcm;
        int n = pcm.Length / 2;
        long outN = Math.Max(1, (long)Math.Round(n / ratio));
        var res = new byte[outN * 2];
        for (long i = 0; i < outN; i++)
        {
            double pos = i * ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, n - 1);
            double t = pos - i0;
            double a = (short)(pcm[2 * i0] | (pcm[2 * i0 + 1] << 8));
            double b = (short)(pcm[2 * i1] | (pcm[2 * i1 + 1] << 8));
            int v = (int)Math.Round(a + (b - a) * t);
            short s = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            res[2 * i] = (byte)(s & 0xFF);
            res[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return res;
    }

    // WSOLA time-stretch: factor > 1 = longer/slower, < 1 = shorter/faster, and
    // PITCH IS UNTOUCHED — this is what lets speed and pitch move independently.
    // Frames are overlap-added with a Hann window, and a short correlation search
    // picks the frame offset that best continues the previous frame, keeping the
    // overlap in phase instead of comb-filtering. Clips shorter than two frames
    // skip it (nothing to align).
    public static byte[] Stretch(byte[] pcm, double factor)
    {
        if (pcm.Length < 4096 || Math.Abs(factor - 1.0) < 0.0015) return pcm;
        int n = pcm.Length / 2;
        long outN = Math.Max(1024, (long)Math.Round(n * factor));

        const int frame = 1024, hop = 512, delta = 384;
        double[] win = new double[frame];
        for (int i = 0; i < frame; i++)
            win[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (frame - 1));

        var acc = new double[outN + frame];
        var wsum = new double[outN + frame];

        // input is conceptually zero-padded with `frame` samples so the tail survives
        double X(int i) => i < n ? (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) : 0;

        double posD = 0;   // nominal analysis position (advances by hop/factor)
        int k = 0, prevA = 0;
        while (true)
        {
            int outPos = k * hop;
            int nom = (int)Math.Round(posD);
            if (nom > n || outPos + frame > outN) break;

            int a = nom;
            if (k > 0)
            {
                // search near the nominal position for the segment that best matches
                // the natural continuation of the previous frame; normalize by
                // candidate energy so loud segments don't win just by being loud
                int t = prevA + hop;
                double best = double.MinValue;
                int lo = Math.Max(0, nom - delta);
                int hi = Math.Min(n, nom + delta);
                for (int p = lo; p <= hi; p += 4)
                {
                    double dot = 0, en = 0;
                    for (int i = 0; i < hop; i++)
                    {
                        double v = X(p + i);
                        dot += v * X(t + i);
                        en += v * v;
                    }
                    double score = dot / Math.Sqrt(en + 1e-12);
                    if (score > best) { best = score; a = p; }
                }
            }

            for (int i = 0; i < frame; i++)
            {
                int o = outPos + i;
                if (o >= outN) break;
                acc[o] += X(a + i) * win[i];
                wsum[o] += win[i];
            }

            prevA = a;
            posD += (double)hop / factor;
            k++;
        }

        var res = new byte[outN * 2];
        for (int i = 0; i < outN; i++)
        {
            double w = wsum[i];
            double v = w > 0.05 ? acc[i] / w : 0;
            int s = (int)Math.Round(Math.Clamp(v, -32768.0, 32767.0));
            res[2 * i] = (byte)(s & 0xFF);
            res[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return res;
    }

    // Resample + WSOLA composed so pitch and speed move independently:
    //   pitch = 1.15 → +15% frequency;  speed = 1.15 → 15% faster
    // The stretch runs FIRST, at the source rate (longer frames = better
    // alignment): it applies the speed change and pre-compensates the duration
    // the pitch resample is about to remove.
    public static byte[] PitchShiftSpeed(byte[] pcm, int srcRate, int dstRate, double pitch, double speed)
    {
        var stretched = Stretch(pcm, pitch / speed);
        return ResampleLinear(stretched, srcRate * pitch / dstRate);
    }

    public static byte[] Boost(byte[] pcm, double gain)
    {
        if (Math.Abs(gain - 1.0) < 0.0001 || pcm.Length < 4) return pcm;
        var res = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            int v = (int)Math.Round((short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) * gain);
            short s = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            res[2 * i] = (byte)(s & 0xFF);
            res[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return res;
    }

    // chop leading/trailing near-silence (with a small pad) — TTS engines pad a
    // lot. NOT used for sound files: their silence is usually intentional.
    public static byte[] Trim(byte[] pcm)
    {
        int n = pcm.Length / 2;
        int first = -1, last = -1;
        for (int i = 0; i < n && first < 0; i++)
            if (Math.Abs((short)(pcm[2 * i] | (pcm[2 * i + 1] << 8))) > 500) first = i;
        for (int i = n - 1; i >= 0 && last < 0; i--)
            if (Math.Abs((short)(pcm[2 * i] | (pcm[2 * i + 1] << 8))) > 500) last = i;
        if (first < 0) return Array.Empty<byte>();

        int s = Math.Max(0, first - 150), e = Math.Min(n - 1, last + 150);
        var outb = new byte[(e - s + 1) * 2];
        Array.Copy(pcm, s * 2, outb, 0, outb.Length);
        return outb;
    }
}

// Windows' built-in SAPI voices. Renders on a background thread, caches results
// keyed on (shout variant, expanded text), and pushes finished PCM through
// OnSamples at the negotiated output rate.
internal sealed class SapiTts : ITts
{
    sealed class Cmd { public string Kind = "", Text = ""; public int Num; }

    readonly BlockingCollection<Cmd> _q = new();
    readonly ManualResetEvent _ready = new(false);
    readonly Dictionary<string, byte[]> _cache = new();
    readonly SpeechSynthesizer _synth = new();

    string[] _voices = Array.Empty<string>();
    int _rate = 4;
    int _pitchPct;
    int _shoutPct = 15;
    int _renderRate = 48000;
    string _voice = "";

    double ShoutGain => 1.0 + _shoutPct * 0.03;   // shout slider also drives loudness

    public WaveFormat Format { get; private set; } = new WaveFormat(48000, 16, 1);
    public Action<byte[]>? OnSamples { get; set; }
    public string[] Voices => _voices;

    public SapiTts()
    {
        new Thread(Run) { IsBackground = true, Name = "tts" }.Start();
        _q.Add(new Cmd { Kind = "warm" });
        _ready.WaitOne();
    }

    public void Speak(string text) => _q.Add(new Cmd { Kind = "say", Text = text });
    public void Preload(string text) => _q.Add(new Cmd { Kind = "preload", Text = text });
    public void SetVoice(string name) => _q.Add(new Cmd { Kind = "voice", Text = name });
    public void SetRate(int rate) => _q.Add(new Cmd { Kind = "rate", Num = rate });
    public void SetPitch(int pct) => _q.Add(new Cmd { Kind = "pitch", Num = pct });
    public void SetShout(int pct) => _q.Add(new Cmd { Kind = "shout", Num = pct });
    public void SetOutputRate(int r) => _q.Add(new Cmd { Kind = "outrate", Num = r });

    void Run()
    {
        foreach (var c in _q.GetConsumingEnumerable())
        {
            try
            {
                switch (c.Kind)
                {
                    case "warm":
                        _synth.Rate = _rate;
                        Render(" ");
                        _voices = _synth.GetInstalledVoices()
                            .Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToArray();
                        Log.Enqueue($"SAPI ready — {Format}, {_voices.Length} voices installed");
                        _ready.Set();
                        break;

                    // say and preload share the whole pipeline — segmentation, shout
                    // variants, caching — so a board phrase with mixed caps preloads
                    // exactly the segments it will later fire
                    case "say":
                    case "preload":
                        {
                            var segs = Pcm.Segment(c.Text);
                            if (segs.Count == 0) break;

                            var parts = new List<byte[]>();
                            foreach (var (seg, shout) in segs)
                            {
                                // the abbreviation dictionary lives here: speech-side,
                                // invisible to the display. Shout detection ran on the TYPED
                                // segment, so "WTF" still shouts after expanding to lowercase
                                string say = Expansions.Expand(seg,
                                    _synth.Voice?.Culture?.Name ?? "",
                                    _synth.Voice?.Name ?? "");
                                if (say.Length == 0) continue;

                                string key = (shout ? "!" : "") + say;
                                if (!_cache.TryGetValue(key, out var pcm))
                                {
                                    pcm = Render(say);
                                    // pitch is pure DSP (engine SSML support is inconsistent
                                    // across SAPI voices); shout is pitch + volume only —
                                    // no speed change
                                    int pitchPct = _pitchPct + (shout ? _shoutPct : 0);
                                    if (pitchPct != 0 && pcm.Length > 0)
                                        pcm = Pcm.PitchShiftSpeed(pcm, _renderRate, _renderRate,
                                            1.0 + pitchPct / 100.0, 1.0);
                                    if (shout && pcm.Length > 0) pcm = Pcm.Boost(pcm, ShoutGain);
                                    _cache[key] = pcm;
                                }
                                if (pcm.Length > 0) parts.Add(pcm);
                            }
                            if (parts.Count == 0) break;

                            // mixed caps = multiple segments: small pause at each
                            // voice-character switch
                            byte[] all = Pcm.Join(parts, (int)(_renderRate * 0.06));
                            if (c.Kind == "say") OnSamples?.Invoke(all);
                            break;
                        }

                    case "voice":
                        if (_voice != c.Text)
                        {
                            _voice = c.Text;
                            _synth.SelectVoice(c.Text);
                            _cache.Clear();
                            Log.Enqueue("SAPI voice -> " + c.Text);
                        }
                        break;

                    case "rate":
                        if (_rate != c.Num) { _rate = c.Num; _synth.Rate = c.Num; _cache.Clear(); }
                        break;

                    case "pitch":
                        if (_pitchPct != c.Num) { _pitchPct = c.Num; _cache.Clear(); }
                        break;

                    case "shout":
                        if (_shoutPct != c.Num) { _shoutPct = c.Num; _cache.Clear(); }
                        break;

                    case "outrate":
                        if (_renderRate != c.Num)
                        {
                            _renderRate = c.Num;
                            Format = new WaveFormat(c.Num, 16, 1);
                            _cache.Clear();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Enqueue("SAPI error: " + ex.Message);
            }
        }
    }

    byte[] Render(string text)
    {
        using var ms = new MemoryStream();
        _synth.SetOutputToAudioStream(ms,
            new SpeechAudioFormatInfo(_renderRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        _synth.Speak(text);
        _synth.SetOutputToNull();

        byte[] bytes = ms.ToArray();
        if (bytes.Length >= 44 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I')
        {
            var (fmt, pcm) = ParseWav(bytes);
            if (fmt.SampleRate != _renderRate)
                Log.Enqueue($"SAPI voice rendered at {fmt.SampleRate}Hz, requested {_renderRate}Hz");
            return Pcm.Trim(pcm);
        }
        return Pcm.Trim(bytes);
    }

    static (WaveFormat fmt, byte[] pcm) ParseWav(byte[] wav)
    {
        WaveFormat fmt = null!;
        byte[] pcm = Array.Empty<byte>();
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int len = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;
            if (id == "fmt " && fmt == null)
            {
                int channels = BitConverter.ToInt16(wav, body + 2);
                int rate = BitConverter.ToInt32(wav, body + 4);
                int bits = BitConverter.ToInt16(wav, body + 14);
                fmt = new WaveFormat(rate, bits, channels);
            }
            else if (id == "data")
            {
                pcm = new byte[len];
                Array.Copy(wav, body, pcm, 0, len);
            }
            pos = body + len + (len & 1);
        }
        return (fmt, pcm);
    }
}

// Plays whatever the engines render into every ticked output device (usually a
// virtual cable that Discord listens to). Negotiates (rate, channels) once —
// some virtual cables reject mono in shared mode, so stereo is tried too. A
// total failure resets the negotiation so re-ticking outputs retries.
internal sealed class AudioOut : IDisposable
{
    readonly ITts _tts;
    readonly List<WasapiOut> _outs = new();
    readonly List<BufferedWaveProvider> _bufs = new();
    readonly object _lock = new();
    static bool _noDeviceCtor;
    int _negotiatedRate;        // 0 = not negotiated yet
    int _negotiatedChannels = 1;

    static readonly (int rate, int ch)[] Candidates =
    {
        (48000, 1), (48000, 2), (44100, 1), (44100, 2),
        (22050, 1), (22050, 2), (16000, 1), (16000, 2),
        (96000, 1), (96000, 2), (88200, 1), (88200, 2),
        (32000, 1), (32000, 2), (11025, 1), (11025, 2), (8000, 1), (8000, 2),
    };

    public AudioOut(ITts tts)
    {
        _tts = tts;
        _tts.OnSamples = Write;
    }

    public int Rate => _negotiatedRate;

    // sound files enter here — mono 16-bit at the negotiated rate, same pipe as the voice
    public void WriteMono(byte[] pcm) => Write(pcm);

    void Write(byte[] pcm)
    {
        if (_negotiatedChannels == 2 && pcm.Length >= 2)
        {
            // duplicate mono → stereo for devices that insist on it
            var st = new byte[pcm.Length * 2];
            for (int k = 0; k < pcm.Length / 2; k++)
            {
                byte lo = pcm[2 * k], hi = pcm[2 * k + 1];
                st[4 * k] = lo; st[4 * k + 1] = hi;       // left
                st[4 * k + 2] = lo; st[4 * k + 3] = hi;   // right
            }
            pcm = st;
        }
        lock (_lock)
        {
            foreach (var b in _bufs) b.AddSamples(pcm, 0, pcm.Length);
        }
    }

    public void SetDevices(List<MMDevice> devices)
    {
        if (_noDeviceCtor && devices.Count > 1) devices = devices.GetRange(0, 1);
        lock (_lock)
        {
            foreach (var o in _outs) { try { o.Stop(); o.Dispose(); } catch { } }
            _outs.Clear();
            _bufs.Clear();

            if (devices.Count == 0)
            {
                Log.Enqueue("no outputs ticked — words render but go nowhere");
                return;
            }

            if (_negotiatedRate != 0)
            {
                Adopt(BuildAt(devices, _negotiatedRate, _negotiatedChannels), _negotiatedRate, _negotiatedChannels);
                if (_outs.Count == 0)
                {
                    Log.Enqueue("outputs failed at the negotiated format — re-negotiating on next change");
                    _negotiatedRate = 0;
                }
                return;
            }

            foreach (var c in Candidates)   // pass 1: every device must accept it
            {
                var built = BuildAt(devices, c.rate, c.ch);
                if (built.Count == devices.Count) { Adopt(built, c.rate, c.ch); return; }
                Discard(built);
            }
            foreach (var c in Candidates)   // pass 2: keep whatever works
            {
                var built = BuildAt(devices, c.rate, c.ch);
                if (built.Count > 0) { Adopt(built, c.rate, c.ch); return; }
                Discard(built);
            }
            Log.Enqueue("no device accepted any format — untick/re-tick outputs to retry");
        }
    }

    void Adopt(List<(WasapiOut o, BufferedWaveProvider b)> built, int rate, int channels)
    {
        foreach (var (o, b) in built) { _outs.Add(o); _bufs.Add(b); }
        if (_negotiatedRate == 0)
        {
            _negotiatedRate = rate;
            _negotiatedChannels = channels;
            _tts.SetOutputRate(rate);
        }
        Log.Enqueue($"output format: {rate}Hz 16-bit {(channels == 1 ? "mono" : "stereo")}");
    }

    static void Discard(List<(WasapiOut o, BufferedWaveProvider b)> built)
    {
        foreach (var (o, _) in built) { try { o.Stop(); o.Dispose(); } catch { } }
    }

    List<(WasapiOut o, BufferedWaveProvider b)> BuildAt(List<MMDevice> devices, int rate, int channels)
    {
        var built = new List<(WasapiOut, BufferedWaveProvider)>();
        foreach (var d in devices)
        {
            try
            {
                var buf = new BufferedWaveProvider(new WaveFormat(rate, 16, channels))
                {
                    BufferDuration = TimeSpan.FromSeconds(60),
                    ReadFully = true   // keep the stream alive when idle (a live mic, not a jukebox)
                };
                var o = CreateOut(d);
                o.Init(buf);
                o.Play();
                built.Add((o, buf));
            }
            catch (Exception ex)
            {
                Log.Enqueue($"{d.FriendlyName} rejected {rate}Hz {(channels == 1 ? "mono" : "stereo")}: 0x{ex.HResult:X8}");
            }
        }
        return built;
    }

    // Create the right WasapiOut for this NAudio version — the constructor set
    // has changed between releases, so find one that takes an MMDevice.
    WasapiOut CreateOut(MMDevice device)
    {
        foreach (var ctor in typeof(WasapiOut).GetConstructors())
        {
            var ps = ctor.GetParameters();
            if (ps.Length == 0 || ps[0].ParameterType != typeof(MMDevice)) continue;

            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                if (t == typeof(MMDevice)) args[i] = device;
                else if (t == typeof(AudioClientShareMode)) args[i] = AudioClientShareMode.Shared;
                else if (t == typeof(bool)) args[i] = true;
                else args[i] = 30;
            }
            try
            {
                return (WasapiOut)ctor.Invoke(args);
            }
            catch (Exception ex)
            {
                Log.Enqueue("WasapiOut constructor failed: " + ex.Message);
            }
        }

        _noDeviceCtor = true;
        Log.Enqueue("no MMDevice constructor available — falling back to the DEFAULT output device");
        return new WasapiOut(AudioClientShareMode.Shared, true, 30);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var o in _outs) { try { o.Stop(); o.Dispose(); } catch { } }
            _outs.Clear();
            _bufs.Clear();
        }
    }
}