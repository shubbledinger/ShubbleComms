using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;

// Piper — fully offline neural TTS, run as a subprocess. Put piper.exe (plus its
// dlls and espeak-ng-data) and voice models (*.onnx + matching *.onnx.json) in a
// "piper" folder next to the app, then hit "rescan voices".
//
// One long-lived piper process keeps the model loaded, so each utterance only
// costs synthesis time (~100-300ms) instead of a full process + model load
// (~1s). We feed it one line of text at a time and read back one utterance per
// line. Piper builds differ in what they write where, so the output mode is
// probed once and remembered in piper\commspike-pipermode.txt:
//   1. stdout WAV — a sync-scanning reader parses WAVs off stdout, tolerating
//      any junk around them
//   2. --output_dir — piper writes one WAV per line into a private temp dir
//      (both --output-dir and --output_dir spellings are tried)
//   3. per-utterance — spawn piper.exe per phrase; slow but always works
// Delete the remembered-mode file to force a re-probe (e.g. after updating
// piper).
internal sealed class PiperTts : ITts
{
    enum PiperMode { StdoutWav, OutputDir, PerUtterance }

    sealed class Cmd { public string Kind = "", Text = ""; public int Num; }

    readonly BlockingCollection<Cmd> _fast = new();   // say + control commands
    readonly BlockingCollection<Cmd> _slow = new();   // preload (render & cache only)
    readonly string _dir, _exe;
    string[] _voices = Array.Empty<string>();
    string _model = "", _modelName = "";
    int _modelRate = 22050;
    string _modelLang = "";   // e.g. "en-us" — from the model config, for #lang abbreviations
    int _rate = 4;
    int _pitchPct;
    int _shoutPct = 15;
    int _renderRate = 48000;
    readonly Dictionary<string, byte[]> _cache = new();

    PiperProc? _proc;
    bool _warmed;
    PiperMode _mode = PiperMode.StdoutWav;
    string _outDirFlag = "--output-dir";
    bool _triedOutdirRetry;
    int _saysFailed;

    double ShoutPitch => 1.0 + _shoutPct / 100.0;
    double ShoutGain => 1.0 + _shoutPct * 0.03;

    public WaveFormat Format { get; private set; } = new WaveFormat(48000, 16, 1);
    public Action<byte[]>? OnSamples { get; set; }
    public string[] Voices => _voices;
    public string Dir => _dir;

    PiperTts(string dir)
    {
        _dir = dir;
        _exe = Path.Combine(dir, "piper.exe");
        Scan();
        LoadSavedMode();
        new Thread(() =>
        {
            var cols = new[] { _fast, _slow };   // _fast is always drained first
            while (true)
            {
                if (BlockingCollection<Cmd>.TryTakeFromAny(cols, out var c, 500) < 0) continue;
                try { Handle(c); }
                catch (Exception ex) { Log.Enqueue("piper error: " + ex.Message); }
            }
        })
        { IsBackground = true, Name = "piper" }.Start();
    }

    public static string DefaultDir => Path.Combine(AppContext.BaseDirectory, "piper");

    // null = no piper.exe found — SAPI-only until the user installs one and rescans
    public static PiperTts? TryCreate()
    {
        foreach (var dir in new[] { DefaultDir, AppContext.BaseDirectory })
            if (File.Exists(Path.Combine(dir, "piper.exe")))
                return new PiperTts(dir);
        return null;
    }

    void Scan()
    {
        try
        {
            // voice models live in the piper root or, tidier, in piper\voices
            string voices = Path.Combine(_dir, "voices");
            var files = Directory.GetFiles(_dir, "*.onnx");
            if (Directory.Exists(voices))
                files = files.Concat(Directory.GetFiles(voices, "*.onnx")).ToArray();
            _voices = files
                .Select(Path.GetFileName)
                .Where(f => f != null && !f.EndsWith(".onnx.json", StringComparison.Ordinal))
                .Cast<string>()
                .Distinct()
                .ToArray();
        }
        catch (Exception ex) { Log.Enqueue("piper scan failed: " + ex.Message); }

        // failed probes leave wavs in piper's working directory — surface them
        try
        {
            int strays = Directory.GetFiles(_dir, "*.wav").Length;
            if (strays > 0)
                Log.Enqueue($"{strays} stray .wav(s) in the piper folder — safe to delete");
        }
        catch { }
    }

