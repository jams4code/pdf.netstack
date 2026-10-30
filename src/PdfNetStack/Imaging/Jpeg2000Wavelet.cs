using System.Numerics;

namespace PdfNetStack.Imaging;

/// <summary>
/// The lifting steps of the inverse discrete wavelet transform (T.800 Annex F) with whole-sample
/// symmetric extension. Columns are lifted across a block of rows; rows are lifted with their
/// low-pass and high-pass samples still apart, each step a plain loop over one of the two, and
/// interleaved afterwards. Position k is even (low-pass) when k + parity is. Vectors lift several
/// samples at once with the operations of the scalar code, so the values are the same.
/// </summary>
internal static class Jpeg2000Wavelet
{
    // Table F.4: the 9/7 lifting parameters.
    private const float Alpha = -1.586134342059924f;
    private const float Beta = -0.052980118572961f;
    private const float Gamma = 0.882911075530934f;
    private const float Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;
    private const float InverseK = (float)(1 / 1.230174104914001);

    /// <summary>Lifting steps each transform takes; a value wrong at the cut of a window travels one sample per step.</summary>
    public const int Steps53 = 2;

    public const int Steps97 = 4;

    /// <summary>The 5/3 reversible transform (F.3.7.1) down the columns of n rows, s wide, from an offset.</summary>
    public static void Columns53(int[] x, int offset, int n, int parity, int s)
    {
        if (n == 1)
        {
            // A lone odd sample is the high-pass value halved (F.3.7).
            if (parity == 1)
            {
                for (int j = 0; j < s; j++) x[offset + j] /= 2;
            }

            return;
        }

        // F-5: even samples from their odd neighbours, then F-6: odd samples from the new evens.
        for (int k = parity; k < n; k += 2)
        {
            int l = offset + ((k > 0 ? k - 1 : 1) * s), r = offset + ((k + 1 < n ? k + 1 : n - 2) * s), at = offset + (k * s);
            int j = 0;
#if NET8_0_OR_GREATER
            var two = new Vector<int>(2);
            for (; j <= s - Vector<int>.Count; j += Vector<int>.Count)
            {
                var sum = new Vector<int>(x, l + j) + new Vector<int>(x, r + j) + two;
                (new Vector<int>(x, at + j) - Vector.ShiftRightArithmetic(sum, 2)).CopyTo(x, at + j);
            }
#endif
            for (; j < s; j++) x[at + j] -= (x[l + j] + x[r + j] + 2) >> 2;
        }

        for (int k = 1 - parity; k < n; k += 2)
        {
            int l = offset + ((k > 0 ? k - 1 : 1) * s), r = offset + ((k + 1 < n ? k + 1 : n - 2) * s), at = offset + (k * s);
            int j = 0;
#if NET8_0_OR_GREATER
            for (; j <= s - Vector<int>.Count; j += Vector<int>.Count)
            {
                var sum = new Vector<int>(x, l + j) + new Vector<int>(x, r + j);
                (new Vector<int>(x, at + j) + Vector.ShiftRightArithmetic(sum, 1)).CopyTo(x, at + j);
            }
#endif
            for (; j < s; j++) x[at + j] += (x[l + j] + x[r + j]) >> 1;
        }
    }

    /// <summary>The 9/7 irreversible transform (F.3.8.2) down the columns of n rows, s wide, from an offset.</summary>
    public static void Columns97(float[] x, int offset, int n, int parity, int s)
    {
        if (n == 1)
        {
            if (parity == 1)
            {
                for (int j = 0; j < s; j++) x[offset + j] /= 2;
            }

            return;
        }

        // Steps 1 and 2 of F.3.8.2: scale, then four lifting steps alternating even and odd.
        for (int k = 0; k < n; k++)
        {
            float f = ((k + parity) & 1) == 0 ? K : InverseK;
            int at = offset + (k * s), j = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var factor = new Vector<float>(f);
                for (; j <= s - Vector<float>.Count; j += Vector<float>.Count) (new Vector<float>(x, at + j) * factor).CopyTo(x, at + j);
            }

            for (; j < s; j++) x[at + j] *= f;
        }

