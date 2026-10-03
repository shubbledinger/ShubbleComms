using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Abbreviation dictionary, applied speech-side only (the display always shows
// what was typed). Entries:
//   abbr -> expansion          (every voice)
//   abbr -> expansion #en      (voices whose culture starts with "en", e.g. en-US)
//   abbr -> expansion @glados  (voices whose NAME contains "glados" — the piper
//                               model file glados.onnx matches; SAPI names match
//                               too, e.g. @zira)
// Longest abbreviation wins; it must not be flanked by letters inside a word;
// one replacement per word. The file auto-reloads the moment you save it.
internal static class Abbrev
{
    sealed class Entry { public string Expansion = ""; public string? Tag; public bool VoiceTag; }

    static readonly Dictionary<string, List<Entry>> _map = new();
    static DateTime _stamp;
    static int _count;

    public static string FilePath => Path.Combine(AppData.Folder, "abbreviations.txt");

    public static string Expand(string text, string voiceLang, string voiceName)
    {
        EnsureLoaded();
        if (_count == 0 || text.Length == 0) return text;

        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
            words[i] = ExpandWord(words[i], voiceLang, voiceName);
        return string.Join(" ", words);
    }

    static string ExpandWord(string word, string voiceLang, string voiceName)
    {
        string lower = word.ToLowerInvariant();

        // every occurrence of every abbreviation, then longest-first / earliest-position
        var hits = new List<(int len, int pos, string abbr, Entry e)>();
        foreach (var (abbr, list) in _map)
        {
            int idx = lower.IndexOf(abbr, StringComparison.Ordinal);
            while (idx >= 0)
            {
                foreach (var e in list) hits.Add((abbr.Length, idx, abbr, e));
                idx = lower.IndexOf(abbr, idx + 1, StringComparison.Ordinal);
            }
        }
        hits.Sort((x, y) => x.len != y.len ? y.len - x.len : x.pos - y.pos);

        foreach (var h in hits)
        {
            bool beforeLetter = h.pos > 0 && IsLetter(lower[h.pos - 1]);
            bool afterLetter = h.pos + h.abbr.Length < lower.Length && IsLetter(lower[h.pos + h.abbr.Length]);
            if (beforeLetter || afterLetter) continue;
            if (!TagMatches(h.e, voiceLang, voiceName)) continue;

            return word.Substring(0, h.pos) + h.e.Expansion + word.Substring(h.pos + h.abbr.Length);
        }
        return word;
    }

    // no tag = all voices. "#xx" = language prefix match (unknown voice expands
    // all). "@xx" = voice NAME contains xx.
    static bool TagMatches(Entry e, string voiceLang, string voiceName)
    {
        if (string.IsNullOrEmpty(e.Tag)) return true;
        if (e.VoiceTag)
        {
            return !string.IsNullOrEmpty(voiceName) &&
                   voiceName.ToLowerInvariant().Contains(e.Tag, StringComparison.Ordinal);
        }
        if (string.IsNullOrEmpty(voiceLang)) return true;
        string v = voiceLang.ToLowerInvariant();
        return v.StartsWith(e.Tag, StringComparison.Ordinal) || e.Tag.StartsWith(v, StringComparison.Ordinal);
    }

    static bool IsLetter(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' || c >= (char)192;   // include accented range

    static void EnsureLoaded()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, DefaultFile);

            var fi = new FileInfo(FilePath);
            if (fi.LastWriteTime == _stamp) return;
            _stamp = fi.LastWriteTime;

            _map.Clear(); _count = 0;
            foreach (var raw in File.ReadAllLines(fi.FullName))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                string abbr, expansion; string? tag = null; bool voiceTag = false;
                var m = Regex.Match(line, @"^(.+?)\s*->\s*(.+?)\s*([#@])([\w.-]+)$");   // [\w.-] so #en-GB and @glados.onnx parse
                if (m.Success)
                {
                    abbr = m.Groups[1].Value.Trim().ToLowerInvariant();
                    expansion = m.Groups[2].Value.Trim();
                    voiceTag = m.Groups[3].Value[0] == '@';
                    tag = m.Groups[4].Value.Trim().ToLowerInvariant();
                }
                else
                {
                    int i = line.IndexOf("->", StringComparison.Ordinal);
                    if (i < 0) continue;
                    abbr = line[..i].Trim().ToLowerInvariant();
                    expansion = line[(i + 2)..].Trim();
                }
                if (abbr.Length == 0 || expansion.Length == 0) continue;

                if (!_map.TryGetValue(abbr, out var list)) _map[abbr] = list = new List<Entry>();
                list.Add(new Entry { Expansion = expansion, Tag = tag, VoiceTag = voiceTag });
                _count++;
            }
        }
        catch (Exception ex)
        {
            Log.Enqueue("abbreviations error: " + ex.Message);
        }
    }

    const string DefaultFile = @"
# Abbreviations — applied to speech only; the display shows what you typed.
#   abbr -> expansion           (every voice)
#   abbr -> expansion #en       (any English voice: en-US, en-GB, ...)
#   abbr -> expansion #en-GB    (country-specific: ONLY British voices)
#   abbr -> expansion @glados   (ONLY voices whose name contains 'glados' —
#                                e.g. the piper model glados.onnx)
#   abbr -> expansion @zira     (SAPI voice names match too, by substring)
# Longest abbreviation wins; it must not be surrounded by letters inside a word.
# This file reloads automatically the moment you save it.

mb -> my bad #en
mf -> motherfucker #en
im -> i'm
ur -> your
gl -> good luck
gn -> good night
mr -> mister #en
dr -> doctor #en
ig -> i guess #en
af -> as fuck #en
sh -> sjesje #en
fr -> for real
yk -> you know
rq -> real quick
tf -> the fuck #en
ik -> i know #en
rn -> right now #en
dw -> don't worry #en
dr. -> doctor #en
tbf -> to be fair #en
atp -> at this point #en
cya -> see yuh
omw -> on my way #en
tmr -> tomorrow #en
wtv -> whatever #en
kys -> kill yourself
fym -> fuck you mean #en
nvm -> nevermind
btw -> by the way #en
abt -> about
mrs -> misses #en
imo -> in my opinion
asf -> as fuck #en
wya -> where you at #en
ngl -> not gonna lie #en
icl -> i can't lie #en
brb -> be right back
lol -> lawl #en
idk -> i don't know #en
idc -> i don't care #en
tbh -> to be honest #en
wtf -> what the fuck #en
ikr -> i know right #en
lmk -> let me know #en
sum -> something #en
gtg -> gotta go #en
gif -> ghif #en
ofc -> of course #en
sec -> seck #en
ww1 -> world war 1
ww2 -> world war 2
res -> rez #en
ppl -> people #en
smth -> something #en
msgs -> messages
idfk -> i don't fucking know #en
idfc -> i don't fucking care #en
idek -> i don't even know #en
lmao -> luhmaow #en
syfm -> shut your fucking mouth #en
stfu -> shut the fuck up #en
idrc -> i don't really care #en
wdym -> what do you mean #en
idgaf -> i don't give a fuck #en
afaik -> as far as i know #en
takin -> taking #en
niche -> neesh #en
retcon -> reht-con #en
ensign -> ensin #en
glados -> gladdos #en
glados -> gladdaus @glados
";
}