    // model files live in the piper root or in piper\voices — find whichever exists
    string ResolveModel(string fileName)
    {
        string root = Path.Combine(_dir, fileName);
        if (File.Exists(root)) return root;
        string sub = Path.Combine(_dir, "voices", fileName);
        return File.Exists(sub) ? sub : root;
    }

    // remembered output mode for this piper build
    string ModeFile => Path.Combine(_dir, "commspike-pipermode.txt");

    bool LoadSavedMode()
    {
        try
        {
            if (!File.Exists(ModeFile)) return false;
            string s = File.ReadAllText(ModeFile).Trim();
            switch (s)
            {
                case "stdout":
                    _mode = PiperMode.StdoutWav;
                    break;
                case "--output-dir":
                case "--output_dir":
                    _mode = PiperMode.OutputDir;
                    _outDirFlag = s;
                    break;
                case "per_utterance":
                    _mode = PiperMode.PerUtterance;
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    void PersistMode()
    {
        try
        {
            File.WriteAllText(ModeFile, _mode switch
            {
                PiperMode.StdoutWav => "stdout",
                PiperMode.OutputDir => _outDirFlag,
                _ => "per_utterance",
            });
        }
        catch { }
    }

    public void Speak(string text) => _fast.Add(new Cmd { Kind = "say", Text = text });
    public void Preload(string text) => _slow.Add(new Cmd { Kind = "preload", Text = text });
    public void SetVoice(string name) => _fast.Add(new Cmd { Kind = "voice", Text = name });
    public void SetRate(int rate) => _fast.Add(new Cmd { Kind = "rate", Num = rate });
    public void SetPitch(int pct) => _fast.Add(new Cmd { Kind = "pitch", Num = pct });
    public void SetShout(int pct) => _fast.Add(new Cmd { Kind = "shout", Num = pct });
    public void SetOutputRate(int r) => _fast.Add(new Cmd { Kind = "outrate", Num = r });
    public void Rescan() => Scan();

    void Handle(Cmd c)
    {
        switch (c.Kind)
        {
            case "voice":
                if (_modelName != c.Text)
                {
                    _modelName = c.Text;
                    _model = ResolveModel(c.Text);
                    (_modelRate, _modelLang) = ReadModelInfo(_model);
                    _cache.Clear();
                    _proc?.Dispose();
                    _proc = null;
                    _warmed = false;
                    _saysFailed = 0;
                    Log.Enqueue($"piper voice -> {c.Text} ({_modelRate}Hz, {_modelLang})");
                    // pay the model load NOW, in the background — not on the first word
                    if (_mode != PiperMode.PerUtterance) EnsureWarmed();
                }
                break;

            case "rate":
                if (_rate != c.Num) { _rate = c.Num; _cache.Clear(); }
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

            case "say":
            case "preload":
                {
                    var segs = Pcm.Segment(c.Text);
                    if (segs.Count == 0) break;

                    var parts = new List<byte[]>();
                    foreach (var (seg, shout) in segs)
                    {
                        string say = Expansions.Expand(seg, _modelLang, _modelName);
                        if (say.Length == 0) continue;
                        string key = (shout ? "!" : "") + say;
                        if (!_cache.TryGetValue(key, out var pcm))
                        {
                            var (raw, srcRate) = Render(say);
                            if (raw.Length == 0 || srcRate <= 0) continue;   // segment failed — others may still speak
                                                                             // pitch and speed are independent: pitch from the pitch slider
                                                                             // (+ shout's pitch bump), speed from the rate slider alone
                            double pitch = (1.0 + _pitchPct / 100.0) * (shout ? ShoutPitch : 1.0);
                            double speed = RateToSpeed(_rate);
                            pcm = Pcm.PitchShiftSpeed(Pcm.Trim(raw), srcRate, _renderRate, pitch, speed);
                            if (shout) pcm = Pcm.Boost(pcm, ShoutGain);
                            _cache[key] = pcm;
                        }
                        if (pcm.Length > 0) parts.Add(pcm);
                    }
                    if (parts.Count == 0) break;

                    byte[] all = Pcm.Join(parts, (int)(_renderRate * 0.06));
                    if (c.Kind == "say") OnSamples?.Invoke(all);
                    break;
                }
        }
    }

    // ---- rendering ----

    // rate slider to speed factor: +10 → 1.4x faster, -10 → 0.6x
    static double RateToSpeed(int rate) => Math.Clamp(1.0 + rate * 0.04, 0.55, 1.9);

    (byte[] pcm, int rate) Render(string text)
    {
        if (_mode != PiperMode.PerUtterance)
        {
            var f = RenderPersistent(text);
            if (f != null) return (f.Value.pcm, f.Value.rate);

            bool flipped = _mode == PiperMode.PerUtterance;   // may have given up mid-attempt
            if (!flipped && ++_saysFailed >= 3)
            {
                _mode = PiperMode.PerUtterance;
                flipped = true;
                PersistMode();
                Log.Enqueue("piper persistent mode failing repeatedly — falling back to per-utterance");
            }
            if (flipped)
            {
                var once = RunPiperOnce(text);   // don't drop the utterance we already failed twice
                return (once, _modelRate);
            }
            return (Array.Empty<byte>(), 0);
        }
        var b = RunPiperOnce(text);
        return (b, _modelRate);
    }

    (byte[] pcm, int rate, int ch)? RenderPersistent(string text)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!EnsureWarmed()) return null;
            if (_proc == null) return null;

            if (!_proc.WriteLine(text))
            {
                Log.Enqueue("piper stdin pipe broke — respawning");
                _proc.Dispose(); _proc = null; _warmed = false;
                continue;
            }

            var frame = _proc.WaitFrame(15000);
            if (frame != null)
            {
                _saysFailed = 0;
                var (pcm, rate, ch) = frame.Value;
                return (FoldChannels(pcm, ch), rate, 1);
            }

            if (_proc.Malformed)
            {
                string junk = _proc.JunkReport();
                Log.Enqueue("piper stream went bad mid-session — respawning" +
                            (junk.Length > 0 ? " (" + junk + ")" : ""));
                _proc.Dispose(); _proc = null; _warmed = false;
                _mode = PiperMode.PerUtterance;
                PersistMode();
                return null;
            }
            Log.Enqueue("piper produced no audio for a line — respawning");
            _proc.Dispose(); _proc = null; _warmed = false;
        }
        return null;
    }

