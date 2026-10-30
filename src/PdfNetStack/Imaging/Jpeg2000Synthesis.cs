namespace PdfNetStack.Imaging;

/// <summary>
/// Reconstructs a tile-component a band of rows at a time: tier-1 decoding, dequantization and
/// the inverse wavelet transform (T.800 Annexes D, E and F). A resolution's rows come from rows
/// of the resolution below and of its subbands, and a subband decodes a row of code-blocks when a
/// row first needs it, so memory follows the tile's width rather than its area. The values are
/// those of the transform over the whole tile: each window of rows reaches far enough past the
/// rows asked for that every lifting step inside them sees its true neighbours.
/// </summary>
internal abstract class Jpeg2000Synthesis
{
    /// <summary>A synthesis in 32 bit integers for the 5/3 transform, in floats for the 9/7.</summary>
    /// <param name="component">The component, which names the buffers it takes from <paramref name="buffers"/>.</param>
    public static Jpeg2000Synthesis Create(Jpeg2000TileComponent tc, int component, Jpeg2000BlockDecoder decoder, Jpeg2000Buffers buffers) =>
        tc.Coding.Reversible ? new Jpeg2000Synthesis<int>(tc, component, decoder, buffers) : new Jpeg2000Synthesis<float>(tc, component, decoder, buffers);

    /// <summary>The rows of the last <see cref="Rows"/> call, from index 0, the tile-component's width apart.</summary>
    public abstract Array Output { get; }

    /// <summary>Reconstructs rows <paramref name="y0"/> to <paramref name="y1"/> of the tile-component into <see cref="Output"/>.</summary>
    public abstract void Rows(int y0, int y1);

    /// <summary>
    /// Bytes a synthesis holds at most for rows <paramref name="rows"/> at a time over a tile-component
    /// <paramref name="width"/> by <paramref name="height"/>: a window of rows per resolution, the rows
    /// below it, and up to three rows of code-blocks per subband.
    /// </summary>
    public static long WorkingBytes(long width, long height, int rows, int blockHeight)
    {
        long margin = 2 * (Jpeg2000Wavelet.Steps97 + 2);
        long windows = Math.Min(height, rows + margin) * 2;
        long lower = Math.Min(height, rows + (2 * margin));
        long bands = Math.Min(height, (rows / 2) + (3L << blockHeight)) * 2;
        return 4 * width * (windows + lower + bands);
    }
}

