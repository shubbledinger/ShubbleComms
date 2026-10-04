using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

// Dictionary + n-gram prediction engine for typing help. All lookups from the
// hook thread are in-memory; all file I/O happens on the engine's own thread,
// which polls the active voice's language, loads the matching dictionary, and
// persists learned data.
//
// Dictionary files: one word per line, optionally "word<TAB>count" (or a space —
// both accepted). Searched in %APPDATA%\ShubbleComms\dictionaries\<lang>.txt,
// then <exe>\dictionaries\<lang>.txt, then <exe>\<lang>.txt.
//
// Validity: a word can only APPEAR as a suggestion (or count as "known" to
// autocorrect) if it's in the shipped list or was added with the add-word bind.
// Usage-derived data — unigram counts, bigrams, trigrams — only SCORES
// candidates, so misspellings and random strings typed once never enter the
// autocomplete pool.
internal sealed class WordEngine
{
    readonly ITts _tts;
    readonly object _gate = new();     // guards the learned tables
    readonly Thread _thread;

    // the shipped word list: alphabetically sorted (prefix scans are a binary
    // search + walk) with frequency riding along; swapped atomically on load
    sealed class ListModel
    {
        public string[] Words = Array.Empty<string>();
        public long[] Freq = Array.Empty<long>();
    }
    volatile ListModel _model = new();

    // learned tables: mutated by the hook thread, persisted by the engine thread
    Dictionary<string, long> _added = new();     // manually added — VALID words
    Dictionary<string, long> _counts = new();    // usage counts — scoring only
    Dictionary<string, Dictionary<string, long>> _bigrams = new();
    Dictionary<string, Dictionary<string, long>> _trigrams = new();
    bool _dirty;

    string _lang = "";    // current short language code ("en"); engine thread only

    public string DictionariesDir => Path.Combine(AppData.Folder, "dictionaries");

    public WordEngine(ITts tts)
    {
        _tts = tts;
        _thread = new Thread(Run) { IsBackground = true, Name = "words" };
        _thread.Start();
    }

    // ---- queries (hook thread) ----

    public bool HasList => _model.Words.Length > 0;

    public bool Known(string word)
    {
        if (word.Length == 0) return true;
        word = word.ToLowerInvariant();
        lock (_gate) return IsValidLocked(word) || ApostropheValidLocked(word);
    }

    // "dont" counts as a word if "don't" is one: insert a single apostrophe at
    // every position and check the result against the list + added words.
    // Typing fast skips apostrophes; that's not a typo worth correcting.
    // Only runs for words NOT already valid, so it can never hijack a real word.
    bool ApostropheValidLocked(string word)   // caller holds _gate
    {
        var m = _model;
        for (int i = 0; i <= word.Length; i++)
        {
            string candidate = word.Insert(i, "'");
            if (_added.ContainsKey(candidate)) return true;
            int idx = LowerBound(m.Words, candidate);
            if (idx < m.Words.Length && m.Words[idx] == candidate) return true;
        }
        return false;
    }

    // valid = in the shipped list, or added with the add-word bind
    bool IsValidLocked(string word)   // caller holds _gate
    {
        if (_added.ContainsKey(word)) return true;
        var m = _model;
        int i = LowerBound(m.Words, word);
        return i < m.Words.Length && m.Words[i] == word;
    }

    // completions for a typed prefix; context words boost candidates that
    // followed them in the user's history. The exact word itself is excluded —
    // completing "banana" to "banana" is noise.
    public List<string> Complete(string prefix, string prev1, string prev2, string[] recent)
    {
        var scores = new Dictionary<string, double>();
        lock (_gate)
        {
            // n-gram continuations — high quality, tiny sets, valid words only
            if (prev2.Length > 0 && _trigrams.TryGetValue(prev2 + " " + prev1, out var tri))
                foreach (var (w, c) in tri)
                    if (w.StartsWith(prefix, StringComparison.Ordinal) && IsValidLocked(w))
                        Bump(scores, w, c * 4);
            if (prev1.Length > 0 && _bigrams.TryGetValue(prev1, out var bi))
                foreach (var (w, c) in bi)
                    if (w.StartsWith(prefix, StringComparison.Ordinal) && IsValidLocked(w))
                        Bump(scores, w, c * 2);

            // manually added words
            foreach (var (w, c) in _added)
                if (w.StartsWith(prefix, StringComparison.Ordinal))
                    Bump(scores, w, 2 + c);

            // words already used in this message — people reuse words
            if (recent != null)
                foreach (var w in recent)
                    if (w.StartsWith(prefix, StringComparison.Ordinal) && IsValidLocked(w))
                        Bump(scores, w, 2);

            // the shipped list: binary search + capped walk
            var m = _model;
            int lo = LowerBound(m.Words, prefix);
            int cap = Math.Min(m.Words.Length, lo + 250);
            for (int i = lo; i < cap && m.Words[i].StartsWith(prefix, StringComparison.Ordinal); i++)
                Bump(scores, m.Words[i], m.Freq[i] > 0 ? 1.0 / (1 + i) + 0.5 : 0.5);

            // usage frequency boost (capped, so an old favorite can't dominate forever)
            var keys = scores.Keys.ToArray();
            foreach (var w in keys)
                if (_counts.TryGetValue(w, out long c))
                    scores[w] += Math.Min(c, 8) * 0.5;
        }

        scores.Remove(prefix);
        return Top(scores, 3);
    }

