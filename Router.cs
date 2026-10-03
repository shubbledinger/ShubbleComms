using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// The keyboard and mouse plumbing. Two low-level Windows hooks (keyboard and
// mouse) run on a dedicated thread with its own message pump; everything the
// app does — typing capture, key binds, the radial wheel — happens inside those
// hook callbacks.
//
// Terms used throughout:
//   GAME mode   — hooks are passive; every key reaches the focused app
//   TYPING mode — captured keys are swallowed and edited into _text instead
//   pinned keys — keys held when TYPING opened stay "down" for the game
//   injections  — input events we send ourselves, tagged so the hooks can
//                 recognize and route them

internal enum Mode { Game, Typing }

// How an injected key should be treated by our own hook:
// Never   — always pass it through to the OS
// IfModeA — hide it only when hidden injections update the async key state
// Always  — always swallow it
internal enum Hide { Never, IfModeA, Always }

internal sealed class KeyboardRouter : IDisposable
{
    Thread _pump;
    IntPtr _hook;
    IntPtr _mouseHook;
    static Native.LowLevelKeyboardProc _proc;
    static Native.LowLevelMouseProc _mouseProc;
    uint _pumpThreadId;
    readonly ManualResetEvent _ready = new(false);

    // keys currently considered held (repeat suppression)
    readonly HashSet<uint> _held = new();
    // keys held when TYPING opened — kept logically down for the game
    readonly HashSet<uint> _pinned = new();
    readonly HashSet<uint> _pinnedMouse = new();
    static readonly uint[] MouseButtons = { 0x01, 0x02, 0x04, 0x05, 0x06 };

    readonly ITts _tts;
    readonly object _textLock = new();
    readonly StringBuilder _text = new();
    // in stream mode, [0, _wordStart) is already-spoken text, the rest is live
    int _wordStart;
    int _cursor, _anchor;
    // our own mirror of keyboard state, built from hook events
    readonly byte[] _ks = new byte[256];

    public volatile Mode Mode = Mode.Game;
    public volatile bool Tier1HoldAssert = false;
    public volatile bool StreamWords = true;
    public volatile bool FlushOnClose = true;
    public string SelfTestResult = "?";
    public volatile string PinnedPreview = "";
    public volatile string TypedText = "";
    public volatile string UnspokenText = "";
    public volatile int Cursor, SelStart, SelEnd;

    // bind slots: a key (vk >= 8) or a mouse button (1-6), plus modifiers
    public volatile uint BindTypingVk = 0x70;
    public volatile bool BindTypingAlt, BindTypingCtrl, BindTypingShift;
    public volatile uint BindStreamVk = 0x45;
    public volatile bool BindStreamAlt = true, BindStreamCtrl, BindStreamShift;
    public volatile uint RadialTriggerVk = 0x04;   // middle mouse button
    public volatile bool RadialAlt = true, RadialCtrl, RadialShift;

    // bind recorder: while armed, the next key or mouse button (plus whatever
    // modifiers are held) becomes the new bind. The settings window polls
    // RecReady/RecVk and applies the result.
    public IntPtr SettingsHwnd;
    public volatile bool BindRecording;
    public volatile bool RecReady;
    public volatile uint RecVk;
    public volatile bool RecAlt, RecCtrl, RecShift;

    // radial wheel state (written here, read by the overlay)
    public volatile bool RadialOpen;
    public volatile bool RadialCancelled;
    public volatile bool RadialToggle;          // toggle mode: trigger opens, LMB fires, RMB closes
    public volatile bool RadialFireRequest;     // LMB in toggle mode; overlay fires and clears it
    public volatile int RadialAnchorX, RadialAnchorY, RadialVirtX, RadialVirtY;
    public int RadialWheelNotches;
    int _wheelAcc;
    // while the wheel is open the trigger's modifiers are swallowed and cleanly
    // released, so the focused app never sees a lone Alt/Ctrl/Shift
    bool _shieldAlt, _shieldCtrl, _shieldShift;
    volatile bool _rmbUpPending;
    readonly object _recLock = new();

    public readonly Injector Injector = new();
    // work the hook thread needs done on the UI thread (clipboard access, etc.)
    public readonly ConcurrentQueue<Action> Ui = new();

    public KeyboardRouter(ITts tts)
    {
        _tts = tts;
        _pump = new Thread(RunPump) { IsBackground = false, Name = "llhook" };
        _pump.Start();
        _ready.WaitOne();
        SelfTest();
    }

