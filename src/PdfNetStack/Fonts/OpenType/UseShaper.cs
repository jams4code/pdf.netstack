using PdfNetStack.Text;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Categories of the Universal Shaping Engine. Positional variants carry their side: Abv above,
/// Blw below, Pst after and Pre before the base.
/// </summary>
internal enum UseCategory : byte
{
    O,
    B,
    N,
    GB,
    CGJ,
    SUB,
    H,
    HN,
    ZWNJ,
    WJ,
    R,
    CS,
    IS,
    Sk,
    G,
    J,
    SB,
    SE,
    HVM,
    HM,
    HR,
    RK,
    FAbv,
    FBlw,
    FPst,
    MAbv,
    MBlw,
    MPst,
    MPre,
    CMAbv,
    CMBlw,
    VAbv,
    VBlw,
    VPst,
    VPre,
    VMAbv,
    VMBlw,
    VMPst,
    VMPre,
    SMAbv,
    SMBlw,
    FMAbv,
    FMBlw,
    FMPst,
}

/// <summary>
/// The Universal Shaping Engine, from Microsoft's "Creating and supporting OpenType fonts for the
/// Universal Shaping Engine" with the cluster grammar and the refinements HarfBuzz and Uniscribe
/// agree on. Text splits into clusters by USE category; the pre-processing, reordering and
/// orthographic features apply per cluster; repha moves before the first post-base glyph and
/// pre-base vowels and medials move to the start (after a virama); then the topographical
/// (joining) and presentation features apply. Used for the Brahmic scripts of Southeast and
/// Central Asia (Balinese, Javanese, Sinhala, Tibetan...), Mongolian and the other joining
/// scripts after Arabic and Syriac, and Indic fonts made for it (dev3...).
/// </summary>
internal sealed partial class Shaper
{
    // Cluster types: the grammars' indices; Other is what none matches.
    private const int UseViramaTerminated = 0, UseSakotTerminated = 1, UseStandard = 2, UseNumberJoinerTerminated = 3,
        UseNumeral = 4, UseSymbol = 5, UseHieroglyph = 6, UseFinalModifier = 7, UseBroken = 8, UseOther = 9;

    private const long UsePostBase = 1L << (int)UseCategory.FAbv | 1L << (int)UseCategory.FBlw | 1L << (int)UseCategory.FPst
        | 1L << (int)UseCategory.MAbv | 1L << (int)UseCategory.MBlw | 1L << (int)UseCategory.MPst | 1L << (int)UseCategory.MPre
        | 1L << (int)UseCategory.VAbv | 1L << (int)UseCategory.VBlw | 1L << (int)UseCategory.VPst | 1L << (int)UseCategory.VPre
        | 1L << (int)UseCategory.VMAbv | 1L << (int)UseCategory.VMBlw | 1L << (int)UseCategory.VMPst | 1L << (int)UseCategory.VMPre;

    private static readonly SyllableGrammar.Machine[] UseMachines = BuildUseMachines();

    private static readonly ShapingFeature[] UseOrthographicFeatures =
    [
        new("rkrf", PerSyllable: true, ManualZwj: true, Global: true), new("abvf", PerSyllable: true, ManualZwj: true, Global: true),
        new("blwf", PerSyllable: true, ManualZwj: true, Global: true), new("half", PerSyllable: true, ManualZwj: true, Global: true),
        new("pstf", PerSyllable: true, ManualZwj: true, Global: true), new("vatu", PerSyllable: true, ManualZwj: true, Global: true),
        new("cjct", PerSyllable: true, ManualZwj: true, Global: true),
    ];

    private static readonly ShapingFeature[] UsePresentationFeatures =
    [
        new("abvs", ManualZwj: true), new("blws", ManualZwj: true), new("haln", ManualZwj: true), new("pres", ManualZwj: true),
        new("psts", ManualZwj: true), new("rlig"), new("calt"), new("clig"), new("liga"), new("rclt"),
    ];