    // Walk the strategy ladder until one rung warms up. The rung that works is
    // remembered so restarts skip straight to it.
    bool EnsureWarmed()
    {
        if (_warmed && _proc != null && !_proc.IsDead) return true;
        if (_model.Length == 0 || !File.Exists(_model)) return false;

        for (int round = 0; round < 5; round++)
        {
            _proc?.Dispose();
            _proc = null;
            _warmed = false;

            var sw = Stopwatch.StartNew();

            if (_mode == PiperMode.StdoutWav)
            {
                var proc = PiperProc.StartStdout(_exe, _dir, _model);
                if (proc == null) return false;
                _proc = proc;
                proc.WriteLine("warm up");
                var frame = proc.WaitFrame(12000);
                if (frame != null)
                {
                    _warmed = true;
                    _triedOutdirRetry = false;
                    PersistMode();
                    Log.Enqueue($"piper ready (stdout WAV mode) — model load+warm {sw.ElapsedMilliseconds}ms");
                    return true;
                }
                string junk = proc.JunkReport();
                Log.Enqueue((proc.Malformed
                        ? "piper stdout never produced a WAV frame" + (junk.Length > 0 ? " — " + junk : "")
                        : $"piper stdout warmup failed ({(proc.IsDead ? "process died" : "timeout")})" +
                          (junk.Length > 0 ? " — " + junk : "")) + " — trying --output_dir mode");
                _mode = PiperMode.OutputDir;
                continue;
            }

            if (_mode == PiperMode.OutputDir)
            {
                var proc = PiperProc.StartOutputDir(_exe, _dir, _model, _outDirFlag);
                if (proc == null)
                {
                    Log.Enqueue("piper spawn failed in output-dir mode — falling back to per-utterance");
                    _mode = PiperMode.PerUtterance;
                    PersistMode();
                    continue;
                }
                _proc = proc;
                proc.WriteLine("warm up");
                var frame = proc.WaitFrame(12000);
                if (frame != null)
                {
                    _warmed = true;
                    _triedOutdirRetry = false;
                    PersistMode();
                    Log.Enqueue($"piper ready ({_outDirFlag}) — model load+warm {sw.ElapsedMilliseconds}ms");
                    return true;
                }
                if (!_triedOutdirRetry)
                {
                    _triedOutdirRetry = true;
                    if (_outDirFlag == "--output-dir")
                    {
                        // flag spellings vary between piper builds — try the other one
                        _outDirFlag = "--output_dir";
                        Log.Enqueue("piper output-dir warmup failed — retrying with --output_dir");
                    }
                    else
                    {
                        Log.Enqueue("piper output-dir warmup failed — retrying once");
                    }
                    continue;
                }
                Log.Enqueue("piper --output_dir produced no files — falling back to per-utterance");
                _mode = PiperMode.PerUtterance;
                PersistMode();
                continue;
            }

            break;   // per-utterance: nothing to warm
        }
        return false;
    }

