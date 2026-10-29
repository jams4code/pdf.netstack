using System.Globalization;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Converts a CFF2 table (variable OpenType outlines) at one location of the design space into a
/// CID-keyed CFF table that PDF viewers read as FontFile3 /CIDFontType0C. Blends resolve against
/// the variation store, subroutines are inlined, and argument runs longer than CFF allows are
/// split into equivalent operators. Glyph ids become CIDs one to one. Written from Adobe Technical
/// Notes 5176 and 5177 and the OpenType CFF2 specification.
/// </summary>
internal sealed class Cff2Converter
{
    // Charstring operators.
    private const int HStem = 1, VStem = 3, RLineTo = 5, HLineTo = 6, VLineTo = 7, RRCurveTo = 8, CallSubr = 10, Return = 11;
    private const int EndChar = 14, VsIndex = 15, Blend = 16, HStemHm = 18, HintMask = 19, CntrMask = 20, VStemHm = 23;
    private const int RCurveLine = 24, RLineCurve = 25, VVCurveTo = 26, HHCurveTo = 27, CallGSubr = 29, VHCurveTo = 30, HVCurveTo = 31;

    // DICT operators.
    private const int OpCharset = 15, OpCharStrings = 17, OpPrivate = 18, OpSubrs = 19, OpVsIndex = 22, OpBlend = 23, OpVStore = 24;
    private const int OpFontMatrix = 1207, OpRos = 1230, OpCidCount = 1234, OpFdArray = 1236, OpFdSelect = 1237;

    /// <summary>CFF limits a charstring's operand stack to 48 entries; CFF2 to 513.</summary>
    private const int MaxCffArgs = 48, MaxCff2Stack = 513;

    private const int MaxSubrDepth = 10;

    /// <summary>Charstring steps per glyph and output bytes per font; inlining cannot run away.</summary>
    private const int MaxStepsPerGlyph = 1 << 20, MaxOutput = 64 << 20;

    /// <summary>
    /// Work for the whole font: every step, plus the length of every subroutine each time it is
    /// called, so many glyphs or repeated calls cannot add up past it.
    /// </summary>
    private const long MaxStepsPerFont = 1L << 26;

    private readonly byte[] _d;
    private readonly float[] _coordinates;
    private readonly ItemVariationStore? _store;
    private readonly Dictionary<int, double[]> _scalars = [];
    private readonly Index _globalSubrs;
    private Index _localSubrs;
    private int _vsIndex;
    private int _steps;
    private long _fontSteps;
    private long _maxFontSteps = MaxStepsPerFont;
    private int _stems;
    private int _output;
    private readonly List<double> _stack = [];
    private FontWriter _w = new();

    private Cff2Converter(byte[] data, float[] coordinates)
    {
        _d = data;
        _coordinates = coordinates;
        if (U8(data, 0) != 2) throw new FontFormatException("Only CFF2 version 2 is supported.");
        int headerSize = U8(data, 2), topLength = U16(data, 3);
        Top = ReadDict(Slice(data, headerSize, topLength), resolve: false);
        int at = headerSize + topLength;
        _globalSubrs = Index.Read(data, ref at);
        if (Find(Top, OpVStore) is double[] vstore && vstore.Length > 0)
        {
            // A 16 bit length, then the item variation store.
            _store = ItemVariationStore.Read(data, Offset(vstore[0]) + 2);
        }
    }

    private List<(int Op, double[] Operands)> Top { get; }

    /// <summary>The CFF table for the CFF2 table <paramref name="cff2"/> at <paramref name="coordinates"/>.</summary>
    /// <param name="maxSteps">The work budget for the whole font; lowered only by tests.</param>
    /// <exception cref="FontFormatException">The CFF2 table is damaged or exceeds what CFF can hold.</exception>
    public static byte[] ToCff(ReadOnlySpan<byte> cff2, float[] coordinates, string fontName, int glyphCount, long maxSteps = MaxStepsPerFont)
    {
        var converter = new Cff2Converter(cff2.ToArray(), coordinates) { _maxFontSteps = maxSteps };
        return converter.Convert(fontName, glyphCount);
    }

