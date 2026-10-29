"""Regenerates the AcroForm test fixture with ReportLab, a common producer of fillable PDFs.

    pip install reportlab
    python tools/make_form_fixtures.py

tests/PdfNetStack.Tests/Fixtures/Forms/form.pdf holds text fields (single line, multiline, with a
maximum length), a check box, a radio group, a combo box and a list box, some prefilled.
"""

from pathlib import Path

from reportlab.lib.colors import HexColor
from reportlab.pdfgen import canvas

OUT = Path(__file__).resolve().parent.parent / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Forms"


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUT / "form.pdf"), pagesize=(595, 842), invariant=1)
    c.setFont("Helvetica-Bold", 16)
    c.drawString(50, 790, "Application")
    c.setFont("Helvetica", 10)
    form = c.acroForm
    border = HexColor("#94A3B8")

    c.drawString(50, 752, "Name")
    form.textfield(name="name", x=150, y=745, width=250, height=20, borderColor=border, fontName="Helvetica", fontSize=11, tooltip="Full name")
    c.drawString(50, 722, "Email")
    form.textfield(name="email", value="someone@example.com", x=150, y=715, width=250, height=20, borderColor=border, fontName="Helvetica", fontSize=11)
    c.drawString(50, 692, "Code")
    form.textfield(name="code", x=150, y=685, width=80, height=20, borderColor=border, maxlen=6, fontName="Courier", fontSize=11)
    c.drawString(50, 662, "Notes")
    form.textfield(name="notes", x=150, y=595, width=250, height=80, borderColor=border, fieldFlags="multiline", fontName="Helvetica", fontSize=10)

    c.drawString(50, 562, "Subscribe")
    form.checkbox(name="subscribe", x=150, y=557, size=14, buttonStyle="check", borderColor=border, checked=False)
    c.drawString(50, 532, "Plan")
    for i, plan in enumerate(["basic", "pro", "team"]):
        form.radio(name="plan", value=plan, selected=(plan == "basic"), x=150 + 70 * i, y=527, size=14, buttonStyle="circle", borderColor=border)
        c.drawString(168 + 70 * i, 530, plan)

    c.drawString(50, 502, "Country")
    form.choice(name="country", value="Switzerland", options=["France", "Switzerland", "Morocco"], x=150, y=495, width=150, height=20, borderColor=border, fontName="Helvetica", fontSize=10)
    c.drawString(50, 472, "Languages")
    form.listbox(name="languages", value="English", options=["Arabic", "English", "French"], x=150, y=420, width=150, height=60, borderColor=border, fontName="Helvetica", fontSize=10)
    c.showPage()
    c.save()
    print(OUT / "form.pdf")


if __name__ == "__main__":
    main()
