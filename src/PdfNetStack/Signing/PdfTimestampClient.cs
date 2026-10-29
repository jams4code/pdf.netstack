using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// An RFC 3161 time stamp authority. The library builds the request and checks the reply; the
/// application supplies the transport, usually an HTTP POST of the request with content type
/// application/timestamp-query that returns the application/timestamp-reply body. The library
/// itself never opens a connection.
/// </summary>
/// <example>
/// <code>
/// var http = new HttpClient();
/// var tsa = new PdfTimestampClient(request =>
/// {
///     using var content = new ByteArrayContent(request);
///     content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
///     using HttpResponseMessage reply = http.PostAsync("https://tsa.example/", content).GetAwaiter().GetResult();
///     reply.EnsureSuccessStatusCode();
///     return reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
/// });
/// </code>
/// </example>
public sealed class PdfTimestampClient
{
    private readonly Func<byte[], byte[]> _send;

    /// <summary>Creates a client that sends each DER encoded TimeStampReq through <paramref name="send"/> and reads the DER TimeStampResp it returns.</summary>
    public PdfTimestampClient(Func<byte[], byte[]> send)
    {
        Compat.ThrowIfNull(send, nameof(send));
        _send = send;
    }

    /// <summary>The hash sent to the authority. Default SHA-256.</summary>
    public PdfDigestAlgorithm Digest { get; init; } = PdfDigestAlgorithm.Sha256;

    /// <summary>The policy to request, as an object identifier; null for the authority's default.</summary>
    public string? PolicyOid { get; init; }

    /// <summary>
    /// Bytes reserved in the PDF for a token. Default 12288, enough for a token that carries the
    /// authority's certificate chain. Signing fails, after the request, when a token is larger.
    /// </summary>
    public int ReservedSize { get; init; } = 12288;

    /// <summary>
    /// Timestamps <paramref name="data"/>: sends its hash with a random nonce and returns the
    /// DER encoded token after checking the reply. The reply must grant the request, echo the
    /// hash and the nonce, and be signed by a certificate in the token that is marked for time
    /// stamping only (a critical extended key usage).
    /// </summary>
    /// <exception cref="CryptographicException">The authority refused the request or its reply is invalid.</exception>
    public byte[] GetToken(byte[] data)
    {
        Compat.ThrowIfNull(data, nameof(data));
        return GetTokenForDigest(Pkix.Hash(PdfSigner.HashName(Digest), data));
    }

    /// <summary>Timestamps a hash made with <see cref="Digest"/>.</summary>
    internal byte[] GetTokenForDigest(byte[] imprint)
    {
        HashAlgorithmName hash = PdfSigner.HashName(Digest);

        // A positive 64 bit nonce ties the reply to this request.
        byte[] nonce = Compat.RandomBytes(8);
        nonce[0] &= 0x7F;
        nonce[0] |= 0x01;
        byte[] request = Der.Seq(
            Der.Int(1),
            Der.Seq(Der.Seq(Der.Oid(Pkix.HashOid(hash)), Der.NullValue()), Der.Octets(imprint)),
            PolicyOid is string policy ? Der.Oid(policy) : [],
            Der.IntRaw(nonce),
            Der.Bool(true));

        byte[] reply = _send(request) ?? throw new CryptographicException("The time stamp authority sent no reply.");
        try
        {
            DerNode response = DerNode.Parse(reply);
            DerNode status = response.At(0);
            long granted = status.At(0).SmallInteger();
            if (granted is not (0 or 1)) throw new CryptographicException("The time stamp authority refused the request (status " + granted + ").");
            byte[] tokenBytes = response.At(1).Encoded.ToArray();
            TimestampToken token = TimestampToken.Parse(tokenBytes);
            if (token.ImprintHash != hash || !token.Imprint.AsSpan().SequenceEqual(imprint)) throw new CryptographicException("The timestamp does not cover the data sent.");
            if (token.Nonce is null || !token.Nonce.AsSpan().SequenceEqual(nonce)) throw new CryptographicException("The timestamp does not echo the request's nonce.");
            if (PolicyOid is string p && token.Policy != p) throw new CryptographicException("The timestamp has a different policy than requested.");
            if (!token.Verify()) throw new CryptographicException("The timestamp's signature is invalid: " + (token.Cms.Problem ?? "the message digest does not match."));
            if (!TimestampToken.IsTimeStampingCertificate(token.Cms.Signer!, out string? why)) throw new CryptographicException(why!);
            return tokenBytes;
        }
        catch (FormatException e)
        {
            throw new CryptographicException("The time stamp authority's reply cannot be read: " + e.Message, e);
        }
    }
}

/// <summary>An RFC 3161 timestamp token: a SignedData encapsulating a TSTInfo.</summary>
internal sealed class TimestampToken
{
    private TimestampToken(CmsSignedData cms) => Cms = cms;

    public CmsSignedData Cms { get; }

    public HashAlgorithmName ImprintHash { get; private set; }

    public byte[] Imprint { get; private set; } = [];

    public string Policy { get; private set; } = "";

    public byte[] SerialNumber { get; private set; } = [];

    public DateTimeOffset GenTime { get; private set; }

    public byte[]? Nonce { get; private set; }

    /// <exception cref="FormatException">The bytes are not a timestamp token.</exception>
    public static TimestampToken Parse(byte[] der)
    {
        CmsSignedData cms = CmsSignedData.Parse(der);
        if (cms.ContentType != Signing.Cms.TstInfo || cms.Content is null) throw new FormatException("The token does not hold a TSTInfo.");
        var token = new TimestampToken(cms);
        List<DerNode> info = DerNode.Parse(cms.Content).Children();
        if (info.Count < 5) throw new FormatException("TSTInfo is truncated.");
        token.Policy = info[1].OidValue();
        token.ImprintHash = Pkix.HashFromOid(info[2].At(0).At(0).OidValue());
        token.Imprint = info[2].At(1).Content.ToArray();
        token.SerialNumber = info[3].Content.ToArray();
        token.GenTime = info[4].TimeValue();
        foreach (DerNode optional in info.Skip(5))
        {
            if (optional.Tag == Der.Integer) token.Nonce = optional.Content.ToArray();
        }

        return token;
    }

    /// <summary>Whether the token is signed (signed attributes required) and its message digest matches the TSTInfo.</summary>
    public bool Verify()
    {
        if (!Cms.HasSignedAttributes || Cms.MessageDigest is null) return false;
        if (!Pkix.Hash(Cms.Hash, Cms.Content!).AsSpan().SequenceEqual(Cms.MessageDigest)) return false;

        // With signed attributes the detached content is never read.
        return Cms.VerifySignature(() => []);
    }

    /// <summary>RFC 3161, 2.3: the authority's certificate has one extended key usage, time stamping, marked critical.</summary>
    public static bool IsTimeStampingCertificate(X509Certificate2 certificate, out string? problem)
    {
        problem = null;
        IReadOnlyList<string>? usages;
        bool critical;
        try
        {
            usages = Pkix.ExtendedKeyUsages(certificate, out critical);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            problem = "The time stamp authority's extended key usage cannot be read.";
            return false;
        }

        if (usages is null || !critical || usages.Count != 1 || usages[0] != Pkix.TimeStampingUsage)
        {
            problem = "The time stamp authority's certificate is not marked for time stamping only (critical extended key usage).";
            return false;
        }

        return true;
    }
}
