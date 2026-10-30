using System.Collections.Concurrent;
using System.Globalization;
using PdfNetStack.Text;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Indic shaping categories of characters (Microsoft script development specifications, with
/// HarfBuzz's refinements). The Khmer and Myanmar shapers share them and add their own.
/// </summary>
internal enum IndicCategory : byte
{
    X,
    C,
    V,
    N,
    H,
    Zwnj,
    Zwj,
    M,
    SM,
    A,
    Placeholder,
    DottedCircle,
    RS,
    Repha,
    Ra,
    CM,
    Symbol,
    CS,

    // Khmer and Myanmar: vowel signs by side, Khmer robat and sign groups, Myanmar asat, medials,
    // pwo tones and variation selectors.
    VAbv,
    VBlw,
    VPre,
    VPst,
    Robatic,
    Xgroup,
    Ygroup,
    As,
    MH,
    MR,
    MW,
    MY,
    ML,
    PT,
    VS,
}

/// <summary>Positions in a syllable, in the order the syllable is sorted into.</summary>
internal enum IndicPosition : byte
{
    Start,
    RaToBecomeReph,
    PreM,
    PreC,
    BaseC,
    AfterMain,
    AboveC,
    BeforeSub,
    BelowC,
    AfterSub,
    BeforePost,
    PostC,
    AfterPost,
    Smvd,
    End,
}

/// <summary>How a script writes reph: from Ra and virama, only with an explicit ZWJ, or as a separate logical character.</summary>
internal enum RephMode
{
    Implicit,
    Explicit,
    LogicalRepha,
}

/// <summary>A script's shaping parameters.</summary>
/// <param name="Script">ISO 15924 tag in lower case, as OpenType's old Indic tags.</param>
/// <param name="Virama">The script's virama.</param>
/// <param name="RephPosition">Where reph goes in final reordering.</param>
/// <param name="RephMode">How reph is written.</param>
/// <param name="BelowFormsBeforeBase">Whether consonants before the base take below forms too (new specification only).</param>
internal sealed record IndicConfig(string Script, int Virama, IndicPosition RephPosition, RephMode RephMode, bool BelowFormsBeforeBase);

/// <summary>What the Indic shaper knows about one font and script: specification version, virama glyph, which features it has.</summary>
internal sealed class IndicPlan(IndicConfig config)
{
    private readonly ConcurrentDictionary<int, IndicPosition> _consonantPositions = new();

    public IndicConfig Config { get; } = config;

    /// <summary>The font uses the old (deva, beng...) rather than the version 2 (dev2, bng2...) specification.</summary>
    public bool OldSpec { get; init; }

    public int ViramaGlyph { get; init; }

    /// <summary>Would-substitute tests ignore rules with context; the old specification and Malayalam allow context.</summary>
    public bool ZeroContext { get; init; }

    public int[] Rphf { get; init; } = [];

    public int[] Pref { get; init; } = [];

    public int[] Blwf { get; init; } = [];

    public int[] Pstf { get; init; } = [];

    public int[] Vatu { get; init; } = [];

    public uint RphfMask { get; init; }

    public uint PrefMask { get; init; }

    public uint HalfMask { get; init; }

    public uint BlwfMask { get; init; }

    public uint AbvfMask { get; init; }

    public uint PstfMask { get; init; }

    public uint InitMask { get; init; }

    /// <summary>
    /// Where a consonant goes in a syllable: below or after the base when the font has a below,
    /// post-base or pre-base form for it after a virama (either order, as fonts differ), else base.
    /// </summary>
    public IndicPosition ConsonantPosition(LayoutTable gsub, int consonant) =>
        _consonantPositions.GetOrAdd(consonant, c =>
        {
            int v = ViramaGlyph;
            if (Would(gsub, Blwf, v, c) || Would(gsub, Blwf, c, v) || Would(gsub, Vatu, v, c) || Would(gsub, Vatu, c, v)) return IndicPosition.BelowC;
            if (Would(gsub, Pstf, v, c) || Would(gsub, Pstf, c, v)) return IndicPosition.PostC;
            if (Would(gsub, Pref, v, c) || Would(gsub, Pref, c, v)) return IndicPosition.PostC;
            return IndicPosition.BaseC;
        });

    /// <summary>True when one of <paramref name="lookups"/> substitutes exactly <paramref name="glyphs"/>.</summary>
    public bool Would(LayoutTable gsub, int[] lookups, params int[] glyphs)
    {
        foreach (int l in lookups)
        {
            if (gsub.WouldSubstitute(l, glyphs, ZeroContext)) return true;
        }

        return false;
    }
}

/// <summary>
/// The Indic shaper (Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada,
/// Malayalam), from Microsoft's "Developing OpenType Fonts for ... Script" specifications, with
/// the refinements HarfBuzz and Uniscribe agree on: text splits into syllables, each syllable is
/// reordered (base consonant, reph, pre-base matras and consonants), the basic shaping forms
/// apply one feature at a time, a final reordering follows the font's substitutions, and the
/// presentation features apply to the whole syllable.
/// </summary>
internal sealed partial class Shaper
{
    private const int ConsonantFlags = 1 << (int)IndicCategory.C | 1 << (int)IndicCategory.CS | 1 << (int)IndicCategory.Ra
        | 1 << (int)IndicCategory.CM | 1 << (int)IndicCategory.V | 1 << (int)IndicCategory.Placeholder | 1 << (int)IndicCategory.DottedCircle;

    private const int JoinerFlags = 1 << (int)IndicCategory.Zwj | 1 << (int)IndicCategory.Zwnj;

    private static readonly IndicConfig[] IndicConfigs =
    [
        new("deva", 0x094D, IndicPosition.BeforePost, RephMode.Implicit, true),
        new("beng", 0x09CD, IndicPosition.AfterSub, RephMode.Implicit, true),
        new("guru", 0x0A4D, IndicPosition.BeforeSub, RephMode.Implicit, true),
        new("gujr", 0x0ACD, IndicPosition.BeforePost, RephMode.Implicit, true),
        new("orya", 0x0B4D, IndicPosition.AfterMain, RephMode.Implicit, true),
        new("taml", 0x0BCD, IndicPosition.AfterPost, RephMode.Implicit, true),
        new("telu", 0x0C4D, IndicPosition.AfterPost, RephMode.Explicit, false),
        new("knda", 0x0CCD, IndicPosition.AfterPost, RephMode.Implicit, false),
        new("mlym", 0x0D4D, IndicPosition.AfterMain, RephMode.LogicalRepha, true),
    ];

    /// <summary>Basic shaping forms, applied one feature at a time between the two reorderings.</summary>
    private static readonly string[] IndicBasicFeatures = ["nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "abvf", "half", "pstf", "vatu", "cjct"];

    /// <summary>Presentation forms and the common features, applied together after final reordering.</summary>
    private static readonly ShapingFeature[] IndicPresentationFeatures =
    [
        new("init", true, true), new("pres", true, true), new("abvs", true, true), new("blws", true, true),
        new("psts", true, true), new("haln", true, true), new("calt"), new("clig"), new("rclt"), new("rlig"),
    ];

    private enum SyllableType
    {
        Consonant,
        Vowel,
        Standalone,
        Symbol,
        Broken,
        NonIndic,
    }

    private static IndicCategory CategoryOf(GlyphInfo g) => (IndicCategory)g.IndicCategory;

    private static IndicPosition PositionOf(GlyphInfo g) => (IndicPosition)g.IndicPosition;

    /// <summary>True when the glyph's category is one of <paramref name="flags"/>; a ligature is none of them.</summary>
    private static bool IsOneOf(GlyphInfo g, long flags) => (g.Flags & GlyphFlags.Ligated) == 0 && ((1L << g.IndicCategory) & flags) != 0;

    private static bool IsConsonant(GlyphInfo g) => IsOneOf(g, ConsonantFlags);

    private static bool IsJoiner(GlyphInfo g) => IsOneOf(g, JoinerFlags);

    private static bool IsHalant(GlyphInfo g) => IsOneOf(g, 1 << (int)IndicCategory.H);

    private static bool LigatedAndDidntMultiply(GlyphInfo g) => (g.Flags & (GlyphFlags.Ligated | GlyphFlags.Multiplied)) == GlyphFlags.Ligated;

    private void SubstituteIndic(string script)
    {
        IndicPlan plan = _tables.IndicPlans.GetOrAdd(script, CreateIndicPlan);
        foreach (GlyphInfo g in _buffer)
        {
            int v = ShapingData.Indic(g.CodePoint);
            g.IndicCategory = (byte)(v & 0xFF);
            g.IndicPosition = (byte)(v >> 8);
        }

        ApplyStage(_gsub, _gsubFeatures, [new("rvrn")]);
        IndicSyllables.Find(_buffer);
        ApplyStage(_gsub, _gsubFeatures, [new("locl", PerSyllable: true), new("ccmp", PerSyllable: true)]);
        InitialReordering(plan, script);
        foreach (string tag in IndicBasicFeatures) ApplyStage(_gsub, _gsubFeatures, [new(tag, true, true)]);
        FinalReordering(plan, script);
        ApplyStage(_gsub, _gsubFeatures, IndicPresentationFeatures);
    }

    private IndicPlan CreateIndicPlan(string script)
    {
        IndicConfig config = Array.Find(IndicConfigs, c => c.Script == script)!;
        bool oldSpec = _gsubScript is not { Length: 4 } || _gsubScript[3] != '2';
        int[] Lookups(string tag) => [.. _gsubFeatures.Where(f => f.Tag == tag).SelectMany(f => f.Lookups).Distinct()];
        uint MaskIf(string tag, uint mask) => HasSubstitution(tag) ? mask : 0;
        return new IndicPlan(config)
        {
            OldSpec = oldSpec,
            ViramaGlyph = GlyphOf(config.Virama),
            ZeroContext = !oldSpec && script != "mlym",
            Rphf = Lookups("rphf"),
            Pref = Lookups("pref"),
            Blwf = Lookups("blwf"),
            Pstf = Lookups("pstf"),
            Vatu = Lookups("vatu"),
            RphfMask = MaskIf("rphf", Rphf),
            PrefMask = MaskIf("pref", Pref),
            HalfMask = MaskIf("half", Half),
            BlwfMask = MaskIf("blwf", Blwf),
            AbvfMask = MaskIf("abvf", Abvf),
            PstfMask = MaskIf("pstf", Pstf),
            InitMask = MaskIf("init", Init),
        };
    }

    // ---------------------------------------------------------------- Initial reordering

    private void InitialReordering(IndicPlan plan, string script)
    {
        if (_gsub is not null)
        {
            foreach (GlyphInfo g in _buffer)
            {
                if (PositionOf(g) == IndicPosition.BaseC) g.IndicPosition = (byte)plan.ConsonantPosition(_gsub, g.Glyph);
            }
        }

        InsertDottedCircles();
        foreach ((int start, int end) in Syllables())
        {
            var type = (SyllableType)(_buffer[start].Syllable & 0x0F);
            if (type is SyllableType.Consonant or SyllableType.Vowel or SyllableType.Standalone or SyllableType.Broken)
            {
                InitialReorderingSyllable(plan, script, start, end);
            }
        }
    }

    /// <summary>The syllables of the buffer, as glyph ranges.</summary>
    private List<(int Start, int End)> Syllables()
    {
        var result = new List<(int, int)>();
        for (int start = 0; start < _buffer.Count;)
        {
            int end = start + 1;
            while (end < _buffer.Count && _buffer[end].Syllable == _buffer[start].Syllable) end++;
            result.Add((start, end));
            start = end;
        }

        return result;
    }

    /// <summary>
    /// A syllable that starts with a mark, a virama or another sign that needs a base gets a
    /// dotted circle to stand on, after a leading repha, when the font has one.
    /// </summary>
    private void InsertDottedCircles()
    {
        if (!_buffer.Any(g => (g.Syllable & 0x0F) == (int)SyllableType.Broken) || !_font.TryGetGlyph(0x25CC, out int circle)) return;
        var output = new List<GlyphInfo>(_buffer.Count + 4);
        int last = 0;
        for (int i = 0; i < _buffer.Count;)
        {
            GlyphInfo g = _buffer[i];
            if (g.Syllable != last && (g.Syllable & 0x0F) == (int)SyllableType.Broken)
            {
                last = g.Syllable;
                while (i < _buffer.Count && _buffer[i].Syllable == last && CategoryOf(_buffer[i]) == IndicCategory.Repha) output.Add(_buffer[i++]);
                var dotted = new GlyphInfo
                {
                    Glyph = circle,
                    Cluster = g.Cluster,
                    Mask = g.Mask,
                    Syllable = g.Syllable,
                    IndicCategory = (byte)IndicCategory.DottedCircle,
                    IndicPosition = (byte)IndicPosition.End,
                };
                SetCharacter(dotted, 0x25CC);
                dotted.Class = _gdef?.HasClasses == true ? _gdef.ClassOf(circle) : GlyphClass.Base;
                output.Add(dotted);
                continue;
            }

            output.Add(g);
            i++;
        }

        _buffer = output;
    }

    private void InitialReorderingSyllable(IndicPlan plan, string script, int start, int end)
    {
        List<GlyphInfo> b = _buffer;

        // Kannada Ra, halant, ZWJ behaves as Ra, ZWJ, halant (legacy usage).
        if (script == "knda" && start + 3 <= end && IsOneOf(b[start], 1 << (int)IndicCategory.Ra)
            && IsHalant(b[start + 1]) && IsOneOf(b[start + 2], 1 << (int)IndicCategory.Zwj))
        {
            MergeClusters(start + 1, start + 3);
            (b[start + 1], b[start + 2]) = (b[start + 2], b[start + 1]);
        }

        // 1. Find the base consonant: from the end, the first consonant without a below-base or
        // post-base form, or the first consonant of the syllable. A leading Ra and halant that
        // can form reph are not candidates when another consonant follows.
        int baseIndex = end;
        bool hasReph = false;
        int limit = start;
        if (plan.RphfMask != 0 && start + 3 <= end
            && ((plan.Config.RephMode == RephMode.Implicit && !IsJoiner(b[start + 2]))
                || (plan.Config.RephMode == RephMode.Explicit && CategoryOf(b[start + 2]) == IndicCategory.Zwj)))
        {
            int third = plan.Config.RephMode == RephMode.Explicit ? b[start + 2].Glyph : 0;
            if ((_gsub is not null && plan.Would(_gsub, plan.Rphf, b[start].Glyph, b[start + 1].Glyph))
                || (plan.Config.RephMode == RephMode.Explicit && _gsub is not null && plan.Would(_gsub, plan.Rphf, b[start].Glyph, b[start + 1].Glyph, third)))
            {
                limit += 2;
                while (limit < end && IsJoiner(b[limit])) limit++;
                baseIndex = start;
                hasReph = true;
            }
        }
        else if (plan.Config.RephMode == RephMode.LogicalRepha && CategoryOf(b[start]) == IndicCategory.Repha)
        {
            limit++;
            while (limit < end && IsJoiner(b[limit])) limit++;
            baseIndex = start;
            hasReph = true;
        }

        {
            int i = end;
            bool seenBelow = false;
            do
            {
                i--;
                if (IsConsonant(b[i]))
                {
                    IndicPosition position = PositionOf(b[i]);
                    if (position != IndicPosition.BelowC && (position != IndicPosition.PostC || seenBelow))
                    {
                        baseIndex = i;
                        break;
                    }

                    if (position == IndicPosition.BelowC) seenBelow = true;
                    baseIndex = i;
                }
                else if (start < i && CategoryOf(b[i]) == IndicCategory.Zwj && CategoryOf(b[i - 1]) == IndicCategory.H)
                {
                    // A ZWJ after a halant asks for a half form and stops the search; before a
                    // halant it asks for a subjoined form, and the search goes on.
                    break;
                }
            }
            while (i > limit);
        }

        // With no other consonant, Ra stays the base and forms no reph.
        if (hasReph && baseIndex == start && limit - baseIndex <= 2) hasReph = false;

        // 2 and 3. Matras are already decomposed and marks in canonical order (normalization).
        for (int i = start; i < baseIndex; i++) b[i].IndicPosition = Math.Min((byte)IndicPosition.PreC, b[i].IndicPosition);
        if (baseIndex < end) b[baseIndex].IndicPosition = (byte)IndicPosition.BaseC;
        if (hasReph) b[start].IndicPosition = (byte)IndicPosition.RaToBecomeReph;

        // Old specification fonts: the first halant after the base moves after the last
        // consonant. Kannada does not do it when a halant is already there.
        if (plan.OldSpec)
        {
            bool disallowDoubleHalants = script == "knda";
            for (int i = baseIndex + 1; i < end; i++)
            {
                if (CategoryOf(b[i]) != IndicCategory.H) continue;
                int j = end - 1;
                while (j > i && !IsConsonant(b[j]) && !(disallowDoubleHalants && CategoryOf(b[j]) == IndicCategory.H)) j--;
                if (CategoryOf(b[j]) != IndicCategory.H && j > i)
                {
                    GlyphInfo halant = b[i];
                    b.RemoveAt(i);
                    b.Insert(j, halant);
                }

                break;
            }
        }

        // Joiners, nuktas, register shifters, medials and halants move with what comes before them.
        IndicPosition lastPosition = IndicPosition.Start;
        for (int i = start; i < end; i++)
        {
            GlyphInfo g = b[i];
            long flag = 1L << g.IndicCategory;
            if ((flag & (JoinerFlags | 1 << (int)IndicCategory.N | 1 << (int)IndicCategory.RS | 1 << (int)IndicCategory.CM | 1 << (int)IndicCategory.H)) != 0)
            {
                g.IndicPosition = (byte)lastPosition;

                // A halant after a left matra stays: it belongs to the consonant before the matra.
                if (CategoryOf(g) == IndicCategory.H && lastPosition == IndicPosition.PreM)
                {
                    for (int j = i; j > start; j--)
                    {
                        if (PositionOf(b[j - 1]) == IndicPosition.PreM) continue;
                        g.IndicPosition = b[j - 1].IndicPosition;
                        break;
                    }
                }
            }
            else if (PositionOf(g) != IndicPosition.Smvd)
            {
                lastPosition = PositionOf(g);
            }
        }

        // Post-base consonants own whatever comes before them since the last consonant or matra.
        {
            int last = baseIndex;
            for (int i = baseIndex + 1; i < end; i++)
            {
                if (IsConsonant(b[i]))
                {
                    for (int j = last + 1; j < i; j++)
                    {
                        if (b[j].IndicPosition < (byte)IndicPosition.Smvd) b[j].IndicPosition = b[i].IndicPosition;
                    }

                    last = i;
                }
                else if (CategoryOf(b[i]) == IndicCategory.M)
                {
                    last = i;
                }
            }
        }

        // Sort by position, stably, remembering where each glyph came from.
        var order = new int[end - start];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        int[] sorted = [.. order.OrderBy(k => b[start + k].IndicPosition)];
        GlyphInfo[] originals = [.. b.GetRange(start, end - start)];
        for (int i = 0; i < sorted.Length; i++) b[start + i] = originals[sorted[i]];

        // Find the base again; a run of left matras is reversed back to typing order.
        int firstLeftMatra = end, lastLeftMatra = end;
        baseIndex = end;
        for (int i = start; i < end; i++)
        {
            if (PositionOf(b[i]) == IndicPosition.BaseC)
            {
                baseIndex = i;
                break;
            }

            if (PositionOf(b[i]) == IndicPosition.PreM)
            {
                if (firstLeftMatra == end) firstLeftMatra = i;
                lastLeftMatra = i;
            }
        }

        if (firstLeftMatra < lastLeftMatra)
        {
            b.Reverse(firstLeftMatra, lastLeftMatra - firstLeftMatra + 1);
            int[] moved = [.. sorted];
            Array.Reverse(moved, firstLeftMatra - start, lastLeftMatra - firstLeftMatra + 1);
            sorted = moved;

            // Nuktas and the like that went with each matra go back after it.
            int k = firstLeftMatra;
            for (int j = k; j <= lastLeftMatra; j++)
            {
                if (CategoryOf(b[j]) != IndicCategory.M) continue;
                b.Reverse(k, j - k + 1);
                Array.Reverse(sorted, k - start, j - k + 1);
                k = j + 1;
            }
        }

        // What moved after the base merges clusters with where it came from; before the base,
        // final reordering takes care of clusters.
        if (plan.OldSpec || end - start > 127)
        {
            MergeClusters(baseIndex, end);
        }
        else
        {
            var at = new int[sorted.Length];
            for (int i = 0; i < sorted.Length; i++) at[i] = sorted[i];
            var done = new bool[sorted.Length];
            for (int i = baseIndex; i < end; i++)
            {
                if (done[i - start]) continue;
                int min = i, max = i;
                int j = start + at[i - start];
                while (j != i && !done[j - start])
                {
                    min = Math.Min(min, j);
                    max = Math.Max(max, j);
                    done[j - start] = true;
                    j = start + at[j - start];
                }

                MergeClusters(Math.Max(baseIndex, min), max + 1);
            }
        }

        // Masks: reph; half and (new specification) below forms before the base; below, above
        // and post-base forms after it.
        for (int i = start; i < end && PositionOf(b[i]) == IndicPosition.RaToBecomeReph; i++) b[i].Mask |= plan.RphfMask;
        uint preMask = plan.HalfMask | (!plan.OldSpec && plan.Config.BelowFormsBeforeBase ? plan.BlwfMask : 0);
        for (int i = start; i < baseIndex; i++) b[i].Mask |= preMask;
        uint postMask = plan.BlwfMask | plan.AbvfMask | plan.PstfMask;
        for (int i = baseIndex + 1; i < end; i++) b[i].Mask |= postMask;

        // Old Devanagari fonts form the eyelash Ra below half forms too, unless Ra, halant, ZWJ asks for it.
        if (plan.OldSpec && script == "deva")
        {
            for (int i = start; i + 1 < baseIndex; i++)
            {
                if (CategoryOf(b[i]) == IndicCategory.Ra && CategoryOf(b[i + 1]) == IndicCategory.H
                    && (i + 2 == baseIndex || CategoryOf(b[i + 2]) != IndicCategory.Zwj))
                {
                    b[i].Mask |= plan.BlwfMask;
                    b[i + 1].Mask |= plan.BlwfMask;
                }
            }
        }

        // A halant and Ra after the base that the font forms into a pre-base form.
        if (plan.PrefMask != 0 && _gsub is not null && baseIndex + 2 < end)
        {
            for (int i = baseIndex + 1; i + 1 < end; i++)
            {
                if (!plan.Would(_gsub, plan.Pref, b[i].Glyph, b[i + 1].Glyph)) continue;
                b[i].Mask |= plan.PrefMask;
                b[i + 1].Mask |= plan.PrefMask;
                break;
            }
        }

        // A ZWNJ keeps the consonants before it from taking half forms.
        for (int i = baseIndex + 1; i < end; i++)
        {
            if (!IsJoiner(b[i]) || CategoryOf(b[i]) != IndicCategory.Zwnj) continue;
            int j = i;
            do
            {
                j--;
                b[j].Mask &= ~plan.HalfMask;
            }
            while (j > start && !IsConsonant(b[j]));
        }
    }

    // ---------------------------------------------------------------- Final reordering

    private void FinalReordering(IndicPlan plan, string script)
    {
        foreach ((int start, int end) in Syllables())
        {
            var type = (SyllableType)(_buffer[start].Syllable & 0x0F);
            if (type is SyllableType.Consonant or SyllableType.Vowel or SyllableType.Standalone or SyllableType.Broken)
            {
                FinalReorderingSyllable(plan, script, start, end);
            }
        }
    }

    private void FinalReorderingSyllable(IndicPlan plan, string script, int start, int end)
    {
        List<GlyphInfo> b = _buffer;

        // A virama glyph that came out of a ligature then a multiple substitution is a halant again.
        if (plan.ViramaGlyph != 0)
        {
            for (int i = start; i < end; i++)
            {
                GlyphInfo g = b[i];
                if (g.Glyph != plan.ViramaGlyph || (g.Flags & (GlyphFlags.Ligated | GlyphFlags.Multiplied)) != (GlyphFlags.Ligated | GlyphFlags.Multiplied)) continue;
                g.IndicCategory = (byte)IndicCategory.H;
                g.Flags &= ~(GlyphFlags.Ligated | GlyphFlags.Multiplied);
            }
        }

        bool tryPref = plan.PrefMask != 0;

        // Find the base again.
        int baseIndex;
        for (baseIndex = start; baseIndex < end; baseIndex++)
        {
            if (PositionOf(b[baseIndex]) < IndicPosition.BaseC) continue;
            if (tryPref && baseIndex + 1 < end)
            {
                for (int i = baseIndex + 1; i < end; i++)
                {
                    if ((b[i].Mask & plan.PrefMask) == 0) continue;
                    if (!((b[i].Flags & GlyphFlags.Substituted) != 0 && LigatedAndDidntMultiply(b[i])))
                    {
                        // A pre-base candidate that formed nothing: the base is around here.
                        baseIndex = i;
                        while (baseIndex < end && IsHalant(b[baseIndex])) baseIndex++;
                        if (baseIndex < end) b[baseIndex].IndicPosition = (byte)IndicPosition.BaseC;
                        tryPref = false;
                    }

                    break;
                }

                if (baseIndex == end) break;
            }

            // Malayalam skips over below-base forms that did not form.
            if (script == "mlym")
            {
                for (int i = baseIndex + 1; i < end; i++)
                {
                    while (i < end && IsJoiner(b[i])) i++;
                    if (i == end || !IsHalant(b[i])) break;
                    i++;
                    while (i < end && IsJoiner(b[i])) i++;
                    if (i < end && IsConsonant(b[i]) && PositionOf(b[i]) == IndicPosition.BelowC)
                    {
                        baseIndex = i;
                        b[baseIndex].IndicPosition = (byte)IndicPosition.BaseC;
                    }
                }
            }

            if (start < baseIndex && PositionOf(b[baseIndex]) > IndicPosition.BaseC) baseIndex--;
            break;
        }

        if (baseIndex == end && start < baseIndex && IsOneOf(b[baseIndex - 1], 1 << (int)IndicCategory.Zwj)) baseIndex--;
        if (baseIndex < end)
        {
            while (start < baseIndex && IsOneOf(b[baseIndex], 1 << (int)IndicCategory.N | 1 << (int)IndicCategory.H)) baseIndex--;
        }

        // Pre-base matras move to after the last standalone halant before the base. A halant
        // followed by ZWJ keeps its half form, so the search goes on before it.
        if (start + 1 < end && start < baseIndex)
        {
            int newPosition = baseIndex == end ? baseIndex - 2 : baseIndex - 1;

            // Malayalam and Tamil have no half forms: what 'half' made are chillus or explicit viramas, and the matra goes after them.
            if (script is not ("mlym" or "taml"))
            {
                while (true)
                {
                    while (newPosition > start && !IsOneOf(b[newPosition], 1 << (int)IndicCategory.M | 1 << (int)IndicCategory.H)) newPosition--;
                    if (IsHalant(b[newPosition]) && PositionOf(b[newPosition]) != IndicPosition.PreM)
                    {
                        if (newPosition + 1 < end && CategoryOf(b[newPosition + 1]) == IndicCategory.Zwj && newPosition > start)
                        {
                            newPosition--;
                            continue;
                        }
                    }
                    else
                    {
                        newPosition = start;
                    }

                    break;
                }
            }

            if (start < newPosition && PositionOf(b[newPosition]) != IndicPosition.PreM)
            {
                for (int i = newPosition; i > start; i--)
                {
                    if (PositionOf(b[i - 1]) != IndicPosition.PreM) continue;
                    int oldPosition = i - 1;
                    if (oldPosition < baseIndex && baseIndex <= newPosition) baseIndex--;
                    GlyphInfo matra = b[oldPosition];
                    b.RemoveAt(oldPosition);
                    b.Insert(newPosition, matra);
                    MergeClusters(newPosition, Math.Min(end, baseIndex + 1));
                    newPosition--;
                }
            }
            else
            {
                for (int i = start; i < baseIndex; i++)
                {
                    if (PositionOf(b[i]) != IndicPosition.PreM) continue;
                    MergeClusters(i, Math.Min(end, baseIndex + 1));
                    break;
                }
            }
        }

        // Reph moves only when Ra and halant formed it (or a logical repha did not ligate).
        if (start + 1 < end && PositionOf(b[start]) == IndicPosition.RaToBecomeReph
            && (CategoryOf(b[start]) == IndicCategory.Repha) ^ LigatedAndDidntMultiply(b[start]))
        {
            int newReph = RephTarget(plan, start, end, baseIndex);
            MergeClusters(start, newReph + 1);
            GlyphInfo reph = b[start];
            b.RemoveAt(start);
            b.Insert(newReph, reph);
            if (start < baseIndex && baseIndex <= newReph) baseIndex--;
        }

        // A pre-base form (from halant and Ra) moves before the base, after a standalone halant.
        if (tryPref && baseIndex + 1 < end)
        {
            for (int i = baseIndex + 1; i < end; i++)
            {
                if ((b[i].Mask & plan.PrefMask) == 0) continue;
                if (LigatedAndDidntMultiply(b[i]))
                {
                    int newPosition = baseIndex;
                    if (script is not ("mlym" or "taml"))
                    {
                        while (newPosition > start && !IsOneOf(b[newPosition - 1], 1 << (int)IndicCategory.M | 1 << (int)IndicCategory.H)) newPosition--;
                    }

                    if (newPosition > start && IsHalant(b[newPosition - 1]) && newPosition < end && IsJoiner(b[newPosition])) newPosition++;
                    int oldPosition = i;
                    MergeClusters(newPosition, oldPosition + 1);
                    GlyphInfo form = b[oldPosition];
                    b.RemoveAt(oldPosition);
                    b.Insert(newPosition, form);
                    if (newPosition <= baseIndex && baseIndex < oldPosition) baseIndex++;
                }

                break;
            }
        }

        // A left matra at the start of a word takes its initial form.
        if (PositionOf(b[start]) == IndicPosition.PreM && (start == 0 || !IsWordCharacter(b[start - 1].Category))) b[start].Mask |= plan.InitMask;
    }

    /// <summary>Letters, marks and format characters continue a word; anything else (spaces, digits, punctuation) starts one.</summary>
    private static bool IsWordCharacter(UnicodeCategory c) => c is UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
        or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.LowercaseLetter or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.UppercaseLetter
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.NonSpacingMark;

    /// <summary>Where reph goes, by the script's reph position (the specification's steps 1 to 6).</summary>
    private int RephTarget(IndicPlan plan, int start, int end, int baseIndex)
    {
        List<GlyphInfo> b = _buffer;
        IndicPosition rephPosition = plan.Config.RephPosition;
        int target;

        // 2. After the first explicit halant between reph and the main consonant (and a joiner after it).
        if (rephPosition != IndicPosition.AfterPost && AfterFirstHalant(out target)) return target;

        // 3. After the main consonant and what is placed with it.
        if (rephPosition == IndicPosition.AfterMain)
        {
            target = baseIndex;
            while (target + 1 < end && PositionOf(b[target + 1]) <= IndicPosition.AfterMain) target++;
            if (target < end) return target;
        }

        // 4. Before the first post-base form, matra or sign.
        if (rephPosition == IndicPosition.AfterSub)
        {
            target = baseIndex;
            while (target + 1 < end && PositionOf(b[target + 1]) is not (IndicPosition.PostC or IndicPosition.AfterPost or IndicPosition.Smvd)) target++;
            if (target < end) return target;
        }

        // 5. As step 2.
        if (AfterFirstHalant(out target)) return target;

        // 6. The end of the syllable, before syllable modifiers and vedic signs; before a final
        // halant that follows a matra, so it can interact with the matra.
        target = end - 1;
        while (target > start && PositionOf(b[target]) == IndicPosition.Smvd) target--;
        if (IsHalant(b[target]))
        {
            for (int i = baseIndex + 1; i < target; i++)
            {
                if (CategoryOf(b[i]) == IndicCategory.M) target--;
            }
        }

        return target;

        bool AfterFirstHalant(out int position)
        {
            position = start + 1;
            while (position < baseIndex && !IsHalant(b[position])) position++;
            if (position >= baseIndex) return false;
            if (position + 1 < baseIndex && IsJoiner(b[position + 1])) position++;
            return true;
        }
    }
}

/// <summary>
/// Splits Indic text into syllables with the grammar of the Microsoft specifications, as refined
/// by HarfBuzz: consonant syllables, vowel syllables, placeholders and dotted circles that carry
/// marks, symbols with their marks, and broken clusters that start with a sign. The longest match
/// wins; on a tie the earlier kind does.
/// </summary>
internal static class IndicSyllables
{
    private static readonly SyllableGrammar.Machine[] Machines = Build();

    /// <summary>Marks each glyph with its syllable: a serial number above 0, shifted left by 4, plus the syllable type.</summary>
    public static void Find(List<GlyphInfo> buffer)
    {
        var categories = new int[buffer.Count];
        for (int i = 0; i < categories.Length; i++) categories[i] = buffer[i].IndicCategory;
        int serial = 1;
        foreach ((int start, int length, int type) in SyllableGrammar.Split(Machines, categories, 5))
        {
            for (int i = start; i < start + length; i++) buffer[i].Syllable = serial << 4 | type;
            serial++;
        }
    }

    private static SyllableGrammar.Machine[] Build()
    {
        SyllableGrammar.Rx c = Sym(IndicCategory.C, IndicCategory.Ra);
        SyllableGrammar.Rx n = Seq(Opt(Seq(Opt(Sym(IndicCategory.Zwnj)), Sym(IndicCategory.RS))), Opt(Seq(Sym(IndicCategory.N), Opt(Sym(IndicCategory.N)))));
        SyllableGrammar.Rx z = Sym(IndicCategory.Zwj, IndicCategory.Zwnj);
        SyllableGrammar.Rx reph = Or(Seq(Sym(IndicCategory.Ra), Sym(IndicCategory.H)), Sym(IndicCategory.Repha));
        SyllableGrammar.Rx cn = Seq(c, Opt(Sym(IndicCategory.Zwj)), n);
        SyllableGrammar.Rx symbol = Seq(Sym(IndicCategory.Symbol), Opt(Sym(IndicCategory.N)));
        SyllableGrammar.Rx matraGroup = Seq(Star(z), Sym(IndicCategory.M), Opt(Sym(IndicCategory.N)), Opt(Sym(IndicCategory.H)));
        SyllableGrammar.Rx syllableTail = Seq(Opt(Seq(Opt(z), Sym(IndicCategory.SM), Opt(Sym(IndicCategory.SM)), Opt(Sym(IndicCategory.Zwnj)))), Star(Sym(IndicCategory.A)));
        SyllableGrammar.Rx halantGroup = Seq(Opt(z), Sym(IndicCategory.H), Opt(Seq(Sym(IndicCategory.Zwj), Opt(Sym(IndicCategory.N)))));
        SyllableGrammar.Rx finalHalantGroup = Or(halantGroup, Seq(Sym(IndicCategory.H), Sym(IndicCategory.Zwnj)));
        SyllableGrammar.Rx medialGroup = Opt(Sym(IndicCategory.CM));
        SyllableGrammar.Rx halantOrMatraGroup = Or(finalHalantGroup, Star(matraGroup));
        SyllableGrammar.Rx complexTail = Seq(Star(Seq(halantGroup, cn)), medialGroup, halantOrMatraGroup, syllableTail);
        SyllableGrammar.Rx rephaOrStacker = Opt(Sym(IndicCategory.Repha, IndicCategory.CS));

        SyllableGrammar.Rx consonant = Seq(rephaOrStacker, cn, complexTail);
        SyllableGrammar.Rx vowel = Seq(Opt(reph), Sym(IndicCategory.V), n, Or(Sym(IndicCategory.Zwj), complexTail));
        SyllableGrammar.Rx standalone = Seq(Or(Seq(rephaOrStacker, Sym(IndicCategory.Placeholder)), Seq(Opt(reph), Sym(IndicCategory.DottedCircle))), n, complexTail);
        SyllableGrammar.Rx symbolCluster = Seq(symbol, syllableTail);
        SyllableGrammar.Rx broken = Seq(Opt(reph), n, complexTail);
        return [.. new[] { consonant, vowel, standalone, symbolCluster, broken }.Select(SyllableGrammar.Machine.From)];
    }

    private static SyllableGrammar.Rx Sym(params IndicCategory[] categories) => SyllableGrammar.Sym([.. categories.Select(k => (int)k)]);

    private static SyllableGrammar.Rx Seq(params SyllableGrammar.Rx[] parts) => SyllableGrammar.Seq(parts);

    private static SyllableGrammar.Rx Or(params SyllableGrammar.Rx[] parts) => SyllableGrammar.Or(parts);

    private static SyllableGrammar.Rx Opt(SyllableGrammar.Rx r) => SyllableGrammar.Opt(r);

    private static SyllableGrammar.Rx Star(SyllableGrammar.Rx r) => SyllableGrammar.Star(r);
}
