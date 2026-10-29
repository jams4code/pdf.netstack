using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Objects;

namespace PdfNetStack.Signing;

/// <summary>The document security store (ISO 32000-2, 12.8.4.3; ETSI EN 319 142-1, 5.4): validation data kept in the document.</summary>
internal sealed class SecurityStore
{
    private const int MaxEntries = 10_000;

    public List<X509Certificate2> Certificates { get; } = [];

    public List<Ocsp> Ocsps { get; } = [];

    public List<Crl> Crls { get; } = [];

    public static SecurityStore Read(PdfDocument doc)
    {
        var store = new SecurityStore();
        if (doc.Resolve(doc.Catalog["DSS"]) is not PdfDictionary dss) return store;
        foreach (byte[] data in Streams(doc, dss, "Certs"))
        {
            try
            {
                store.Certificates.Add(Pkix.Load(data));
            }
            catch (FormatException)
            {
                // A damaged entry is skipped; the others still count.
            }
        }

        foreach (byte[] data in Streams(doc, dss, "OCSPs"))
        {
            if (Ocsp.TryParse(data) is Ocsp o) store.Ocsps.Add(o);
        }

        foreach (byte[] data in Streams(doc, dss, "CRLs"))
        {
            if (Crl.TryParse(data) is Crl c) store.Crls.Add(c);
        }

        return store;
    }

    /// <summary>The decoded streams of one DSS array; entries that are not readable streams are skipped.</summary>
    public static IEnumerable<byte[]> Streams(PdfDocument doc, PdfDictionary dss, string key)
    {
        if (doc.Resolve(dss[key]) is not PdfArray array) yield break;
        foreach (PdfObject item in array.Take(MaxEntries))
        {
            if (doc.Resolve(item) is not PdfStream stream) continue;
            byte[]? data;
            try
            {
                data = doc.GetStreamData(stream);
            }
            catch (PdfFormatException)
            {
                data = null;
            }

            if (data is not null) yield return data;
        }
    }
}

/// <summary>
/// Builds the next document security store: the existing entries kept, new certificates, OCSP
/// responses and CRLs added once each, and a VRI entry per signature.
/// </summary>
internal sealed class DssWriter
{
    private readonly PdfDocument _doc;
    private readonly PdfDictionary _dss;
    private readonly PdfDictionary _vri;
    private readonly Dictionary<string, PdfArray> _arrays = new();
    private readonly Dictionary<string, PdfObject> _known = new();

    public DssWriter(PdfDocument doc)
    {
        _doc = doc;
        PdfDictionary? existing = doc.Resolve(doc.Catalog["DSS"]) as PdfDictionary;
        _dss = existing is null ? new PdfDictionary() : PdfDocument.Clone(existing);
        _dss.Set("Type", new PdfName("DSS"));
        _vri = doc.Resolve(_dss["VRI"]) is PdfDictionary vri ? PdfDocument.Clone(vri) : new PdfDictionary();
        foreach (string key in new[] { "Certs", "OCSPs", "CRLs" })
        {
            var array = new PdfArray();
            if (doc.Resolve(_dss[key]) is PdfArray old)
            {
                foreach (PdfObject item in old)
                {
                    array.Add(item);
                    if (doc.Resolve(item) is not PdfStream stream) continue;
                    try
                    {
                        _known[Id(key, doc.GetStreamData(stream))] = item;
                    }
                    catch (PdfFormatException)
                    {
                        // An unreadable entry stays, but cannot be matched.
                    }
                }
            }

            _arrays[key] = array;
        }
    }

    /// <summary>The entry for these bytes in one of the arrays, added when new.</summary>
    public PdfObject Add(string key, byte[] data)
    {
        string id = Id(key, data);
        if (!_known.TryGetValue(id, out PdfObject? reference))
        {
            reference = _doc.AddObject(PdfStream.FromData(data));
            _known[id] = reference;
            _arrays[key].Add(reference);
        }

        return reference;
    }

    /// <summary>The validation data of one signature, keyed by its VRI name.</summary>
    public void Vri(string key, List<PdfObject> certs, List<PdfObject> ocsps, List<PdfObject> crls)
    {
        var entry = new PdfDictionary();
        Put(entry, "Cert", certs);
        Put(entry, "OCSP", ocsps);
        Put(entry, "CRL", crls);
        _vri.Set(key, entry);

        static void Put(PdfDictionary d, string name, List<PdfObject> items)
        {
            if (items.Count == 0) return;
            var array = new PdfArray();
            foreach (PdfObject item in items.Distinct()) array.Add(item);
            d.Set(name, array);
        }
    }

    /// <summary>Points the catalog at the new store.</summary>
    public void WriteTo(IncrementalUpdate update)
    {
        foreach ((string key, PdfArray array) in _arrays)
        {
            if (array.Count > 0) _dss.Set(key, array);
            else _dss.Remove(key);
        }

        if (_vri.Count > 0) _dss.Set("VRI", _vri);
        _doc.Catalog.Set("DSS", _doc.AddObject(_dss));
        update.Changed(_doc.CatalogNumber);
    }

    private static string Id(string key, byte[] data) => key + ":" + Compat.ToHex(Pkix.Hash(HashAlgorithmName.SHA256, data));
}
