namespace PdfNetStack.Imaging;

/// <summary>
/// The inverse discrete wavelet transform of a tile-component (T.800 Annex F): resolution by
/// resolution, rows then columns, by lifting with whole-sample symmetric extension. Samples carry
/// their absolute coordinates' parity, so odd tile and image origins interleave correctly.
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

    // Columns transformed together, so the vertical pass walks memory in rows.
    private const int Strip = 32;

    /// <summary>The 5/3 reversible transform (F.3.7.1), in place in the tile-component's buffer.</summary>
    public static void Inverse(int[] buffer, Jpeg2000TileComponent tc)
    {
        int stride = tc.Width;
        int longest = Math.Max(tc.Width, tc.Height);
        var line = new int[longest];
        var strip = new int[longest * Strip];
        for (int r = 1; r < tc.Resolutions.Length; r++)
        {
            Jpeg2000Resolution res = tc.Resolutions[r], lower = tc.Resolutions[r - 1];
            int width = res.X1 - res.X0, height = res.Y1 - res.Y0;
            if (width == 0 || height == 0) continue;
            int lw = lower.X1 - lower.X0, lh = lower.Y1 - lower.Y0;
            int px = res.X0 & 1, py = res.Y0 & 1;
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                Interleave(buffer, row, lw, width, px, line);
                Lift53(line, width, px);
                Array.Copy(line, 0, buffer, row, width);
            }

            for (int x0 = 0; x0 < width; x0 += Strip)
            {
                int s = Math.Min(Strip, width - x0);
                for (int k = 0; k < height; k++) Array.Copy(buffer, (RowOf(k, py, lh) * stride) + x0, strip, k * s, s);

                Lift53(strip, height, py, s);
                for (int k = 0; k < height; k++) Array.Copy(strip, k * s, buffer, (k * stride) + x0, s);
            }
        }
    }

    /// <summary>The 9/7 irreversible transform (F.3.8.2), in place in the tile-component's buffer.</summary>
    public static void Inverse(float[] buffer, Jpeg2000TileComponent tc)
    {
        int stride = tc.Width;
        int longest = Math.Max(tc.Width, tc.Height);
        var line = new float[longest];
        var strip = new float[longest * Strip];
        for (int r = 1; r < tc.Resolutions.Length; r++)
        {
            Jpeg2000Resolution res = tc.Resolutions[r], lower = tc.Resolutions[r - 1];
            int width = res.X1 - res.X0, height = res.Y1 - res.Y0;
            if (width == 0 || height == 0) continue;
            int lw = lower.X1 - lower.X0, lh = lower.Y1 - lower.Y0;
            int px = res.X0 & 1, py = res.Y0 & 1;
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                Interleave(buffer, row, lw, width, px, line);
                Lift97(line, width, px, 1);
                Array.Copy(line, 0, buffer, row, width);
            }

            for (int x0 = 0; x0 < width; x0 += Strip)
            {
                int s = Math.Min(Strip, width - x0);
                for (int k = 0; k < height; k++) Array.Copy(buffer, (RowOf(k, py, lh) * stride) + x0, strip, k * s, s);
                Lift97(strip, height, py, s);
                for (int k = 0; k < height; k++) Array.Copy(strip, k * s, buffer, (k * stride) + x0, s);
            }
        }
    }

    /// <summary>
    /// Where position k of an interleaved column comes from: low-pass rows first in the buffer,
    /// then high-pass rows. Even absolute positions are low-pass.
    /// </summary>
    private static int RowOf(int k, int parity, int lows) => ((k + parity) & 1) == 0 ? (k - parity) >> 1 : lows + ((k - 1 + parity) >> 1);

    private static void Interleave(int[] buffer, int row, int lows, int count, int parity, int[] line)
    {
        for (int k = 0; k < count; k++) line[k] = buffer[row + RowOf(k, parity, lows)];
    }

    private static void Interleave(float[] buffer, int row, int lows, int count, int parity, float[] line)
    {
        for (int k = 0; k < count; k++) line[k] = buffer[row + RowOf(k, parity, lows)];
    }

    /// <summary>Lifting on n samples of s interleaved lines each; position k is even when k + parity is.</summary>
    private static void Lift53(int[] x, int n, int parity, int s = 1)
    {
        if (n == 1)
        {
            // A lone odd sample is the high-pass value halved (F.3.7).
            if (parity == 1)
            {
                for (int j = 0; j < s; j++) x[j] /= 2;
            }

            return;
        }

        // F-5: even samples from their odd neighbours, then F-6: odd samples from the new evens.
        for (int k = parity; k < n; k += 2)
        {
            int l = (k > 0 ? k - 1 : 1) * s, r = (k + 1 < n ? k + 1 : n - 2) * s, at = k * s;
            for (int j = 0; j < s; j++) x[at + j] -= (x[l + j] + x[r + j] + 2) >> 2;
        }

        for (int k = 1 - parity; k < n; k += 2)
        {
            int l = (k > 0 ? k - 1 : 1) * s, r = (k + 1 < n ? k + 1 : n - 2) * s, at = k * s;
            for (int j = 0; j < s; j++) x[at + j] += (x[l + j] + x[r + j]) >> 1;
        }
    }

    private static void Lift97(float[] x, int n, int parity, int s)
    {
        if (n == 1)
        {
            if (parity == 1)
            {
                for (int j = 0; j < s; j++) x[j] /= 2;
            }

            return;
        }

        // Steps 1 and 2 of F.3.8.2: scale, then four lifting steps alternating even and odd.
        for (int k = 0; k < n; k++)
        {
            float f = ((k + parity) & 1) == 0 ? K : InverseK;
            int at = k * s;
            for (int j = 0; j < s; j++) x[at + j] *= f;
        }

        LiftStep(x, n, parity, s, Delta);
        LiftStep(x, n, 1 - parity, s, Gamma);
        LiftStep(x, n, parity, s, Beta);
        LiftStep(x, n, 1 - parity, s, Alpha);
    }

    private static void LiftStep(float[] x, int n, int first, int s, float c)
    {
        for (int k = first; k < n; k += 2)
        {
            int l = (k > 0 ? k - 1 : 1) * s, r = (k + 1 < n ? k + 1 : n - 2) * s, at = k * s;
            for (int j = 0; j < s; j++) x[at + j] -= c * (x[l + j] + x[r + j]);
        }
    }
}