    private void SubstituteUse(bool joining)
    {
        foreach (GlyphInfo g in _buffer) g.UseCategory = (byte)ShapingData.Use(g.CodePoint);
        ApplyStage(_gsub, _gsubFeatures, [new("rvrn")]);
        FindUseClusters();
        foreach ((int start, int end) in Syllables())
        {
            int limit = (UseCategory)_buffer[start].UseCategory == UseCategory.R ? 1 : Math.Min(3, end - start);
            for (int i = start; i < start + limit; i++) _buffer[i].Mask |= Rphf;
        }

        if (!joining) SetClusterJoiningForms();

        // Pre-processing.
        ApplyStage(_gsub, _gsubFeatures,
            [new("locl", PerSyllable: true), new("ccmp", PerSyllable: true), new("nukt", PerSyllable: true, ManualZwj: true), new("akhn", PerSyllable: true, ManualZwj: true)]);

        // Reordering group: what rphf forms is a repha, what pref forms a pre-base vowel.
        ClearChanged();
        ApplyStage(_gsub, _gsubFeatures, [new("rphf", PerSyllable: true, ManualZwj: true)]);
        foreach ((int start, int end) in Syllables())
        {
            for (int i = start; i < end && (_buffer[i].Mask & Rphf) != 0; i++)
            {
                if ((_buffer[i].Flags & GlyphFlags.Changed) == 0) continue;
                _buffer[i].UseCategory = (byte)UseCategory.R;
                break;
            }
        }

        ClearChanged();
        ApplyStage(_gsub, _gsubFeatures, [new("pref", PerSyllable: true, ManualZwj: true, Global: true)]);
        foreach ((int start, int end) in Syllables())
        {
            for (int i = start; i < end; i++)
            {
                if ((_buffer[i].Flags & GlyphFlags.Changed) == 0) continue;
                _buffer[i].UseCategory = (byte)UseCategory.VPre;
                break;
            }
        }

        ApplyStage(_gsub, _gsubFeatures, UseOrthographicFeatures);
        InsertDottedCircles(UseBroken, g => g.UseCategory = (byte)UseCategory.B, g => (UseCategory)g.UseCategory == UseCategory.R);
        foreach ((int start, int end) in Syllables()) ReorderUse(start, end);

        ApplyStage(_gsub, _gsubFeatures, [new("isol"), new("init"), new("medi"), new("fina")]);
        ApplyStage(_gsub, _gsubFeatures, UsePresentationFeatures);
    }

    /// <summary>
    /// The Indic and USE shapers break vowel sequences that look like another vowel (a letter a
    /// with the sign aa looks like the letter aa) with a dotted circle before their last character.
    /// </summary>
    private void PreprocessVowelConstraints()
    {
        int[] codePoints = [.. _buffer.Select(g => g.CodePoint)];
        List<GlyphInfo>? output = null;
        for (int i = 0; i < codePoints.Length;)
        {
            int length = i + 1 < codePoints.Length ? ShapingData.InvalidVowelSequence(codePoints, i) : 0;
            if (length == 0)
            {
                output?.Add(_buffer[i]);
                i++;
                continue;
            }

            // The circle takes the properties of the character it goes before, as in HarfBuzz.
            output ??= _buffer.GetRange(0, i);
            for (int k = 0; k < length - 1; k++) output.Add(_buffer[i + k]);
            GlyphInfo last = _buffer[i + length - 1];
            GlyphInfo circle = last.Copy();
            circle.CodePoint = 0x25CC;
            circle.Text = "";
            output.Add(circle);
            output.Add(last);
            i += length;
        }

        if (output is not null) _buffer = output;
    }

    private void ClearChanged()
    {
        foreach (GlyphInfo g in _buffer) g.Flags &= ~GlyphFlags.Changed;
    }

    /// <summary>
    /// Marks each glyph with its cluster, as <c>serial &lt;&lt; 4 | type</c>. The grammar does not
    /// see CGJ, ZWJ and variation selectors, nor a ZWNJ before a mark; they stay in the cluster
    /// around them.
    /// </summary>
    private void FindUseClusters()
    {
        var seen = new List<int>(_buffer.Count);
        for (int i = 0; i < _buffer.Count; i++)
        {
            var category = (UseCategory)_buffer[i].UseCategory;
            if (category == UseCategory.CGJ) continue;
            if (category == UseCategory.ZWNJ)
            {
                int j = i + 1;
                while (j < _buffer.Count && (UseCategory)_buffer[j].UseCategory == UseCategory.CGJ) j++;
                if (j < _buffer.Count && _buffer[j].IsMark) continue;
            }

            seen.Add(i);
        }

        int serial = 1;
        foreach ((int start, int length, int type) in SyllableGrammar.Split(UseMachines, [.. seen.Select(i => (int)_buffer[i].UseCategory)], UseOther))
        {
            int end = start + length < seen.Count ? seen[start + length] : _buffer.Count;
            for (int i = seen[start]; i < end; i++) _buffer[i].Syllable = serial << 4 | type;
            serial++;
        }
    }

