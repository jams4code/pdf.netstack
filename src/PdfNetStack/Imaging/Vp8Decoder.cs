namespace PdfNetStack.Imaging;

/// <summary>
/// Decodes a VP8 key frame (RFC 6386), the lossy format of WebP, to RGB. Decoding to Y'CbCr follows
/// the specification exactly: boolean entropy decoding, segments, intra prediction, dequantization,
/// the inverse WHT and DCT, and the normal and simple loop filters.
/// </summary>
/// <remarks>
/// The conversion to RGB is not part of VP8. It is done as libwebp does by default, so that pixels
/// match it exactly: chroma upsampled with its "fancy" filter (each output weights the four
/// nearest chroma samples 9, 3, 3 and 1) and Rec. 601 converted in 14 bit fixed point.
/// </remarks>
internal static class Vp8Decoder
{
    private const int DcPred = 0, VPred = 1, HPred = 2, TmPred = 3;
    private const int BDc = 0, BTm = 1, BVe = 2, BHe = 3, BLd = 4, BRd = 5, BVr = 6, BVl = 7, BHd = 8, BHu = 9;

    /// <summary>The subblock mode tree (RFC 6386, 11.2): leaves are negated modes.</summary>
    private static readonly sbyte[] SubblockModeTree = [-BDc, 2, -BTm, 4, -BVe, 6, 8, 12, -BHe, 10, -BRd, -BVr, -BLd, 14, -BVl, 16, -BHd, -BHu];

