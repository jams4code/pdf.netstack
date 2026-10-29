---
title: Digitally sign PDF files in C# and .NET
description: Sign PDFs in .NET with PAdES B-B signatures, RSA or ECDSA keys, visible or invisible, and verify every signature in a file. Pure C#, MIT licensed.
since: '1.1'
sidebar:
  label: Signatures
faq:
  - q: Which signature format does PdfNetStack write?
    a: PAdES baseline B-B. The signature is a CAdES detached CMS (ETSI.CAdES.detached) with SHA-256, SHA-384 or SHA-512 and an RSA or ECDSA key, written in an incremental update.
  - q: Does signing need another package?
    a: No. The CMS structure is encoded by the library itself, on top of the .NET cryptography classes.
  - q: Does PdfSigner.Verify check that the certificate is trusted?
    a: No. It checks the byte range, the digest and the cryptographic signature, and returns the certificate so your application makes the trust decision.
---

Sign a PDF with a certificate, add a visible signature box, and check every signature a file
carries.

## Short answer

Call `PdfSigner.Sign` with the PDF bytes and an `X509Certificate2` that has a private key. It
appends a PAdES B-B signature in an incremental update and returns the signed file.
`PdfSigner.Verify` lists every signature with what held and what did not.

```csharp sample="signatures-sign.cs" region="sign"
```

The certificate can come from a .pfx file, the Windows certificate store, or a smart card through
its provider: anything `X509Certificate2` can reach with a private key. The sample creates a
throwaway self-signed certificate so it runs anywhere.

![A contract page with a visible signature box in the top right corner](/previews/signatures-visible.webp)

## What a signature contains

- A CAdES detached CMS signature (`ETSI.CAdES.detached`) with the ESS signing certificate v2
  attribute, as PAdES baseline B-B requires.
- A digest of SHA-256 (the default), SHA-384 or SHA-512, and an RSA or ECDSA key.
- The signing time, reason, location and contact information you pass, in the signature
  dictionary.
- Intermediate certificates from `Chain`, so validators can build the path to a trusted root.

The CMS structure is encoded by the library. No package beyond .NET is needed, and OpenSSL
verifies the result.

## Options

| `PdfSignatureOptions` | Default | Notes |
|---|---|---|
| `Reason`, `Location`, `ContactInfo` | null | Written to the signature dictionary |
| `FieldName` | null | The signature field's name; null picks Signature1, Signature2 and so on |
| `Digest` | `Sha256` | `Sha384` and `Sha512` are also available |
| `Chain` | empty | Intermediate certificates to embed |
| `Appearance` | null | A visible box; null signs invisibly |
| `SigningTime` | now | The time recorded in the signature dictionary, not a trusted timestamp |

## A visible signature

`PdfSignatureAppearance` places a box on a page. `PageIndex` is zero based, and `X`, `Y`,
`Width` and `Height` are in points from the top left of the page, as for
[drawing](/guides/drawing/). By default the box shows "Digitally signed by", the signer's name, the
date and the reason. `Text` replaces that text and `Font` changes the font (Helvetica by default;
use an embedded font in a [PDF/A](/guides/pdf-a/) document).

## Verify signatures

```csharp sample="signatures-sign.cs" region="verify"
```

For each signature field with a value, `Verify` checks that the byte range excludes exactly the
signature's own contents, that the signed bytes still hash to the signed digest, and that the
signature verifies with the certificate's public key.

| `PdfSignatureInfo` | Meaning |
|---|---|
| `IsIntact` | The signed content is intact and the signature is cryptographically valid |
| `DigestMatches`, `SignatureValid` | The two checks behind `IsIntact` |
| `CoversWholeDocument` | Nothing was appended after this signature |
| `Problem` | Why verification failed, or null |
| `SignerName`, `Certificate` | The signer's common name and certificate, for your trust decision |
| `FieldName`, `SubFilter`, `Reason`, `Location`, `SigningTime` | What the signature dictionary says |

Change one signed byte and the signature no longer holds:

```csharp sample="signatures-sign.cs" region="tamper"
```

## Several signatures

Each signature is appended in its own incremental update that matches the file's cross-reference
style, so earlier signatures stay valid. After a second signature, the first one reports
`IsIntact` true and `CoversWholeDocument` false: it is valid for the revision it signed, and
something was added after it.

```csharp sample="signatures-sign.cs" region="countersign"
```

To prepare an empty signature field for someone else to sign, see
[Forms](/guides/forms/). `PdfSigner.Sign` adds a new field of its own each time.

## Signed documents elsewhere in PdfNetStack

- Editing a signed document with `PdfDocument` and saving it invalidates its signatures.
- The [optimizer](/guides/compress-pdf/) never rewrites a signed file: it returns the input
  unchanged.
- Sign last: create, fill, convert or archive the document first, then sign the saved bytes.

## Limits

- Signing writes the B-B level. RFC 3161 timestamps (PAdES B-T), long term validation data
  (B-LT), and certificate chain and revocation checks are on the [roadmap](/roadmap/).
- `Verify` does not decide whether the certificate is trusted, valid at the signing time or
  revoked. It returns the certificate for that decision.
- `SigningTime` is the time the signer's software recorded, not a trusted timestamp.
- Encrypted documents cannot be signed: `Sign` throws `NotSupportedException`.

## Related

- [PDF/A](/guides/pdf-a/): archive first, then sign
- [Forms](/guides/forms/): signature fields
- [Encryption](/guides/encryption/)
- API: [PdfSigner](/api/pdfnetstack-signing/pdfsigner/), [PdfSignatureOptions](/api/pdfnetstack-signing/pdfsignatureoptions/), [PdfSignatureAppearance](/api/pdfnetstack-signing/pdfsignatureappearance/), [PdfSignatureInfo](/api/pdfnetstack-signing/pdfsignatureinfo/)
