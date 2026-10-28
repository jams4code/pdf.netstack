namespace PdfNetStack.Parsing;

internal static class PdfSyntax
{
    private static readonly byte[] CharClass = BuildTable();

    // 0 = regular, 1 = whitespace, 2 = delimiter
    private static byte[] BuildTable()
    {
        var table = new byte[256];
        foreach (byte b in "\0\t\n\f\r "u8) table[b] = 1;
        foreach (byte b in "()<>[]{}/%"u8) table[b] = 2;
        return table;
    }

    public static bool IsWhitespace(byte b) => CharClass[b] == 1;

    public static bool IsDelimiter(byte b) => CharClass[b] == 2;

    public static bool IsRegular(byte b) => CharClass[b] == 0;

    public static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    public static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