    // next-word predictions right after a space — learned context only, valid
    // words only; a cold model predicts nothing rather than suggesting "the"
    public List<string> Predict(string prev1, string prev2, string[] recent)
    {
        var scores = new Dictionary<string, double>();
        lock (_gate)
        {
            if (prev2.Length > 0 && _trigrams.TryGetValue(prev2 + " " + prev1, out var tri))
                foreach (var (w, c) in tri)
                    if (IsValidLocked(w)) Bump(scores, w, c * 4);
            if (prev1.Length > 0 && _bigrams.TryGetValue(prev1, out var bi))
                foreach (var (w, c) in bi)
                    if (IsValidLocked(w)) Bump(scores, w, c * 2);
            if (recent != null)
                foreach (var w in recent)
                    if (IsValidLocked(w)) Bump(scores, w, 1);
            var keys = scores.Keys.ToArray();
            foreach (var w in keys)
                if (_counts.TryGetValue(w, out long c))
                    scores[w] += Math.Min(c, 8) * 0.5;
        }
        return Top(scores, 3);
    }

    // autocorrect: the word is unknown and one candidate is an obvious fix —
    // distance 1, or distance 2 sharing the first letter. Candidates are
    // RANKED: distance first, then edit class (letter-preserving transposition
    // "yaeh" -> "yeah", dropped doubles "helo" -> "hello"), then the SCORE —
    // list frequency, user usage, and the N-GRAM CONTEXT. Score outranks
    // length proximity, which is the whole point: "tst" after "epic sigma"
    // becomes "test" (trigram evidence) even though tot/tat/tit are same-length
    // distance-1 matches. A complete tie on every signal still refuses.
    public string? Correct(string word, string prev1, string prev2)
    {
        if (!HasList || word.Length < 2) return null;
        word = word.ToLowerInvariant();
        if (Known(word)) return null;

        var cands = new List<(int dist, int kind, int lenDiff, double score, string w)>();
        void Consider(string cand, double baseScore)
        {
            if (Math.Abs(cand.Length - word.Length) > 2) return;
            int d = EditDistance(word, cand, 2);
            if (d > 2) return;
            if (d == 2 && cand[0] != word[0]) return;
            int kind = LettersEqual(word, cand) ? 0
                     : IsDoubling(word, cand) ? 1 : 2;
            cands.Add((d, kind, Math.Abs(cand.Length - word.Length), baseScore, cand));
        }

        var m = _model;
        for (int i = 0; i < m.Words.Length; i++)
            Consider(m.Words[i], m.Freq[i] > 0 ? Math.Min(m.Freq[i], 100000) / 100000.0 : 0);

        lock (_gate)
        {
            foreach (var (w, c) in _added)
                Consider(w, 2 + Math.Min(c, 8));
            // usage frequency AND the n-gram context break ties the list can't:
            // "haet" between "hate" and "heat" comes down to which one the user
            // actually writes after these words. No data on either -> they still
            // tie completely -> we still refuse to guess.
            for (int i = 0; i < cands.Count; i++)
            {
                double s = cands[i].score;
                if (_counts.TryGetValue(cands[i].w, out long c))
                    s += Math.Min(c, 8) * 0.25;
                if (prev2.Length > 0 && _trigrams.TryGetValue(prev2 + " " + prev1, out var tri) &&
                    tri.TryGetValue(cands[i].w, out var tc))
                    s += tc * 4;
                if (prev1.Length > 0 && _bigrams.TryGetValue(prev1, out var bi) &&
                    bi.TryGetValue(cands[i].w, out var bc))
                    s += bc * 2;
                if (s != cands[i].score)
                    cands[i] = (cands[i].dist, cands[i].kind, cands[i].lenDiff, s, cands[i].w);
            }
        }

        if (cands.Count == 0) return null;
        // score (freq + usage + n-gram context) ranks ABOVE length proximity
        cands.Sort((a, b) =>
        {
            int c = a.dist.CompareTo(b.dist); if (c != 0) return c;
            c = a.kind.CompareTo(b.kind); if (c != 0) return c;
            c = b.score.CompareTo(a.score); if (c != 0) return c;
            c = a.lenDiff.CompareTo(b.lenDiff); if (c != 0) return c;
            return string.CompareOrdinal(a.w, b.w);
        });

        if (cands.Count > 1)
        {
            var a = cands[0]; var b = cands[1];
            if (a.dist == b.dist && a.kind == b.kind && a.score == b.score && a.lenDiff == b.lenDiff)
                return null;   // genuinely ambiguous — refuse to guess
        }
        return cands[0].w;
    }