    // piper is mono; if a model ever isn't, fold it down
    static byte[] FoldChannels(byte[] pcm, int ch)
    {
        if (ch <= 1) return pcm;
        int frames = pcm.Length / 2 / ch;
        var res = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            int sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int idx = (i * ch + c) * 2;
                sum += (short)(pcm[idx] | (pcm[idx + 1] << 8));
            }
            short v = (short)(sum / ch);
            res[2 * i] = (byte)(v & 0xFF);
            res[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return res;
    }

    // ---- fallback path: one process per utterance ----

    byte[] RunPiperOnce(string text)
    {
        if (_model.Length == 0 || !File.Exists(_model))
        {
            Log.Enqueue("no piper model selected");
            return Array.Empty<byte>();
        }
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            Arguments = $"--model \"{_model}\" --output-raw",
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) { Log.Enqueue("failed to start piper.exe"); return Array.Empty<byte>(); }

        var errTask = p.StandardError.ReadToEndAsync();   // drain first: no pipe deadlock
        p.StandardInput.Write(text);
        p.StandardInput.Write('\n');
        p.StandardInput.Close();

        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);

        if (!p.WaitForExit(20000))
        {
            try { p.Kill(); } catch { }
            Log.Enqueue("piper timed out after 20s");
            return Array.Empty<byte>();
        }
        if (p.ExitCode != 0)
        {
            string err = errTask.IsCompleted ? errTask.Result : "";
            Log.Enqueue($"piper exited {p.ExitCode}: {err.Trim()}");
            return Array.Empty<byte>();
        }
        return ms.ToArray();   // int16 mono @ model rate
    }

    // Sample rate and language from the model's .onnx.json: audio.sample_rate
    // and espeak.voice (e.g. "en-us"). Language falls back to the filename
    // prefix ("en_US-lessac-medium.onnx" -> "en-US"), then to unknown.
    static (int rate, string lang) ReadModelInfo(string model)
    {
        int rate = 22050;   // the common default
        string lang = "";
        try
        {
            string cfg = model + ".json";
            if (File.Exists(cfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("audio", out var audio) &&
                    audio.TryGetProperty("sample_rate", out var sr))
                    rate = sr.GetInt32();
                if (doc.RootElement.TryGetProperty("espeak", out var espeak) &&
                    espeak.TryGetProperty("voice", out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    lang = v.GetString() ?? "";
            }
        }
        catch { }

        if (lang.Length == 0)
        {
            string name = Path.GetFileNameWithoutExtension(model);
            int dash = name.IndexOf('-');
            if (dash > 0) lang = name[..dash].Replace('_', '-');
        }
        return (rate, lang);
    }
}

// One long-lived piper.exe. We own its stdin (exactly one line in flight at a
// time). Frame source, fixed at spawn:
//   stdout mode — a sync-scanning reader parses one complete WAV per line off
//   stdout, skipping (and previewing) any junk around the audio
//   file mode — piper writes WAVs into a private output dir; we poll for the new
//   file. stdout is still drained so the pipe can never fill and wedge piper.
// stderr is drained continuously either way. Multi-sentence lines (a sentence
// ender followed by more words) get a short settle window and a join, since
// some builds write one file per sentence.
sealed class PiperProc : IDisposable
{
    readonly Process _p;
    readonly StreamWriter _stdin;
    readonly bool _fileMode;
    readonly string _outDir = "";
    readonly BlockingCollection<(byte[] pcm, int rate, int ch)> _frames = new();
    readonly ManualResetEvent _dead = new(false);
    WavSyncReader? _sync;        // stdout mode only
    string[]? _fileBaseline;     // file mode: wavs present before the current line
    bool _multiFilePossible;     // set per line: 2+ sentences → maybe multiple files

    public volatile bool Malformed;

    PiperProc(Process p, bool fileMode, string outDir)
    {
        _p = p;
        _fileMode = fileMode;
        _outDir = outDir;
        _stdin = p.StandardInput;
        new Thread(ReadStdout) { IsBackground = true, Name = "piper-reader" }.Start();
        new Thread(DrainStderr) { IsBackground = true, Name = "piper-stderr" }.Start();
    }

    public static PiperProc? StartStdout(string exe, string dir, string model) =>
        Start(exe, dir, $"--model \"{model}\"", fileMode: false, "");

    public static PiperProc? StartOutputDir(string exe, string dir, string model, string flag)
    {
        string outDir = Path.Combine(Path.GetTempPath(), "ShubbleComms-piper");
        try
        {
            Directory.CreateDirectory(outDir);
            foreach (var f in Directory.GetFiles(outDir, "*.wav"))
                try { File.Delete(f); } catch { }
        }
        catch { }
        return Start(exe, dir, $"--model \"{model}\" {flag} \"{outDir}\"", fileMode: true, outDir);
    }

    static PiperProc? Start(string exe, string dir, string args, bool fileMode, string outDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = dir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            if (p == null) return null;
            return new PiperProc(p, fileMode, outDir);
        }
        catch (Exception ex)
        {
            Log.Enqueue("piper start failed: " + ex.Message);
            return null;
        }
    }

    public bool IsDead => _dead.WaitOne(0);

    public string JunkReport() => _sync?.Report() ?? "";

    // hand piper one utterance. '\n' is written by hand — WriteLine would append
    // \r\n and the trailing \r ends up inside the text piper phonemizes
    public bool WriteLine(string text)
    {
        try
        {
            if (_fileMode)
                _fileBaseline = Directory.GetFiles(_outDir, "*.wav");
            _multiFilePossible = MultiSentence(text);
            _stdin.Write(text);
            _stdin.Write('\n');
            _stdin.Flush();
            return true;
        }
        catch { return false; }
    }

    // a sentence-ender with more non-space text after it = possibly 2+ sentences
    static bool MultiSentence(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            for (int j = i + 1; j < text.Length; j++)
                if (!char.IsWhiteSpace(text[j])) return true;
        }
        return false;
    }

    public (byte[] pcm, int rate, int ch)? WaitFrame(int timeoutMs)
    {
        if (!_fileMode)
        {
            (byte[] pcm, int rate, int ch) f;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (_frames.TryTake(out f, 100)) break;   // wakes the moment a frame lands
                if (_dead.WaitOne(0))
                {
                    if (!_frames.TryTake(out f, 0)) return null;   // a fully-read frame still counts
                    break;
                }
                if (sw.ElapsedMilliseconds > timeoutMs) return null;
            }

            // multi-sentence line: this build may emit one WAV per sentence —
            // consume the siblings NOW, or the next line inherits a stale frame
            // and its audio gets cached under the wrong text
            if (_multiFilePossible)
            {
                Thread.Sleep(100);
                var parts = new List<(byte[] pcm, int rate, int ch)> { f };
                while (_frames.TryTake(out var more, 0)) parts.Add(more);
                if (parts.Count > 1)
                    return Concat(parts);
            }
            return f;
        }
        return WaitFileFrame(timeoutMs);
    }

    void ReadStdout()
    {
        var s = _p.StandardOutput.BaseStream;
        if (_fileMode)
        {
            // drain only — the audio lands in files; draining keeps the pipe
            // from ever filling up and wedging piper
            try
            {
                var scratch = new byte[8192];
                while (s.Read(scratch, 0, scratch.Length) > 0) { }
            }
            catch { }
            finally { _dead.Set(); }
            return;
        }

        _sync = new WavSyncReader(s);
        try
        {
            while (true)
            {
                var (ok, frame) = _sync.ReadFrame();
                if (frame != null) { _frames.Add(frame.Value); continue; }
                if (!ok) Malformed = true;
                break;
            }
        }
        catch { }
        finally
        {
            try { _frames.CompleteAdding(); } catch { }
            _dead.Set();
        }
    }

    // ---- file mode ----

    (byte[] pcm, int rate, int ch)? WaitFileFrame(int timeoutMs)
    {
        var seen = new HashSet<string>(_fileBaseline ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var found = CollectFrames(seen);
            if (found.Count > 0) return Finish(found, seen);
            if (IsDead) return Finish(CollectFrames(seen), seen);   // last look — files can be fully written
            Thread.Sleep(15);
        }
        return Finish(CollectFrames(seen), seen);   // one more after the deadline — files can land late
    }

    (byte[] pcm, int rate, int ch)? Finish(List<FileFrame> found, HashSet<string> seen)
    {
        if (found.Count == 0) return null;
        if (_multiFilePossible)
        {
            // 2+ sentences and this build may write one wav per sentence — give
            // siblings a moment to land so none get silently dropped
            Thread.Sleep(150);
            found.AddRange(CollectFrames(seen));
        }
        if (found.Count == 1) return (found[0].Pcm, found[0].Rate, found[0].Ch);

        found.Sort((a, b) => a.StampUtc != b.StampUtc
            ? a.StampUtc.CompareTo(b.StampUtc)
            : string.CompareOrdinal(a.Name, b.Name));
        return Concat(found.Select(x => (x.Pcm, x.Rate, x.Ch)).ToList());
    }

    static (byte[] pcm, int rate, int ch) Concat(List<(byte[] pcm, int rate, int ch)> parts)
    {
        long total = 0;
        foreach (var p in parts) total += p.pcm.Length;
        var pcm = new byte[total];
        int pos = 0;
        foreach (var p in parts) { Array.Copy(p.pcm, 0, pcm, pos, p.pcm.Length); pos += p.pcm.Length; }
        return (pcm, parts[0].rate, parts[0].ch);
    }

    readonly record struct FileFrame(byte[] Pcm, int Rate, int Ch, DateTime StampUtc, string Name);

    List<FileFrame> CollectFrames(HashSet<string> seen)
    {
        var list = new List<FileFrame>();
        try
        {
            foreach (var f in Directory.GetFiles(_outDir, "*.wav"))
            {
                if (seen.Contains(f)) continue;
                var frame = TryReadWavFile(f);
                if (frame == null) continue;   // incomplete (still being written) — retried next sweep
                DateTime stamp;
                try { stamp = File.GetLastWriteTimeUtc(f); } catch { stamp = DateTime.UtcNow; }
                try { File.Delete(f); } catch { }
                list.Add(new FileFrame(frame.Value.pcm, frame.Value.rate, frame.Value.ch, stamp, Path.GetFileName(f)));
            }
        }
        catch { }
        return list;
    }

    // a file counts as a frame only when it parses as a COMPLETE WAV — that's
    // what makes half-written files safe to skip and retry
    static (byte[] pcm, int rate, int ch)? TryReadWavFile(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 44) return null;
            if (Ascii(b, 0, 4) != "RIFF" || Ascii(b, 8, 4) != "WAVE") return null;
            int pos = 12, rate = 0, ch = 1, bits = 16;
            while (pos + 8 <= b.Length)
            {
                string id = Ascii(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "data")
                {
                    if (rate == 0 || bits != 16) return null;
                    if (body + len > b.Length) return null;       // still being written
                    var pcm = new byte[len];
                    Array.Copy(b, body, pcm, 0, len);
                    return (pcm, rate, ch);
                }
                if (id == "fmt " && len >= 16)
                {
                    ch = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                pos = body + len + (len & 1);
            }
            return null;
        }
        catch { return null; }
    }

    static string Ascii(byte[] b, int off, int len) => Encoding.ASCII.GetString(b, off, len);

    // keep the error pipe empty (a full pipe would deadlock piper); keep the
    // interesting lines for the log
    void DrainStderr()
    {
        try
        {
            while (true)
            {
                string? line = _p.StandardError.ReadLine();
                if (line == null) break;
                if (line.Length > 0 &&
                    (line.Contains("load", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("fail", StringComparison.OrdinalIgnoreCase)))
                    Log.Enqueue("piper: " + line.Trim());
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { if (!_p.HasExited) _p.Kill(); } catch { }
        try { _stdin.Dispose(); } catch { }
        try { _p.Dispose(); } catch { }
        _dead.Set();
    }
}

// Tolerant stdout reader: scans for the next "RIFF...WAVE" sync marker and skips
// (while previewing) whatever sits between frames — some piper builds interleave
// log text with the audio, or prefix it. If the stream never syncs, that's
// reported so the owner can fall back to another output mode.
sealed class WavSyncReader
{
    readonly Stream _s;
    readonly MemoryStream _pending = new();
    readonly byte[] _scratch = new byte[8192];
    int _start;                 // valid bytes are [_start, _pending.Length)
    int _junk;
    readonly byte[] _preview = new byte[64];
    int _previewLen;

    public WavSyncReader(Stream s) { _s = s; }

    // (ok, frame): frame = one complete WAV; (true, null) = clean end of stream;
    // (false, null) = bytes arrived that never became a WAV — wrong output mode
    public (bool ok, (byte[] pcm, int rate, int ch)? frame) ReadFrame()
    {
        if (!Sync()) return (_junk > 0, null);

        int pos = _start + 12;
        int rate = 0, ch = 1, bits = 16;
        while (true)
        {
            if (!Ensure(pos + 8)) return (true, null);
            byte[] b = _pending.GetBuffer();
            string id = Ascii(b, pos, 4);
            int len = BitConverter.ToInt32(b, pos + 4);
            if (id == "data")
            {
                if (rate == 0 || bits != 16) return (false, null);
                if (!Ensure(pos + 8 + len)) return (true, null);   // died mid-frame — not a framing problem
                b = _pending.GetBuffer();
                var pcm = new byte[len];
                Array.Copy(b, pos + 8, pcm, 0, len);
                _start = pos + 8 + len + (len & 1);
                Compact();
                return (true, (pcm, rate, ch));
            }
            if (!Ensure(pos + 8 + len + (len & 1))) return (true, null);
            b = _pending.GetBuffer();
            if (id == "fmt " && len >= 16)
            {
                ch = BitConverter.ToInt16(b, pos + 10);
                rate = BitConverter.ToInt32(b, pos + 12);
                bits = BitConverter.ToInt16(b, pos + 22);
            }
            pos += 8 + len + (len & 1);
        }
    }

    // position at the next RIFF....WAVE marker; false = EOF first
    bool Sync()
    {
        while (true)
        {
            byte[] b = _pending.GetBuffer();
            int len = (int)_pending.Length;
            for (int i = _start; i + 12 <= len; i++)
            {
                if (b[i] == (byte)'R' && b[i + 1] == (byte)'I' && b[i + 2] == (byte)'F' && b[i + 3] == (byte)'F' &&
                    b[i + 8] == (byte)'W' && b[i + 9] == (byte)'A' && b[i + 10] == (byte)'V' && b[i + 11] == (byte)'E')
                {
                    if (i > _start) TakeJunk(i - _start);
                    return true;
                }
            }
            // no sync yet: only the last 11 bytes could be a partial marker — junk the rest
            int keep = Math.Min(11, len - _start);
            if (len - _start - keep > 0) TakeJunk(len - _start - keep);
            int r = _s.Read(_scratch, 0, _scratch.Length);
            if (r <= 0) return false;
            _pending.Write(_scratch, 0, r);
        }
    }

    bool Ensure(int need)
    {
        while (_pending.Length < need)
        {
            int r = _s.Read(_scratch, 0, _scratch.Length);
            if (r <= 0) return false;
            _pending.Write(_scratch, 0, r);
        }
        return true;
    }

    void TakeJunk(int n)
    {
        byte[] b = _pending.GetBuffer();
        for (int i = 0; i < n && _previewLen < _preview.Length; i++)
            _preview[_previewLen++] = b[_start + i];
        _junk += n;
        _start += n;
    }

    void Compact()
    {
        byte[] b = _pending.GetBuffer();
        int len = (int)_pending.Length;
        int rem = len - _start;
        if (_start > 0 && rem > 0)
            Array.Copy(b, _start, b, 0, rem);   // Array.Copy handles overlap
        _pending.SetLength(Math.Max(0, rem));
        _start = 0;
    }

    public string Report()
    {
        if (_junk == 0) return "";
        var hex = new StringBuilder();
        var txt = new StringBuilder();
        for (int i = 0; i < _previewLen; i++)
        {
            hex.Append(_preview[i].ToString("X2")).Append(' ');
            char c = (char)_preview[i];
            txt.Append(c is >= ' ' and < (char)127 ? c : '.');
        }
        return $"non-WAV bytes on stdout: {_junk} — first {_previewLen}: hex[ {hex}] text[ {txt}]";
    }

    static string Ascii(byte[] b, int off, int len) => Encoding.ASCII.GetString(b, off, len);
}

// One ITts face over SAPI + piper. Voice names carry an engine prefix so a
// single dropdown picks the engine: "sapi — Microsoft Zira", "piper — glados".
internal sealed class TtsRouter : ITts
{
    const string SapiPrefix = "sapi — ", PiperPrefix = "piper — ";

    readonly SapiTts _sapi;
    PiperTts? _piper;
    ITts _cur;
    Action<byte[]>? _onSamples;
    int _rate = 4, _pitchPct, _shoutPct = 15, _renderRate;

    public TtsRouter(SapiTts sapi, PiperTts? piper)
    {
        _sapi = sapi;
        _piper = piper;
        _cur = sapi;
    }

    public WaveFormat Format => _cur.Format;
    public Action<byte[]>? OnSamples
    {
        get => _onSamples;
        set { _onSamples = value; _cur.OnSamples = value; }
    }

    public string[] Voices
    {
        get
        {
            var list = _sapi.Voices.Select(v => SapiPrefix + v).ToList();
            list.AddRange((_piper?.Voices ?? Array.Empty<string>()).Select(v => PiperPrefix + v));
            return list.ToArray();
        }
    }

    public void SetVoice(string name)
    {
        if (name.StartsWith(PiperPrefix, StringComparison.Ordinal))
        {
            string model = name[PiperPrefix.Length..];
            if (_piper == null)
            {
                _piper = PiperTts.TryCreate();   // maybe it appeared since startup
                if (_piper == null)
                {
                    Log.Enqueue("piper: piper.exe not found — it belongs in a 'piper' folder next to the app (see the README)");
                    return;
                }
            }
            if (_cur != _piper) Switch(_piper);
            _piper.SetVoice(model);
        }
        else if (name.StartsWith(SapiPrefix, StringComparison.Ordinal))
        {
            if (_cur != _sapi) Switch(_sapi);
            _sapi.SetVoice(name[SapiPrefix.Length..]);
        }
    }

    public void SetRate(int rate) { _rate = rate; _cur.SetRate(rate); }
    public void SetPitch(int pct) { _pitchPct = pct; _cur.SetPitch(pct); }
    public void SetShout(int pct) { _shoutPct = pct; _cur.SetShout(pct); }
    public void SetOutputRate(int rate) { _renderRate = rate; _cur.SetOutputRate(rate); }
    public void Speak(string text) => _cur.Speak(text);
    public void Preload(string text) => _cur.Preload(text);

    public void Rescan()
    {
        _piper ??= PiperTts.TryCreate();
        _piper?.Rescan();
    }

    // engine switch: carry the live settings over and rewire the audio pipe
    void Switch(ITts engine)
    {
        _cur.OnSamples = null;
        _cur = engine;
        engine.OnSamples = _onSamples;
        engine.SetRate(_rate);
        engine.SetPitch(_pitchPct);
        engine.SetShout(_shoutPct);
        if (_renderRate > 0) engine.SetOutputRate(_renderRate);
        Log.Enqueue($"tts engine -> {(engine == _sapi ? "sapi" : "piper")}");
    }
}