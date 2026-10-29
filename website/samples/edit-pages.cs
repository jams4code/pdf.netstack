#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

#region edit
PdfDocument doc = PdfDocument.Open("contract.pdf");

doc.Pages.RemoveAt(0);                         // drop the cover
doc.Pages.Move(2, 0);                          // the third page becomes the first
doc.Pages[0].Rotate(90);                       // clockwise, in steps of 90 degrees
doc.Pages.Add(PdfRectangle.A4);                // a blank page at the end
doc.Pages.Insert(1, PdfRectangle.Letter);      // or anywhere

PdfDocument annex = PdfDocument.Open("annex.pdf");
doc.Pages.Import(annex.Pages);                 // shared fonts and images are copied once

doc.Save("contract-edited.pdf");               // optimized and verified by default
#endregion

Console.WriteLine($"{doc.Pages.Count} pages, first page rotated {doc.Pages[0].Rotation} degrees");