    // same letters, different order — the transposition typo class. The
    // strongest ranking signal after edit distance: a word with all the right
    // letters just out of order beats words that lost or changed one.
    static bool LettersEqual(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var ca = a.ToCharArray();
        var cb = b.ToCharArray();
        Array.Sort(ca);
        Array.Sort(cb);
        for (int i = 0; i < ca.Length; i++)
            if (ca[i] != cb[i]) return false;
        return true;
    }

    // one word is the other with a doubled letter lost or gained — the
    // dropped-double typo class ("helo" -> "hello", "helloo" -> "hello").
    // Ranks between transposition and everything else: without it, same-length
    // substitutions ("hero", "halo") outrank the doubling fix.
    static bool IsDoubling(string a, string b)
    {
        string longer = a.Length > b.Length ? a : b;
        string shorter = longer == a ? b : a;
        if (longer.Length - shorter.Length != 1) return false;
        for (int i = 0; i < longer.Length; i++)
        {
            bool doubled = (i > 0 && longer[i] == longer[i - 1]) ||
                           (i + 1 < longer.Length && longer[i] == longer[i + 1]);
            if (doubled && longer.Remove(i, 1) == shorter) return true;
        }
        return false;
    }

    // ---- learning (hook thread) ----

    // learn from a sent message: unigram/bigram/trigram counts — scoring only;
    // nothing becomes a valid word just by being typed
    public void Learn(string text)
    {
        if (text.Length == 0) return;
        var words = text.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        lock (_gate)
        {
            foreach (var w in words)
            {
                _counts.TryGetValue(w, out long c);
                _counts[w] = c + 1;
            }
            for (int i = 1; i < words.Length; i++)
            {
                if (!_bigrams.TryGetValue(words[i - 1], out var bi))
                    _bigrams[words[i - 1]] = bi = new Dictionary<string, long>();
                bi.TryGetValue(words[i], out long c);
                bi[words[i]] = c + 1;
            }
            for (int i = 2; i < words.Length; i++)
            {
                string key = words[i - 2] + " " + words[i - 1];
                if (!_trigrams.TryGetValue(key, out var tri))
                    _trigrams[key] = tri = new Dictionary<string, long>();
                tri.TryGetValue(words[i], out long c);
                tri[words[i]] = c + 1;
            }
            _dirty = true;
        }
    }

    // manual add (the add-word bind): the word becomes valid and suggestible
    public void AddWord(string word)
    {
        if (word.Length == 0) return;
        lock (_gate)
        {
            _added.TryGetValue(word, out long c);
            _added[word] = c + 1;
            _counts.TryGetValue(word, out long c2);
            _counts[word] = c2 + 1;
            _dirty = true;
        }
    }

    // ---- engine thread ----

    void Run()
    {
        while (true)
        {
            try
            {
                string lang = ShortLang(_tts.Language);
                if (lang != _lang)
                {
                    Save();
                    _lang = lang;
                    if (lang.Length == 0)
                    {
                        Log.Enqueue("dictionary: the active voice reports no language — suggestions off");
                        WriteDebug("no language from the active voice");
                    }
                    else Load(lang);
                }
                else if (Dirty()) Save();
            }
            catch (Exception ex) { Log.Enqueue("dictionary: " + ex.Message); }
            Thread.Sleep(500);
        }
    }

    bool Dirty() { lock (_gate) return _dirty; }

    // "en-us" / "en_US" / "en" -> "en"
    static string ShortLang(string lang)
    {
        if (string.IsNullOrEmpty(lang)) return "";
        int dash = lang.IndexOf('-');
        if (dash < 0) dash = lang.IndexOf('_');
        return (dash > 0 ? lang[..dash] : lang).ToLowerInvariant();
    }

