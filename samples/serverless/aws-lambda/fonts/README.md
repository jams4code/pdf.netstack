# Fonts

Put the font files the HTML uses here: `.ttf`, `.otf`, `.ttc`, `.woff` or `.woff2`. They are
copied next to the executable on publish and loaded once per instance. Serverless hosts have no
fonts installed, or only a few, so the converter uses these and the standard 14 PDF fonts and
nothing else.

Use fonts whose license allows embedding in documents, such as fonts under the SIL Open Font
License, and keep the license file with them. Font files are not committed to this repository.

With no fonts here the samples still work: CSS families fall back to Helvetica, Times and
Courier, which cover Western European text.
