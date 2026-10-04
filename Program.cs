using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.ThreadException += (s, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash((Exception)e.ExceptionObject);

        try
        {
            ApplicationConfiguration.Initialize();

            var sapi = new SapiTts();
            var tts = new TtsRouter(sapi, PiperTts.TryCreate());
            using var audio = new AudioOut(tts);
            using var router = new KeyboardRouter(tts);
            Application.Run(new SettingsForm(router, tts, audio));
        }
        catch (Exception ex) { Crash(ex); }
    }

    static void Crash(Exception ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "crash.txt");
            File.WriteAllText(path, ex + "\n\n--- recent log ---\n" + Log.Dump());
            MessageBox.Show(ex + "\n\n(also saved to " + path + ")",
                "ShubbleComms crashed — attach crash.txt when reporting",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}

// Where the user's files live (settings.json, expansions.txt, soundboard.txt,
// history.json). Migrates the old CommsSpike folder once so nothing is lost.
internal static class AppData
{
    public static string Folder { get; } = Init();

    static string Init()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string newPath = Path.Combine(appData, "ShubbleComms");
        string oldPath = Path.Combine(appData, "CommsSpike");
        try
        {
            if (!File.Exists(Path.Combine(newPath, "settings.json")) && Directory.Exists(oldPath))
            {
                Directory.CreateDirectory(newPath);
                foreach (string name in new[] { "settings.json", "abbreviations.txt", "expansions.txt", "soundboard.txt", "history.json" })
                {
                    string src = Path.Combine(oldPath, name);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(newPath, name), true);
                }
            }
            Directory.CreateDirectory(newPath);
        }
        catch { }
        return newPath;
    }
}

// Small in-memory log. Threads can call Enqueue freely; the last 200 lines end
// up in crash.txt when the app dies. Nothing displays it live.
internal static class Log
{
    const int Cap = 200;
    static readonly object Gate = new();
    static readonly Queue<string> Lines = new();

    public static void Enqueue(string line)
    {
        lock (Gate)
        {
            Lines.Enqueue(line);
            while (Lines.Count > Cap) Lines.Dequeue();
        }
    }

    public static string Dump()
    {
        lock (Gate) return string.Join(Environment.NewLine, Lines);
    }
}

// Everything persisted between runs. The property names are the JSON keys in
// settings.json — renaming one silently resets that setting for existing users.
internal sealed class Settings
{
    public string? Voice { get; set; }
    public int Rate { get; set; } = 4;
    public int Shout { get; set; } = 15;
    public int Pitch { get; set; }
    public bool StreamWords { get; set; } = true;
    public bool FlushOnClose { get; set; } = true;
    public bool Tier1 { get; set; }
    public bool RadialToggle { get; set; }
    public List<string> Outputs { get; set; } = new();
    public string Theme { get; set; } = "Dark";
    public List<int> BindTyping { get; set; } = new() { 0x70, 0, 0, 0 };
    public List<int> BindStream { get; set; } = new() { 0x45, 1, 0, 0 };
    public List<int> BindRadial { get; set; } = new() { 0x04, 1, 0, 0 };   // Alt+MMB
    public List<int> BindAddWord { get; set; } = new() { 0x44, 0, 1, 0 };  // Ctrl+D
    public bool HelpSuggest { get; set; } = true;
    public bool HelpCorrect { get; set; } = true;
    public bool HelpPredict { get; set; } = true;
}

internal sealed class SettingsForm : Form
{
    readonly KeyboardRouter _router;
    readonly ITts _tts;
    readonly AudioOut _audio;

    NotifyIcon _tray = null!;
    bool _exiting, _allowVisible, _loading;
    Button? _recTarget;
    string? _recSlot;
    long _recArm;