    void RunPump()
    {
        _pumpThreadId = Native.GetCurrentThreadId();
        _proc = HookCallback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc,
                                        Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Enqueue($"keyboard hook FAILED: {Marshal.GetLastWin32Error()} — try running as admin");

        _mouseProc = MouseCallback;
        _mouseHook = Native.SetWindowsHookExMouse(Native.WH_MOUSE_LL, _mouseProc,
                                                  Native.GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Enqueue($"mouse hook FAILED: {Marshal.GetLastWin32Error()}");

        // seed held-key state from the OS so nothing starts out stuck
        for (uint vk = 8; vk < 256; vk++)
            if ((Native.GetAsyncKeyState((int)vk) & 0x8000) != 0)
            {
                _held.Add(vk);
                _ks[vk] = 0xFF;
            }

        _ready.Set();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }

        Native.UnhookWindowsHookEx(_hook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
    }

    // ============ bind helpers (both hooks run on this same thread) ============

    // Exact modifier match, with a self-heal: if the tracked state fails to match
    // but disagrees with the OS's live physical state, re-derive from physical and
    // try once more. Stale tracked modifiers would otherwise permanently break a
    // bind — the bind key falls through to the focused app.
    // GAME MODE ONLY: there, every key passes through the hook and the async
    // table is ground truth. In TYPING mode every key is swallowed, the async
    // table is blind, and syncing from it would ERASE held modifiers — pressing
    // the Alt+E bind while Shift was held used to wipe Shift and turn
    // everything lowercase until Shift was re-pressed.
    // RMENU (AltGr) never counts as Alt, so AltGr+key can't fire an Alt bind on
    // any layout that uses it (AZERTY, QWERTZ, Nordic, Polish, ...).
    bool ModsMatch(bool alt, bool ctrl, bool shift)
    {
        if (MatchMods(alt, ctrl, shift)) return true;
        if (Mode == Mode.Game && ModsDisagreeWithPhysical()) SyncFromPhysical();
        return MatchMods(alt, ctrl, shift);
    }

    bool MatchMods(bool alt, bool ctrl, bool shift) =>
        (((_ks[Native.VK_LMENU] & 0x80) != 0) == alt) &&
        (_ks[Native.VK_RMENU] & 0x80) == 0 &&
        (((_ks[Native.VK_LCONTROL] & 0x80) != 0) == ctrl) &&
        (((_ks[Native.VK_SHIFT] & 0x80) != 0) == shift);

    // does our tracked state disagree with the live OS state on any modifier family?
    bool ModsDisagreeWithPhysical()
    {
        bool Track(uint vk) => (_ks[(int)vk] & 0x80) != 0;
        bool Phys(uint vk) => (Native.GetAsyncKeyState((int)vk) & 0x8000) != 0;
        return Track(Native.VK_LMENU) != Phys(Native.VK_LMENU) ||
               Track(Native.VK_RMENU) != Phys(Native.VK_RMENU) ||
               Track(Native.VK_LCONTROL) != Phys(Native.VK_LCONTROL) ||
               Track(Native.VK_RCONTROL) != Phys(Native.VK_RCONTROL) ||
               Track(Native.VK_LSHIFT) != Phys(Native.VK_LSHIFT) ||
               Track(Native.VK_RSHIFT) != Phys(Native.VK_RSHIFT);
    }

    static string BindName(uint vk) => vk switch
    {
        0x01 => "LMB",
        0x02 => "RMB",
        0x04 => "MMB",
        0x05 => "X1",
        0x06 => "X2",
        _ => ((Keys)vk).ToString()
    };

    void CaptureBind(uint vk)
    {
        lock (_recLock)
        {
            if (!BindRecording || RecReady) return;
            RecAlt = (_ks[Native.VK_LMENU] & 0x80) != 0 && (_ks[Native.VK_RMENU] & 0x80) == 0;
            RecCtrl = (_ks[Native.VK_LCONTROL] & 0x80) != 0;
            RecShift = (_ks[Native.VK_SHIFT] & 0x80) != 0;
            RecVk = vk;
            RecReady = true;
            BindRecording = false;
            Log.Enqueue($"bind recorded: {BindName(vk)}");
        }
    }

    // raw-input capture path — raw input arrives in BOTH focus states, so the
    // settings window feeds this from WM_INPUT while it has focus
    public void CaptureFromRaw(uint vk)
    {
        lock (_recLock)
        {
            if (!BindRecording || RecReady) return;
            if (vk == 0 || vk == 0x01) return;   // LMB is never a bind
            if (vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x10 or 0x11 or 0x12)
                return;                          // bare modifiers are recorded as modifiers, not binds

            if (vk == Native.VK_ESCAPE)
            {
                RecVk = 0; RecReady = true; BindRecording = false;
                return;
            }
            RecAlt = (Native.GetAsyncKeyState((int)Native.VK_LMENU) & 0x8000) != 0
                     && (Native.GetAsyncKeyState((int)Native.VK_RMENU) & 0x8000) == 0;
            RecCtrl = (Native.GetAsyncKeyState((int)Native.VK_LCONTROL) & 0x8000) != 0;
            RecShift = (Native.GetAsyncKeyState((int)Native.VK_SHIFT) & 0x8000) != 0;
            RecVk = vk;
            RecReady = true;
            BindRecording = false;
            Log.Enqueue($"bind recorded: {BindName(vk)}");
        }
    }

    // Switching from stream to full mode discards the already-spoken text: the
    // full-mode Enter must not re-speak it. Only unspoken text carries over.
    public void SetStreamWords(bool value)
    {
        bool changed = value != StreamWords;
        StreamWords = value;
        if (changed)
            Log.Enqueue(value ? "stream words ON" : "full message mode — spoken text discarded");
        if (!value)
        {
            lock (_textLock)
            {
                if (_wordStart > 0)
                {
                    _text.Remove(0, _wordStart);
                    _wordStart = 0;
                    Snapshot();
                }
            }
        }
    }

    void OpenRadial(int x, int y)
    {
        RadialAnchorX = x; RadialAnchorY = y;
        RadialVirtX = 0; RadialVirtY = 0; RadialWheelNotches = 0;
        RadialCancelled = false;
        _wheelAcc = 0;
        // swallow the trigger's modifiers while the wheel is open
        _shieldAlt = RadialAlt; _shieldCtrl = RadialCtrl; _shieldShift = RadialShift;
        _rmbUpPending = false;
        RadialOpen = true;
    }

    // Arm the shield for whatever modifiers are PHYSICALLY held right now. Used
    // on all close paths: closing while Alt is still held must clean-release Alt
    // the same way the open did, or the trailing Alt-up pops the browser menu.
    void ArmShieldForHeldMods()
    {
        _shieldAlt = (Native.GetAsyncKeyState((int)Native.VK_LMENU) & 0x8000) != 0;
        _shieldCtrl = (Native.GetAsyncKeyState((int)Native.VK_LCONTROL) & 0x8000) != 0;
        _shieldShift = (Native.GetAsyncKeyState((int)Native.VK_SHIFT) & 0x8000) != 0;
    }

    // Re-derive held keys + keyboard state from the OS. The tracked state can go
    // stale when events are missed (window focus quirks, injections), and stuck
    // modifiers self-heal here.
    void SyncFromPhysical()
    {
        for (uint vk = 8; vk < 256; vk++)
        {
            bool phys = (Native.GetAsyncKeyState((int)vk) & 0x8000) != 0;
            _ks[vk] = phys ? (byte)0xFF : (byte)0;
            if (phys) _held.Add(vk); else _held.Remove(vk);
        }
    }

    // ============ keyboard hook ============

    // Which keys are captured into the text editor in TYPING mode. In full mode
    // the list grows to include the navigation/editing keys.
    static bool IsCaptureKey(uint vk, bool full)
    {
        if (vk >= 0xA0 && vk <= 0xA5) return true;                                    // modifiers
        if (vk is 0x08 or 0x09 or 0x0D or 0x10 or 0x11 or 0x12 or 0x14 or 0x1B or 0x20) return true;
        if (vk is >= 0x30 and <= 0x39) return true;                                   // 0-9
        if (vk is >= 0x41 and <= 0x5A) return true;                                   // A-Z
        if (vk is >= 0x60 and <= 0x6E) return true;                                   // numpad
        if (vk is >= 0xBA and <= 0xE4) return true;                                   // punctuation
        if (full && (vk is 0x23 or 0x24 or 0x2E || (vk >= 0x25 && vk <= 0x28))) return true;  // arrows/Home/End/Del
        return false;
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        bool down = wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN;
        bool up = wParam == (IntPtr)Native.WM_KEYUP || wParam == (IntPtr)Native.WM_SYSKEYUP;

        if (down || up) UpdateKs(k.vkCode, down);

        // our own injections: hide them or pass them, but never process them
        if (k.dwExtraInfo == (IntPtr)Native.MAGIC_HIDE || k.dwExtraInfo == (IntPtr)Native.MAGIC_VISIBLE)
        {
            if (k.dwExtraInfo == (IntPtr)Native.MAGIC_HIDE) return (IntPtr)1;
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // ---- bind recorder: swallow everything except bare modifiers ----
        if (BindRecording)
        {
            if (down && k.vkCode is not (0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5
                                         or 0x10 or 0x11 or 0x12))
            {
                if (k.vkCode == Native.VK_ESCAPE)
                {
                    RecVk = 0; RecReady = true; BindRecording = false;   // Esc = clear the bind
                }
                else CaptureBind(k.vkCode);
            }
            if (down) _held.Add(k.vkCode); else if (up) _held.Remove(k.vkCode);
            return (IntPtr)1;
        }

        // ---- typing bind (key form) ----
        if (k.vkCode == BindTypingVk && BindTypingVk >= 0x08 &&
            ModsMatch(BindTypingAlt, BindTypingCtrl, BindTypingShift))
        {
            if (down && !_held.Contains(k.vkCode) && !RadialOpen) ToggleTyping();
            if (down) _held.Add(k.vkCode); else if (up) _held.Remove(k.vkCode);
            return (IntPtr)1;
        }

        // ---- stream bind (key form) ----
        if (k.vkCode == BindStreamVk && BindStreamVk >= 0x08 &&
            ModsMatch(BindStreamAlt, BindStreamCtrl, BindStreamShift))
        {
            if (down && !_held.Contains(k.vkCode)) SetStreamWords(!StreamWords);
            if (down) _held.Add(k.vkCode); else if (up) _held.Remove(k.vkCode);
            return (IntPtr)1;
        }

        // ---- radial trigger (key form): hold opens / toggle mode re-press closes ----
        if (RadialTriggerVk >= 0x08 && k.vkCode == RadialTriggerVk)
        {
            if (down && !_held.Contains(k.vkCode) && !RadialOpen && Mode == Mode.Game &&
                ModsMatch(RadialAlt, RadialCtrl, RadialShift))
            {
                Native.GetCursorPos(out var cp);
                OpenRadial(cp.x, cp.y);
            }
            else if (down && !_held.Contains(k.vkCode) && RadialOpen && RadialToggle)
            {
                ArmShieldForHeldMods();
                RadialCancelled = true;
                RadialOpen = false;
            }
            else if (up && RadialOpen && !RadialToggle)
            {
                RadialOpen = false;
            }
            if (down) _held.Add(k.vkCode); else if (up) _held.Remove(k.vkCode);
            return (IntPtr)1;
        }

        // ---- radial modifier shield: swallow trigger modifiers while the wheel is
        // open. On release: CLEAN RELEASE — inert F24 press then the modifier's
        // keyup, so the app releases Alt without treating it as "Alt alone"
        // (no browser menubar flash).
        if (_shieldAlt && k.vkCode is 0x12 or 0xA4 or 0xA5)
        {
            if (up)
            {
                _shieldAlt = false;
                Injector.Send(0xF7, keyUp: false, Hide.Never);   // F24 down — inert
                Injector.Send(0xF7, keyUp: true, Hide.Never);
                Injector.Send(0x12, keyUp: true, Hide.Never);    // Alt up — clean release
            }
            return (IntPtr)1;
        }
        if (_shieldCtrl && k.vkCode is 0x11 or 0xA2 or 0xA3)
        {
            if (up) { _shieldCtrl = false; Injector.Send(0x11, keyUp: true, Hide.Never); }
            return (IntPtr)1;
        }
        if (_shieldShift && k.vkCode is 0x10 or 0xA0 or 0xA1)
        {
            if (up) { _shieldShift = false; Injector.Send(0x10, keyUp: true, Hide.Never); }
            return (IntPtr)1;
        }

        // ---- typing mode editor ----
        if (Mode == Mode.Typing)
        {
            // caps lock passes through: the OS tracks its toggle, and both the
            // display and ToUnicodeEx depend on that state
            if (k.vkCode == Native.VK_CAPITAL)
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            bool full = !StreamWords;

            if (!IsCaptureKey(k.vkCode, full))
            {
                if (down) _held.Add(k.vkCode); else if (up) _held.Remove(k.vkCode);
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);   // game keys stay live
            }

            if (down)
            {
                lock (_textLock) Normalize();
                bool repeat = _held.Contains(k.vkCode);
                _held.Add(k.vkCode);

                if (k.vkCode == Native.VK_ESCAPE && !repeat)
                {
                    lock (_textLock) ClearText();   // Esc clears the box; the bind key closes it
                    Snapshot();
                    return (IntPtr)1;
                }

                bool ctrl = (_ks[Native.VK_CONTROL] & 0x80) != 0;
                bool shift = (_ks[Native.VK_SHIFT] & 0x80) != 0;

                switch (k.vkCode)
                {
                    case Native.VK_SPACE:
                        lock (_textLock)
                        {
                            if (full) { DeleteSel(); _text.Insert(_cursor, " "); _cursor++; _anchor = _cursor; }
                            else { _text.Append(' '); _anchor = _cursor = _text.Length; SubmitWord(); }
                        }
                        Snapshot();
                        break;

                    case Native.VK_RETURN:
                        if (repeat) break;
                        lock (_textLock)
                        {
                            if (StreamWords) { SubmitWord(); ClearText(); }
                            else
                            {
                                string whole = _text.ToString().Trim();
                                if (whole.Length > 0) _tts?.Speak(whole);
                                ClearText();
                            }
                        }
                        Snapshot();
                        ToggleTyping();
                        break;

                    case Native.VK_BACK:
                        lock (_textLock)
                        {
                            if (ctrl)
                            {
                                int target = WordBack(_cursor);
                                _text.Remove(target, _cursor - target);
                                _cursor = _anchor = target;
                            }
                            else if (full && _anchor != _cursor) DeleteSel();
                            else if (_cursor > _wordStart)
                            {
                                _cursor--;
                                _text.Remove(_cursor, 1);
                                _anchor = _cursor;
                            }
                        }
                        Snapshot();
                        break;

                    case Native.VK_DELETE:
                        if (!full) break;
                        lock (_textLock)
                        {
                            if (_anchor != _cursor) DeleteSel();
                            else if (_cursor < _text.Length)
                            {
                                int to = ctrl ? WordFwd(_cursor) : _cursor + 1;
                                _text.Remove(_cursor, Math.Min(to, _text.Length) - _cursor);
                            }
                        }
                        Snapshot();
                        break;

                    case Native.VK_HOME:
                    case Native.VK_UP:
                        if (!full) break;
                        lock (_textLock) { _cursor = _wordStart; if (!shift) _anchor = _cursor; }
                        Snapshot();
                        break;

                    case Native.VK_END:
                    case Native.VK_DOWN:
                        if (!full) break;
                        lock (_textLock) { _cursor = _text.Length; if (!shift) _anchor = _cursor; }
                        Snapshot();
                        break;

                    case Native.VK_LEFT:
                        if (!full) break;
                        lock (_textLock)
                        {
                            _cursor = ctrl ? WordBack(_cursor) : Math.Max(_wordStart, _cursor - 1);
                            if (!shift) _anchor = _cursor;
                        }
                        Snapshot();
                        break;

                    case Native.VK_RIGHT:
                        if (!full) break;
                        lock (_textLock)
                        {
                            _cursor = ctrl ? WordFwd(_cursor) : Math.Min(_text.Length, _cursor + 1);
                            if (!shift) _anchor = _cursor;
                        }
                        Snapshot();
                        break;

                    default:
                        if (ctrl && k.vkCode >= 0x41 && k.vkCode <= 0x5A)
                        {
                            if (full) CtrlLetter(k.vkCode);
                            break;
                        }
                        string chars = ToChars(k);
                        if (chars.Length > 0)
                        {
                            lock (_textLock)
                            {
                                if (full) DeleteSel();
                                _text.Insert(full ? _cursor : _text.Length, chars);
                                if (full) { _cursor += chars.Length; _anchor = _cursor; }
                                else _anchor = _cursor = _text.Length;
                            }
                            Snapshot();
                        }
                        break;
                }
            }
            else if (up)
            {
                _held.Remove(k.vkCode);
                // Tier1: keep pinned keys logically held for games that read raw input
                if (_pinned.Contains(k.vkCode) && Tier1HoldAssert)
                    Injector.Send(k.vkCode, keyUp: false, Hide.IfModeA);
            }
            return (IntPtr)1;
        }

        // ---- game mode: track and pass through ----
        if (down) _held.Add(k.vkCode); else if (up) _held.Remove(k.vkCode);
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ============ mouse hook ============

    static uint ButtonVk(int msg, ushort xbtn) => msg switch
    {
        Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP => 0x01,
        Native.WM_RBUTTONDOWN or Native.WM_RBUTTONUP => 0x02,
        Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP => 0x04,
        Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP => xbtn == 1 ? 0x05u : 0x06u,
        _ => 0
    };

    bool OverSettingsWindow(Native.POINT pt)
    {
        if (SettingsHwnd == IntPtr.Zero) return false;
        if (!Native.GetWindowRect(SettingsHwnd, out var r)) return false;
        return pt.x >= r.Left && pt.x < r.Right && pt.y >= r.Top && pt.y < r.Bottom;
    }

    IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        ushort xbtn = (ushort)((m.mouseData >> 16) & 0xFFFF);

        bool isDown = msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN
                      or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN;
        bool isUp = msg is Native.WM_LBUTTONUP or Native.WM_RBUTTONUP
                    or Native.WM_MBUTTONUP or Native.WM_XBUTTONUP;
        uint bvk = ButtonVk(msg, xbtn);

        // our own injections (camera-lock counters, button restores): swallow —
        // raw input still sees them on this machine, which is the entire point
        if (m.dwExtraInfo == (IntPtr)Native.MAGIC_HIDE || m.dwExtraInfo == (IntPtr)Native.MAGIC_VISIBLE)
            return (IntPtr)1;

        // ---- recorder: a button press outside our window records the bind.
        // LMB is never a bind — left clicks stay normal everywhere.
        if (BindRecording)
        {
            if (OverSettingsWindow(m.pt))
                return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            if (bvk == 0x01)
                return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            if (isDown && bvk != 0) CaptureBind(bvk);
            if (isDown || isUp) return (IntPtr)1;
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);   // moves/wheel pass
        }

        // ---- typing mode: pinned mouse buttons frozen held ----
        if (Mode == Mode.Typing && _pinnedMouse.Count > 0 && bvk != 0 && _pinnedMouse.Contains(bvk))
        {
            if (isUp) Injector.MouseButton(bvk, down: true, magic: true);   // restore held for raw games
            return (IntPtr)1;
        }

        // ---- binds (mouse-button form) + radial trigger ----
        if (isDown && bvk != 0)
        {
            if (bvk == BindTypingVk && BindTypingVk <= 0x06 &&
                ModsMatch(BindTypingAlt, BindTypingCtrl, BindTypingShift) && !RadialOpen)
            {
                ToggleTyping();
                return (IntPtr)1;
            }
            if (bvk == BindStreamVk && BindStreamVk <= 0x06 &&
                ModsMatch(BindStreamAlt, BindStreamCtrl, BindStreamShift))
            {
                SetStreamWords(!StreamWords);
                return (IntPtr)1;
            }
            if (bvk == RadialTriggerVk && RadialTriggerVk <= 0x06 && Mode == Mode.Game &&
                ModsMatch(RadialAlt, RadialCtrl, RadialShift))
            {
                if (RadialOpen && RadialToggle)
                {
                    ArmShieldForHeldMods();
                    if (bvk == 0x02) _rmbUpPending = true;
                    RadialCancelled = true;
                    RadialOpen = false;
                }
                else if (!RadialOpen)
                {
                    OpenRadial(m.pt.x, m.pt.y);
                }
                return (IntPtr)1;
            }
        }

        // the RMB up that follows an RMB-close must not leak to the focused app
        if (_rmbUpPending)
        {
            if (msg == Native.WM_RBUTTONDOWN) _rmbUpPending = false;
            if (msg == Native.WM_RBUTTONUP) { _rmbUpPending = false; return (IntPtr)1; }
        }

        if (RadialOpen)
        {
            if (isUp && bvk == RadialTriggerVk && !RadialToggle)
            {
                RadialOpen = false;
                return (IntPtr)1;
            }

            // toggle mode: LMB fires the hovered wedge, wheel stays open
            if (msg == Native.WM_LBUTTONDOWN)
            {
                if (RadialToggle) RadialFireRequest = true;
                return (IntPtr)1;
            }
            if (msg == Native.WM_LBUTTONUP) return (IntPtr)1;

            if (msg == Native.WM_RBUTTONDOWN)
            {
                ArmShieldForHeldMods();
                _rmbUpPending = true;
                RadialCancelled = true;
                RadialOpen = false;
                return (IntPtr)1;
            }
            if (msg == Native.WM_RBUTTONUP) return (IntPtr)1;

            if (msg == Native.WM_MOUSEWHEEL)
            {
                short d = (short)(m.mouseData >> 16);
                _wheelAcc += d;
                while (_wheelAcc >= 120) { RadialWheelNotches++; _wheelAcc -= 120; }
                while (_wheelAcc <= -120) { RadialWheelNotches--; _wheelAcc += 120; }
                Injector.MouseWheel(-d);   // counter-spin so the game's weapon wheel doesn't move
                return (IntPtr)1;
            }

            if (msg == Native.WM_MOUSEMOVE)
            {
                // virtual cursor: accumulate movement for the overlay, counter-move
                // the physical cursor so the game camera stays locked
                int dx = m.pt.x - RadialAnchorX;
                int dy = m.pt.y - RadialAnchorY;
                RadialVirtX += dx; RadialVirtY += dy;
                if (dx != 0 || dy != 0) Injector.MouseMove(-dx, -dy);
                return (IntPtr)1;
            }

            return (IntPtr)1;
        }

        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ============ text engine ============

    // keep the cursor/anchor inside the live (unspoken) region
    void Normalize()
    {
        int len = _text.Length;
        if (_wordStart > len) _wordStart = len;
        if (_cursor > len) _cursor = len;
        if (_anchor > len) _anchor = len;
        if (_cursor < _wordStart) _cursor = _wordStart;
        if (_anchor < _wordStart) _anchor = _wordStart;
    }

    // left-Alt and AltGr are DIFFERENT keys — never conflate them
    void UpdateKs(uint vk, bool down)
    {
        byte v = down ? (byte)0xFF : (byte)0;
        switch (vk)
        {
            case Native.VK_SHIFT:
            case Native.VK_LSHIFT:
            case Native.VK_RSHIFT:
                _ks[Native.VK_SHIFT] = v; _ks[Native.VK_LSHIFT] = v; _ks[Native.VK_RSHIFT] = v; break;
            case Native.VK_CONTROL:
            case Native.VK_LCONTROL:
            case Native.VK_RCONTROL:
                _ks[Native.VK_CONTROL] = v; _ks[Native.VK_LCONTROL] = v; _ks[Native.VK_RCONTROL] = v; break;
            case Native.VK_LMENU:
            case Native.VK_MENU:
                _ks[Native.VK_LMENU] = v; _ks[Native.VK_MENU] = v; _ks[Native.VK_RMENU] = 0; break;
            case Native.VK_RMENU:
                _ks[Native.VK_RMENU] = v; _ks[Native.VK_MENU] = v; _ks[Native.VK_LMENU] = 0; break;
            default: if (vk < 256) _ks[vk] = v; break;
        }
    }

    // Translate a keypress into characters using the FOREGROUND app's keyboard
    // layout (correct for AZERTY etc.).
    // Modifier state = live physical (async) OR our own hook tracking. The async
    // table doesn't reflect keys the hook swallowed — and TYPING mode swallows
    // EVERY key, so a Shift pressed mid-message read as "not held" and everything
    // came out lowercase. Our tracker sees every event the hook sees; OR them so
    // neither blind spot can blank the modifiers. L and R stay distinct so AltGr
    // is never conflated with left Alt.
    string ToChars(Native.KBDLLHOOKSTRUCT k)
    {
        var ks = (byte[])_ks.Clone();

        bool la = Async(0xA4) || (_ks[0xA4] & 0x80) != 0;
        bool ra = Async(0xA5) || (_ks[0xA5] & 0x80) != 0;
        bool lc = Async(0xA2) || (_ks[0xA2] & 0x80) != 0;
        bool rc = Async(0xA3) || (_ks[0xA3] & 0x80) != 0;
        bool sh = (Async(0xA0) || (_ks[0xA0] & 0x80) != 0) ||
                  (Async(0xA1) || (_ks[0xA1] & 0x80) != 0);
        ks[0x12] = (la || ra) ? (byte)0xFF : (byte)0; ks[0xA4] = la ? (byte)0xFF : (byte)0; ks[0xA5] = ra ? (byte)0xFF : (byte)0;
        ks[0x11] = (lc || rc) ? (byte)0xFF : (byte)0; ks[0xA2] = lc ? (byte)0xFF : (byte)0; ks[0xA3] = rc ? (byte)0xFF : (byte)0;
        ks[0x10] = sh ? (byte)0xFF : (byte)0; ks[0xA0] = sh ? (byte)0xFF : (byte)0; ks[0xA1] = sh ? (byte)0xFF : (byte)0;

        ks[Native.VK_CAPITAL] = (byte)(Native.GetKeyState((int)Native.VK_CAPITAL) & 1);
        var sb = new StringBuilder(8);
        int n = Native.ToUnicodeEx(k.vkCode, k.scanCode, ks, sb, sb.Capacity, 0, CurrentLayout());
        return n > 0 ? sb.ToString(0, n) : "";
    }

    static bool Async(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    static IntPtr CurrentLayout()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            uint tid = Native.GetWindowThreadProcessId(fg, out _);
            IntPtr hkl = Native.GetKeyboardLayout(tid);
            if (hkl != IntPtr.Zero) return hkl;
        }
        return Native.GetKeyboardLayout(0);
    }

