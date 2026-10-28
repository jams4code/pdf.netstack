using PdfNetStack.Security;

namespace PdfNetStack;

/// <summary>Encryption algorithm for new password protection.</summary>
public enum PdfEncryption
{
    /// <summary>AES with a 256-bit key, security handler revision 6. Requires a PDF 1.7 extension level 8 or PDF 2.0 reader.</summary>
    Aes256,

    /// <summary>AES with a 128-bit key, security handler revision 4. Readable by PDF 1.6 and later.</summary>
    Aes128,
}

/// <summary>What a user who only knows the user password may do (ISO 32000-2, table 22).</summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>Nothing beyond viewing.</summary>
    None = 0,

    /// <summary>Print, possibly at reduced quality.</summary>
    Print = 1 << 2,

    /// <summary>Change content other than annotations and forms.</summary>
    Modify = 1 << 3,

    /// <summary>Copy or extract text and graphics.</summary>
    CopyContent = 1 << 4,

    /// <summary>Add or change annotations and fill forms.</summary>
    Annotate = 1 << 5,

    /// <summary>Fill existing form fields.</summary>
    FillForms = 1 << 8,

    /// <summary>Extract text and graphics for accessibility.</summary>
    ExtractForAccessibility = 1 << 9,

    /// <summary>Insert, rotate or delete pages and create bookmarks.</summary>
    Assemble = 1 << 10,

    /// <summary>Print at full quality.</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>Every permission.</summary>
    All = Print | Modify | CopyContent | Annotate | FillForms | ExtractForAccessibility | Assemble | PrintHighQuality,
}

/// <summary>The supplied password matches neither the user nor the owner password.</summary>
public sealed class PdfPasswordException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PdfPasswordException() : base("The password does not open this document.") { }

    /// <summary>Creates the exception with a message.</summary>
    public PdfPasswordException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfPasswordException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Encryption state of a <see cref="PdfDocument"/>. A document opened with its owner password is
/// saved with its original encryption, so both passwords keep working, unless
/// <see cref="Encrypt"/> or <see cref="RemoveEncryption"/> says otherwise.
/// </summary>
public sealed class PdfSecurity
{
    // Bits 7-8 and 13-32 are reserved and must be set for revision 3 and later.
    private const int ReservedBits = unchecked((int)0xFFFFF0C0);

    private readonly PdfDocument _owner;

    internal PdfSecurity(PdfDocument owner, StandardSecurity? source)
    {
        _owner = owner;
        Source = source;
        Target = source;
    }

    internal StandardSecurity? Source { get; }

    internal StandardSecurity? Target { get; private set; }

    /// <summary>True when the document was encrypted when it was opened.</summary>
    public bool WasEncrypted => Source is not null;

    /// <summary>True when the document was opened with its owner password, or was not encrypted.</summary>
    public bool HasOwnerAccess => Source is null || Source.IsOwner;

    /// <summary>Permissions granted to the user password of the opened document. All for unencrypted documents.</summary>
    public PdfPermissions Permissions => Source is null ? PdfPermissions.All : (PdfPermissions)Source.Permissions & PdfPermissions.All;

    /// <summary>True when saving will write an encrypted file.</summary>
    public bool WillEncrypt => Target is not null;

    /// <summary>
    /// Protects the document on save. Readers ask for <paramref name="userPassword"/> to open it (empty means
    /// anyone can open it); <paramref name="ownerPassword"/> unlocks every permission. An empty owner password
    /// is replaced by a random one.
    /// </summary>
    public void Encrypt(string userPassword, string ownerPassword, PdfPermissions permissions = PdfPermissions.All, PdfEncryption algorithm = PdfEncryption.Aes256)
    {
        Compat.ThrowIfNull(userPassword, nameof(userPassword));
        Compat.ThrowIfNull(ownerPassword, nameof(ownerPassword));
        RequireOwner();
        byte[] id0 = _owner.EnsureId();
        Target = StandardSecurity.Create(userPassword, ownerPassword, ReservedBits | (int)permissions, algorithm, id0);
    }

    /// <summary>Saves the document without encryption. Requires owner access.</summary>
    public void RemoveEncryption()
    {
        RequireOwner();
        Target = null;
    }

    internal void RequireOwner()
    {
        if (!HasOwnerAccess)
        {
            throw new InvalidOperationException("The document was opened with the user password. Open it with the owner password to change or save it.");
        }
    }

    internal void UseForTests(StandardSecurity security) => Target = security;
}