    readonly ComboBox _voice = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 300,
        DropDownWidth = 600,
        ForeColor = Color.Black
    };
    readonly TrackBar _rate = new()
    {
        Minimum = -10,
        Maximum = 10,
        Width = 120,
        Height = 26,
        TickStyle = TickStyle.None
    };
    readonly Label _rateLabel = new() { AutoSize = true, ForeColor = Color.White };
    readonly TrackBar _pitch = new()
    {
        Minimum = -10,
        Maximum = 10,
        Width = 110,
        Height = 26,
        TickStyle = TickStyle.None
    };
    readonly Label _pitchLabel = new() { AutoSize = true, ForeColor = Color.White };
    readonly TrackBar _shout = new()
    {
        Minimum = 0,
        Maximum = 50,
        Width = 110,
        Height = 26,
        TickStyle = TickStyle.None
    };
    readonly Label _shoutLabel = new() { AutoSize = true, ForeColor = Color.White };

    readonly CheckedListBox _outputs = new()
    {
        CheckOnClick = true,
        Width = 560,
        Height = 88,
        BackColor = Color.FromArgb(30, 30, 30),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.None,
        Margin = new Padding(6, 2, 6, 4)
    };
    readonly ComboBox _theme = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 110,
        ForeColor = Color.Black
    };

    // bind buttons double as the display: the button shows the current bind.
    // LMB = re-record, MMB/RMB = clear, Esc while recording = clear.
    readonly Button _typingBind, _streamBind, _radialBind, _addWordBind;

    readonly CheckBox _streamWords, _releaseKeys, _tier1, _radialToggle;
    readonly CheckBox _helpSuggest, _helpCorrect, _helpPredict;

    readonly TypingOverlay? _typingOverlay;
    readonly RadialOverlay? _radialOverlay;

    List<MMDevice> _devices = new();
    bool _populating;
    Settings _settings = new();

    static string SettingsPath => Path.Combine(AppData.Folder, "settings.json");
    static string HistoryPath => Path.Combine(AppData.Folder, "history.json");

    public SettingsForm(KeyboardRouter router, ITts tts, AudioOut audio)
    {
        _router = router; _tts = tts; _audio = audio;
        _typingBind = BindButton();
        _streamBind = BindButton();
        _radialBind = BindButton();
        _addWordBind = BindButton();

        _streamWords = new CheckBox { Text = "stream words", AutoSize = true, ForeColor = Color.White };
        _releaseKeys = new CheckBox { Text = "release keys on close", AutoSize = true, ForeColor = Color.White, Checked = true };
        _tier1 = new CheckBox { Text = "Tier1 (experimental)", AutoSize = true, ForeColor = Color.White };
        _radialToggle = new CheckBox { Text = "radial toggle mode", AutoSize = true, ForeColor = Color.White };
        _helpSuggest = new CheckBox { Text = "suggestions", AutoSize = true, ForeColor = Color.White };
        _helpCorrect = new CheckBox { Text = "autocorrect", AutoSize = true, ForeColor = Color.White };
        _helpPredict = new CheckBox { Text = "predict next word", AutoSize = true, ForeColor = Color.White };

        Text = "ShubbleComms";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 780; Height = 740;
        BackColor = Color.FromArgb(24, 24, 24);
        Font = new Font("Segoe UI", 9f);

        bool freshInstall = !File.Exists(SettingsPath);
        try
        {
            if (!freshInstall)
                _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Enqueue("settings load failed: " + ex.Message); }

        // sent-message history (Up/Down in typing mode cycles through it)
        try
        {
            if (File.Exists(HistoryPath))
            {
                var messages = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(HistoryPath));
                if (messages != null) _router.LoadHistory(messages);
            }
        }
        catch (Exception ex) { Log.Enqueue("history load failed: " + ex.Message); }

        // sections stack top-to-bottom; the panel scrolls if the window is small
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoScroll = true,
            ColumnCount = 1,
            Padding = new Padding(6)
        };
        stack.Controls.Add(VoiceGroup());
        stack.Controls.Add(OutputsGroup());
        stack.Controls.Add(BindsGroup());
        stack.Controls.Add(TypingGroup());
        stack.Controls.Add(FilesGroup());
        stack.Controls.Add(ThemeGroup());

        Controls.Add(stack);

        _loading = true;

        _voice.SelectedIndexChanged += (s, e) => { _tts.SetVoice((string)_voice.SelectedItem!); Save(); };
        RebuildVoices();

        _rate.ValueChanged += (s, e) => { _rateLabel.Text = "rate " + _rate.Value; _tts.SetRate(_rate.Value); Save(); };
        _rate.Value = Math.Clamp(_settings.Rate, -10, 10);
        _rateLabel.Text = "rate " + _rate.Value;

        _pitch.ValueChanged += (s, e) => { _tts.SetPitch(_pitch.Value * 5); UpdatePitchLabel(); Save(); };
        _pitch.Value = Math.Clamp(_settings.Pitch / 5, -10, 10);
        UpdatePitchLabel();

        _shout.ValueChanged += (s, e) => { _tts.SetShout(_shout.Value); UpdateShoutLabel(); Save(); };
        _shout.Value = Math.Clamp(_settings.Shout, 0, 50);
        UpdateShoutLabel();

        _streamWords.CheckedChanged += (s, e) =>
        {
            if (_router.StreamWords != _streamWords.Checked) _router.SetStreamWords(_streamWords.Checked);
            Save();
        };
        _releaseKeys.CheckedChanged += (s, e) => { _router.FlushOnClose = _releaseKeys.Checked; Save(); };
        _tier1.CheckedChanged += (s, e) => { _router.Tier1HoldAssert = _tier1.Checked; Save(); };
        _radialToggle.CheckedChanged += (s, e) => { _router.RadialToggle = _radialToggle.Checked; Save(); };
        _helpSuggest.CheckedChanged += (s, e) => { _router.SuggestOn = _helpSuggest.Checked; Save(); };
        _helpCorrect.CheckedChanged += (s, e) => { _router.CorrectOn = _helpCorrect.Checked; Save(); };
        _helpPredict.CheckedChanged += (s, e) => { _router.PredictOn = _helpPredict.Checked; Save(); };

        _streamWords.Checked = _settings.StreamWords;
        _releaseKeys.Checked = _settings.FlushOnClose;
        _tier1.Checked = _settings.Tier1;
        _radialToggle.Checked = _settings.RadialToggle;
        _helpSuggest.Checked = _settings.HelpSuggest;
        _helpCorrect.Checked = _settings.HelpCorrect;
        _helpPredict.Checked = _settings.HelpPredict;

        Theme.Init();
        foreach (string n in Theme.Names) _theme.Items.Add(n);
        _theme.SelectedIndexChanged += (s, e) => { Theme.Apply((string)_theme.SelectedItem!); Save(); };
        string themeName = Theme.Names.Contains(_settings.Theme) ? _settings.Theme : Theme.Names[0];
        _theme.SelectedItem = themeName;
        Theme.Apply(themeName);

        _devices = new MMDeviceEnumerator()
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        _populating = true;
        foreach (var d in _devices)
        {
            int i = _outputs.Items.Add(d.FriendlyName);
            bool tick = _settings.Outputs.Contains(d.FriendlyName);
            // fresh install: pre-tick anything that looks like a virtual cable
            if (!tick && freshInstall && d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                tick = true;
            _outputs.SetItemChecked(i, tick);
        }
        _populating = false;
        _outputs.ItemCheck += (s, e) =>
        {
            if (!_populating && IsHandleCreated) BeginInvoke((Action)ApplyOutputs);
        };
        ApplyOutputs();

        foreach (var (btn, slot) in new[] { (_typingBind, "typing"), (_streamBind, "stream"), (_radialBind, "radial"), (_addWordBind, "addword") })
        {
            Button b = btn; string which = slot;
            b.Click += (s, e) => ArmRecorder(b, which);
            b.MouseDown += (s, e) =>
            {
                if (e.Button is MouseButtons.Middle or MouseButtons.Right) ClearBind(which);
            };
        }
        ApplyBinds();
        UpdateBindLabels();

        try { _typingOverlay = new TypingOverlay(router); _ = _typingOverlay.Handle; }
        catch (Exception ex) { Log.Enqueue("typing overlay failed: " + ex.Message); }
        try { _radialOverlay = new RadialOverlay(router, tts, audio); _ = _radialOverlay.Handle; }
        catch (Exception ex) { Log.Enqueue("radial overlay failed: " + ex.Message); }

        _loading = false;
        Save();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (s, e) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (s, e) => { _exiting = true; Close(); });
        _tray = new NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "ShubbleComms",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (s, e) => ShowFromTray();

        _ = Handle;                       // force handle creation (raw input target)
        _router.SettingsHwnd = Handle;

        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        timer.Tick += (s, e) => Drain();
        timer.Start();
    }

    // ---- UI construction helpers ----

    static GroupBox Group(string title, params Control[] content)
    {
        var g = new GroupBox
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Padding = new Padding(8, 2, 8, 8)
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown
        };
        foreach (var c in content) flow.Controls.Add(c);
        g.Controls.Add(flow);
        return g;
    }

    static FlowLayoutPanel Row(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        foreach (var i in items) f.Controls.Add(i);
        return f;
    }

    static Label Lbl(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.White,
        Margin = new Padding(3, 9, 3, 0)
    };

    static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.Silver,
        Margin = new Padding(3, 4, 3, 0)
    };

    Button MkButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(58, 58, 58),
            Margin = new Padding(3, 4, 3, 3)
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(96, 96, 96);
        b.Click += (s, e) => onClick();
        return b;
    }

    Button BindButton()
    {
        var b = MkButton("none", () => { });
        b.MinimumSize = new Size(96, 0);
        return b;
    }

    GroupBox VoiceGroup()
    {
        return Group("Voice",
            Row(Lbl("voice"), _voice, Lbl("rate"), _rate, _rateLabel),
            Row(Lbl("pitch"), _pitch, _pitchLabel, Lbl("shout (CAPS)"), _shout, _shoutLabel),
            Row(
                MkButton("voices folder", OpenVoicesFolder),
                MkButton("rescan voices", () => { if (_tts is TtsRouter tr) { tr.Rescan(); RebuildVoices(); } }),
                Hint("extra piper voices (.onnx + .onnx.json) go in that folder")));
    }

    GroupBox OutputsGroup()
    {
        return Group("Audio outputs",
            Row(Hint("tick each device the voice should play into — e.g. your virtual cable")),
            _outputs);
    }

    GroupBox BindsGroup()
    {
        return Group("Key binds",
        Row(Lbl("typing"), _typingBind, Lbl("stream"), _streamBind, Lbl("radial"), _radialBind, Lbl("add"), _addWordBind),
            Row(_radialToggle, Hint("click to re-record · middle/right-click to clear · extra held modifiers are ignored")));
    }

    GroupBox TypingGroup()
    {
        return Group("Typing mode",
            Row(_streamWords, _releaseKeys, _tier1),
            Row(_helpSuggest, _helpCorrect, _helpPredict),
            Row(
                MkButton("clear message history", () =>
                {
                    _router.ClearHistory();
                    try { File.Delete(HistoryPath); } catch { }
                }),
                Hint("Up/Down cycles sent messages · Tab completes + cycles suggestions · Shift+Tab backwards")));
    }

    GroupBox FilesGroup()
    {
        return Group("Files",
            Row(
                MkButton("edit expansions", () => OpenFile(Expansions.FilePath)),
                MkButton("edit soundboard", () => OpenFile(RadialOverlay.BoardPath)),
                MkButton("open config folder", () => OpenFolder(AppData.Folder))));
    }

    GroupBox ThemeGroup()
    {
        return Group("Theme", Row(Lbl("theme"), _theme));
    }

    // ---- labels ----

    void RebuildVoices()
    {
        string want = _voice.SelectedItem as string ?? _settings.Voice;
        _voice.Items.Clear();
        foreach (var v in _tts.Voices) _voice.Items.Add(v);
        if (want != null && _voice.Items.Contains(want)) _voice.SelectedItem = want;
        else if (_voice.Items.Count > 0) _voice.SelectedIndex = 0;
    }

    void UpdateShoutLabel() =>
        _shoutLabel.Text = _shout.Value == 0 ? "off" : $"+{_shout.Value}%";

    void UpdatePitchLabel()
    {
        int p = _pitch.Value * 5;
        _pitchLabel.Text = p == 0 ? "0" : $"{(p > 0 ? "+" : "")}{p}%";
    }

    void UpdateBindLabels()
    {
        // never clobber the "recording…" text on an armed button
        if (_recTarget != _typingBind)
            _typingBind.Text = BindText(_router.BindTypingVk, _router.BindTypingAlt, _router.BindTypingCtrl, _router.BindTypingShift);
        if (_recTarget != _streamBind)
            _streamBind.Text = BindText(_router.BindStreamVk, _router.BindStreamAlt, _router.BindStreamCtrl, _router.BindStreamShift);
        if (_recTarget != _radialBind)
            _radialBind.Text = BindText(_router.RadialTriggerVk, _router.RadialAlt, _router.RadialCtrl, _router.RadialShift);
        if (_recTarget != _addWordBind)
            _addWordBind.Text = BindText(_router.BindAddWordVk, _router.BindAddWordAlt, _router.BindAddWordCtrl, _router.BindAddWordShift);
    }

    static string BindText(uint vk, bool alt, bool ctrl, bool shift)
    {
        if (vk == 0) return "none";
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(vk switch
        {
            0x01 => "LMB",
            0x02 => "RMB",
            0x04 => "MMB",
            0x05 => "X1",
            0x06 => "X2",
            _ => ((Keys)vk).ToString()
        });
        return string.Join("+", parts);
    }

    // ---- bind recorder ----

    void ArmRecorder(Button btn, string slot)
    {
        if (_recTarget != null) UpdateBindLabels();   // restore a previously armed button
        _recTarget = btn; _recSlot = slot;
        _recArm = Environment.TickCount64;
        _router.RecVk = 0;
        _router.RecReady = false;
        _router.BindRecording = true;
        btn.Text = "recording…";
    }

    void ClearBind(string slot)
    {
        var zero = new List<int> { 0, 0, 0, 0 };
        if (slot == "typing") _settings.BindTyping = zero;
        else if (slot == "stream") _settings.BindStream = zero;
        else if (slot == "addword") _settings.BindAddWord = zero;
        else _settings.BindRadial = zero;
        ApplyBinds(); UpdateBindLabels(); Save();
    }

    void ApplyBinds()
    {
        _router.BindTypingVk = (uint)Val(_settings.BindTyping, 0, 0x70);
        _router.BindTypingAlt = Val(_settings.BindTyping, 1, 0) != 0;
        _router.BindTypingCtrl = Val(_settings.BindTyping, 2, 0) != 0;
        _router.BindTypingShift = Val(_settings.BindTyping, 3, 0) != 0;
        _router.BindStreamVk = (uint)Val(_settings.BindStream, 0, 0x45);
        _router.BindStreamAlt = Val(_settings.BindStream, 1, 1) != 0;
        _router.BindStreamCtrl = Val(_settings.BindStream, 2, 0) != 0;
        _router.BindStreamShift = Val(_settings.BindStream, 3, 0) != 0;
        _router.RadialTriggerVk = (uint)Val(_settings.BindRadial, 0, 0x04);
        _router.RadialAlt = Val(_settings.BindRadial, 1, 1) != 0;
        _router.RadialCtrl = Val(_settings.BindRadial, 2, 0) != 0;
        _router.RadialShift = Val(_settings.BindRadial, 3, 0) != 0;
        _router.BindAddWordVk = (uint)Val(_settings.BindAddWord, 0, 0x44);
        _router.BindAddWordAlt = Val(_settings.BindAddWord, 1, 0) != 0;
        _router.BindAddWordCtrl = Val(_settings.BindAddWord, 2, 1) != 0;
        _router.BindAddWordShift = Val(_settings.BindAddWord, 3, 0) != 0;
    }

    static int Val(List<int> bind, int i, int fallback) =>
        bind != null && bind.Count > i ? bind[i] : fallback;

    // ---- files / folders / outputs ----

    void OpenFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Enqueue("open file failed: " + ex.Message); }
    }

    void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Enqueue("open folder failed: " + ex.Message); }
    }

    void OpenVoicesFolder()
    {
        try
        {
            string dir = Path.Combine(PiperTts.DefaultDir, "voices");
            Directory.CreateDirectory(dir);
            string readme = Path.Combine(dir, "PUT VOICES HERE.txt");
            if (!File.Exists(readme)) File.WriteAllText(readme, PiperVoicesReadme);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Enqueue("voices folder failed: " + ex.Message); }
    }

    const string PiperVoicesReadme = @"Piper voice models live in this folder.