    void ClearText() { _text.Clear(); _wordStart = 0; _cursor = 0; _anchor = 0; }

    void DeleteSel()
    {
        Normalize();
        int a = Math.Min(_anchor, _cursor), b = Math.Max(_anchor, _cursor);
        if (a >= b) return;
        _text.Remove(a, b - a);
        _cursor = _anchor = a;
    }

    int WordBack(int from)
    {
        int i = from;
        while (i > _wordStart && _text[i - 1] == ' ') i--;
        while (i > _wordStart && _text[i - 1] != ' ') i--;
        return i;
    }

    int WordFwd(int from)
    {
        int len = _text.Length, i = from;
        while (i < len && _text[i] == ' ') i++;
        while (i < len && _text[i] != ' ') i++;
        return i;
    }

    // stream mode: speak everything past the spoken marker
    void SubmitWord()
    {
        if (_text.Length <= _wordStart) return;
        string w = _text.ToString(_wordStart, _text.Length - _wordStart).Trim();
        _wordStart = _text.Length;
        if (w.Length > 0) _tts?.Speak(w);
    }

    void CtrlLetter(uint vk)
    {
        switch ((char)('A' + (vk - 0x41)))
        {
            case 'A':
                lock (_textLock) { _anchor = _wordStart; _cursor = _text.Length; }
                Snapshot();
                break;
            case 'C': CopySel(false); break;
            case 'X': CopySel(true); break;
            case 'V': Paste(); break;
        }
    }

