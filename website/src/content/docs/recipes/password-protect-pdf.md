---
title: Password protect a PDF in C#
description: Encrypt a PDF with AES-256 in .NET, with a user password, an owner password and permissions such as print and copy. MIT licensed, pure C#.
since: '1.0'
sidebar:
  label: Password protect
---

Encrypt a PDF so it needs a password to open, and limit what readers may do with it.

## Short answer

Call `doc.Security.Encrypt` with a user password, an owner password and the permissions to grant,
then save. The user password opens the file with those permissions; the owner password gives full
access. New protection is AES-256 unless you pass `PdfEncryption.Aes128`.

```csharp sample="recipe-password-protect-pdf.cs"
```

```bash
dotnet add package PdfNetStack
```

## Notes

Permissions are `Print`, `PrintHighQuality`, `Modify`, `CopyContent`, `Annotate`, `FillForms`,
`ExtractForAccessibility` and `Assemble`, combined with `|`. A document opened with only the user
password can be read but not saved. One opened with the owner password keeps its passwords and
permissions when saved.

## Related

- [Encryption](/guides/encryption/)
- [Remove a password from a PDF](/recipes/remove-pdf-password/)
- [Command line](/cli/): `pdfnetstack encrypt in.pdf out.pdf --user-password u --owner-password o --allow print,copy`
- API: [PdfSecurity](/api/pdfnetstack/pdfsecurity/), [PdfPermissions](/api/pdfnetstack/pdfpermissions/)