    /// <summary>
    /// For scripts that do not join like Arabic: clusters next to each other (not parted by
    /// spaces, punctuation or hieroglyphs) take the isolated, initial, medial and final forms.
    /// </summary>
    private void SetClusterJoiningForms()
    {
        const uint forms = Isol | Init | Medi | Fina;
        int lastStart = 0;
        uint lastForm = 0;
        foreach ((int start, int end) in Syllables())
        {
            int type = _buffer[start].Syllable & 0x0F;
            if (type is UseHieroglyph or UseFinalModifier or UseOther)
            {
                lastForm = 0;
                lastStart = start;
                continue;
            }

            bool join = lastForm is Fina or Isol;
            if (join)
            {
                lastForm = lastForm == Fina ? Medi : Init;
                for (int i = lastStart; i < start; i++) _buffer[i].Mask = (_buffer[i].Mask & ~forms) | lastForm;
            }

            lastForm = join ? Fina : Isol;
            for (int i = start; i < end; i++) _buffer[i].Mask = (_buffer[i].Mask & ~forms) | lastForm;
            lastStart = start;
        }
    }

    private static bool IsUseHalant(GlyphInfo g) =>
        (UseCategory)g.UseCategory is UseCategory.H or UseCategory.HVM or UseCategory.IS && (g.Flags & GlyphFlags.Ligated) == 0;

    /// <summary>
    /// Moves a repha forward to just before the first post-base glyph (or to the end), and each
    /// pre-base vowel or vowel modifier back to the start of the cluster, or just after the last
    /// virama before it.
    /// </summary>
    private void ReorderUse(int start, int end)
    {
        int type = _buffer[start].Syllable & 0x0F;
        if (type is not (UseViramaTerminated or UseSakotTerminated or UseStandard or UseSymbol or UseBroken)) return;
        List<GlyphInfo> b = _buffer;

        if ((UseCategory)b[start].UseCategory == UseCategory.R && end - start > 1)
        {
            for (int i = start + 1; i < end; i++)
            {
                bool postBase = ((1L << b[i].UseCategory) & UsePostBase) != 0 || IsUseHalant(b[i]);
                if (!postBase && i != end - 1) continue;
                if (postBase) i--;
                MergeClusters(start, i + 1);
                GlyphInfo repha = b[start];
                b.RemoveAt(start);
                b.Insert(i, repha);
                break;
            }
        }

        for (int i = start, j = start; i < end; i++)
        {
            if (IsUseHalant(b[i]))
            {
                j = i + 1;
            }
            else if ((UseCategory)b[i].UseCategory is UseCategory.VPre or UseCategory.VMPre && b[i].Component == 0 && j < i)
            {
                // Only the first glyph of a multiple substitution moves.
                MergeClusters(j, i + 1);
                GlyphInfo moved = b[i];
                b.RemoveAt(i);
                b.Insert(j, moved);
            }
        }
    }