    void CopySel(bool cut)
    {
        string s;
        lock (_textLock)
        {
            Normalize();
            int a = Math.Min(_anchor, _cursor), b = Math.Max(_anchor, _cursor);
            s = a < b ? _text.ToString(a, b - a) : "";
            if (cut && a < b) { _text.Remove(a, b - a); _cursor = _anchor = a; }
        }
        Snapshot();
        if (s.Length > 0)
            Ui.Enqueue(() => { try { Clipboard.SetText(s); } catch { } });
    }

    void Paste()
    {
        Ui.Enqueue(() =>
        {
            string s = "";
            try { s = Clipboard.GetText(); } catch { }
            if (s.Length == 0) return;
            s = s.Replace("\r", "").Replace("\n", " ");
            lock (_textLock)
            {
                Normalize();
                DeleteSel();
                _text.Insert(_cursor, s);
                _cursor += s.Length; _anchor = _cursor;
            }
            Snapshot();
        });
    }

    void Snapshot()
    {
        lock (_textLock)
        {
            TypedText = _text.ToString();
            UnspokenText = _wordStart < _text.Length
                ? _text.ToString(_wordStart, _text.Length - _wordStart) : "";
            Cursor = _cursor; SelStart = _anchor; SelEnd = _cursor;
        }
    }

