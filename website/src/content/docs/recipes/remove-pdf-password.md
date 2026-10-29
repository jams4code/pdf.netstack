---
title: Remove a password from a PDF in C#
description: Decrypt a password protected PDF in .NET with its owner password and save it unprotected. Reads RC4, AES-128 and AES-256. MIT licensed.
since: '1.0'
sidebar:
  label: Remove a password
---

Save an unprotected copy of an encrypted PDF you have the owner password for.

## Short answer

Open the file with its owner password, call `Security.RemoveEncryption`, and save. PdfNetStack
opens every standard security handler revision: RC4 40 to 128 bit, AES-128 and AES-256.

```csharp sample="recipe-remove-pdf-password.cs" region="short"
```

```bash
dotnet add package PdfNetStack
```

## Notes

The owner password is required: a document opened with only the user password can be read but not
saved. A wrong password raises `PdfPasswordException`.

## Related

- [Encryption](/guides/encryption/)
- [Password protect a PDF](/recipes/password-protect-pdf/)
- [Command line](/cli/): `pdfnetstack decrypt in.pdf out.pdf --password o`
- API: [PdfSecurity](/api/pdfnetstack/pdfsecurity/), [PdfPasswordException](/api/pdfnetstack/pdfpasswordexception/)
