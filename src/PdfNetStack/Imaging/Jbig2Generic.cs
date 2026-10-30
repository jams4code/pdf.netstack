using System.Runtime.CompilerServices;

namespace PdfNetStack.Imaging;

/// <summary>
/// The generic region decoding procedure (T.88, 6.2) with arithmetic coding or MMR, and the
/// generic refinement region decoding procedure (T.88, 6.3).
/// </summary>
internal static class Jbig2Generic
{
    /// <summary>Contexts for the generic region procedure: 16 template pixels at most.</summary>
    public static byte[] GenericContexts() => new byte[1 << 16];

    /// <summary>Contexts for the refinement procedure: 13 template pixels at most.</summary>
    public static byte[] RefinementContexts() => new byte[1 << 13];

    /// <summary>The nominal AT pixels (T.88, Table 5) as x, y pairs.</summary>
    public static int[] NominalAt(int template) => template == 0 ? [3, -1, -3, -1, 2, -2, -2, -2] : template == 1 ? [3, -1] : [2, -1];

    // 6.2.5.7, Figures 8 to 11: the context that codes SLTP, in this decoder's pixel order.
    private static readonly int[] TypicalContext = [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>
    /// 6.2.5: decodes a <paramref name="width"/> by <paramref name="height"/> bitmap with template
    /// <paramref name="template"/>, its AT pixels in <paramref name="at"/> as x, y pairs.
    /// </summary>
    public static Jbig2Bitmap Decode(
        Jbig2ArithmeticDecoder decoder, byte[] contexts, long width, long height, int template, bool typical, Jbig2Bitmap? skip, int[] at, Jbig2Budget budget)
    {
        Jbig2Bitmap b = Jbig2Bitmap.Create(width, height, budget);
        int w = b.Width, stride = b.Stride;
        byte[] d = b.Data;
        int ltp = 0;
        int pairs = template == 0 ? 4 : 1;
        var atRows = new int[pairs];
        for (int y = 0; y < b.Height; y++)
        {
            if (typical)
            {
                ltp ^= decoder.Decode(contexts, TypicalContext[template]);
                if (ltp != 0)
                {
                    b.CopyRow(y - 1, y);
                    continue;
                }
            }

            // Rows are read through their offsets, -1 for a row outside the bitmap, which reads as 0.
            int row = y * stride, up1 = Row(y - 1, b), up2 = Row(y - 2, b);
            for (int k = 0; k < pairs; k++) atRows[k] = Row((long)y + at[(2 * k) + 1], b);

            // The pixel order follows each template's rows, so each row of the template slides by one bit per pixel.
            switch (template)
            {
                case 0:
                {
                    int line1 = Pixel(d, up2, 1, w) | (Pixel(d, up2, 0, w) << 1);
                    int line2 = Pixel(d, up1, 2, w) | (Pixel(d, up1, 1, w) << 1) | (Pixel(d, up1, 0, w) << 2);
                    int line3 = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int bit = 0;
                        if (skip is null || skip.Get(x, y) == 0)
                        {
                            int cx = line3 | (Pixel(d, atRows[0], x + at[0], w) << 4) | (line2 << 5) | (Pixel(d, atRows[1], x + at[2], w) << 10)
                                | (Pixel(d, atRows[2], x + at[4], w) << 11) | (line1 << 12) | (Pixel(d, atRows[3], x + at[6], w) << 15);
                            bit = decoder.Decode(contexts, cx);
                            if (bit != 0) d[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }

                        line1 = ((line1 << 1) | Pixel(d, up2, x + 2, w)) & 0x07;
                        line2 = ((line2 << 1) | Pixel(d, up1, x + 3, w)) & 0x1F;
                        line3 = ((line3 << 1) | bit) & 0x0F;
                    }

                    break;
                }

                case 1:
                {
                    int line1 = Pixel(d, up2, 2, w) | (Pixel(d, up2, 1, w) << 1) | (Pixel(d, up2, 0, w) << 2);
                    int line2 = Pixel(d, up1, 2, w) | (Pixel(d, up1, 1, w) << 1) | (Pixel(d, up1, 0, w) << 2);
                    int line3 = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int bit = 0;
                        if (skip is null || skip.Get(x, y) == 0)
                        {
                            int cx = line3 | (Pixel(d, atRows[0], x + at[0], w) << 3) | (line2 << 4) | (line1 << 9);
                            bit = decoder.Decode(contexts, cx);
                            if (bit != 0) d[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }

                        line1 = ((line1 << 1) | Pixel(d, up2, x + 3, w)) & 0x0F;
                        line2 = ((line2 << 1) | Pixel(d, up1, x + 3, w)) & 0x1F;
                        line3 = ((line3 << 1) | bit) & 0x07;
                    }

                    break;
                }

                case 2:
                {
                    int line1 = Pixel(d, up2, 1, w) | (Pixel(d, up2, 0, w) << 1);
                    int line2 = Pixel(d, up1, 1, w) | (Pixel(d, up1, 0, w) << 1);
                    int line3 = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int bit = 0;
                        if (skip is null || skip.Get(x, y) == 0)
                        {
                            int cx = line3 | (Pixel(d, atRows[0], x + at[0], w) << 2) | (line2 << 3) | (line1 << 7);
                            bit = decoder.Decode(contexts, cx);
                            if (bit != 0) d[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }

                        line1 = ((line1 << 1) | Pixel(d, up2, x + 2, w)) & 0x07;
                        line2 = ((line2 << 1) | Pixel(d, up1, x + 2, w)) & 0x0F;
                        line3 = ((line3 << 1) | bit) & 0x03;
                    }

                    break;
                }

                default:
                {
                    int line1 = Pixel(d, up1, 1, w) | (Pixel(d, up1, 0, w) << 1);
                    int line2 = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int bit = 0;
                        if (skip is null || skip.Get(x, y) == 0)
                        {
                            int cx = line2 | (Pixel(d, atRows[0], x + at[0], w) << 4) | (line1 << 5);
                            bit = decoder.Decode(contexts, cx);
                            if (bit != 0) d[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }

                        line1 = ((line1 << 1) | Pixel(d, up1, x + 2, w)) & 0x1F;
                        line2 = ((line2 << 1) | bit) & 0x0F;
                    }

                    break;
                }
            }
        }

        return b;
    }

    /// <summary>Where row <paramref name="y"/> starts in the bitmap's data, or -1 outside it.</summary>
    private static int Row(long y, Jbig2Bitmap b) => y >= 0 && y < b.Height ? (int)y * b.Stride : -1;

    /// <summary>The pixel in column <paramref name="x"/> of the row at <paramref name="row"/>, 0 outside the bitmap (6.2.5.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pixel(byte[] data, int row, int x, int width) =>
        row >= 0 && (uint)x < (uint)width ? (data[row + (x >> 3)] >> (7 - (x & 7))) & 1 : 0;

    /// <summary>
    /// 6.3.5: decodes a bitmap as a refinement of <paramref name="reference"/>, whose pixel
    /// (x - <paramref name="dx"/>, y - <paramref name="dy"/>) lines up with pixel (x, y).
    /// </summary>
    public static Jbig2Bitmap Refine(
        Jbig2ArithmeticDecoder decoder, byte[] contexts, long width, long height, int template, Jbig2Bitmap reference, int dx, int dy, bool typical, int[] at, Jbig2Budget budget)
    {
        Jbig2Bitmap g = Jbig2Bitmap.Create(width, height, budget);
        Jbig2Bitmap r = reference;
        int ltp = 0;
        for (int y = 0; y < g.Height; y++)
        {
            // 6.3.5.6, Figures 14 and 15: SLTP is coded in the context of a lone reference pixel.
            if (typical) ltp ^= decoder.Decode(contexts, template == 0 ? 0x0010 : 0x0008);
            int ry = y - dy;
            for (int x = 0; x < g.Width; x++)
            {
                int rx = x - dx;
                if (ltp != 0)
                {
                    // TPGR: a pixel whose 3 by 3 reference neighbourhood is uniform takes its value.
                    int sum = 0;
                    for (int j = -1; j <= 1; j++)
                    {
                        for (int i = -1; i <= 1; i++) sum += r.Get(rx + i, ry + j);
                    }

                    if (sum is 0 or 9)
                    {
                        if (sum == 9) g.Set(x, y, 1);
                        continue;
                    }
                }

                int cx = template == 0
                    ? r.Get(rx + 1, ry + 1) | (r.Get(rx, ry + 1) << 1) | (r.Get(rx - 1, ry + 1) << 2) | (r.Get(rx + 1, ry) << 3)
                        | (r.Get(rx, ry) << 4) | (r.Get(rx - 1, ry) << 5) | (r.Get(rx + 1, ry - 1) << 6) | (r.Get(rx, ry - 1) << 7)
                        | (r.Get(rx + at[2], ry + at[3]) << 8) | (g.Get(x - 1, y) << 9) | (g.Get(x + 1, y - 1) << 10)
                        | (g.Get(x, y - 1) << 11) | (g.Get(x + at[0], y + at[1]) << 12)
                    : r.Get(rx + 1, ry + 1) | (r.Get(rx, ry + 1) << 1) | (r.Get(rx + 1, ry) << 2) | (r.Get(rx, ry) << 3)
                        | (r.Get(rx - 1, ry) << 4) | (r.Get(rx, ry - 1) << 5) | (g.Get(x - 1, y) << 6) | (g.Get(x + 1, y - 1) << 7)
                        | (g.Get(x, y - 1) << 8) | (g.Get(x - 1, y - 1) << 9);
                if (decoder.Decode(contexts, cx) != 0) g.Set(x, y, 1);
            }
        }

        return g;
    }

    /// <summary>
    /// 6.2.6: MMR coded rows (ITU-T T.6 without EOL codes), 1 for black. Rows after damage or an
    /// early EOFB stay 0. Returns the bitmap and the bits read.
    /// </summary>
    public static (Jbig2Bitmap Bitmap, long Bits) DecodeMmr(ReadOnlySpan<byte> data, long width, long height, Jbig2Budget budget)
    {
        Jbig2Bitmap b = Jbig2Bitmap.Create(width, height, budget);
        int columns = b.Width;
        if (columns > CcittDecoder.MaxColumns) throw new Jbig2FormatException("An MMR region is too wide.");
        var bits = new CcittDecoder.BitReader(data);
        if (columns == 0) return (b, 0);

        // A row has at most columns + 1 changes, plus the two sentinels past them.
        var reference = new int[columns + 4];
        var coding = new int[columns + 4];
        int referenceCount = 0;
        for (int y = 0; y < b.Height; y++)
        {
            if (bits.AtEnd) break;
            int count = ReadRow(ref bits, reference, referenceCount, coding, columns);
            if (count < 0) break;

            // Black between every other pair of changes, white first.
            for (int i = 0; i < count; i += 2)
            {
                int from = Math.Max(0, coding[i]), to = i + 1 < count ? Math.Min(columns, coding[i + 1]) : columns;
                if (from < to) b.SetRun(y, from, to);
            }

            (reference, coding) = (coding, reference);
            referenceCount = count;
        }

        return (b, bits.Position);
    }

    /// <summary>One T.6 row coded against the reference row above. Returns its changes, or -1.</summary>
    private static int ReadRow(ref CcittDecoder.BitReader bits, int[] reference, int referenceCount, int[] coding, int columns)
    {
        reference[referenceCount] = columns;
        reference[referenceCount + 1] = columns;
        int count = 0, a0 = -1, color = 0, search = 0;
        while (a0 < columns)
        {
            // b1: the first change right of a0 to the color opposite a0's; changes alternate, white to black first.
            while (search > 0 && reference[search - 1] > a0) search--;
            int i = search;
            while (i < referenceCount && (reference[i] <= a0 || (i & 1) != color)) i++;
            search = i;
            int b1 = reference[i], b2 = reference[Math.Min(i + 1, referenceCount + 1)];

            int mode = CcittDecoder.ReadMode(ref bits);
            switch (mode)
            {
                case CcittDecoder.Pass:
                    if (b2 <= a0) return -1;
                    a0 = b2;
                    break;
                case CcittDecoder.Horizontal:
                {
                    int first = CcittDecoder.ReadRun(ref bits, color == 0);
                    int second = first < 0 ? -1 : CcittDecoder.ReadRun(ref bits, color != 0);
                    if (second < 0 || count + 2 > columns + 2) return -1;
                    int a1 = Math.Min(columns, Math.Max(a0, 0) + first), a2 = Math.Min(columns, a1 + second);
                    coding[count++] = a1;
                    coding[count++] = a2;
                    a0 = a2;
                    break;
                }

                case >= -3 and <= 3:
                {
                    int a1 = b1 + mode;
                    if (a1 < Math.Max(a0, 0) || a1 > columns || count + 1 > columns + 2) return -1;
                    coding[count++] = a1;
                    a0 = a1;
                    color ^= 1;
                    break;
                }

                default:
                    return -1;
            }
        }

        while (count > 0 && coding[count - 1] >= columns) count--;
        return count;
    }
}