/// <summary>The synthesis over samples of type <typeparamref name="T"/>: int for the 5/3 transform, float for the 9/7.</summary>
internal sealed class Jpeg2000Synthesis<T> : Jpeg2000Synthesis
    where T : struct
{
    // What each buffer of a resolution is for, in the keys of the shared buffers.
    private const int Window = 0;
    private const int Lows = 1;
    private const int Bands = 2;
    private const int Out = 5;
    private const int RowLow = 6;
    private const int RowHigh = 7;

    private readonly Jpeg2000TileComponent _tc;
    private readonly int _component;
    private readonly Jpeg2000Buffers _buffers;
    private readonly int _margin;
    private readonly Subband[][] _bands;
    private T[] _output = [];

    public Jpeg2000Synthesis(Jpeg2000TileComponent tc, int component, Jpeg2000BlockDecoder decoder, Jpeg2000Buffers buffers)
    {
        _tc = tc;
        _component = component;
        _buffers = buffers;
        _margin = tc.Coding.Reversible ? Jpeg2000Wavelet.Steps53 : Jpeg2000Wavelet.Steps97;
        int count = tc.Resolutions.Length;
        _bands = new Subband[count][];
        for (int r = 0; r < count; r++)
        {
            Jpeg2000Resolution res = tc.Resolutions[r];
            _bands[r] = new Subband[res.Bands.Length];
            for (int b = 0; b < res.Bands.Length; b++) _bands[r][b] = new Subband(tc, res, b, decoder, buffers, Key(r, Bands + b));
        }
    }

    public override Array Output => _output;

    public override void Rows(int y0, int y1)
    {
        int levels = _tc.Resolutions.Length - 1;
        Jpeg2000Resolution top = _tc.Resolutions[levels];
        _output = _buffers.Get<T>(Key(levels, Out), (long)(y1 - y0) * (top.X1 - top.X0));
        Synthesize(levels, y0, y1, _output);
    }

    /// <summary>The key of this component's buffer for resolution <paramref name="r"/> and use <paramref name="use"/>.</summary>
    private int Key(int r, int use) => Jpeg2000Buffers.Key(_component, r, use);

    /// <summary>Rows <paramref name="y0"/> to <paramref name="y1"/> of resolution <paramref name="r"/> into the start of <paramref name="dest"/>.</summary>
    private void Synthesize(int r, int y0, int y1, T[] dest)
    {
        Jpeg2000Resolution res = _tc.Resolutions[r];
        int width = res.X1 - res.X0, height = res.Y1 - res.Y0;
        if (width == 0 || height == 0 || y0 >= y1) return;
        if (r == 0)
        {
            Subband ll = _bands[0][0];
            int at = ll.Fetch(y0, y1);
            Array.Copy(ll.Buffer, at, dest, 0, (y1 - y0) * width);
            return;
        }

        Jpeg2000Resolution lower = _tc.Resolutions[r - 1];
        int lw = lower.X1 - lower.X0;
        int px = res.X0 & 1, py = res.Y0 & 1;

        // The rows lifted together, and among them the low-pass ones (from the resolution below and
        // HL) and the high-pass ones (from LH and HH). Even absolute positions are low-pass.
        int w0 = Math.Max(0, y0 - _margin), w1 = Math.Min(height, y1 + _margin);
        int firstLow = w0 + ((w0 + py) & 1), firstHigh = w0 + ((w0 + py + 1) & 1);
        int low0 = (firstLow - py) >> 1, lows = firstLow < w1 ? (w1 - firstLow + 1) >> 1 : 0;
        int high0 = (firstHigh - 1 + py) >> 1, highs = firstHigh < w1 ? (w1 - firstHigh + 1) >> 1 : 0;

        T[] below = _buffers.Get<T>(Key(r, Lows), (long)lows * lw), window = _buffers.Get<T>(Key(r, Window), (long)(w1 - w0) * width);
        Synthesize(r - 1, low0, low0 + lows, below);
        Subband hl = _bands[r][0], lh = _bands[r][1], hh = _bands[r][2];
        int hlAt = hl.Fetch(low0, low0 + lows), lhAt = lh.Fetch(high0, high0 + highs), hhAt = hh.Fetch(high0, high0 + highs);

        int hw = width - lw;
        T[] rowLow = _buffers.Get<T>(Key(r, RowLow), lw), rowHigh = _buffers.Get<T>(Key(r, RowHigh), hw);
        for (int k = w0; k < w1; k++)
        {
            if (((k + py) & 1) == 0)
            {
                int j = ((k - py) >> 1) - low0;
                Array.Copy(below, j * lw, rowLow, 0, lw);
                Array.Copy(hl.Buffer, hlAt + (j * hl.Width), rowHigh, 0, hw);
            }
            else
            {
                int j = ((k - 1 + py) >> 1) - high0;
                Array.Copy(lh.Buffer, lhAt + (j * lh.Width), rowLow, 0, lw);
                Array.Copy(hh.Buffer, hhAt + (j * hh.Width), rowHigh, 0, hw);
            }

            if (typeof(T) == typeof(int)) Jpeg2000Wavelet.Row53((int[])(object)rowLow, lw, (int[])(object)rowHigh, hw, px);
            else Jpeg2000Wavelet.Row97((float[])(object)rowLow, lw, (float[])(object)rowHigh, hw, px);
            Interleave(rowLow, 0, rowHigh, 0, window, (k - w0) * width, width, lw, px);
        }

        if (typeof(T) == typeof(int)) Jpeg2000Wavelet.Columns53((int[])(object)window, 0, w1 - w0, (w0 + py) & 1, width);
        else Jpeg2000Wavelet.Columns97((float[])(object)window, 0, w1 - w0, (w0 + py) & 1, width);
        Array.Copy(window, (y0 - w0) * width, dest, 0, (y1 - y0) * width);
    }

    /// <summary>A row of low-pass samples and a row of high-pass ones, interleaved by absolute parity.</summary>
    private static void Interleave(T[] low, int lowAt, T[] high, int highAt, T[] line, int at, int count, int lows, int parity)
    {
        int highs = count - lows;
        if (parity == 0)
        {
            for (int i = 0; i < lows; i++) line[at + (2 * i)] = low[lowAt + i];
            for (int i = 0; i < highs; i++) line[at + (2 * i) + 1] = high[highAt + i];
        }
        else
        {
            for (int i = 0; i < highs; i++) line[at + (2 * i)] = high[highAt + i];
            for (int i = 0; i < lows; i++) line[at + (2 * i) + 1] = low[lowAt + i];
        }
    }

    /// <summary>
    /// The coefficients of one subband, held a few rows of code-blocks at a time: the rows asked
    /// for, from the start of their row of code-blocks on.
    /// </summary>
    private sealed class Subband
    {
        private readonly Jpeg2000TileComponent _tc;
        private readonly Jpeg2000Band _band;
        private readonly Jpeg2000BlockDecoder _decoder;
        private readonly int _blockHeight;
        private readonly int _height;
        private readonly Jpeg2000Resolution _res;
        private readonly int _index;
        private readonly Jpeg2000Buffers _buffers;
        private readonly int _key;
        private List<Jpeg2000CodeBlock>?[]? _blockRows;
        private int _start;
        private int _end;

        public Subband(Jpeg2000TileComponent tc, Jpeg2000Resolution res, int index, Jpeg2000BlockDecoder decoder, Jpeg2000Buffers buffers, int key)
        {
            _buffers = buffers;
            _key = key;
            _tc = tc;
            _res = res;
            _index = index;
            _band = res.Bands[index];
            _decoder = decoder;
            _blockHeight = res.BlockHeight;
            Width = _band.X1 - _band.X0;
            _height = _band.Y1 - _band.Y0;
        }

        public int Width { get; }

        public T[] Buffer { get; private set; } = [];

        /// <summary>Makes rows <paramref name="a"/> to <paramref name="b"/> of the subband available and returns where row a starts.</summary>
        public int Fetch(int a, int b)
        {
            if (a >= b || Width == 0) return 0;
            if (a < _start || a > _end)
            {
                _start = Top(a);
                _end = _start;
            }

            while (_end < b)
            {
                // Rows above the row of code-blocks holding row a are no longer needed.
                int keep = Top(a);
                if (keep > _start)
                {
                    Array.Copy(Buffer, (keep - _start) * Width, Buffer, 0, (_end - keep) * Width);
                    _start = keep;
                }

                int bottom = Bottom(_end);
                Buffer = _buffers.Get<T>(_key, (long)(bottom - _start) * Width, (_end - _start) * Width);

                Decode(_end, bottom);
                _end = bottom;
            }

            return (a - _start) * Width;
        }

        /// <summary>The first row of the row of code-blocks holding row <paramref name="row"/>.</summary>
        private int Top(int row) => Math.Max(0, (((_band.Y0 + row) >> _blockHeight) << _blockHeight) - _band.Y0);

        private int Bottom(int row) => (int)Math.Min(_height, ((((long)(_band.Y0 + row) >> _blockHeight) + 1) << _blockHeight) - _band.Y0);

        /// <summary>One row of code-blocks, rows <paramref name="top"/> to <paramref name="bottom"/>, through tier-1 and dequantization.</summary>
        private void Decode(int top, int bottom)
        {
            int at = (top - _start) * Width;
            Array.Clear(Buffer, at, (bottom - top) * Width);
            _blockRows ??= BlockRows();
            int index = ((_band.Y0 + top) >> _blockHeight) - (_band.Y0 >> _blockHeight);
            if (_blockRows[index] is not List<Jpeg2000CodeBlock> blocks) return;

            int roi = _tc.RoiShift;
            float half = _band.Step / 2;
            foreach (Jpeg2000CodeBlock cb in blocks)
            {
                int planes = _band.Planes + roi - cb.ZeroPlanes;
                int[] data = _decoder.Decode(cb, _band.Orientation, _tc.Coding.Style, planes);
                int w = cb.X1 - cb.X0, h = cb.Y1 - cb.Y0;
                if (roi > 0) Unshift(data, w * h, roi);
                int origin = at + ((cb.Y0 - _band.Y0 - top) * Width) + cb.X0 - _band.X0;
                for (int y = 0; y < h; y++)
                {
                    int o = origin + (y * Width), i = y * w;
                    if (typeof(T) == typeof(int))
                    {
                        int[] ints = (int[])(object)Buffer;
                        for (int x = 0; x < w; x++) ints[o + x] = data[i + x] / 2;
                    }
                    else
                    {
                        float[] floats = (float[])(object)Buffer;
                        for (int x = 0; x < w; x++) floats[o + x] = data[i + x] * half;
                    }
                }
            }
        }

        /// <summary>The subband's coded code-blocks by row of code-blocks, over every precinct of the resolution.</summary>
        private List<Jpeg2000CodeBlock>?[] BlockRows()
        {
            int first = _band.Y0 >> _blockHeight;
            var rows = new List<Jpeg2000CodeBlock>?[((_band.Y1 - 1) >> _blockHeight) - first + 1];
            foreach (Jpeg2000PrecinctBand[]? precinct in _res.Precincts)
            {
                if (precinct is null) continue;
                foreach (Jpeg2000CodeBlock cb in precinct[_index].Blocks)
                {
                    if (cb.Segments is null) continue;
                    (rows[(cb.Y0 >> _blockHeight) - first] ??= []).Add(cb);
                }
            }

            return rows;
        }

        /// <summary>
        /// The maximum shift method (T.800 H.1): coefficients at or above 2^s are the region of
        /// interest and are shifted back down; the rest is background. Values here are doubled.
        /// </summary>
        private static void Unshift(int[] data, int count, int shift)
        {
            if (shift >= 30)
            {
                Array.Clear(data, 0, count);
                return;
            }

            int threshold = 1 << (shift + 1);
            for (int i = 0; i < count; i++)
            {
                int v = data[i], magnitude = Math.Abs(v);
                if (magnitude >= threshold) data[i] = v < 0 ? -(magnitude >> shift) : magnitude >> shift;
            }
        }
    }
}