        ColumnStep(x, offset, n, parity, s, Delta);
        ColumnStep(x, offset, n, 1 - parity, s, Gamma);
        ColumnStep(x, offset, n, parity, s, Beta);
        ColumnStep(x, offset, n, 1 - parity, s, Alpha);
    }

    private static void ColumnStep(float[] x, int offset, int n, int first, int s, float c)
    {
        var factor = new Vector<float>(c);
        for (int k = first; k < n; k += 2)
        {
            int l = offset + ((k > 0 ? k - 1 : 1) * s), r = offset + ((k + 1 < n ? k + 1 : n - 2) * s), at = offset + (k * s);
            int j = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; j <= s - Vector<float>.Count; j += Vector<float>.Count)
                {
                    (new Vector<float>(x, at + j) - (factor * (new Vector<float>(x, l + j) + new Vector<float>(x, r + j)))).CopyTo(x, at + j);
                }
            }

            for (; j < s; j++) x[at + j] -= c * (x[l + j] + x[r + j]);
        }
    }

    /// <summary>
    /// The 5/3 transform along a row held as its <paramref name="lows"/> low-pass samples and its
    /// <paramref name="highs"/> high-pass ones, lifted in place; <paramref name="parity"/> is the
    /// parity of the row's first position.
    /// </summary>
    public static void Row53(int[] low, int lows, int[] high, int highs, int parity)
    {
        if (lows + highs == 1)
        {
            if (parity == 1) high[0] /= 2;
            return;
        }

        // A low-pass sample's neighbours are the high-pass samples before and after it: index i - 1
        // and i when the row starts low, i and i + 1 when it starts high; the other way round for
        // a high-pass sample. Past either end the extension mirrors onto the other neighbour.
        RowStep53(low, lows, high, highs, parity == 0 ? -1 : 0, update: false);
        RowStep53(high, highs, low, lows, parity == 0 ? 0 : -1, update: true);
    }

    /// <summary>F-5 (<paramref name="update"/> false) or F-6 on <paramref name="a"/> from its neighbours in <paramref name="b"/>, as in <see cref="RowStep"/>.</summary>
    private static void RowStep53(int[] a, int count, int[] b, int others, int first, bool update)
    {
        int start = Math.Min(count, -first), end = Math.Max(start, Math.Min(count, others - first - 1));
        for (int i = 0; i < start; i++) a[i] = Lift53(a[i], b[i + first + 1], b[i + first + 1], update);
        for (int j = start; j < end; j++) a[j] = Lift53(a[j], b[j + first], b[j + first + 1], update);
        for (int i = end; i < count; i++) a[i] = Lift53(a[i], b[i + first], b[i + first], update);
    }

    private static int Lift53(int value, int left, int right, bool update) => update ? value + ((left + right) >> 1) : value - ((left + right + 2) >> 2);

    /// <summary>The 9/7 transform along a row held apart as for <see cref="Row53"/>.</summary>
    public static void Row97(float[] low, int lows, float[] high, int highs, int parity)
    {
        if (lows + highs == 1)
        {
            if (parity == 1) high[0] /= 2;
            return;
        }

        Scale(low, lows, K);
        Scale(high, highs, InverseK);
        int lowFirst = parity == 0 ? -1 : 0, highFirst = parity == 0 ? 0 : -1;
        RowStep(low, lows, high, highs, lowFirst, Delta);
        RowStep(high, highs, low, lows, highFirst, Gamma);
        RowStep(low, lows, high, highs, lowFirst, Beta);
        RowStep(high, highs, low, lows, highFirst, Alpha);
    }

    private static void Scale(float[] x, int count, float f)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var factor = new Vector<float>(f);
            for (; i <= count - Vector<float>.Count; i += Vector<float>.Count) (new Vector<float>(x, i) * factor).CopyTo(x, i);
        }

        for (; i < count; i++) x[i] *= f;
    }

    /// <summary>
    /// One step on <paramref name="a"/> from its neighbours in <paramref name="b"/>, at index
    /// i + <paramref name="first"/> and the one after; past either end the other neighbour stands in.
    /// </summary>
    private static void RowStep(float[] a, int count, float[] b, int others, int first, float c)
    {
        // Indexes whose two neighbours both exist: from -first up to others - first - 1.
        int start = Math.Min(count, -first), end = Math.Max(start, Math.Min(count, others - first - 1));
        for (int i = 0; i < start; i++) a[i] -= c * (b[i + first + 1] + b[i + first + 1]);
        int j = start;
        if (Vector.IsHardwareAccelerated)
        {
            var factor = new Vector<float>(c);
            for (; j <= end - Vector<float>.Count; j += Vector<float>.Count)
            {
                (new Vector<float>(a, j) - (factor * (new Vector<float>(b, j + first) + new Vector<float>(b, j + first + 1)))).CopyTo(a, j);
            }
        }

        for (; j < end; j++) a[j] -= c * (b[j + first] + b[j + first + 1]);
        for (int i = end; i < count; i++) a[i] -= c * (b[i + first] + b[i + first]);
    }
}