    private byte[] Convert(string fontName, int glyphCount)
    {
        int charStringsAt = Offset(Find(Top, OpCharStrings)?.FirstOrDefault() ?? throw new FontFormatException("CFF2 font has no CharStrings."));
        Index charStrings = Index.Read(_d, ref charStringsAt);
        if (charStrings.Count != glyphCount) throw new FontFormatException("The CFF2 table and maxp disagree on the number of glyphs.");

        int fdArrayAt = Offset(Find(Top, OpFdArray)?.FirstOrDefault() ?? throw new FontFormatException("CFF2 font has no FDArray."));
        Index fdArray = Index.Read(_d, ref fdArrayAt);
        if (fdArray.Count is 0 or > 256) throw new FontFormatException("CFF2 FDArray size is not supported.");
        int[] fdOfGlyph = ReadFdSelect(glyphCount, fdArray.Count);

        // Each font dictionary: its FontMatrix, its Private DICT resolved, its local subroutines and default vsindex.
        var fonts = new List<(double[]? Matrix, List<(int Op, double[] Operands)> Private, Index Subrs, int VsIndex)>();
        for (int i = 0; i < fdArray.Count; i++)
        {
            List<(int Op, double[] Operands)> fd = ReadDict(fdArray.Item(_d, i), resolve: false);
            double[] priv = Find(fd, OpPrivate) ?? throw new FontFormatException("CFF2 font dictionary has no Private DICT.");
            if (priv.Length != 2) throw new FontFormatException("CFF2 Private operator is invalid.");
            int size = Offset(priv[0]), offset = Offset(priv[1]);
            _vsIndex = 0;
            List<(int Op, double[] Operands)> dict = ReadDict(Slice(_d, offset, size), resolve: true);
            Index subrs = Index.Empty;
            if (Find(dict, OpSubrs) is double[] s && s.Length > 0)
            {
                int subrsAt = offset + Offset(s[0]);
                subrs = Index.Read(_d, ref subrsAt);
            }

            int vsIndex = Find(dict, OpVsIndex) is double[] v && v.Length > 0 ? (int)v[0] : 0;
            dict.RemoveAll(e => e.Op is OpSubrs or OpVsIndex);
            fonts.Add((Find(fd, OpFontMatrix), dict, subrs, vsIndex));
        }

        var programs = new List<byte[]>(glyphCount);
        for (int g = 0; g < glyphCount; g++)
        {
            (_, _, Index subrs, int vsIndex) = fonts[fdOfGlyph[g]];
            programs.Add(ConvertGlyph(charStrings.Item(_d, g), subrs, vsIndex));
        }

        return Write(fontName, glyphCount, programs, fdOfGlyph, fonts.Select(f => (f.Matrix, f.Private)).ToList());
    }

    private int[] ReadFdSelect(int glyphCount, int fdCount)
    {
        var fds = new int[glyphCount];
        if (Find(Top, OpFdSelect) is not double[] select || select.Length == 0)
        {
            if (fdCount != 1) throw new FontFormatException("CFF2 font with several font dictionaries has no FDSelect.");
            return fds;
        }

        int at = Offset(select[0]);
        int format = U8(_d, at);
        switch (format)
        {
            case 0:
                for (int g = 0; g < glyphCount; g++) fds[g] = U8(_d, at + 1 + g);
                break;
            case 3 or 4:
            {
                bool wide = format == 4;
                long ranges = wide ? U32(_d, at + 1) : U16(_d, at + 1);
                int record = at + (wide ? 5 : 3), size = wide ? 6 : 3;
                if (ranges > (_d.Length - record) / size) throw new FontFormatException("CFF2 FDSelect is truncated.");
                for (int r = 0; r < ranges; r++)
                {
                    long first = wide ? U32(_d, record) : U16(_d, record);
                    int fd = wide ? U16(_d, record + 4) : U8(_d, record + 2);
                    long next = wide ? U32(_d, record + size) : U16(_d, record + size);
                    for (long g = first; g < next && g < glyphCount; g++) fds[g] = fd;
                    record += size;
                }

                break;
            }

            default:
                throw new FontFormatException("CFF2 FDSelect format is invalid.");
        }

        for (int g = 0; g < glyphCount; g++)
        {
            if (fds[g] >= fdCount) throw new FontFormatException("CFF2 FDSelect names a missing font dictionary.");
        }

        return fds;
    }