    void Load(string lang)
    {
        // AppData copy first (user's own), then the shipped one, then flat next
        // to the exe
        string file = Path.Combine(DictionariesDir, lang + ".txt");
        if (!File.Exists(file)) file = Path.Combine(AppContext.BaseDirectory, "dictionaries", lang + ".txt");
        if (!File.Exists(file)) file = Path.Combine(AppContext.BaseDirectory, lang + ".txt");

        var model = new ListModel();
        if (File.Exists(file))
        {
            var words = new List<(string w, long f)>();
            foreach (var raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                long f = 0;
                string w = line;
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                if (sp > 0)
                {
                    w = line[..sp].Trim();
                    long.TryParse(line[(sp + 1)..].Trim(), out f);
                }
                if (w.Length > 0) words.Add((w.ToLowerInvariant(), f));
            }
            var arr = words.OrderBy(kv => kv.w, StringComparer.Ordinal).ToArray();
            model.Words = arr.Select(kv => kv.w).ToArray();
            model.Freq = arr.Select(kv => kv.f).ToArray();
        }
        lock (_gate) { _model = model; }

        string status = model.Words.Length > 0
            ? $"lang '{lang}': {model.Words.Length} words from {file}"
            : $"lang '{lang}': no dictionary file (looked in {DictionariesDir}, " +
              $"{Path.Combine(AppContext.BaseDirectory, "dictionaries")}, and beside the exe)";
        Log.Enqueue("dictionary: " + status);
        WriteDebug(status);

        // learned data for this language
        var added = new Dictionary<string, long>();
        var counts = new Dictionary<string, long>();
        var bi = new Dictionary<string, Dictionary<string, long>>();
        var tri = new Dictionary<string, Dictionary<string, long>>();
        try
        {
            string learnFile = Path.Combine(DictionariesDir, "learn-" + lang + ".json");
            if (File.Exists(learnFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(learnFile));
                var root = doc.RootElement;
                if (root.TryGetProperty("added", out var aj))
                    foreach (var p in aj.EnumerateObject()) added[p.Name] = p.Value.GetInt64();
                if (root.TryGetProperty("counts", out var cj))
                    foreach (var p in cj.EnumerateObject()) counts[p.Name] = p.Value.GetInt64();
                if (root.TryGetProperty("bigrams", out var bj))
                    foreach (var p in bj.EnumerateObject())
                    {
                        var inner = new Dictionary<string, long>();
                        foreach (var q in p.Value.EnumerateObject()) inner[q.Name] = q.Value.GetInt64();
                        bi[p.Name] = inner;
                    }
                if (root.TryGetProperty("trigrams", out var tj))
                    foreach (var p in tj.EnumerateObject())
                    {
                        var inner = new Dictionary<string, long>();
                        foreach (var q in p.Value.EnumerateObject()) inner[q.Name] = q.Value.GetInt64();
                        tri[p.Name] = inner;
                    }
            }
        }
        catch (Exception ex) { Log.Enqueue("dictionary: learned data unreadable: " + ex.Message); }
        lock (_gate)
        {
            _added = added;
            _counts = counts;
            _bigrams = bi;
            _trigrams = tri;
            _dirty = false;
        }
    }

    void Save()
    {
        if (_lang.Length == 0) return;
        try
        {
            string json;
            lock (_gate)
            {
                _dirty = false;
                var data = new Dictionary<string, object?>
                {
                    ["added"] = _added,
                    ["counts"] = _counts,
                    ["bigrams"] = _bigrams,
                    ["trigrams"] = _trigrams,
                };
                json = JsonSerializer.Serialize(data);
            }
            Directory.CreateDirectory(DictionariesDir);
            File.WriteAllText(Path.Combine(DictionariesDir, "learn-" + _lang + ".json"), json);
        }
        catch (Exception ex) { Log.Enqueue("dictionary: save failed: " + ex.Message); }
    }

    // engine status lands here so "did it load" never needs guessing
    static void WriteDebug(string line)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dictionary-debug.txt"), line); }
        catch { }
    }

    // ---- helpers ----

    static void Bump(Dictionary<string, double> scores, string w, double add)
    {
        scores.TryGetValue(w, out double s);
        scores[w] = s + add;
    }

    static List<string> Top(Dictionary<string, double> scores, int n)
    {
        var list = new List<string>();
        foreach (var kv in scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
        {
            list.Add(kv.Key);
            if (list.Count == n) break;
        }
        return list;
    }

    static int LowerBound(string[] list, string prefix)
    {
        int lo = 0, hi = list.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (string.CompareOrdinal(list[mid], prefix) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // Damerau-Levenshtein (optimal string alignment) with a distance cap —
    // transpositions ("liek" -> "like") count as a single edit
    static int EditDistance(string a, string b, int cap)
    {
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > cap) return cap + 1;
        int[] prev = new int[lb + 1], cur = new int[lb + 1], pprev = new int[lb + 1];
        for (int j = 0; j <= lb; j++) prev[j] = j;
        for (int i = 1; i <= la; i++)
        {
            cur[0] = i;
            int rowMin = i;
            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(prev[j] + 1,                 // deletion
                           Math.Min(cur[j - 1] + 1,           // insertion
                                    prev[j - 1] + cost));     // substitution
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, pprev[j - 2] + 1);        // transposition
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > cap) return cap + 1;                 // whole row is hopeless
            var t = pprev; pprev = prev; prev = cur; cur = t;
        }
        return prev[lb];
    }
}