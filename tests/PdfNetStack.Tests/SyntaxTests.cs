using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class SyntaxTests
{
    private static PdfObject Parse(string text) => new PdfParser(Encoding.Latin1.GetBytes(text)).ParseObject();

    [TestMethod]
    [DataRow(@"(abc)", "abc")]
    [DataRow(@"(a\nb)", "a\nb")]
    [DataRow(@"(a\(b\)c)", "a(b)c")]
    [DataRow(@"(a(b)c)", "a(b)c")]
    [DataRow(@"(\101\102)", "AB")]
    [DataRow(@"(\0053)", "\u00053")]
    [DataRow(@"(\q)", "q")]
    [DataRow("(line\\\ncontinued)", "linecontinued")]
    [DataRow("(cr\rlf)", "cr\nlf")]
    [DataRow("(crlf\r\nx)", "crlf\nx")]
    [DataRow(@"(\\)", "\\")]
    public void LiteralStrings(string syntax, string expected)
    {
        var s = (PdfString)Parse(syntax);
        Assert.AreEqual(expected, Encoding.Latin1.GetString(s.Value));
    }

    [TestMethod]
    [DataRow("<414243>", "ABC")]
    [DataRow("<41 42\n43>", "ABC")]
    [DataRow("<414>", "A@")]
    [DataRow("<>", "")]
    public void HexStrings(string syntax, string expected)
    {
        var s = (PdfString)Parse(syntax);
        Assert.AreEqual(expected, Encoding.Latin1.GetString(s.Value));
    }

    [TestMethod]
    [DataRow("/Type", "Type")]
    [DataRow("/A#20B", "A B")]
    [DataRow("/#2F", "/")]
    [DataRow("/", "")]
    [DataRow("/a#zz", "a#zz")]
    public void Names(string syntax, string expected) => Assert.AreEqual(expected, ((PdfName)Parse(syntax)).Value);

    [TestMethod]
    [DataRow("0", "0")]
    [DataRow("+17", "17")]
    [DataRow("-0", "0")]
    [DataRow("007", "7")]
    [DataRow("3.140", "3.14")]
    [DataRow("-.50", "-.5")]
    [DataRow("4.", "4")]
    [DataRow("0.0", "0")]
    [DataRow("123456789012345678901234", "123456789012345678901234")]
    [DataRow("1.000000000000000000001", "1.000000000000000000001")]
    public void NumbersAreCanonicalAndExact(string syntax, string expected) =>
        Assert.AreEqual(expected, ((PdfNumber)Parse(syntax)).ToString());

    [TestMethod]
    public void MalformedNumberIsKeptVerbatim() => Assert.AreEqual("--5", ((PdfNumber)Parse("--5")).ToString());

    [TestMethod]
    public void ReferencesAreRecognizedByLookahead()
    {
        var a = (PdfArray)Parse("[1 0 R 2 3 4 0 R 5 6 7 R]");
        Assert.AreEqual(6, a.Count);
        Assert.IsInstanceOfType<PdfReference>(a[0]);
        Assert.AreEqual("2", a[1].ToString());
        Assert.AreEqual("3", a[2].ToString());
        Assert.AreEqual(4, ((PdfReference)a[3]).Number);
        Assert.AreEqual(6, ((PdfReference)a[5]).Number);
        Assert.AreEqual(7, ((PdfReference)a[5]).Generation);
    }

    [TestMethod]
    public void DictionariesSkipCommentsAndTreatMissingValueAsAbsent()
    {
        var d = (PdfDictionary)Parse("<< /A 1 % comment >>\n /B [ /C ] /D >>");
        Assert.AreEqual("1", d["A"]!.ToString());
        Assert.IsInstanceOfType<PdfArray>(d["B"]);
        Assert.IsFalse(d.ContainsKey("D"));
    }

    [TestMethod]
    public void DeepNestingFailsCleanly()
    {
        string hostile = new string('[', 100_000) + new string(']', 100_000);
        Assert.ThrowsExactly<PdfFormatException>(() => Parse(hostile));
    }

    [TestMethod]
    public void UnterminatedStringFailsCleanly() => Assert.ThrowsExactly<PdfFormatException>(() => Parse("(abc"));

    [TestMethod]
    public void SerializerRoundTripsRandomStringsAndNames()
    {
        var random = new Random(1234);
        var serializer = new PdfSerializer(r => r.Number);
        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 40)];
            random.NextBytes(bytes);
            string name = Encoding.Latin1.GetString(bytes).Replace("\0", "", StringComparison.Ordinal);

            var array = new PdfArray([new PdfString(bytes), new PdfName(name), PdfNumber.FromInteger(i), new PdfName(""), PdfNumber.FromInteger(7)]);
            var sink = new ByteSink();
            serializer.Write(sink, array);
            var parsed = (PdfArray)new PdfParser(sink.ToArray()).ParseObject();

            Assert.AreEqual(5, parsed.Count, Encoding.Latin1.GetString(sink.WrittenSpan));
            CollectionAssert.AreEqual(bytes, ((PdfString)parsed[0]).Value);
            Assert.AreEqual(name, ((PdfName)parsed[1]).Value);
            Assert.AreEqual(i.ToString(System.Globalization.CultureInfo.InvariantCulture), parsed[2].ToString());
            Assert.AreEqual("", ((PdfName)parsed[3]).Value);
            Assert.AreEqual("7", parsed[4].ToString());
        }
    }

    [TestMethod]
    public void SerializerUsesMinimalSeparators()
    {
        var d = (PdfDictionary)Parse("<< /Type /Page /Kids [ 1 0 R 2 0 R ] /N 5 /S (x) /Null null >>");
        var sink = new ByteSink();
        new PdfSerializer(r => r.Number).Write(sink, d);
        Assert.AreEqual("<</Type/Page/Kids[1 0 R 2 0 R]/N 5/S(x)>>", Encoding.Latin1.GetString(sink.WrittenSpan));
    }
}
