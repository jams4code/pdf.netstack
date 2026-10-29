using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Acrobat format actions applied in C# when field appearances are drawn.</summary>
[TestClass]
public sealed class FieldFormatTests
{
    private static string? Format(string script, string value) =>
        FieldFormat.Call.Parse(script) is FieldFormat.Call call ? FieldFormat.Apply(call, value)?.Text : null;

    private static PdfTextField Field(PdfDocument doc, string script, string? value = null)
    {
        PdfPage page = doc.Pages.Count > 0 ? doc.Pages[0] : doc.Pages.Add(PdfRectangle.A4);
        PdfTextField field = doc.Form.AddTextField(page, "f" + doc.Form.Fields.Count, 72, 100, 200, 20);
        field.Dictionary.Set("AA", new PdfDictionary
        {
            ["F"] = new PdfDictionary { ["S"] = new PdfName("JavaScript"), ["JS"] = PdfString.FromText(script) },
        });
        if (value is not null) field.Text = value;
        return field;
    }

    private static string AppearanceText(PdfDocument doc, PdfField field)
    {
        var ap = (PdfDictionary)doc.Resolve(field.Widgets[0].Dictionary["AP"])!;
        return Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(ap["N"])!));
    }

    [TestMethod]
    [DataRow("AFNumber_Format(2, 0, 0, 0, \"$\", true);", "1234.5", "$1,234.50")]
    [DataRow("AFNumber_Format(2, 0, 0, 0, \"$\", true);", "-1234.5", "-$1,234.50")]
    [DataRow("AFNumber_Format(2, 0, 2, 0, \"$\", true);", "-1234.5", "($1,234.50)")]
    [DataRow("AFNumber_Format(2, 0, 1, 0, \"\", true);", "-3", "3.00")]
    [DataRow("AFNumber_Format(0, 1, 0, 0, \"\", false);", "1234567.5", "1234568")]
    [DataRow("AFNumber_Format(2, 2, 0, 0, \" €\", false);", "1234,567", "1.234,57 €")]
    [DataRow("AFNumber_Format(1, 3, 0, 0, \"\", true);", "-0.04", "0,0")]
    [DataRow("AFNumber_Format(2, 4, 0, 0, \"CHF \", true)", "1234567.891", "CHF 1'234'567.89")]
    [DataRow("AFNumber_Format(3, 0, 0, 0, '', true)", "1,234", "1,234.000")]
    [DataRow("AFNumber_Format(2,0,0,0,\"\",true)", "0.005", "0.01")]
    [DataRow("AFNumber_Format(2, 0, 0, 0, \"\", true);", "12abc", null)]
    [DataRow("AFNumber_Format(99, 0, 0, 0, \"\", true);", "1", "1.0000000000")]
    [DataRow("AFPercent_Format(2, 0);", "0.1234", "12.34%")]
    [DataRow("AFPercent_Format(0, 0, true);", "-0.5", "-%50")]
    [DataRow("AFPercent_Format(1, 2);", "12,3456", "1.234,6%")]
    [DataRow("AFSpecial_Format(0);", "12345", "12345")]
    [DataRow("AFSpecial_Format(1);", "123456789", "12345-6789")]
    [DataRow("AFSpecial_Format(2);", "5551234567", "(555) 123-4567")]
    [DataRow("AFSpecial_Format(2);", "555 1234", "555-1234")]
    [DataRow("AFSpecial_Format(3);", "123-45-6789", "123-45-6789")]
    [DataRow("AFSpecial_Format(3);", "12345678", null)]
    [DataRow("AFSpecial_Format(0);", "1234A", null)]
    public void NumbersPercentsAndSpecialFormats(string script, string value, string? expected) => Assert.AreEqual(expected, Format(script, value));

    [TestMethod]
    [DataRow("AFDate_FormatEx(\"mm/dd/yyyy\");", "3/7/2024", "03/07/2024")]
    [DataRow("AFDate_FormatEx(\"dd.mm.yyyy\");", "2024-03-07", "07.03.2024")]
    [DataRow("AFDate_FormatEx(\"mmmm d, yyyy\");", "D:20240307143000+01'00'", "March 7, 2024")]
    [DataRow("AFDate_FormatEx(\"dddd, mmm d, yy\");", "2024-03-07", "Thursday, Mar 7, 24")]
    [DataRow("AFDate_FormatEx(\"d-mmm-yyyy\");", "7-mar-2024", "7-Mar-2024")]
    [DataRow("AFDate_FormatEx(\"yyyy-mm-dd HH:MM\");", "2024-03-07T14:05", "2024-03-07 14:05")]
    [DataRow("AFDate_FormatEx(\"mm/dd/yyyy\");", "2/30/2024", null)]
    [DataRow("AFDate_FormatEx(\"mm/dd/yyyy\");", "someday", null)]
    [DataRow("AFDate_FormatEx(\"\\\\D\\\\a\\\\y d\");", "2024-03-07", "Day 7")]
    [DataRow("AFDate_Format(1);", "2024-03-07", "3/7/24")]
    [DataRow("AFDate_Format(11);", "2024-12-25", "December 25, 2024")]
    [DataRow("AFDate_Format(99);", "2024-12-25", null)]
    [DataRow("AFTime_Format(1);", "14:05", "2:05 pm")]
    [DataRow("AFTime_Format(3);", "00:05:09", "12:05:09 am")]
    [DataRow("AFTime_FormatEx(\"h:MM tt\");", "9:30 pm", "9:30 pm")]
    [DataRow("AFTime_FormatEx(\"HH:MM\");", "9:30 pm", null)]
    [DataRow("AFTime_FormatEx(\"h:MM t\");", "21:30", "9:30 p")]
    public void DatesAndTimes(string script, string value, string? expected) => Assert.AreEqual(expected, Format(script, value));

    [TestMethod]
    [DataRow("")]
    [DataRow("AFNumber_Format")]
    [DataRow("AFNumber_Format(2, 0, 0, 0, \"$\", true); app.alert('x');")]
    [DataRow("if (event.value) AFNumber_Format(2, 0, 0, 0, \"$\", true);")]
    [DataRow("AFNumber_Format(2, 0, 0, 0, \"$, true);")]
    [DataRow("AFNumber_Format(2, 0, 0, 0, x, true);")]
    [DataRow("AFNumber_Format(2 0)")]
    [DataRow("1AFNumber_Format(2)")]
    [DataRow("event.value = util.printx('99', event.value);")]
    public void OnlyASingleKnownCallIsRead(string script)
    {
        FieldFormat.Call? call = FieldFormat.Call.Parse(script);
        Assert.IsTrue(call is null || FieldFormat.Apply(call, "1") is null, script);
    }

    [TestMethod]
    public void StringsAndArgumentsParse()
    {
        FieldFormat.Call call = FieldFormat.Call.Parse(" \n AFNumber_Format ( 2 , '1' , 0 , 0 , \"\\u20ac\\\"\" , false ) ; \n")!;
        Assert.AreEqual("AFNumber_Format", call.Name);
        Assert.AreEqual(2, call.Int(0, -1));
        Assert.AreEqual(1, call.Int(1, -1));
        Assert.AreEqual("€\"", call.Text(4, ""));
        Assert.IsFalse(call.Bool(5, true));
        Assert.AreEqual(7, call.Int(9, 7));
        Assert.AreEqual(-1, FieldFormat.Call.Parse("f(1e300)")!.Int(0, -1));
        Assert.IsNull(FieldFormat.Call.Parse("f(" + string.Join(",", Enumerable.Repeat("1", 17)) + ")"));
    }

    [TestMethod]
    public void FieldsShowTheFormattedValue()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfTextField amount = Field(doc, "AFNumber_Format(2, 0, 0, 0, \"$\", true);", "1234.5");
        Assert.AreEqual("1234.5", amount.Text);
        Assert.AreEqual("$1,234.50", amount.FormattedText);
        StringAssert.Contains(AppearanceText(doc, amount), "($1,234.50)");

        // A negative number in red: the color replaces the default appearance's.
        PdfTextField debt = Field(doc, "AFNumber_Format(2, 0, 3, 0, \"\", true);", "-20");
        Assert.AreEqual("(20.00)", debt.FormattedText);
        StringAssert.Contains(AppearanceText(doc, debt), "1 0 0 rg");

        PdfTextField day = Field(doc, "AFDate_FormatEx(\"dd mmm yyyy\");", "2024-03-07");
        StringAssert.Contains(AppearanceText(doc, day), "(07 Mar 2024)");

        // Text the format cannot read shows as it is; so does any other script.
        PdfTextField odd = Field(doc, "AFNumber_Format(2, 0, 0, 0, \"$\", true);", "n/a");
        Assert.AreEqual("n/a", odd.FormattedText);
        PdfTextField custom = Field(doc, "event.value = event.value.toUpperCase();", "quiet");
        Assert.AreEqual("quiet", custom.FormattedText);
        PdfTextField empty = Field(doc, "AFNumber_Format(2, 0, 0, 0, \"$\", true);");
        Assert.IsNull(empty.FormattedText);
        empty.Text = "";
        Assert.AreEqual("", empty.FormattedText);
    }

    [TestMethod]
    public void ScriptsInStreamsAndHostileScripts()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfTextField field = Field(doc, "x");
        var action = (PdfDictionary)((PdfDictionary)field.Dictionary["AA"]!)["F"]!;
        action.Set("JS", doc.AddObject(PdfStream.FromData("AFPercent_Format(1, 0);"u8)));
        field.Text = "0.5";
        Assert.AreEqual("50.0%", field.FormattedText);

        // Over long scripts are not read; a broken stream or a non JavaScript action is ignored.
        action.Set("JS", PdfString.FromText("AFPercent_Format(1, 0);" + new string(' ', 10_000)));
        Assert.AreEqual("0.5", field.FormattedText);
        var broken = new PdfStream(new PdfDictionary { ["Filter"] = new PdfName("FlateDecode") }, new byte[] { 1, 2, 3 });
        action.Set("JS", doc.AddObject(broken));
        Assert.AreEqual("0.5", field.FormattedText);

        // A compressed script that inflates past the script limit is refused.
        byte[] bomb = [.. "AFPercent_Format(1, 0);"u8.ToArray(), .. Enumerable.Repeat((byte)' ', 4 << 20)];
        action.Set("JS", doc.AddObject(PdfStream.FromData(bomb)));
        Assert.AreEqual("0.5", field.FormattedText);
        action.Set("S", new PdfName("URI"));
        Assert.AreEqual("0.5", field.FormattedText);
        field.Dictionary.Set("AA", PdfNull.Instance);
        Assert.AreEqual("0.5", field.FormattedText);
    }

    [TestMethod]
    public void ComboBoxesAndPasswordsToo()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfChoiceField combo = doc.Form.AddComboBox(page, "rate", 72, 100, 100, 20, ["0.05", "0.1"], null, new PdfFieldOptions { Editable = true });
        combo.Dictionary.Set("AA", new PdfDictionary { ["F"] = new PdfDictionary { ["S"] = new PdfName("JavaScript"), ["JS"] = PdfString.FromText("AFPercent_Format(0, 0);") } });
        combo.Select("0.1");
        StringAssert.Contains(AppearanceText(doc, combo), "(10%)");

        PdfTextField secret = doc.Form.AddTextField(page, "pin", 72, 200, 100, 20, null, new PdfFieldOptions { Password = true });
        secret.Dictionary.Set("AA", new PdfDictionary { ["F"] = new PdfDictionary { ["S"] = new PdfName("JavaScript"), ["JS"] = PdfString.FromText("AFNumber_Format(2, 0, 0, 0, \"\", true);") } });
        secret.Text = "1234";
        Assert.AreEqual("****", secret.FormattedText);
        StringAssert.Contains(AppearanceText(doc, secret), "(****)");
    }
}
