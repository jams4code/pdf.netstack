using System.Globalization;
using System.Text;

namespace PdfNetStack.Drawing;

/// <summary>
/// Writes content stream operators (ISO 32000-1, 8 and 9) into a growing byte buffer. Numbers are
/// rounded to 5 decimals, about 1/7000 of a millimetre at 1:1, and written in minimal form.
/// </summary>
internal sealed class ContentWriter
{
    private byte[] _buffer = new byte[1024];
    private int _length;
    private bool _needsSpace;

    public int Length => _length;

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public ContentWriter Number(double value)
    {
        Separate();
        double scaled = Math.Round(value * 100000);
        if (Math.Abs(scaled) >= 9e15)
        {
            Ascii(Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture));
            _needsSpace = true;
            return this;
        }

        long v = (long)scaled;
        if (v == 0)
        {
            Byte((byte)'0');
            _needsSpace = true;
            return this;
        }

        if (v < 0)
        {
            Byte((byte)'-');
            v = -v;
        }

        long whole = v / 100000;
        int fraction = (int)(v % 100000);
        if (whole != 0) Integer(whole);
        if (fraction != 0)
        {
            Byte((byte)'.');
            int digits = 5;
            while (fraction % 10 == 0)
            {
                fraction /= 10;
                digits--;
            }

            Span<byte> tmp = stackalloc byte[5];
            for (int i = digits - 1; i >= 0; i--)
            {
                tmp[i] = (byte)('0' + fraction % 10);
                fraction /= 10;
            }

            Bytes(tmp.Slice(0, digits));
        }

        _needsSpace = true;
        return this;
    }

    public ContentWriter Numbers(double a, double b) => Number(a).Number(b);

    public ContentWriter Numbers(double a, double b, double c) => Number(a).Number(b).Number(c);

    public ContentWriter Numbers(double a, double b, double c, double d) => Number(a).Number(b).Number(c).Number(d);

    public ContentWriter Matrix(PdfMatrix m) => Numbers(m.A, m.B, m.C, m.D).Numbers(m.E, m.F);

    /// <summary>A name operand; <paramref name="name"/> must be a plain resource name (letters and digits).</summary>
    public ContentWriter Name(string name)
    {
        Separate();
        Byte((byte)'/');
        Ascii(name);
        _needsSpace = true;
        return this;
    }

    /// <summary>An operand already in PDF syntax, such as a dash array.</summary>
    public ContentWriter Raw(string text)
    {
        Separate();
        Ascii(text);
        _needsSpace = true;
        return this;
    }

    /// <summary>An operator, which ends the line.</summary>
    public ContentWriter Op(string op)
    {
        Separate();
        Ascii(op);
        Byte((byte)'\n');
        _needsSpace = false;
        return this;
    }

    public ContentWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        return this;
    }

    private void Integer(long value)
    {
        Span<byte> tmp = stackalloc byte[20];
        int i = tmp.Length;
        do
        {
            tmp[--i] = (byte)('0' + value % 10);
            value /= 10;
        }
        while (value != 0);
        Bytes(tmp.Slice(i));
    }

    private void Separate()
    {
        if (_needsSpace) Byte((byte)' ');
    }

    private void Ascii(string text)
    {
        Ensure(text.Length);
        _length += Encoding.ASCII.GetBytes(text, 0, text.Length, _buffer, _length);
    }

    private void Byte(byte b)
    {
        Ensure(1);
        _buffer[_length++] = b;
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        int size = Math.Max(_buffer.Length * 2, _length + extra);
        Array.Resize(ref _buffer, size);
    }
}