/// <summary>
/// Buffers a decode keeps from one tile to the next, by component, resolution and use, so a tiled
/// image holds one tile's worth of them.
/// </summary>
internal sealed class Jpeg2000Buffers
{
    private readonly Dictionary<int, Array> _arrays = [];

    /// <summary>The use of the level shifted samples of a band of rows, at resolution 0.</summary>
    public const int Shifted = 15;

    /// <summary>The key of a component's buffer for a resolution and a use (under 16).</summary>
    public static int Key(int component, int resolution, int use) => (((component * 64) + resolution) * 16) + use;

    /// <summary>
    /// The buffer for <paramref name="key"/>, at least <paramref name="size"/> long; when it has
    /// to grow, its first <paramref name="keep"/> elements move to the new one.
    /// </summary>
    public T[] Get<T>(int key, long size, int keep = 0)
        where T : struct
    {
        if (size > Compat.MaxArrayLength) throw new Jpeg2000FormatException("A tile is too large.");
        _arrays.TryGetValue(key, out Array? found);
        if (found is T[] array && array.Length >= size) return array;
        var grown = new T[found is T[] old ? Math.Max(size, Math.Min(Compat.MaxArrayLength, old.Length + (old.Length / 2L))) : size];
        if (keep > 0 && found is T[] previous) Array.Copy(previous, grown, keep);
        _arrays[key] = grown;
        return grown;
    }
}