    /// <summary>The cluster grammar of the USE specification, as HarfBuzz refines it, one machine per cluster type.</summary>
    private static SyllableGrammar.Machine[] BuildUseMachines()
    {
        static SyllableGrammar.Rx U(params UseCategory[] c) => SyllableGrammar.Sym([.. c.Select(k => (int)k)]);
        static SyllableGrammar.Rx Opt(SyllableGrammar.Rx r) => SyllableGrammar.Opt(r);
        static SyllableGrammar.Rx Star(SyllableGrammar.Rx r) => SyllableGrammar.Star(r);
        static SyllableGrammar.Rx Plus(SyllableGrammar.Rx r) => SyllableGrammar.Plus(r);
        static SyllableGrammar.Rx Seq(params SyllableGrammar.Rx[] r) => SyllableGrammar.Seq(r);
        static SyllableGrammar.Rx Or(params SyllableGrammar.Rx[] r) => SyllableGrammar.Or(r);

        SyllableGrammar.Rx b = U(UseCategory.B), n = U(UseCategory.N), hn = U(UseCategory.HN), sk = U(UseCategory.Sk);
        SyllableGrammar.Rx h = U(UseCategory.H, UseCategory.HVM, UseCategory.IS, UseCategory.Sk);
        SyllableGrammar.Rx cmAbvBlw = Seq(Star(U(UseCategory.CMAbv)), Star(U(UseCategory.CMBlw)));
        SyllableGrammar.Rx consonantModifiers = Seq(cmAbvBlw, Star(Seq(Or(Seq(h, b), U(UseCategory.SUB)), cmAbvBlw)));
        SyllableGrammar.Rx medialConsonants = Seq(Opt(U(UseCategory.MPre)), Opt(U(UseCategory.MAbv)), Opt(U(UseCategory.MBlw)), Opt(U(UseCategory.MPst)));
        SyllableGrammar.Rx dependentVowels = Or(
            Seq(Star(U(UseCategory.VPre)), Star(U(UseCategory.VAbv)), Star(U(UseCategory.VBlw)), Star(U(UseCategory.VPst))), U(UseCategory.H));
        SyllableGrammar.Rx vowelModifiers = Seq(
            Opt(U(UseCategory.HVM)), Star(U(UseCategory.VMPre)), Star(U(UseCategory.VMAbv)), Star(U(UseCategory.VMBlw)), Star(U(UseCategory.VMPst)));
        SyllableGrammar.Rx finalConsonants = Seq(Star(U(UseCategory.FAbv)), Star(U(UseCategory.FBlw)), Star(U(UseCategory.FPst)));
        SyllableGrammar.Rx finalModifiers = Or(Seq(Star(U(UseCategory.FMAbv)), Star(U(UseCategory.FMBlw))), Opt(U(UseCategory.FMPst)));
        SyllableGrammar.Rx start = Seq(Opt(U(UseCategory.R, UseCategory.CS)), U(UseCategory.B, UseCategory.GB));
        SyllableGrammar.Rx middle = Seq(consonantModifiers, medialConsonants, dependentVowels, vowelModifiers, Star(Seq(sk, b)));
        SyllableGrammar.Rx tail = Seq(middle, finalConsonants, finalModifiers);
        SyllableGrammar.Rx numberJoinerTail = Seq(Star(Seq(hn, n)), hn);
        SyllableGrammar.Rx numeralTail = Plus(Seq(hn, n));
        SyllableGrammar.Rx symbolTail = Or(Seq(Plus(U(UseCategory.SMAbv)), Star(U(UseCategory.SMBlw))), Plus(U(UseCategory.SMBlw)));
        SyllableGrammar.Rx viramaTail = Seq(consonantModifiers, U(UseCategory.IS, UseCategory.RK));
        SyllableGrammar.Rx sakotTail = Seq(middle, sk);
        SyllableGrammar.Rx anyTail = Or(tail, sakotTail, symbolTail, viramaTail);
        SyllableGrammar.Rx hieroglyph = Seq(
            Star(U(UseCategory.SB)), U(UseCategory.G), Opt(U(UseCategory.HR)), Opt(U(UseCategory.HM)), Star(U(UseCategory.SE)),
            Star(Seq(U(UseCategory.J), Star(U(UseCategory.SB)), U(UseCategory.G), Opt(U(UseCategory.HR)), Opt(U(UseCategory.HM)), Star(U(UseCategory.SE)))));
        SyllableGrammar.Rx zwnj = Opt(U(UseCategory.ZWNJ));

        SyllableGrammar.Rx[] clusters =
        [
            Seq(start, viramaTail, zwnj),
            Seq(start, sakotTail, zwnj),
            Seq(start, tail, zwnj),
            Seq(n, numberJoinerTail, zwnj),
            Seq(n, Opt(numeralTail), zwnj),
            Seq(U(UseCategory.O, UseCategory.GB, UseCategory.SB), Opt(anyTail), zwnj),
            Seq(hieroglyph, zwnj),
            U(UseCategory.FMPst),
            Seq(Opt(U(UseCategory.R)), Or(anyTail, numberJoinerTail, numeralTail), zwnj),
        ];
        return [.. clusters.Select(SyllableGrammar.Machine.From)];
    }
}
