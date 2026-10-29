#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;

// A reviewed copy to flatten: a stamp, a highlight and a note on the first page.
PdfDocument review = PdfDocument.Open("contract.pdf");
review.Pages[0].Annotations.AddStamp(PdfStampName.Final, 400, 30, 150, 45);
review.Pages[0].Annotations.AddTextMarkup(PdfTextMarkup.Highlight, 70, 160, 410, 16, comment: "Agreed");
review.Pages[0].Annotations.AddNote(500, 150, "Signed copy to follow", author: "Legal");
review.Save("contract-reviewed.pdf");

#region all
PdfDocument doc = PdfDocument.Open("contract-reviewed.pdf");
int flattened = doc.FlattenAnnotations();                 // every page; links and form fields stay
doc.Save("contract-flat.pdf");
Console.WriteLine($"{flattened} annotations are now page content");
#endregion

#region one
PdfDocument other = PdfDocument.Open("contract-reviewed.pdf");
PdfPage page = other.Pages[0];
PdfAnnotation stamp = page.Annotations.First(a => a.Subtype == "Stamp");
page.Annotations.Flatten(stamp);                          // only the stamp; the rest stay editable
other.Save("contract-stamped.pdf");
#endregion
