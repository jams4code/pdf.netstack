namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// The Hangul shaper, after HarfBuzz's: a syllable is drawn precomposed when the font has the
/// syllable, else as conjoining jamo that the ljmo, vjmo and tjmo features shape; and a tone mark
/// after a syllable moves before it, unless the font draws the mark with no width (overstriking).
/// </summary>
internal sealed partial class Shaper
{
    private const int LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7, SBase = 0xAC00;
    private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    private static readonly ShapingFeature[][] HangulStages =
    [
        [new("rvrn")],
        [new("ljmo"), new("vjmo"), new("tjmo"), new("ccmp"), new("locl"), new("rlig"), new("calt"), new("clig"), new("liga"), new("rclt")],
    ];

    private static bool IsLeadingJamo(int u) => u is (>= 0x1100 and <= 0x115F) or (>= 0xA960 and <= 0xA97C);

    private static bool IsVowelJamo(int u) => u is (>= 0x1160 and <= 0x11A7) or (>= 0xD7B0 and <= 0xD7C6);

    private static bool IsTrailingJamo(int u) => u is (>= 0x11A8 and <= 0x11FF) or (>= 0xD7CB and <= 0xD7FB);

    private static bool IsCombiningT(int u) => u > TBase && u < TBase + TCount;

    /// <summary>Composes or decomposes each syllable for the font and moves tone marks; jamo get their feature masks.</summary>
    private void PreprocessHangul()
    {
        var input = _buffer;
        var output = new List<GlyphInfo>(input.Count);

        // The syllable last output, [start, end); a tone mark can follow only a syllable.
        int start = 0, end = 0;
        for (int i = 0; i < input.Count;)
        {
            GlyphInfo g = input[i];
            int u = g.CodePoint;
            if (u is 0x302E or 0x302F)
            {
                bool zeroWidth = _font.TryGetGlyph(u, out int tone) && _font.Advance(tone) == 0;
                if (start < end && end == output.Count)
                {
                    output.Add(g);
                    if (!zeroWidth)
                    {
                        MergeOutClusters(output, start, end + 1, input, i + 1);
                        output.RemoveAt(end);
                        output.Insert(start, g);
                    }
                }
                else if (_font.TryGetGlyph(0x25CC, out _))
                {
                    // No syllable to carry it: a dotted circle does, after it unless the mark overstrikes.
                    GlyphInfo circle = g.Copy();
                    circle.CodePoint = 0x25CC;
                    circle.Text = "";
                    output.Add(zeroWidth ? circle : g);
                    output.Add(zeroWidth ? g : circle);
                }
                else
                {
                    output.Add(g);
                }

                start = end = output.Count;
                i++;
                continue;
            }

            start = output.Count;
            if (IsLeadingJamo(u) && i + 1 < input.Count && IsVowelJamo(input[i + 1].CodePoint))
            {
                int l = u, v = input[i + 1].CodePoint;
                int t = i + 2 < input.Count && IsTrailingJamo(input[i + 2].CodePoint) ? input[i + 2].CodePoint : 0;
                int length = t != 0 ? 3 : 2;
                if (l - LBase is >= 0 and < LCount && v - VBase is >= 0 and < VCount && (t == 0 || IsCombiningT(t)))
                {
                    int s = SBase + ((l - LBase) * NCount) + ((v - VBase) * TCount) + (t == 0 ? 0 : t - TBase);
                    if (_font.TryGetGlyph(s, out _))
                    {
                        output.Add(Combine(input, i, length, s));
                        end = start + 1;
                        i += length;
                        continue;
                    }
                }

                // Old Hangul, or a syllable the font lacks: the jamo shape it.
                input[i].Mask |= Ljmo;
                input[i + 1].Mask |= Vjmo;
                if (t != 0) input[i + 2].Mask |= Tjmo;
                for (int k = 0; k < length; k++) output.Add(input[i + k]);
                end = start + length;
                MergeOutClusters(output, start, end, input, i + length);
                i += length;
                continue;
            }

            if (u - SBase is >= 0 and < SCount)
            {
                bool hasSyllable = _font.TryGetGlyph(u, out _);
                int lIndex = (u - SBase) / NCount, vIndex = (u - SBase) % NCount / TCount, tIndex = (u - SBase) % TCount;
                int next = i + 1 < input.Count ? input[i + 1].CodePoint : 0;
                if (tIndex == 0 && IsCombiningT(next) && _font.TryGetGlyph(u + next - TBase, out _))
                {
                    output.Add(Combine(input, i, 2, u + next - TBase));
                    end = start + 1;
                    i += 2;
                    continue;
                }

                // Decompose a syllable the font lacks, or one followed by a trailing jamo that cannot combine.
                if (!hasSyllable || (tIndex == 0 && IsTrailingJamo(next)))
                {
                    int[] jamo = tIndex == 0 ? [LBase + lIndex, VBase + vIndex] : [LBase + lIndex, VBase + vIndex, TBase + tIndex];
                    if (jamo.All(j => _font.TryGetGlyph(j, out _)))
                    {
                        uint[] masks = [Ljmo, Vjmo, Tjmo];
                        for (int k = 0; k < jamo.Length; k++)
                        {
                            GlyphInfo part = k == 0 ? g : g.Copy();
                            part.CodePoint = jamo[k];
                            part.Mask |= masks[k];
                            if (k > 0) part.Text = "";
                            output.Add(part);
                        }

                        // A trailing jamo that made the syllable decompose belongs to it.
                        if (hasSyllable && tIndex == 0)
                        {
                            input[i + 1].Mask |= Tjmo;
                            output.Add(input[i + 1]);
                            i++;
                        }

                        end = output.Count;
                        MergeOutClusters(output, start, end, input, i + 1);
                        i++;
                        continue;
                    }
                }

                if (hasSyllable) end = start + 1;
            }

            output.Add(g);
            i++;
        }

        _buffer = output;
    }

    /// <summary>Glyphs <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> of <paramref name="input"/> as one precomposed syllable.</summary>
    private static GlyphInfo Combine(List<GlyphInfo> input, int start, int count, int syllable)
    {
        MergeClusters(input, start, start + count);
        GlyphInfo s = input[start];
        s.Text = string.Concat(input.GetRange(start, count).Select(g => g.Text));
        s.CodePoint = syllable;
        return s;
    }
}
