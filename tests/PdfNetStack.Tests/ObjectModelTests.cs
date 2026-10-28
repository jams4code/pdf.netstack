using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>The public object model: what callers use for features the high level API lacks.</summary>
[TestClass]
public sealed class ObjectModelTests
{
    [TestMethod]
    public void NumbersAreWrittenCanonically()
    {
        Assert.AreEqual("12", PdfNumber.FromInteger(12).ToString());
        Assert.AreEqual(".5", PdfNumber.FromDouble(0.5).ToString(), "minimal syntax drops the leading zero");
        Assert.AreEqual("-3.25", PdfNumber.FromDouble(-3.25).ToString());
        Assert.AreEqual(".333333", PdfNumber.FromDouble(1.0 / 3).ToString());
        Assert.AreEqual("0", PdfNumber.FromDouble(-0.0000001).ToString());
        Assert.AreEqual("1000000000000", PdfNumber.FromDouble(1e12).ToString(), "never an exponent");

        PdfNumber whole = PdfNumber.FromDouble(72.0);
        Assert.IsTrue(whole.IsInteger);
        Assert.AreEqual(72, whole.IntegerValue);
        Assert.AreEqual(0.25, PdfNumber.FromDouble(0.25).ToDouble());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfNumber.FromDouble(double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfNumber.FromDouble(double.PositiveInfinity));
    }

    [TestMethod]
    public void TextStringsRoundTripAnyLanguage()
    {
        foreach (string text in new[] { "Plain ASCII", "Déjà vu", "مرحبا بالعالم", "日本語", "" })
        {
            Assert.AreEqual(text, PdfString.FromText(text).ToText());
        }

        Assert.AreEqual((byte)'D', PdfString.FromText("Déjà").Bytes[0], "Latin text stays single byte");
        Assert.AreEqual(0xFE, PdfString.FromText("مرحبا").Bytes[0], "other scripts use UTF-16 with a BOM");

        byte[] binary = [0, 1, 2, 255];
        var s = new PdfString(binary.AsSpan()); // the public constructor; this assembly also sees an internal one
        binary[0] = 9;
        Assert.AreEqual(0, s.Bytes[0], "the constructor copies");
    }

    [TestMethod]
    public void ArraysAndDictionariesBehaveLikeCollections()
    {
        PdfArray box = PdfArray.Of(0, 0, 595.28, 841.89);
        Assert.HasCount(4, box);
        Assert.AreEqual("595.28", box[2].ToString());

        var array = PdfArray.Of(new PdfName("A"), PdfBoolean.True);
        array.Add(PdfNull.Instance);
        array.Insert(0, PdfNumber.FromInteger(1));
        array.RemoveAt(1);
        CollectionAssert.AreEqual(new[] { "1", "true", "null" }, array.Select(o => o.ToString()).ToArray());
        Assert.ThrowsExactly<ArgumentNullException>(() => array.Add(null!));
        Assert.ThrowsExactly<ArgumentException>(() => PdfArray.Of(new PdfObject[] { null! }));

        var d = new PdfDictionary { ["Type"] = new PdfName("Example"), ["Count"] = PdfNumber.FromInteger(3) };
        d.Set("Type", new PdfName("Changed"));
        CollectionAssert.AreEqual(new[] { "Type", "Count" }, d.Keys.ToArray(), "replacing keeps the position");
        Assert.AreEqual("Changed", d.GetName("Type"));
        d["Count"] = null;
        Assert.IsFalse(d.ContainsKey("Count"));
        Assert.AreEqual(1, d.Count(e => e.Value is PdfName));

        // Past the index threshold, lookups still work and order is kept.
        var big = new PdfDictionary();
        for (int i = 0; i < 40; i++) big.Set("K" + i, PdfNumber.FromInteger(i));
        big.Remove("K5");
        Assert.AreEqual("K6", big.Keys[5]);
        Assert.AreEqual("39", big["K39"]!.ToString());
    }

    [TestMethod]
    public void CatalogAndPageDictionariesCanBeExtended()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);

        // Viewer preferences and page labels have no high level API yet.
        doc.Catalog.Set("PageMode", new PdfName("UseOutlines"));
        doc.Catalog.Set("ViewerPreferences", new PdfDictionary { ["DisplayDocTitle"] = PdfBoolean.True });
        page.Dictionary.Set("UserUnit", PdfNumber.FromDouble(1.5));

        // A new content stream, added as an indirect object.
        byte[] content = Encoding.ASCII.GetBytes("0 0 1 rg 72 72 144 144 re f");
        PdfReference contents = doc.AddObject(PdfStream.FromData(content));
        page.Dictionary.Set("Contents", contents);

        PdfDocument reopened = PdfDocument.Open(doc.ToArray());
        Assert.AreEqual("UseOutlines", reopened.Catalog.GetName("PageMode"));
        var prefs = (PdfDictionary)reopened.Resolve(reopened.Catalog["ViewerPreferences"])!;
        Assert.AreSame(PdfBoolean.True, reopened.Resolve(prefs["DisplayDocTitle"]));

        PdfPage first = reopened.Pages[0];
        Assert.AreEqual("1.5", reopened.Resolve(first.Dictionary["UserUnit"])!.ToString());
        var stream = (PdfStream)reopened.Resolve(first.Dictionary["Contents"])!;
        CollectionAssert.AreEqual(content, reopened.GetStreamData(stream));
    }

    [TestMethod]
    public void StreamsFromDataAreCompressedOnRequest()
    {
        byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT /F1 12 Tf (repeat) Tj ET ", 100)));

        PdfStream compressed = PdfStream.FromData(data, new PdfDictionary { ["DecodeParms"] = new PdfDictionary() });
        Assert.AreEqual("FlateDecode", compressed.Dictionary.GetName("Filter"));
        Assert.IsFalse(compressed.Dictionary.ContainsKey("DecodeParms"));
        Assert.IsLessThan(data.Length / 4, compressed.Data.Length);

        PdfStream plain = PdfStream.FromData(data, compress: false);
        Assert.IsFalse(plain.Dictionary.ContainsKey("Filter"));
        Assert.IsTrue(plain.Data.Span.SequenceEqual(data));

        PdfDocument doc = PdfDocument.Create();
        CollectionAssert.AreEqual(data, doc.GetStreamData(compressed));
        CollectionAssert.AreEqual(data, doc.GetStreamData(plain));

        var broken = new PdfStream(new PdfDictionary { ["Filter"] = new PdfName("FlateDecode") }, new byte[] { 1, 2, 3 });
        Assert.ThrowsExactly<PdfFormatException>(() => doc.GetStreamData(broken));
    }

    [TestMethod]
    public void ReferencesResolveWithinTheirDocument()
    {
        PdfDocument doc = PdfDocument.Create();
        var target = new PdfDictionary { ["Name"] = PdfString.FromText("shared") };
        PdfReference r = doc.AddObject(target);

        Assert.AreSame(target, doc.Resolve(r));
        Assert.AreSame(target, doc.Resolve(target), "non references come back as they are");
        Assert.IsNull(doc.Resolve(null));
        Assert.ThrowsExactly<ArgumentException>(() => doc.AddObject(r));
        Assert.AreEqual($"{r.Number} 0 R", r.ToString());
    }
}