    void ToggleTyping()
    {
        if (Mode == Mode.Game)
        {
            SyncFromPhysical();   // re-derive key state from the OS before pinning

            _pinned.Clear();
            foreach (var vk in _held)
                if (vk != BindTypingVk && vk != BindStreamVk && vk != RadialTriggerVk &&
                    vk != Native.VK_ESCAPE && vk != Native.VK_CAPITAL)
                    _pinned.Add(vk);

            _pinnedMouse.Clear();
            foreach (uint mvk in MouseButtons)
                if ((Native.GetAsyncKeyState((int)mvk) & 0x8000) != 0) _pinnedMouse.Add(mvk);

            Mode = Mode.Typing;
            PinnedPreview = string.Join(",", _pinned.Select(v => ((Keys)v).ToString()));
            Snapshot();
            Log.Enqueue("TYPING ON — pinned keys: " + PinnedPreview);
        }
        else
        {
            // release anything pinned that the user is no longer holding
            if (FlushOnClose)
            {
                foreach (var vk in _pinned)
                    if (!_held.Contains(vk)) Injector.Send(vk, keyUp: true, Hide.Never);
                foreach (uint mvk in _pinnedMouse)
                    if ((Native.GetAsyncKeyState((int)mvk) & 0x8000) == 0)
                        Injector.MouseButton(mvk, down: false, magic: false);
            }
            _pinned.Clear();
            _pinnedMouse.Clear();
            PinnedPreview = "";
            Mode = Mode.Game;

            // drop the spoken part; unspoken text survives for next time
            lock (_textLock)
            {
                if (_wordStart > 0)
                {
                    int len = _wordStart;
                    _text.Remove(0, len);
                    _wordStart = 0;
                    _cursor = Math.Max(0, _cursor - len);
                    _anchor = Math.Max(0, _anchor - len);
                    Snapshot();
                }
            }
        }
    }

