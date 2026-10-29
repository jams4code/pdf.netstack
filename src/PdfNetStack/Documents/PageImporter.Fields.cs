using PdfNetStack.Objects;

namespace PdfNetStack.Documents;

/// <content>Form fields travelling with the widgets of imported pages.</content>
internal sealed partial class PageImporter
{
    private const int MaxFieldDepth = 64;

    /// <summary>
    /// Adds the fields of the copied widgets to the target's form (ISO 32000-1, 12.7.3). Each
    /// widget keeps its field and the field's ancestors, copied once per call, so a field whose
    /// widgets are on several of the pages stays one field. Widgets on pages left behind are not
    /// copied. A top level field whose name the target form already uses is renamed with the first
    /// free suffix _2, _3 and so on, so imported fields never share a value with fields already
    /// there; scripts that refer to the old name are not rewritten. The form's default appearance,
    /// alignment and resources come along where the target lacks them.
    /// </summary>
    /// <returns>Entries to set on widget copies once the work queue has created them.</returns>
    private List<(int Target, string Key, PdfObject Value)> ImportFields(Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        var fixups = new List<(int Target, string Key, PdfObject Value)>();
        if (_source.Resolve(_source.Catalog["AcroForm"]) is not PdfDictionary sourceForm || _source.Resolve(sourceForm["Fields"]) is not PdfArray roots) return fixups;
        var widgets = new HashSet<int>(batch.Keys.Where(n => _source.Objects.TryGetValue(n, out PdfObject? o) && o is PdfDictionary d && d.GetName("Subtype") == "Widget"));
        if (widgets.Count == 0) return fixups;

        // The field nodes above each copied widget, root first, in tree order.
        var paths = new List<(int Widget, List<int> Fields)>();
        var visited = new HashSet<int>();
        foreach (PdfObject root in roots) Walk(root, [], 0);
        if (paths.Count == 0) return fixups;

        PdfDictionary targetForm = TargetForm();
        PdfArray targetFields = _target.Resolve(targetForm["Fields"]) as PdfArray ?? new PdfArray();
        targetForm.Set("Fields", targetFields);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfObject field in targetFields)
        {
            if (_target.Resolve(field) is PdfDictionary f && _target.Resolve(f["T"]) is PdfString t) names.Add(t.ToText());
        }

        var copies = new Dictionary<int, (int Number, PdfDictionary Dictionary)>();
        foreach ((int widget, List<int> fields) in paths)
        {
            (int Number, PdfDictionary Dictionary)? parent = null;
            foreach (int field in fields)
            {
                if (!copies.TryGetValue(field, out (int Number, PdfDictionary Dictionary) copy))
                {
                    var source = (PdfDictionary)_source.Objects[field];
                    var dictionary = new PdfDictionary(source.Count);
                    for (int i = 0; i < source.Count; i++)
                    {
                        (string key, PdfObject value) = source.EntryAt(i);
                        if (key is not ("Kids" or "Parent")) dictionary.Set(key, CopyValue(value, key, 1, pageMap, batch, work));
                    }

                    dictionary.Set("Kids", new PdfArray());
                    copy = (_target.NextNumber++, dictionary);
                    _target.Objects[copy.Number] = dictionary;
                    copies[field] = copy;
                    if (parent is { } p)
                    {
                        ((PdfArray)p.Dictionary["Kids"]!).Add(new PdfReference(copy.Number, 0));
                        dictionary.Set("Parent", new PdfReference(p.Number, 0));
                    }
                    else
                    {
                        AddRoot(source, copy.Number, key => dictionary.ContainsKey(key), (key, value) => dictionary.Set(key, value));
                    }
                }

                parent = copy;
            }

            int widgetCopy = batch[widget];
            if (parent is { } owner)
            {
                ((PdfArray)owner.Dictionary["Kids"]!).Add(new PdfReference(widgetCopy, 0));
                fixups.Add((widgetCopy, "Parent", new PdfReference(owner.Number, 0)));
            }
            else
            {
                // A top level field that is its own widget: the copy exists once the queue has run.
                var source = (PdfDictionary)_source.Objects[widget];
                AddRoot(source, widgetCopy, source.ContainsKey, (key, value) => fixups.Add((widgetCopy, key, value)));
            }
        }