    // ---------------------------------------------------------------- Charstrings

    private byte[] ConvertGlyph(ReadOnlySpan<byte> program, Index subrs, int vsIndex)
    {
        _w = new FontWriter(program.Length + 16);
        _localSubrs = subrs;
        _vsIndex = vsIndex;
        _steps = 0;
        _stems = 0;
        _stack.Clear();
        Run(program.ToArray(), 0);
        if (_stack.Count > 0) throw new FontFormatException("CFF2 charstring ends with operands left.");
        _w.U8(EndChar);
        _output += _w.Length;
        if (_output > MaxOutput) throw new FontFormatException("CFF2 font expands past the size limit.");
        return _w.ToArray();
    }

    /// <summary>Interprets a charstring or subroutine. Returns false when the glyph ended (endchar).</summary>
    private bool Run(byte[] code, int depth)
    {
        if (depth > MaxSubrDepth) throw new FontFormatException("CFF2 subroutines nest too deeply.");
        if ((_fontSteps += code.Length) > _maxFontSteps) throw new FontFormatException("CFF2 font takes too much work to convert.");
        for (int i = 0; i < code.Length;)
        {
            if (++_steps > MaxStepsPerGlyph || ++_fontSteps > _maxFontSteps) throw new FontFormatException("CFF2 charstring is too long.");
            int b0 = code[i];
            if (b0 >= 32 || b0 == 28 || b0 == 255)
            {
                Push(ReadNumber(code, ref i));
                continue;
            }

            i++;
            int op = b0;
            if (b0 == 12)
            {
                op = 1200 + U8(code, i);
                i++;
            }

            switch (op)
            {
                case CallSubr or CallGSubr:
                {
                    Index subrs = op == CallSubr ? _localSubrs : _globalSubrs;
                    int index = (int)Pop() + Bias(subrs.Count);
                    if ((uint)index >= (uint)subrs.Count) throw new FontFormatException("CFF2 subroutine index is out of range.");
                    if (!Run(subrs.Item(_d, index).ToArray(), depth + 1)) return false;
                    break;
                }

                case Return:
                    return true;
                case EndChar:
                    return false;
                case VsIndex:
                    _vsIndex = (int)Pop();
                    break;
                case Blend:
                    ResolveBlend(_stack);
                    break;
                case HStem or VStem or HStemHm or VStemHm:
                    _stems += _stack.Count / 2;
                    Emit(op);
                    break;
                case HintMask or CntrMask:
                {
                    // Operands before a mask are vertical stems, written out explicitly.
                    if (_stack.Count > 0)
                    {
                        _stems += _stack.Count / 2;
                        Emit(VStemHm);
                    }

                    int bytes = (_stems + 7) / 8;
                    Check(code, i, bytes);
                    _w.U8(op);
                    _w.Bytes(code.AsSpan(i, bytes));
                    i += bytes;
                    break;
                }

                default:
                    Emit(op);
                    break;
            }
        }

        return true;
    }

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private void Push(double v)
    {
        if (_stack.Count >= MaxCff2Stack) throw new FontFormatException("CFF2 charstring overflows the operand stack.");
        _stack.Add(v);
    }