    // Detects whether hidden injections update the async key state on this
    // machine. If they do (Mode A) we can keep our injections invisible; if not
    // (Mode B) they must be visible or the game's own key tracking breaks.
    void SelfTest()
    {
        Injector.Send(Native.VK_F13, keyUp: false, Hide.Always);
        bool held = SpinFor(() => (Native.GetAsyncKeyState((int)Native.VK_F13) & 0x8000) != 0, 100);
        Injector.Send(Native.VK_F13, keyUp: true, Hide.Always);
        SpinFor(() => (Native.GetAsyncKeyState((int)Native.VK_F13) & 0x8000) == 0, 100);

        Injector.SetModeA(held);
        SelfTestResult = held
            ? "hidden injections update async state"
            : "injections must be visible (duplicates are harmless)";
        Log.Enqueue("self-test: " + SelfTestResult);
    }

    static bool SpinFor(Func<bool> cond, int ms)
    {
        for (int i = 0; i < ms; i++) { if (cond()) return true; Thread.Sleep(1); }
        return cond();
    }

    public void Dispose()
    {
        Native.PostThreadMessage(_pumpThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _pump.Join(1000);
    }
}

// Sends keyboard/mouse input on background threads. Everything injected is
// tagged (dwExtraInfo) so the hooks can tell it apart from real user input.
internal sealed class Injector
{
    readonly BlockingCollection<(uint vk, bool keyUp, Hide hide)> _q = new();
    readonly BlockingCollection<(uint flags, uint data, int dx, int dy, bool magic)> _mq = new();
    volatile bool _modeA;