    private static readonly byte[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    private static readonly byte[] Bands = [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];

    private static readonly byte[][] CategoryProbabilities =
    [
        [173, 148, 140],
        [176, 155, 140, 135],
        [180, 157, 141, 134, 130],
        [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129],
    ];

    /// <summary>Width and height from a VP8 frame header, without decoding.</summary>
    /// <exception cref="FormatException">The data is not a VP8 key frame.</exception>
    public static (int Width, int Height) Size(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10) throw new FormatException("WebP lossy frame header is truncated.");
        int tag = data[0] | data[1] << 8 | data[2] << 16;
        if ((tag & 1) != 0) throw new FormatException("WebP lossy frame is not a key frame.");
        if ((tag >> 1 & 7) > 3) throw new FormatException("VP8 version is not supported.");
        if ((tag >> 4 & 1) == 0) throw new FormatException("VP8 frame is not meant to be shown.");
        if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A) throw new FormatException("VP8 start code is missing.");
        return ((data[6] | data[7] << 8) & 0x3FFF, (data[8] | data[9] << 8) & 0x3FFF);
    }

    /// <summary>The frame as 8 bit RGB, rows top to bottom.</summary>
    /// <exception cref="FormatException">The data is damaged.</exception>
    public static byte[] DecodeRgb(ReadOnlySpan<byte> data, out int width, out int height)
    {
        (width, height) = Size(data);
        DecodedImage.CheckSize(width, height, 4, "WebP");
        Frame frame = new Frame(data.ToArray(), width, height);
        frame.Decode();
        return frame.ToRgb();
    }

    /// <summary>The state of one frame being decoded.</summary>
    private sealed class Frame
    {
        private readonly byte[] _data;
        private readonly int _width;
        private readonly int _height;
        private readonly int _mbw;
        private readonly int _mbh;
        private readonly byte[] _y;
        private readonly byte[] _u;
        private readonly byte[] _v;
        private readonly int _yStride;
        private readonly int _uvStride;
        private readonly byte[] _probabilities = (byte[])WebPTables.DefaultCoefficientProbabilities.Clone();
        private readonly int[] _segmentQuantizer = new int[4];
        private readonly int[] _segmentFilter = new int[4];
        private readonly byte[] _segmentTree = [255, 255, 255];
        private readonly int[] _refDelta = new int[4];
        private readonly int[] _modeDelta = new int[4];
        private readonly Quantizer[] _quantizers = new Quantizer[4];
        private readonly List<BoolDecoder> _decoders = [];

        private bool _segmentation;
        private bool _updateMap;
        private bool _absoluteSegments = true;
        private bool _simpleFilter;
        private int _filterLevel;
        private int _sharpness;
        private bool _filterDeltas;
        private bool _skipEnabled;
        private int _skipProbability;

        // Per macroblock results the loop filter needs.
        private byte[] _mbSegment = [];
        private bool[] _mbSubblocks = [];
        private bool[] _mbInner = [];

        public Frame(byte[] data, int width, int height)
        {
            _data = data;
            _width = width;
            _height = height;
            _mbw = (width + 15) >> 4;
            _mbh = (height + 15) >> 4;
            _yStride = _mbw * 16;
            _uvStride = _mbw * 8;
            _y = new byte[(long)_yStride * _mbh * 16];
            _u = new byte[(long)_uvStride * _mbh * 8];
            _v = new byte[(long)_uvStride * _mbh * 8];
        }

        public void Decode()
        {
            int tag = _data[0] | _data[1] << 8 | _data[2] << 16;
            int firstSize = tag >> 5;
            if (firstSize > _data.Length - 10) throw new FormatException("VP8 first partition is truncated.");
            var header = new BoolDecoder(_data, 10, firstSize);
            _decoders.Add(header);
            BoolDecoder[] partitions = ReadHeader(header, 10 + firstSize);

            int mbs = _mbw * _mbh;
            _mbSegment = new byte[mbs];
            _mbSubblocks = new bool[mbs];
            _mbInner = new bool[mbs];
            var context = new Contexts(_mbw);
            var coefficients = new short[25 * 16];
            var modes = new byte[16];
            for (int mby = 0; mby < _mbh; mby++)
            {
                BoolDecoder tokens = partitions[mby & (partitions.Length - 1)];
                context.StartRow();
                for (int mbx = 0; mbx < _mbw; mbx++)
                {
                    int mb = (mby * _mbw) + mbx;
                    int segment = _updateMap ? (header.Bool(_segmentTree[0]) == 0 ? header.Bool(_segmentTree[1]) : 2 + header.Bool(_segmentTree[2])) : 0;
                    bool skip = _skipEnabled && header.Bool(_skipProbability) == 1;
                    bool subblocks = header.Bool(145) == 0;
                    int yMode = 0;
                    if (!subblocks)
                    {
                        yMode = header.Bool(156) == 1 ? (header.Bool(128) == 1 ? TmPred : HPred) : (header.Bool(163) == 1 ? VPred : DcPred);
                        int implied = yMode switch { VPred => BVe, HPred => BHe, TmPred => BTm, _ => BDc };
                        for (int i = 0; i < 4; i++) context.TopModes[(mbx * 4) + i] = context.LeftModes[i] = (byte)implied;
                    }
                    else
                    {
                        for (int y = 0; y < 4; y++)
                        {
                            for (int x = 0; x < 4; x++)
                            {
                                int above = context.TopModes[(mbx * 4) + x], left = context.LeftModes[y];
                                int mode = Tree(header, SubblockModeTree, WebPTables.KeyFrameSubblockModeProbabilities, ((above * 10) + left) * 9);
                                modes[(y * 4) + x] = (byte)mode;
                                context.TopModes[(mbx * 4) + x] = context.LeftModes[y] = (byte)mode;
                            }
                        }
                    }

                    int uvMode = header.Bool(142) == 0 ? DcPred : header.Bool(114) == 0 ? VPred : header.Bool(183) == 1 ? TmPred : HPred;

                    Array.Clear(coefficients, 0, coefficients.Length);
                    bool nonZero = false;
                    if (!skip) nonZero = Residuals(tokens, context, mbx, _quantizers[segment], subblocks, coefficients);
                    else context.Skip(mbx, subblocks);

                    _mbSegment[mb] = (byte)segment;
                    _mbSubblocks[mb] = subblocks;
                    _mbInner[mb] = subblocks || nonZero;
                    ReconstructLuma(mbx, mby, subblocks, yMode, modes, coefficients);
                    ReconstructChroma(_u, mbx, mby, uvMode, coefficients, 16);
                    ReconstructChroma(_v, mbx, mby, uvMode, coefficients, 20);
                }
            }

            foreach (BoolDecoder decoder in _decoders)
            {
                if (decoder.Overrun > 8) throw new FormatException("VP8 data is truncated.");
            }

            if (_filterLevel > 0) LoopFilter();
        }

        /// <summary>The frame header (RFC 6386, 9.2 to 9.11 and 19.2) and the token partitions it sets up.</summary>
        private BoolDecoder[] ReadHeader(BoolDecoder d, int partitionStart)
        {
            d.Literal(1);
            d.Literal(1);

            _segmentation = d.Literal(1) == 1;
            if (_segmentation)
            {
                _updateMap = d.Literal(1) == 1;
                if (d.Literal(1) == 1)
                {
                    _absoluteSegments = d.Literal(1) == 1;
                    for (int i = 0; i < 4; i++) _segmentQuantizer[i] = d.Literal(1) == 1 ? d.Signed(7) : 0;
                    for (int i = 0; i < 4; i++) _segmentFilter[i] = d.Literal(1) == 1 ? d.Signed(6) : 0;
                }

                if (_updateMap)
                {
                    for (int i = 0; i < 3; i++) _segmentTree[i] = (byte)(d.Literal(1) == 1 ? d.Literal(8) : 255);
                }
            }

            _simpleFilter = d.Literal(1) == 1;
            _filterLevel = d.Literal(6);
            _sharpness = d.Literal(3);
            _filterDeltas = d.Literal(1) == 1;
            if (_filterDeltas && d.Literal(1) == 1)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (d.Literal(1) == 1) _refDelta[i] = d.Signed(6);
                }

                for (int i = 0; i < 4; i++)
                {
                    if (d.Literal(1) == 1) _modeDelta[i] = d.Signed(6);
                }
            }

            int count = 1 << d.Literal(2);
            BoolDecoder[] partitions = Partitions(partitionStart, count);

            int baseIndex = d.Literal(7);
            int yDc = Delta(d), y2Dc = Delta(d), y2Ac = Delta(d), uvDc = Delta(d), uvAc = Delta(d);
            for (int s = 0; s < 4; s++)
            {
                int q = !_segmentation ? baseIndex : _absoluteSegments ? _segmentQuantizer[s] : baseIndex + _segmentQuantizer[s];
                _quantizers[s] = new Quantizer(
                    Dc(q + yDc), Ac(q),
                    Dc(q + y2Dc) * 2, Math.Max(8, Ac(q + y2Ac) * 155 / 100),
                    Math.Min(132, Dc(q + uvDc)), Ac(q + uvAc));
            }

            d.Literal(1);
            byte[] update = WebPTables.CoefficientUpdateProbabilities;
            for (int i = 0; i < _probabilities.Length; i++)
            {
                if (d.Bool(update[i]) == 1) _probabilities[i] = (byte)d.Literal(8);
            }

            _skipEnabled = d.Literal(1) == 1;
            if (_skipEnabled) _skipProbability = d.Literal(8);
            return partitions;
        }

        private static int Delta(BoolDecoder d) => d.Literal(1) == 1 ? d.Signed(4) : 0;

        private static int Dc(int index) => WebPTables.DcQuantizer[Compat.Clamp(index, 0, 127)];

        private static int Ac(int index) => WebPTables.AcQuantizer[Compat.Clamp(index, 0, 127)];

        /// <summary>The token partitions: sizes of all but the last in 3 byte fields, then the data (RFC 6386, 9.5).</summary>
        private BoolDecoder[] Partitions(int start, int count)
        {
            int sizes = 3 * (count - 1);
            if (start + sizes > _data.Length) throw new FormatException("VP8 partition table is truncated.");
            var partitions = new BoolDecoder[count];
            int at = start + sizes, left = _data.Length - at;
            for (int p = 0; p < count - 1; p++)
            {
                int i = start + (3 * p);
                int size = Math.Min(_data[i] | _data[i + 1] << 8 | _data[i + 2] << 16, left);
                partitions[p] = new BoolDecoder(_data, at, size);
                at += size;
                left -= size;
            }

            partitions[count - 1] = new BoolDecoder(_data, at, left);
            _decoders.AddRange(partitions);
            return partitions;
        }

        private static int Tree(BoolDecoder d, sbyte[] tree, byte[] probabilities, int offset)
        {
            int i = 0;
            do
            {
                i = tree[i + d.Bool(probabilities[offset + (i >> 1)])];
            }
            while (i > 0);

            return -i;
        }

        /// <summary>
        /// The macroblock's coefficients (RFC 6386, 13), dequantized; with a Y2 block, its inverse
        /// WHT becomes the luma DC terms. True when any block has a non-zero coefficient.
        /// </summary>
        private bool Residuals(BoolDecoder d, Contexts c, int mbx, Quantizer q, bool subblocks, short[] coefficients)
        {
            int first = 0, type = 3;
            bool nonZero = false;
            if (!subblocks)
            {
                var dc = new short[16];
                int n = Coefficients(d, 1, c.TopY2[mbx] + c.LeftY2, q.Y2Dc, q.Y2Ac, 0, dc, 0);
                c.TopY2[mbx] = c.LeftY2 = n > 0 ? (byte)1 : (byte)0;
                InverseWht(dc, coefficients, n > 1);
                first = 1;
                type = 0;
            }

            for (int y = 0; y < 4; y++)
            {
                int left = c.LeftY[y];
                for (int x = 0; x < 4; x++)
                {
                    int block = ((y * 4) + x) * 16;
                    int n = Coefficients(d, type, left + c.TopY[(mbx * 4) + x], q.YDc, q.YAc, first, coefficients, block);
                    left = n > first ? 1 : 0;
                    c.TopY[(mbx * 4) + x] = (byte)left;
                    nonZero |= n > 1 || coefficients[block] != 0;
                }

                c.LeftY[y] = (byte)left;
            }

            for (int plane = 0; plane < 2; plane++)
            {
                byte[] top = plane == 0 ? c.TopU : c.TopV, leftFlags = plane == 0 ? c.LeftU : c.LeftV;
                for (int y = 0; y < 2; y++)
                {
                    int left = leftFlags[y];
                    for (int x = 0; x < 2; x++)
                    {
                        int block = (16 + (plane * 4) + (y * 2) + x) * 16;
                        int n = Coefficients(d, 2, left + top[(mbx * 2) + x], q.UvDc, q.UvAc, 0, coefficients, block);
                        left = n > 0 ? 1 : 0;
                        top[(mbx * 2) + x] = (byte)left;
                        nonZero |= n > 1 || coefficients[block] != 0;
                    }

                    leftFlags[y] = (byte)left;
                }
            }

            return nonZero;
        }

        /// <summary>
        /// One block's tokens (RFC 6386, 13.2 and 13.3), dequantized into zigzag positions. Returns
        /// the position after the last token read: first when the block is empty.
        /// </summary>
        private int Coefficients(BoolDecoder d, int type, int context, int dc, int ac, int n, short[] output, int at)
        {
            byte[] p = _probabilities;
            int prob = Index(type, n, context);
            for (; n < 16; n++)
            {
                if (d.Bool(p[prob]) == 0) return n;
                while (d.Bool(p[prob + 1]) == 0)
                {
                    if (++n == 16) return 16;
                    prob = Index(type, n, 0);
                }

                int value, next;
                if (d.Bool(p[prob + 2]) == 0)
                {
                    value = 1;
                    next = 1;
                }
                else
                {
                    value = LargeValue(d, p, prob);
                    next = 2;
                }

                if (d.Bool(128) == 1) value = -value;
                output[at + Zigzag[n]] = (short)(value * (n > 0 ? ac : dc));
                prob = Index(type, n + 1, next);
            }

            return 16;
        }

        private static int Index(int type, int position, int context) => ((((type * 8) + Bands[position]) * 3) + context) * 11;

        /// <summary>Tokens 2 and up: DCT_2 to DCT_4 and the categories with extra bits.</summary>
        private static int LargeValue(BoolDecoder d, byte[] p, int prob)
        {
            if (d.Bool(p[prob + 3]) == 0) return d.Bool(p[prob + 4]) == 0 ? 2 : 3 + d.Bool(p[prob + 5]);
            if (d.Bool(p[prob + 6]) == 0)
            {
                if (d.Bool(p[prob + 7]) == 0) return 5 + d.Bool(159);
                return 7 + (2 * d.Bool(165)) + d.Bool(145);
            }

            int high = d.Bool(p[prob + 8]);
            int category = (2 * high) + d.Bool(p[prob + 9 + high]);
            int v = 0;
            foreach (byte probability in CategoryProbabilities[category]) v += v + d.Bool(probability);
            return v + 3 + (8 << category);
        }

        /// <summary>The inverse Walsh-Hadamard transform (RFC 6386, 14.3), results going to each luma block's DC.</summary>
        private static void InverseWht(short[] input, short[] coefficients, bool full)
        {
            if (!full)
            {
                short dc = (short)((input[0] + 3) >> 3);
                for (int i = 0; i < 16; i++) coefficients[i * 16] = dc;
                return;
            }

            var t = new int[16];
            for (int i = 0; i < 4; i++)
            {
                int a1 = input[i] + input[12 + i], b1 = input[4 + i] + input[8 + i];
                int c1 = input[4 + i] - input[8 + i], d1 = input[i] - input[12 + i];
                t[i] = a1 + b1;
                t[4 + i] = c1 + d1;
                t[8 + i] = a1 - b1;
                t[12 + i] = d1 - c1;
            }

            for (int i = 0; i < 4; i++)
            {
                int a1 = t[4 * i] + t[(4 * i) + 3], b1 = t[(4 * i) + 1] + t[(4 * i) + 2];
                int c1 = t[(4 * i) + 1] - t[(4 * i) + 2], d1 = t[4 * i] - t[(4 * i) + 3];
                coefficients[((4 * i) + 0) * 16] = (short)((a1 + b1 + 3) >> 3);
                coefficients[((4 * i) + 1) * 16] = (short)((c1 + d1 + 3) >> 3);
                coefficients[((4 * i) + 2) * 16] = (short)((a1 - b1 + 3) >> 3);
                coefficients[((4 * i) + 3) * 16] = (short)((d1 - c1 + 3) >> 3);
            }
        }

        /// <summary>Adds a block's inverse DCT (RFC 6386, 14.4) to the prediction already in the plane.</summary>
        private static void InverseDct(short[] input, int at, byte[] plane, int offset, int stride)
        {
            bool any = false;
            for (int i = 0; i < 16; i++) any |= input[at + i] != 0;
            if (!any) return;

            Span<int> t = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int i0 = input[at + i], i4 = input[at + 4 + i], i8 = input[at + 8 + i], i12 = input[at + 12 + i];
                int a1 = i0 + i8, b1 = i0 - i8;
                int c1 = ((i4 * 35468) >> 16) - (i12 + ((i12 * 20091) >> 16));
                int d1 = i4 + ((i4 * 20091) >> 16) + ((i12 * 35468) >> 16);
                t[i] = a1 + d1;
                t[12 + i] = a1 - d1;
                t[4 + i] = b1 + c1;
                t[8 + i] = b1 - c1;
            }

            for (int i = 0; i < 4; i++)
            {
                int i0 = t[4 * i], i1 = t[(4 * i) + 1], i2 = t[(4 * i) + 2], i3 = t[(4 * i) + 3];
                int a1 = i0 + i2, b1 = i0 - i2;
                int c1 = ((i1 * 35468) >> 16) - (i3 + ((i3 * 20091) >> 16));
                int d1 = i1 + ((i1 * 20091) >> 16) + ((i3 * 35468) >> 16);
                int row = offset + (i * stride);
                plane[row] = Clip(plane[row] + ((a1 + d1 + 4) >> 3));
                plane[row + 3] = Clip(plane[row + 3] + ((a1 - d1 + 4) >> 3));
                plane[row + 1] = Clip(plane[row + 1] + ((b1 + c1 + 4) >> 3));
                plane[row + 2] = Clip(plane[row + 2] + ((b1 - c1 + 4) >> 3));
            }
        }

        private static byte Clip(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

        // ---------------------------------------------------------------- prediction (RFC 6386, 12)

        private void ReconstructLuma(int mbx, int mby, bool subblocks, int mode, byte[] modes, short[] coefficients)
        {
            int x0 = mbx * 16, y0 = mby * 16, origin = (y0 * _yStride) + x0;
            if (!subblocks)
            {
                PredictBlock(_y, _yStride, x0, y0, 16, mode, mbx > 0, mby > 0);
                for (int b = 0; b < 16; b++) InverseDct(coefficients, b * 16, _y, origin + ((b >> 2) * 4 * _yStride) + ((b & 3) * 4), _yStride);
                return;
            }

            Span<int> above = stackalloc int[8];
            Span<int> left = stackalloc int[4];
            for (int b = 0; b < 16; b++)
            {
                int r = b >> 2, c = b & 3, bx = x0 + (4 * c), by = y0 + (4 * r);
                int corner = Edges(mbx, mby, r, c, bx, by, above, left);
                PredictSubblock(_y, (by * _yStride) + bx, _yStride, modes[b], above, left, corner);
                InverseDct(coefficients, b * 16, _y, (by * _yStride) + bx, _yStride);
            }
        }

        /// <summary>
        /// The 8 pixels above a subblock (4 above, 4 above right), the 4 to its left, and the corner,
        /// with the frame edge values and the above right rule of section 12.3.
        /// </summary>
        private int Edges(int mbx, int mby, int r, int c, int bx, int by, Span<int> above, Span<int> left)
        {
            for (int i = 0; i < 4; i++) above[i] = by == 0 ? 127 : _y[((by - 1) * _yStride) + bx + i];

            // Right of the macroblock, rows below its first take the row above the macroblock.
            if (c < 3 && by > 0)
            {
                for (int i = 0; i < 4; i++) above[4 + i] = _y[((by - 1) * _yStride) + bx + 4 + i];
            }
            else if (mby == 0)
            {
                for (int i = 0; i < 4; i++) above[4 + i] = 127;
            }
            else
            {
                int row = ((mby * 16) - 1) * _yStride;
                for (int i = 0; i < 4; i++) above[4 + i] = mbx < _mbw - 1 ? _y[row + (mbx * 16) + 16 + i] : _y[row + (mbx * 16) + 15];
            }

            for (int i = 0; i < 4; i++) left[i] = bx == 0 ? 129 : _y[((by + i) * _yStride) + bx - 1];
            return by == 0 ? 127 : bx == 0 ? 129 : _y[((by - 1) * _yStride) + bx - 1];
        }

        private void ReconstructChroma(byte[] plane, int mbx, int mby, int mode, short[] coefficients, int firstBlock)
        {
            int x0 = mbx * 8, y0 = mby * 8, origin = (y0 * _uvStride) + x0;
            PredictBlock(plane, _uvStride, x0, y0, 8, mode, mbx > 0, mby > 0);
            for (int b = 0; b < 4; b++) InverseDct(coefficients, (firstBlock + b) * 16, plane, origin + ((b >> 1) * 4 * _uvStride) + ((b & 1) * 4), _uvStride);
        }

        /// <summary>16x16 luma or 8x8 chroma prediction: DC, vertical, horizontal or TrueMotion.</summary>
        private static void PredictBlock(byte[] plane, int stride, int x0, int y0, int size, int mode, bool hasLeft, bool hasTop)
        {
            int origin = (y0 * stride) + x0, shift = size == 16 ? 4 : 3;
            int Above(int i) => hasTop ? plane[origin - stride + i] : 127;
            int Left(int i) => hasLeft ? plane[origin + (i * stride) - 1] : 129;
            int corner = !hasTop ? 127 : !hasLeft ? 129 : plane[origin - stride - 1];
            for (int r = 0; r < size; r++)
            {
                int row = origin + (r * stride);
                switch (mode)
                {
                    case VPred:
                        for (int c = 0; c < size; c++) plane[row + c] = (byte)Above(c);
                        break;
                    case HPred:
                        for (int c = 0; c < size; c++) plane[row + c] = (byte)Left(r);
                        break;
                    case TmPred:
                        for (int c = 0; c < size; c++) plane[row + c] = Clip(Left(r) + Above(c) - corner);
                        break;
                    default:
                        if (r > 0)
                        {
                            Array.Copy(plane, origin, plane, row, size);
                            break;
                        }

                        int sum = 0, value;
                        for (int i = 0; i < size; i++) sum += (hasTop ? Above(i) : 0) + (hasLeft ? Left(i) : 0);
                        if (hasTop && hasLeft) value = (sum + size) >> (shift + 1);
                        else if (hasTop || hasLeft) value = (sum + (size >> 1)) >> shift;
                        else value = 128;
                        for (int c = 0; c < size; c++) plane[row + c] = (byte)value;
                        break;
                }
            }
        }

        /// <summary>One of the ten 4x4 luma modes (RFC 6386, 12.3).</summary>
        private static void PredictSubblock(byte[] plane, int at, int stride, int mode, Span<int> a, Span<int> l, int p)
        {
            Span<int> b = stackalloc int[16];
            static int Avg3(int x, int y, int z) => (x + y + y + z + 2) >> 2;
            static int Avg2(int x, int y) => (x + y + 1) >> 1;

            // E: left column bottom to top, the corner, then the row above.
            Span<int> e = stackalloc int[9];
            (e[0], e[1], e[2], e[3], e[4]) = (l[3], l[2], l[1], l[0], p);
            (e[5], e[6], e[7], e[8]) = (a[0], a[1], a[2], a[3]);

            switch (mode)
            {
                case BDc:
                {
                    int v = 4;
                    for (int i = 0; i < 4; i++) v += a[i] + l[i];
                    b.Fill(v >> 3);
                    break;
                }

                case BTm:
                    for (int r = 0; r < 4; r++)
                    {
                        for (int c = 0; c < 4; c++) b[(r * 4) + c] = Clip(l[r] + a[c] - p);
                    }

                    break;
                case BVe:
                    for (int c = 0; c < 4; c++) b[c] = b[4 + c] = b[8 + c] = b[12 + c] = Avg3(c == 0 ? p : a[c - 1], a[c], a[c + 1]);
                    break;
                case BHe:
                    for (int r = 0; r < 4; r++)
                    {
                        int v = r == 3 ? Avg3(l[2], l[3], l[3]) : Avg3(r == 0 ? p : l[r - 1], l[r], l[r + 1]);
                        b[r * 4] = b[(r * 4) + 1] = b[(r * 4) + 2] = b[(r * 4) + 3] = v;
                    }

                    break;
                case BLd:
                    for (int r = 0; r < 4; r++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            int i = r + c;
                            b[(r * 4) + c] = i < 6 ? Avg3(a[i], a[i + 1], a[i + 2]) : Avg3(a[6], a[7], a[7]);
                        }
                    }

                    break;
                case BRd:
                    for (int r = 0; r < 4; r++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            int i = 4 - r + c;
                            b[(r * 4) + c] = Avg3(e[i - 1], e[i], e[i + 1]);
                        }
                    }

                    break;
                case BVr:
                    b[12] = Avg3(e[1], e[2], e[3]);
                    b[8] = Avg3(e[2], e[3], e[4]);
                    b[13] = b[4] = Avg3(e[3], e[4], e[5]);
                    b[9] = b[0] = Avg2(e[4], e[5]);
                    b[14] = b[5] = Avg3(e[4], e[5], e[6]);
                    b[10] = b[1] = Avg2(e[5], e[6]);
                    b[15] = b[6] = Avg3(e[5], e[6], e[7]);
                    b[11] = b[2] = Avg2(e[6], e[7]);
                    b[7] = Avg3(e[6], e[7], e[8]);
                    b[3] = Avg2(e[7], e[8]);
                    break;
                case BVl:
                    b[0] = Avg2(a[0], a[1]);
                    b[4] = Avg3(a[0], a[1], a[2]);
                    b[8] = b[1] = Avg2(a[1], a[2]);
                    b[5] = b[12] = Avg3(a[1], a[2], a[3]);
                    b[9] = b[2] = Avg2(a[2], a[3]);
                    b[13] = b[6] = Avg3(a[2], a[3], a[4]);
                    b[10] = b[3] = Avg2(a[3], a[4]);
                    b[14] = b[7] = Avg3(a[3], a[4], a[5]);
                    b[11] = Avg3(a[4], a[5], a[6]);
                    b[15] = Avg3(a[5], a[6], a[7]);
                    break;
                case BHd:
                    b[12] = Avg2(e[0], e[1]);
                    b[13] = Avg3(e[0], e[1], e[2]);
                    b[8] = b[14] = Avg2(e[1], e[2]);
                    b[9] = b[15] = Avg3(e[1], e[2], e[3]);
                    b[10] = b[4] = Avg2(e[2], e[3]);
                    b[11] = b[5] = Avg3(e[2], e[3], e[4]);
                    b[6] = b[0] = Avg2(e[3], e[4]);
                    b[7] = b[1] = Avg3(e[3], e[4], e[5]);
                    b[2] = Avg3(e[4], e[5], e[6]);
                    b[3] = Avg3(e[5], e[6], e[7]);
                    break;
                default:
                    b[0] = Avg2(l[0], l[1]);
                    b[1] = Avg3(l[0], l[1], l[2]);
                    b[2] = b[4] = Avg2(l[1], l[2]);
                    b[3] = b[5] = Avg3(l[1], l[2], l[3]);
                    b[6] = b[8] = Avg2(l[2], l[3]);
                    b[7] = b[9] = Avg3(l[2], l[3], l[3]);
                    b[10] = b[11] = b[12] = b[13] = b[14] = b[15] = l[3];
                    break;
            }

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++) plane[at + (r * stride) + c] = (byte)b[(r * 4) + c];
            }
        }

        // ---------------------------------------------------------------- loop filter (RFC 6386, 15)

        private void LoopFilter()
        {
            for (int mby = 0; mby < _mbh; mby++)
            {
                for (int mbx = 0; mbx < _mbw; mbx++)
                {
                    int mb = (mby * _mbw) + mbx;
                    int level = _segmentation ? _segmentFilter[_mbSegment[mb]] + (_absoluteSegments ? 0 : _filterLevel) : _filterLevel;
                    if (_filterDeltas)
                    {
                        level += _refDelta[0];
                        if (_mbSubblocks[mb]) level += _modeDelta[0];
                    }

                    level = Compat.Clamp(level, 0, 63);
                    if (level == 0) continue;
                    int interior = level;
                    if (_sharpness > 0)
                    {
                        interior >>= _sharpness > 4 ? 2 : 1;
                        interior = Math.Min(interior, 9 - _sharpness);
                    }

                    interior = Math.Max(1, interior);
                    int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                    int edge = (2 * level) + interior, mbEdge = edge + 4;
                    bool inner = _mbInner[mb];
                    int y = (mby * 16 * _yStride) + (mbx * 16), uv = (mby * 8 * _uvStride) + (mbx * 8);

                    if (_simpleFilter)
                    {
                        if (mbx > 0) SimpleEdge(_y, y, 1, _yStride, mbEdge);
                        if (inner)
                        {
                            for (int i = 4; i < 16; i += 4) SimpleEdge(_y, y + i, 1, _yStride, edge);
                        }

                        if (mby > 0) SimpleEdge(_y, y, _yStride, 1, mbEdge);
                        if (inner)
                        {
                            for (int i = 4; i < 16; i += 4) SimpleEdge(_y, y + (i * _yStride), _yStride, 1, edge);
                        }

                        continue;
                    }

                    if (mbx > 0)
                    {
                        Edge(_y, y, 1, _yStride, 16, mbEdge, interior, hev, true);
                        Edge(_u, uv, 1, _uvStride, 8, mbEdge, interior, hev, true);
                        Edge(_v, uv, 1, _uvStride, 8, mbEdge, interior, hev, true);
                    }

                    if (inner)
                    {
                        for (int i = 4; i < 16; i += 4) Edge(_y, y + i, 1, _yStride, 16, edge, interior, hev, false);
                        Edge(_u, uv + 4, 1, _uvStride, 8, edge, interior, hev, false);
                        Edge(_v, uv + 4, 1, _uvStride, 8, edge, interior, hev, false);
                    }

                    if (mby > 0)
                    {
                        Edge(_y, y, _yStride, 1, 16, mbEdge, interior, hev, true);
                        Edge(_u, uv, _uvStride, 1, 8, mbEdge, interior, hev, true);
                        Edge(_v, uv, _uvStride, 1, 8, mbEdge, interior, hev, true);
                    }

                    if (inner)
                    {
                        for (int i = 4; i < 16; i += 4) Edge(_y, y + (i * _yStride), _yStride, 1, 16, edge, interior, hev, false);
                        Edge(_u, uv + (4 * _uvStride), _uvStride, 1, 8, edge, interior, hev, false);
                        Edge(_v, uv + (4 * _uvStride), _uvStride, 1, 8, edge, interior, hev, false);
                    }
                }
            }
        }

        /// <summary>The simple filter across 16 luma segments; step crosses the edge, along moves to the next segment.</summary>
        private static void SimpleEdge(byte[] p, int at, int step, int along, int limit)
        {
            for (int i = 0; i < 16; i++, at += along)
            {
                if (2 * Math.Abs(p[at - step] - p[at]) + (Math.Abs(p[at - (2 * step)] - p[at + step]) >> 1) <= limit) Adjust(p, at, step, true);
            }
        }

        /// <summary>The normal filter across one edge: the macroblock filter or the subblock filter.</summary>
        private static void Edge(byte[] p, int at, int step, int along, int length, int limit, int interior, int hevThreshold, bool macroblock)
        {
            for (int i = 0; i < length; i++, at += along)
            {
                int p3 = p[at - (4 * step)], p2 = p[at - (3 * step)], p1 = p[at - (2 * step)], p0 = p[at - step];
                int q0 = p[at], q1 = p[at + step], q2 = p[at + (2 * step)], q3 = p[at + (3 * step)];
                if (2 * Math.Abs(p0 - q0) + (Math.Abs(p1 - q1) >> 1) > limit) continue;
                if (Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior || Math.Abs(p1 - p0) > interior) continue;
                if (Math.Abs(q3 - q2) > interior || Math.Abs(q2 - q1) > interior || Math.Abs(q1 - q0) > interior) continue;
                bool hev = Math.Abs(p1 - p0) > hevThreshold || Math.Abs(q1 - q0) > hevThreshold;
                if (hev)
                {
                    Adjust(p, at, step, true);
                    continue;
                }

                if (macroblock)
                {
                    int w = S8(S8(p1 - q1) + (3 * (q0 - p0)));
                    int a = ((27 * w) + 63) >> 7;
                    p[at] = Clip(q0 - a);
                    p[at - step] = Clip(p0 + a);
                    a = ((18 * w) + 63) >> 7;
                    p[at + step] = Clip(q1 - a);
                    p[at - (2 * step)] = Clip(p1 + a);
                    a = ((9 * w) + 63) >> 7;
                    p[at + (2 * step)] = Clip(q2 - a);
                    p[at - (3 * step)] = Clip(p2 + a);
                    continue;
                }

                int adjusted = (Adjust(p, at, step, false) + 1) >> 1;
                p[at + step] = Clip(q1 - adjusted);
                p[at - (2 * step)] = Clip(p1 + adjusted);
            }
        }

        /// <summary>The common adjustment of the two pixels at the edge (RFC 6386, 15.2), returning its size.</summary>
        private static int Adjust(byte[] p, int at, int step, bool outerTaps)
        {
            int p1 = p[at - (2 * step)] - 128, p0 = p[at - step] - 128, q0 = p[at] - 128, q1 = p[at + step] - 128;
            int a = S8((outerTaps ? S8(p1 - q1) : 0) + (3 * (q0 - p0)));
            int b = S8(a + 3) >> 3;
            a = S8(a + 4) >> 3;
            p[at] = (byte)(S8(q0 - a) + 128);
            p[at - step] = (byte)(S8(p0 + b) + 128);
            return a;
        }

        private static int S8(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

        // ---------------------------------------------------------------- Y'CbCr to RGB

        /// <summary>
        /// RGB with libwebp's fancy upsampling: row pairs between two chroma rows, the first and last
        /// rows against one chroma row, each output pixel weighting its nearest chroma samples.
        /// </summary>
        public byte[] ToRgb()
        {
            int w = _width, h = _height;
            var rgb = new byte[(long)w * h * 3];
            Pair(0, -1, 0, 0);
            for (int y = 1; y + 1 < h; y += 2) Pair(y, y + 1, (y - 1) >> 1, (y + 1) >> 1);
            if (h > 1 && (h & 1) == 0) Pair(h - 1, -1, (h - 1) >> 1, (h - 1) >> 1);
            return rgb;

            // Output rows top and bottom (or only top), between chroma rows: the first nearer the top row.
            void Pair(int top, int bottom, int topChroma, int bottomChroma)
            {
                int tc = topChroma * _uvStride, bc = bottomChroma * _uvStride;
                int last = (w - 1) >> 1;
                int tlU = _u[tc], tlV = _v[tc], lU = _u[bc], lV = _v[bc];
                Put(top, 0, (3 * tlU + lU + 2) >> 2, (3 * tlV + lV + 2) >> 2);
                if (bottom >= 0) Put(bottom, 0, (3 * lU + tlU + 2) >> 2, (3 * lV + tlV + 2) >> 2);
                for (int x = 1; x <= last; x++)
                {
                    int tU = _u[tc + x], tV = _v[tc + x], cU = _u[bc + x], cV = _v[bc + x];
                    int d12U = (tlU + (3 * tU) + (3 * lU) + cU + 8) >> 3, d03U = ((3 * tlU) + tU + lU + (3 * cU) + 8) >> 3;
                    int d12V = (tlV + (3 * tV) + (3 * lV) + cV + 8) >> 3, d03V = ((3 * tlV) + tV + lV + (3 * cV) + 8) >> 3;
                    Put(top, (2 * x) - 1, (d12U + tlU) >> 1, (d12V + tlV) >> 1);
                    Put(top, 2 * x, (d03U + tU) >> 1, (d03V + tV) >> 1);
                    if (bottom >= 0)
                    {
                        Put(bottom, (2 * x) - 1, (d03U + lU) >> 1, (d03V + lV) >> 1);
                        Put(bottom, 2 * x, (d12U + cU) >> 1, (d12V + cV) >> 1);
                    }

                    (tlU, tlV, lU, lV) = (tU, tV, cU, cV);
                }

                if ((w & 1) == 0)
                {
                    Put(top, w - 1, (3 * tlU + lU + 2) >> 2, (3 * tlV + lV + 2) >> 2);
                    if (bottom >= 0) Put(bottom, w - 1, (3 * lU + tlU + 2) >> 2, (3 * lV + tlV + 2) >> 2);
                }
            }

            void Put(int row, int x, int u, int v)
            {
                int luma = (_y[(row * _yStride) + x] * 19077) >> 8;
                long at = (((long)row * w) + x) * 3;
                rgb[at] = Fixed(luma + ((v * 26149) >> 8) - 14234);
                rgb[at + 1] = Fixed(luma - ((u * 6419) >> 8) - ((v * 13320) >> 8) + 8708);
                rgb[at + 2] = Fixed(luma + ((u * 33050) >> 8) - 17685);
            }
        }

        /// <summary>A 6 bit fixed point value to 0 to 255.</summary>
        private static byte Fixed(int v) => (v & ~16383) == 0 ? (byte)(v >> 6) : v < 0 ? (byte)0 : (byte)255;
    }

    /// <summary>Dequantization factors of one segment (RFC 6386, 14.1).</summary>
    private readonly record struct Quantizer(int YDc, int YAc, int Y2Dc, int Y2Ac, int UvDc, int UvAc);

    /// <summary>The above and left "has non-zero coefficients" flags of section 13.3, and the subblock mode contexts of section 11.3.</summary>
    private sealed class Contexts
    {
        public Contexts(int mbw)
        {
            TopY = new byte[mbw * 4];
            TopU = new byte[mbw * 2];
            TopV = new byte[mbw * 2];
            TopY2 = new byte[mbw];
            TopModes = new byte[mbw * 4];
        }

        public byte[] TopY { get; }

        public byte[] TopU { get; }

        public byte[] TopV { get; }

        public byte[] TopY2 { get; }

        public byte[] TopModes { get; }

        public byte[] LeftY { get; } = new byte[4];

        public byte[] LeftU { get; } = new byte[2];

        public byte[] LeftV { get; } = new byte[2];

        public byte LeftY2 { get; set; }

        public byte[] LeftModes { get; } = new byte[4];

        public void StartRow()
        {
            Array.Clear(LeftY, 0, 4);
            Array.Clear(LeftU, 0, 2);
            Array.Clear(LeftV, 0, 2);
            Array.Clear(LeftModes, 0, 4);
            LeftY2 = 0;
        }

        /// <summary>A macroblock without coefficients: its blocks count as empty, and so does its Y2 when it has one.</summary>
        public void Skip(int mbx, bool subblocks)
        {
            Array.Clear(LeftY, 0, 4);
            Array.Clear(LeftU, 0, 2);
            Array.Clear(LeftV, 0, 2);
            Array.Clear(TopY, mbx * 4, 4);
            Array.Clear(TopU, mbx * 2, 2);
            Array.Clear(TopV, mbx * 2, 2);
            if (!subblocks)
            {
                LeftY2 = 0;
                TopY2[mbx] = 0;
            }
        }
    }

    /// <summary>The boolean entropy decoder (RFC 6386, 7.3). Reading past the end supplies zeros, counted in <see cref="Overrun"/>.</summary>
    private sealed class BoolDecoder
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos;
        private uint _value;
        private int _range = 255;
        private int _bitCount;

        public BoolDecoder(byte[] data, int start, int length)
        {
            _data = data;
            _pos = start;
            _end = start + length;
            _value = (Next() << 8) | Next();
        }

        public int Overrun { get; private set; }

        public int Bool(int probability)
        {
            uint split = 1 + (uint)(((_range - 1) * probability) >> 8);
            uint bigSplit = split << 8;
            int bit;
            if (_value >= bigSplit)
            {
                bit = 1;
                _range -= (int)split;
                _value -= bigSplit;
            }
            else
            {
                bit = 0;
                _range = (int)split;
            }

            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= Next();
                }
            }

            return bit;
        }

        /// <summary>An unsigned value of n bits, most significant first, each at probability one half.</summary>
        public int Literal(int bits)
        {
            int v = 0;
            while (bits-- > 0) v = (v << 1) | Bool(128);
            return v;
        }

        /// <summary>A magnitude of n bits followed by a sign bit.</summary>
        public int Signed(int bits)
        {
            int v = Literal(bits);
            return Bool(128) == 1 ? -v : v;
        }

        private uint Next()
        {
            if (_pos < _end) return _data[_pos++];
            Overrun++;
            return 0;
        }
    }
}