    private double Pop()
    {
        if (_stack.Count == 0) throw new FontFormatException("CFF2 charstring underflows the operand stack.");
        double v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    /// <summary>
    /// The blend operator: n values, then n times k deltas (k regions of the current vsindex),
    /// then n. Leaves the n interpolated values.
    /// </summary>
    private void ResolveBlend(List<double> stack)
    {
        if (stack.Count == 0) throw new FontFormatException("CFF2 blend has no operand count.");
        int n = (int)stack[^1];
        stack.RemoveAt(stack.Count - 1);
        double[] scalars = Scalars();
        int k = scalars.Length;
        long needed = (long)n * (k + 1);
        if (n < 0 || needed > stack.Count) throw new FontFormatException("CFF2 blend has too few operands.");
        int first = stack.Count - (int)needed;
        for (int v = 0; v < n; v++)
        {
            double value = stack[first + v];
            int deltas = first + n + v * k;
            for (int r = 0; r < k; r++) value += stack[deltas + r] * scalars[r];
            stack[first + v] = value;
        }

        stack.RemoveRange(first + n, stack.Count - first - n);
    }

    private double[] Scalars()
    {
        if (_store is null) return [];
        if (_scalars.TryGetValue(_vsIndex, out double[]? cached)) return cached;
        if ((uint)_vsIndex >= (uint)_store.DataCount) throw new FontFormatException("CFF2 vsindex is out of range.");
        return _scalars[_vsIndex] = _store.Scalars(_vsIndex, _coordinates);
    }

    /// <summary>Writes the operator with the stack as its operands, split where CFF's limit requires.</summary>
    private void Emit(int op)
    {
        List<double> a = _stack;
        int n = a.Count;
        if (n <= MaxCffArgs)
        {
            Write(a, 0, n, op);
        }
        else
        {
            switch (op)
            {
                case HStem or VStem or HStemHm or VStemHm:
                {
                    // Each new operator starts from 0, so its first edge becomes absolute.
                    double edge = 0;
                    for (int k = 0; k < n; k += MaxCffArgs)
                    {
                        int take = Math.Min(MaxCffArgs, n - k);
                        double first = a[k];
                        a[k] += edge;
                        Write(a, k, take, op);
                        a[k] = first;
                        for (int j = k; j < k + take; j++) edge += a[j];
                    }

                    break;
                }

                case RLineTo or RRCurveTo or HLineTo or VLineTo:
                    for (int k = 0; k < n; k += MaxCffArgs) Write(a, k, Math.Min(MaxCffArgs, n - k), op);
                    break;
                case HVCurveTo or VHCurveTo:
                    // Chunks of whole pairs of curves keep the alternation; the last keeps a trailing value.
                    for (int k = 0; k < n;)
                    {
                        int rest = n - k;
                        int take = rest <= MaxCffArgs ? rest : rest == MaxCffArgs + 1 ? 40 : MaxCffArgs;
                        Write(a, k, take, op);
                        k += take;
                    }

                    break;
                case HHCurveTo or VVCurveTo:
                    for (int k = 0; k < n;)
                    {
                        int take = k == 0 && n % 4 == 1 ? 45 : Math.Min(MaxCffArgs, n - k);
                        Write(a, k, take, op);
                        k += take;
                    }

                    break;
                case RCurveLine:
                {
                    int k = 0;
                    while (n - k > MaxCffArgs)
                    {
                        Write(a, k, MaxCffArgs, RRCurveTo);
                        k += MaxCffArgs;
                    }

                    Write(a, k, n - k, n - k == 2 ? RLineTo : RCurveLine);
                    break;
                }

                case RLineCurve:
                {
                    int k = 0;
                    while (n - k > MaxCffArgs)
                    {
                        int take = Math.Min(MaxCffArgs, n - k - 6) & ~1;
                        Write(a, k, take, RLineTo);
                        k += take;
                    }

                    Write(a, k, n - k, RLineCurve);
                    break;
                }

                default:
                    throw new FontFormatException("CFF2 charstring operator has too many operands.");
            }
        }

        a.Clear();
    }

    private void Write(List<double> a, int start, int count, int op)
    {
        for (int k = start; k < start + count; k++) WriteNumber(a[k]);
        if (op >= 1200)
        {
            _w.U8(12);
            _w.U8(op - 1200);
        }
        else
        {
            _w.U8(op);
        }
    }

    /// <summary>A charstring number: an integer form when exact, else 16.16 fixed.</summary>
    private void WriteNumber(double v)
    {
        double fixedValue = Math.Round(v * 65536) / 65536;
        if (fixedValue == Math.Floor(fixedValue) && fixedValue >= -32768 && fixedValue <= 32767)
        {
            int i = (int)fixedValue;
            switch (i)
            {
                case >= -107 and <= 107:
                    _w.U8(i + 139);
                    return;
                case >= 108 and <= 1131:
                    _w.U8(((i - 108) >> 8) + 247);
                    _w.U8((i - 108) & 0xFF);
                    return;
                case >= -1131 and <= -108:
                    _w.U8(((-i - 108) >> 8) + 251);
                    _w.U8((-i - 108) & 0xFF);
                    return;
                default:
                    _w.U8(28);
                    _w.U16(i);
                    return;
            }
        }

        int value = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(v * 65536)));
        _w.U8(255);
        _w.U32((uint)value);
    }

    /// <summary>A charstring operand: the integer forms or 16.16 fixed (255).</summary>
    private static double ReadNumber(byte[] code, ref int i)
    {
        int b0 = code[i];
        switch (b0)
        {
            case 28:
                i += 3;
                return I16(code, i - 2);
            case 255:
                i += 5;
                return (int)U32(code, i - 4) / 65536.0;
            case <= 246:
                i++;
                return b0 - 139;
            case <= 250:
                i += 2;
                return (b0 - 247) * 256 + U8(code, i - 1) + 108;
            default:
                i += 2;
                return -(b0 - 251) * 256 - U8(code, i - 1) - 108;
        }
    }

    // ---------------------------------------------------------------- DICTs

    /// <summary>Reads a CFF2 DICT. With <paramref name="resolve"/>, blends resolve and vsindex is kept as an entry.</summary>
    private List<(int Op, double[] Operands)> ReadDict(ReadOnlySpan<byte> d, bool resolve)
    {
        var entries = new List<(int, double[])>();
        var operands = new List<double>();
        for (int i = 0; i < d.Length;)
        {
            int b0 = d[i];
            if (b0 <= 27)
            {
                int op = b0;
                i++;
                if (b0 == 12)
                {
                    op = 1200 + U8(d, i);
                    i++;
                }

                if (op == OpBlend)
                {
                    if (resolve) ResolveBlend(operands);
                    continue;
                }

                if (op == OpVsIndex && resolve && operands.Count > 0) _vsIndex = (int)operands[0];
                entries.Add((op, [.. operands]));
                operands.Clear();
                continue;
            }

            if (operands.Count >= MaxCff2Stack) throw new FontFormatException("CFF2 DICT has too many operands.");
            switch (b0)
            {
                case 28:
                    operands.Add(I16(d, i + 1));
                    i += 3;
                    break;
                case 29:
                    operands.Add((int)U32(d, i + 1));
                    i += 5;
                    break;
                case 30:
                    operands.Add(ReadReal(d, ref i));
                    break;
                case >= 32 and <= 246:
                    operands.Add(b0 - 139);
                    i++;
                    break;
                case >= 247 and <= 250:
                    operands.Add((b0 - 247) * 256 + U8(d, i + 1) + 108);
                    i += 2;
                    break;
                case >= 251 and <= 254:
                    operands.Add(-(b0 - 251) * 256 - U8(d, i + 1) - 108);
                    i += 2;
                    break;
                default:
                    throw new FontFormatException("CFF2 DICT is invalid.");
            }
        }

        return entries;
    }

    private static double ReadReal(ReadOnlySpan<byte> d, ref int i)
    {
        var s = new System.Text.StringBuilder();
        i++;
        while (true)
        {
            int b = U8(d, i++);
            for (int shift = 4; shift >= 0; shift -= 4)
            {
                int nibble = (b >> shift) & 0xF;
                switch (nibble)
                {
                    case <= 9:
                        s.Append((char)('0' + nibble));
                        break;
                    case 0xA:
                        s.Append('.');
                        break;
                    case 0xB:
                        s.Append('E');
                        break;
                    case 0xC:
                        s.Append("E-");
                        break;
                    case 0xE:
                        s.Append('-');
                        break;
                    case 0xF:
                        return double.TryParse(s.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
                }
            }

            if (s.Length > 64) throw new FontFormatException("CFF2 real number is too long.");
        }
    }

    private static double[]? Find(List<(int Op, double[] Operands)> dict, int op)
    {
        foreach ((int o, double[] operands) in dict)
        {
            if (o == op) return operands;
        }

        return null;
    }

    private int Offset(double v)
    {
        if (v < 0 || v >= _d.Length || v != Math.Floor(v)) throw new FontFormatException("CFF2 offset is out of range.");
        return (int)v;
    }

    // ---------------------------------------------------------------- CFF output

    private static byte[] Write(string fontName, int glyphCount, List<byte[]> programs, int[] fdOfGlyph,
        List<(double[]? Matrix, List<(int Op, double[] Operands)> Private)> fonts)
    {
        byte[] names = WriteIndex([System.Text.Encoding.ASCII.GetBytes(fontName)]);
        byte[] strings = WriteIndex([System.Text.Encoding.ASCII.GetBytes("Adobe"), System.Text.Encoding.ASCII.GetBytes("Identity")]);
        byte[] globals = WriteIndex([]);
        byte[] charset = glyphCount > 1 ? [2, 0, 1, (byte)((glyphCount - 2) >> 8), (byte)(glyphCount - 2)] : [0];
        byte[] fdSelect = WriteFdSelect(fdOfGlyph);
        byte[] charStrings = WriteIndex(programs);
        double[]? matrix = fonts[0].Matrix;
        var privates = fonts.Select(f => WriteDict(f.Private)).ToList();

        int topSize = WriteIndex([Top(0, 0, 0, 0)]).Length;
        int at = 4 + names.Length + topSize + strings.Length + globals.Length;
        int charsetAt = at;
        at += charset.Length;
        int fdSelectAt = at;
        at += fdSelect.Length;
        int charStringsAt = at;
        at += charStrings.Length;
        int fdArrayAt = at;
        at += WriteIndex(privates.Select(p => FontDict(p.Length, 0)).ToList()).Length;
        var fdDicts = new List<byte[]>();
        foreach (byte[] p in privates)
        {
            fdDicts.Add(FontDict(p.Length, at));
            at += p.Length;
        }

        var w = new FontWriter(at);
        w.U8(1);
        w.U8(0);
        w.U8(4);
        w.U8(4);
        w.Bytes(names);
        w.Bytes(WriteIndex([Top(charsetAt, fdSelectAt, charStringsAt, fdArrayAt)]));
        w.Bytes(strings);
        w.Bytes(globals);
        w.Bytes(charset);
        w.Bytes(fdSelect);
        w.Bytes(charStrings);
        w.Bytes(WriteIndex(fdDicts));
        foreach (byte[] p in privates) w.Bytes(p);
        return w.ToArray();

        byte[] Top(int charsetOffset, int fdSelectOffset, int charStringsOffset, int fdArrayOffset)
        {
            var top = new FontWriter(64);
            DictNumber(top, 391);
            DictNumber(top, 392);
            DictNumber(top, 0);
            DictOp(top, OpRos);
            DictNumber(top, glyphCount);
            DictOp(top, OpCidCount);
            if (matrix is { Length: 6 })
            {
                foreach (double v in matrix) DictNumber(top, v);
                DictOp(top, OpFontMatrix);
            }

            DictOffset(top, charsetOffset);
            DictOp(top, OpCharset);
            DictOffset(top, charStringsOffset);
            DictOp(top, OpCharStrings);
            DictOffset(top, fdArrayOffset);
            DictOp(top, OpFdArray);
            DictOffset(top, fdSelectOffset);
            DictOp(top, OpFdSelect);
            return top.ToArray();
        }

        static byte[] FontDict(int size, int offset)
        {
            var fd = new FontWriter(12);
            DictOffset(fd, size);
            DictOffset(fd, offset);
            DictOp(fd, OpPrivate);
            return fd.ToArray();
        }
    }

    /// <summary>FDSelect format 3: runs of glyphs with the same font dictionary.</summary>
    private static byte[] WriteFdSelect(int[] fds)
    {
        var ranges = new List<(int First, int Fd)>();
        for (int g = 0; g < fds.Length; g++)
        {
            if (g == 0 || fds[g] != fds[g - 1]) ranges.Add((g, fds[g]));
        }

        var w = new FontWriter(5 + 3 * ranges.Count);
        w.U8(3);
        w.U16(ranges.Count);
        foreach ((int first, int fd) in ranges)
        {
            w.U16(first);
            w.U8(fd);
        }

        w.U16(fds.Length);
        return w.ToArray();
    }

    private static byte[] WriteDict(List<(int Op, double[] Operands)> dict)
    {
        var w = new FontWriter(64);
        foreach ((int op, double[] operands) in dict)
        {
            foreach (double v in operands) DictNumber(w, v);
            DictOp(w, op);
        }

        return w.ToArray();
    }

    private static void DictOp(FontWriter w, int op)
    {
        if (op >= 1200)
        {
            w.U8(12);
            w.U8(op - 1200);
        }
        else
        {
            w.U8(op);
        }
    }

    /// <summary>An offset as a 5 byte integer, so sizes do not depend on the offsets.</summary>
    private static void DictOffset(FontWriter w, int value)
    {
        w.U8(29);
        w.U32((uint)value);
    }

    /// <summary>A DICT number: the shortest integer form, or a real in nibbles.</summary>
    private static void DictNumber(FontWriter w, double v)
    {
        if (v == Math.Floor(v) && Math.Abs(v) <= int.MaxValue)
        {
            int i = (int)v;
            switch (i)
            {
                case >= -107 and <= 107:
                    w.U8(i + 139);
                    return;
                case >= 108 and <= 1131:
                    w.U8(((i - 108) >> 8) + 247);
                    w.U8((i - 108) & 0xFF);
                    return;
                case >= -1131 and <= -108:
                    w.U8(((-i - 108) >> 8) + 251);
                    w.U8((-i - 108) & 0xFF);
                    return;
                case >= short.MinValue and <= short.MaxValue:
                    w.U8(28);
                    w.U16(i);
                    return;
                default:
                    w.U8(29);
                    w.U32((uint)i);
                    return;
            }
        }

        string s = v.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "E");
        var nibbles = new List<int>();
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (c is >= '0' and <= '9') nibbles.Add(c - '0');
            else if (c == '.') nibbles.Add(0xA);
            else if (c == '-') nibbles.Add(0xE);
            else if (c == 'E' && k + 1 < s.Length && s[k + 1] == '-')
            {
                nibbles.Add(0xC);
                k++;
            }
            else if (c == 'E') nibbles.Add(0xB);
        }

        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
        w.U8(30);
        for (int k = 0; k < nibbles.Count; k += 2) w.U8(nibbles[k] << 4 | nibbles[k + 1]);
    }

    /// <summary>A CFF (version 1) INDEX.</summary>
    private static byte[] WriteIndex(List<byte[]> items)
    {
        if (items.Count == 0) return [0, 0];
        long total = items.Sum(i => (long)i.Length) + 1;
        if (total > int.MaxValue / 2) throw new FontFormatException("CFF INDEX is too large.");
        int offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;
        var w = new FontWriter(3 + (items.Count + 1) * offSize + (int)total);
        w.U16(items.Count);
        w.U8(offSize);
        int offset = 1;
        for (int i = 0; i <= items.Count; i++)
        {
            for (int k = offSize - 1; k >= 0; k--) w.U8(offset >> (8 * k));
            if (i < items.Count) offset += items[i].Length;
        }

        foreach (byte[] item in items) w.Bytes(item);
        return w.ToArray();
    }

    /// <summary>A CFF2 INDEX: a 32 bit count, offsets and data.</summary>
    private readonly struct Index(int count, int offSize, int offsets, int dataStart, int end)
    {
        public static Index Empty => default;

        public int Count => count;

        public static Index Read(byte[] d, ref int at)
        {
            long n = U32(d, at);
            if (n == 0)
            {
                at += 4;
                return default;
            }

            int offSize = U8(d, at + 4);
            if (offSize is < 1 or > 4) throw new FontFormatException("CFF2 INDEX offset size is invalid.");
            if (n > (d.Length - at) / offSize) throw new FontFormatException("CFF2 INDEX is truncated.");
            int offsets = at + 5;
            int dataStart = offsets + (int)(n + 1) * offSize - 1;
            int last = Offset(d, offsets, offSize, (int)n);
            Check(d, dataStart, last);
            at = dataStart + last;
            return new Index((int)n, offSize, offsets, dataStart, at);
        }

        public ReadOnlySpan<byte> Item(byte[] d, int i)
        {
            int a = Offset(d, offsets, offSize, i), b = Offset(d, offsets, offSize, i + 1);
            if (a < 1 || b < a || b > end - dataStart) throw new FontFormatException("CFF2 INDEX is invalid.");
            return d.AsSpan(dataStart + a, b - a);
        }

        private static int Offset(byte[] d, int offsets, int offSize, int i)
        {
            long v = 0;
            for (int k = 0; k < offSize; k++) v = v << 8 | U8(d, offsets + i * offSize + k);
            if (v > int.MaxValue) throw new FontFormatException("CFF2 INDEX offset is out of range.");
            return (int)v;
        }
    }
}