A voice is TWO files, side by side: <name>.onnx and <name>.onnx.json.
Drop new ones in here, then press 'rescan voices' back in ShubbleComms.

More voices:
  https://huggingface.co/rhasspy/piper-voices   (official)
  search Hugging Face for 'piper'               (community-made)

Building from source and piper isn't set up yet? piper.exe and its dlls
go one folder UP from here — grab the Windows release from
https://github.com/rhasspy/piper/releases and extract it there.

Everything runs locally. Nothing is uploaded anywhere, ever.
";

    void ApplyOutputs()
    {
        var selected = new List<MMDevice>();
        for (int i = 0; i < _outputs.Items.Count; i++)
            if (_outputs.GetItemChecked(i)) selected.Add(_devices[i]);
        _audio.SetDevices(selected);
        Save();
    }

    void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(AppData.Folder);
            File.WriteAllText(HistoryPath,
                JsonSerializer.Serialize(_router.HistorySnapshot()));
        }
        catch (Exception ex) { Log.Enqueue("history save failed: " + ex.Message); }
    }

    void Save()
    {
        if (_loading) return;
        try
        {
            _settings.Voice = _voice.SelectedItem as string;
            _settings.Rate = _rate.Value;
            _settings.Shout = _shout.Value;
            _settings.Pitch = _pitch.Value * 5;
            _settings.StreamWords = _streamWords.Checked;
            _settings.FlushOnClose = _releaseKeys.Checked;
            _settings.Tier1 = _tier1.Checked;
            _settings.RadialToggle = _radialToggle.Checked;
            _settings.Outputs = _devices
                .Where((d, i) => _outputs.GetItemChecked(i))
                .Select(d => d.FriendlyName).ToList();
            _settings.Theme = Theme.Current;
            _settings.BindTyping = new List<int> { (int)_router.BindTypingVk, _router.BindTypingAlt ? 1 : 0, _router.BindTypingCtrl ? 1 : 0, _router.BindTypingShift ? 1 : 0 };
            _settings.BindStream = new List<int> { (int)_router.BindStreamVk, _router.BindStreamAlt ? 1 : 0, _router.BindStreamCtrl ? 1 : 0, _router.BindStreamShift ? 1 : 0 };
            _settings.BindRadial = new List<int> { (int)_router.RadialTriggerVk, _router.RadialAlt ? 1 : 0, _router.RadialCtrl ? 1 : 0, _router.RadialShift ? 1 : 0 };
            _settings.BindAddWord = new List<int> { (int)_router.BindAddWordVk, _router.BindAddWordAlt ? 1 : 0, _router.BindAddWordCtrl ? 1 : 0, _router.BindAddWordShift ? 1 : 0 };
            _settings.HelpSuggest = _helpSuggest.Checked;
            _settings.HelpCorrect = _helpCorrect.Checked;
            _settings.HelpPredict = _helpPredict.Checked;
            
            Directory.CreateDirectory(AppData.Folder);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Enqueue("settings save failed: " + ex.Message); }
    }

    // ---- window lifecycle ----

    void ShowFromTray()
    {
        _allowVisible = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void SetVisibleCore(bool value)
    {
        // hide at startup — the app lives in the tray
        if (value && !_allowVisible) value = false;
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Save();
        SaveHistory();
        // X / Alt+F4 / Windows shutdown → real quit. Tray Quit sets _exiting itself.
        if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.WindowsShutDown)
            _exiting = true;
        if (!_exiting) { e.Cancel = true; Hide(); return; }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    // ---- raw input (bind recorder while our own window has focus) ----

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var devices = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = 0x100, hwndTarget = Handle },  // keyboard
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x100, hwndTarget = Handle },  // mouse
        };
        if (!Native.RegisterRawInputDevices(devices, 2, Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            Log.Enqueue("raw input registration failed: " + Marshal.GetLastWin32Error());
    }

    protected override void WndProc(ref Message m)
    {
        // Raw input reaches this window even while it has focus (the low-level
        // hooks don't), so the bind recorder listens here as a second source.
        if (m.Msg == Native.WM_INPUT)
        {
            int hdr = Marshal.SizeOf<Native.RAWINPUTHEADER>();
            var buf = new byte[64];
            uint size = (uint)buf.Length;
            uint ret = Native.GetRawInputDataBytes(m.LParam, Native.RID_INPUT, buf, ref size, (uint)hdr);
            if (ret != uint.MaxValue && ret >= (uint)hdr)
            {
                uint type = BitConverter.ToUInt32(buf, 0);
                if (type == 1)
                {
                    _router.CaptureFromRaw(BitConverter.ToUInt16(buf, hdr + 6));   // keyboard vkey
                }
                else if (type == 0)
                {
                    ushort buttons = BitConverter.ToUInt16(buf, hdr + 4);
                    uint vk = buttons switch
                    {
                        4 => 0x02,    // right button
                        16 => 0x04,   // middle button
                        64 => BitConverter.ToUInt16(buf, hdr + 6) == 1 ? 0x05u : 0x06u,   // X1 / X2
                        _ => 0
                    };
                    // clicks on our own window stay ours
                    if (vk != 0 && !Bounds.Contains(Cursor.Position))
                        _router.CaptureFromRaw(vk);
                }
            }
        }
        base.WndProc(ref m);
    }

    // ---- periodic work ----

    void Drain()
    {
        while (_router.Ui.TryDequeue(out var act)) { try { act(); } catch { } }

        if (_streamWords.Checked != _router.StreamWords)
            _streamWords.Checked = _router.StreamWords;

        // recorder: apply the captured bind (RecVk == 0 means Esc was pressed)
        if (_recTarget != null && _router.RecReady)
        {
            if (_router.RecVk != 0)
            {
                var bind = new List<int>
                {
                    (int)_router.RecVk,
                    _router.RecAlt ? 1 : 0, _router.RecCtrl ? 1 : 0, _router.RecShift ? 1 : 0
                };
                if (_recSlot == "typing") _settings.BindTyping = bind;
                else if (_recSlot == "stream") _settings.BindStream = bind;
                else if (_recSlot == "addword") _settings.BindAddWord = bind;
                else _settings.BindRadial = bind;
            }
            else ClearBind(_recSlot ?? "typing");
            ApplyBinds();
            _recTarget = null; _recSlot = null;
            _router.RecReady = false;
            UpdateBindLabels();
            Save();
        }

        if (_recTarget != null && Environment.TickCount64 - _recArm > 10000)
        {
            _router.BindRecording = false;
            _recTarget = null; _recSlot = null;
            UpdateBindLabels();
            Log.Enqueue("bind recording timed out");
        }

        // persist the message history whenever the hook thread changed it
        if (_router.HistDirty)
        {
            _router.HistDirty = false;
            SaveHistory();
        }
    }
}