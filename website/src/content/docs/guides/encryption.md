---
title: Encrypt and decrypt PDFs in C# and .NET
description: Password protect PDFs in C# with AES-256 or AES-128 and permissions, open RC4 and AES files, and remove protection. Pure C#, MIT licensed.
since: '1.0'
sidebar:
  label: Encryption
faq:
  - q: Which encryption does PdfNetStack write?
    a: AES-256 (security handler revision 6) by default, or AES-128 (revision 4) for older readers. RC4 files can be opened but RC4 is never written.
  - q: Can I open a PDF with only the user password and save it again?
    a: No. With only the user password a document can be read but not saved. Open it with the owner password to change or save it.
  - q: Does saving a protected document keep its passwords?
    a: Yes. A document opened with its owner password is saved with its original encryption, so both passwords keep working, unless you call Encrypt or RemoveEncryption.
---

Open password protected PDFs, protect your own output with AES, and remove protection when you
hold the owner password.

## Short answer

Call `doc.Security.Encrypt` with a user password, an owner password and the permissions, then save.
The file is written with AES-256 unless you ask for AES-128. Open a protected file by passing the
password to `PdfDocument.Open`.

```csharp sample="encryption-protect.cs" region="protect"
```

```bash
dotnet add package PdfNetStack
```

The command line equivalents are `pdfnetstack encrypt` and `pdfnetstack decrypt`, see
[Command line](/cli/#encrypt).

## Passwords

| Password | Who uses it | What it allows |
|---|---|---|
| User password | Anyone who should read the file. Empty means anyone can open it | Viewing, plus the permissions you grant |
| Owner password | You | Every permission, and changing or removing the protection. An empty owner password is replaced by a random one |

## Permissions

`PdfPermissions` flags say what a reader with only the user password may do. Combine them with `|`.

| Flag | Allows |
|---|---|
| `Print` | Printing, possibly at reduced quality |
| `PrintHighQuality` | Printing at full quality |
| `CopyContent` | Copying or extracting text and graphics |
| `ExtractForAccessibility` | Extracting text and graphics for accessibility |
| `Modify` | Changing content other than annotations and forms |
| `Annotate` | Adding or changing annotations and filling forms |
| `FillForms` | Filling existing form fields |
| `Assemble` | Inserting, rotating or deleting pages and creating bookmarks |
| `None`, `All` | Nothing beyond viewing, or every permission (the default) |

Viewers enforce permissions. They are a statement of intent to conforming software, not a
technical barrier for someone who holds the file.

## AES-256 or AES-128

`PdfEncryption.Aes256` (revision 6) is the default. It needs a reader that supports PDF 2.0 or
PDF 1.7 extension level 8, which the file announces. `PdfEncryption.Aes128` (revision 4) is
readable by PDF 1.6 and later:

```csharp sample="encryption-protect.cs" region="aes128"
```

## Open a protected file

Pass either password. Without a password, opening tries the empty user password. A password that
opens nothing raises `PdfPasswordException`.

```csharp sample="encryption-protect.cs" region="open"
```

`doc.Security` tells you what you opened:

| Property | Meaning |
|---|---|
| `WasEncrypted` | The file was encrypted when it was opened |
| `HasOwnerAccess` | Opened with the owner password, or not encrypted |
| `Permissions` | What the user password grants. `All` for files that are not encrypted |
| `WillEncrypt` | Saving will write an encrypted file |

Opening supports every standard security handler revision: RC4 from 40 to 128 bit, AES-128 and
AES-256 (revisions 2 to 6), user and owner passwords, crypt filters and unencrypted metadata.

## Edit, keep or remove the protection

A document opened with its owner password keeps its encryption on save, so both passwords and the
permissions still apply to the new file. `RemoveEncryption` saves it without protection, and
`Encrypt` replaces the protection with new passwords.

```csharp sample="encryption-protect.cs" region="owner"
```

Encryption is the last step of a save: the finished, optimized and verified file is written once
more with every string and stream encrypted.

## Limits

- New protection is AES-256 or AES-128. RC4 is read but never written.
- A document opened with only the user password can be read but not saved, and its pages can be
  imported into another document only when its permissions allow copying.
- Only the standard password security handler is supported. A file protected by another handler,
  such as certificate based security, raises `NotSupportedException` when opened.
- The [optimizer](/guides/compress-pdf/) returns encrypted files unchanged. Open the document with
  its owner password and save it to get an optimized, still encrypted file.

## Related

- [Edit pages](/guides/edit-pages/): everything else you can do once the file is open
- [Signatures](/guides/signatures/): prove who approved a document
- [Command line](/cli/#encrypt): `encrypt` and `decrypt`
- API: [PdfSecurity](/api/pdfnetstack/pdfsecurity/), [PdfPermissions](/api/pdfnetstack/pdfpermissions/), [PdfEncryption](/api/pdfnetstack/pdfencryption/), [PdfPasswordException](/api/pdfnetstack/pdfpasswordexception/)