        MergeFormEntries(sourceForm, targetForm, copies, batch, pageMap, work);
        return fixups;

        void Walk(PdfObject entry, List<int> chain, int depth)
        {
            if (entry is not PdfReference r || depth > MaxFieldDepth || visited.Count >= PdfAcroForm.MaxFieldNodes || !visited.Add(r.Number)) return;
            if (_source.Resolve(r) is not PdfDictionary node) return;
            if (widgets.Contains(r.Number)) paths.Add((r.Number, chain));
            if (_source.Resolve(node["Kids"]) is not PdfArray kids) return;
            List<int> next = [.. chain, r.Number];
            foreach (PdfObject kid in kids) Walk(kid, next, depth + 1);
        }

        // Lists a new top level field in the target form, renamed if its name is taken, with the
        // source form's defaults it would otherwise lose.
        void AddRoot(PdfDictionary source, int number, Func<string, bool> has, Action<string, PdfObject> set)
        {
            targetFields.Add(new PdfReference(number, 0));
            if (_source.Resolve(source["T"]) is PdfString t)
            {
                string name = t.ToText(), unique = name;
                for (int n = 2; names.Contains(unique); n++) unique = name + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                names.Add(unique);
                if (unique != name) set("T", PdfString.FromText(unique));
            }

            string[] inherited = ["DA", "Q"];
            foreach (string key in inherited)
            {
                if (!has(key) && sourceForm[key] is PdfObject value) set(key, CopyValue(value, key, 1, pageMap, batch, work));
            }
        }
    }

    /// <summary>The target document's form, created when it has none.</summary>
    private PdfDictionary TargetForm()
    {
        if (_target.Resolve(_target.Catalog["AcroForm"]) is PdfDictionary existing) return existing;
        var form = new PdfDictionary { ["Fields"] = new PdfArray() };
        _target.Catalog.Set("AcroForm", _target.AddObject(form));
        return form;
    }

    /// <summary>
    /// Default resources the target lacks (a name it already has keeps its own object), the need
    /// for appearances, and the imported fields' place in the calculation order.
    /// </summary>
    private void MergeFormEntries(PdfDictionary sourceForm, PdfDictionary targetForm, Dictionary<int, (int Number, PdfDictionary Dictionary)> copies, Dictionary<int, int> batch, Dictionary<int, int> pageMap, Queue<(PdfObject, int)> work)
    {
        if (!SameDocument && _source.Resolve(sourceForm["DR"]) is PdfDictionary sourceResources)
        {
            PdfDictionary resources = _target.Resolve(targetForm["DR"]) is PdfDictionary r ? PdfDocument.Clone(r) : new PdfDictionary();
            for (int i = 0; i < sourceResources.Count; i++)
            {
                (string category, PdfObject value) = sourceResources.EntryAt(i);
                if (_source.Resolve(value) is not PdfDictionary entries) continue;
                PdfDictionary merged = _target.Resolve(resources[category]) is PdfDictionary c ? PdfDocument.Clone(c) : new PdfDictionary();
                for (int k = 0; k < entries.Count; k++)
                {
                    (string name, PdfObject resource) = entries.EntryAt(k);
                    if (!merged.ContainsKey(name)) merged.Set(name, CopyValue(resource, null, 1, pageMap, batch, work));
                }

                resources.Set(category, merged);
            }

            targetForm.Set("DR", resources);
        }

        if (_source.Resolve(sourceForm["NeedAppearances"]) is PdfBoolean { Value: true }) targetForm.Set("NeedAppearances", PdfBoolean.True);
        if (_source.Resolve(sourceForm["CO"]) is PdfArray order)
        {
            PdfArray targetOrder = _target.Resolve(targetForm["CO"]) as PdfArray ?? new PdfArray();
            int before = targetOrder.Count;
            foreach (PdfObject field in order.ToList())
            {
                if (field is not PdfReference r) continue;
                if (copies.TryGetValue(r.Number, out (int Number, PdfDictionary _) copy)) targetOrder.Add(new PdfReference(copy.Number, 0));
                else if (batch.TryGetValue(r.Number, out int widget)) targetOrder.Add(new PdfReference(widget, 0));
            }

            if (targetOrder.Count > before) targetForm.Set("CO", targetOrder);
        }
    }
}
