using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;

// Sound-file playback for the radial soundboard. Decodes anything NAudio can
// read (wav/mp3/m4a/aac/wma/aif), folds to mono, resamples to the negotiated
// output rate and pushes the PCM through the same AudioOut pipe the voice uses —
// so files land in the same virtual cables, same routing. Playback is queued:
// sounds line up behind each other and behind spoken words, they never overlap.
internal sealed class SoundFiles
{
    readonly AudioOut _out;
    readonly BlockingCollection<string> _q = new();
    readonly Dictionary<string, (int rate, byte[] pcm)> _cache = new();

    public SoundFiles(AudioOut audioOut)
    {
        _out = audioOut;
        new Thread(Run) { IsBackground = true, Name = "sndfiles" }.Start();
    }

    // same entry point for both: decode once and cache; Play also writes it out
    public void Play(string path) => _q.Add(path);
    public void Preload(string path) => _q.Add(path);

    void Run()
    {
        foreach (var path in _q.GetConsumingEnumerable())
        {
            try
            {
                int rate = _out.Rate > 0 ? _out.Rate : 48000;
                if (!_cache.TryGetValue(path, out var hit) || hit.rate != rate)
                {
                    var sw = Stopwatch.StartNew();
                    hit = (rate, Decode(path, rate));
                    _cache[path] = hit;
                    Log.Enqueue($"decoded {Path.GetFileName(path)} @ {rate}Hz ({sw.ElapsedMilliseconds}ms)");
                }
                if (hit.pcm.Length > 0)
                    _out.WriteMono(hit.pcm);
            }
            catch (Exception ex)
            {
                Log.Enqueue($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    // no silence-trim here — a file's leading silence is usually intentional.
    // Resampling goes through the same Pcm.ResampleLinear everything else uses.
    static byte[] Decode(string path, int rate)
    {
        using var reader = new AudioFileReader(path);   // float32, any rate/channels
        ISampleProvider sp = reader;
        if (sp.WaveFormat.Channels > 1) sp = new FoldToMono(sp);

        // float -> 16-bit at the file's OWN rate first...
        int srcRate = sp.WaveFormat.SampleRate;
        using var ms = new MemoryStream();
        var chunk = new float[16384];
        var bytes = new byte[chunk.Length * 2];
        int n;
        while ((n = sp.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int v = (int)(Math.Clamp(chunk[i], -1f, 1f) * 32767f);
                bytes[2 * i] = (byte)(v & 0xFF);
                bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
            }
            ms.Write(bytes, 0, n * 2);
        }

        // ...then the shared resampler (ratio = in/out: srcRate -> rate needs
        // outN = N * rate/srcRate samples, and ResampleLinear's outN = N/ratio)
        return Pcm.ResampleLinear(ms.ToArray(), srcRate / (double)rate);
    }
}

// stereo (or more) → mono: average the channels. Decode is offline, so the
// per-read scratch allocation doesn't matter.
sealed class FoldToMono : ISampleProvider
{
    readonly ISampleProvider _src;
    public WaveFormat WaveFormat { get; }

    public FoldToMono(ISampleProvider src)
    {
        _src = src;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = _src.WaveFormat.Channels;
        var scratch = new float[count * ch];
        int got = _src.Read(scratch, 0, count * ch);
        int frames = got / ch;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += scratch[f * ch + c];
            buffer[offset + f] = sum / ch;
        }
        return frames;
    }
}