    public Injector()
    {
        new Thread(() =>
        {
            foreach (var (vk, keyUp, hide) in _q.GetConsumingEnumerable())
            {
                var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
                input.u.ki = new Native.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    dwFlags = keyUp ? Native.KEYEVENTF_KEYUP : 0,
                    dwExtraInfo = (IntPtr)(hide == Hide.Always || (hide == Hide.IfModeA && _modeA)
                                           ? Native.MAGIC_HIDE : Native.MAGIC_VISIBLE)
                };
                var arr = new[] { input };
                if (Native.SendInput(1, arr, Marshal.SizeOf<Native.INPUT>()) != 1)
                    Log.Enqueue($"SendInput failed: {Marshal.GetLastWin32Error()}");
            }
        })
        { IsBackground = true, Name = "injector" }.Start();

        new Thread(() =>
        {
            foreach (var (flags, data, dx, dy, magic) in _mq.GetConsumingEnumerable())
            {
                var input = new Native.INPUT { type = Native.INPUT_MOUSE };
                input.u.mi = new Native.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = data,
                    dwFlags = flags,
                    dwExtraInfo = magic ? (IntPtr)Native.MAGIC_HIDE : IntPtr.Zero
                };
                var arr = new[] { input };
                if (Native.SendInput(1, arr, Marshal.SizeOf<Native.INPUT>()) != 1)
                    Log.Enqueue($"SendInput (mouse) failed: {Marshal.GetLastWin32Error()}");
            }
        })
        { IsBackground = true, Name = "mouse-injector" }.Start();
    }

    public void SetModeA(bool modeA) => _modeA = modeA;
    public void Send(uint vk, bool keyUp, Hide hide) => _q.Add((vk, keyUp, hide));
    public void MouseMove(int dx, int dy) => _mq.Add((Native.MOUSEEVENTF_MOVE, 0, dx, dy, true));
    public void MouseWheel(int delta) =>
        _mq.Add((Native.MOUSEEVENTF_WHEEL, unchecked((uint)delta), 0, 0, true));
    public void MouseButton(uint vk, bool down, bool magic)
    {
        uint flags, data = 0;
        switch (vk)
        {
            case 0x01: flags = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
            case 0x02: flags = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
            case 0x04: flags = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
            case 0x05: flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; break;
            default: flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; break;
        }
        _mq.Add((flags, data, 0, 0, magic));
    }
}