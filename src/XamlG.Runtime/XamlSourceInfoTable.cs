using System.Threading;

namespace XamlG.Runtime;

/// <summary>Immutable, lazily decoded source metadata shared by instances of a compiled document.</summary>
/// <remarks>The compiler supplies length-prefixed metadata records, never executable code or XAML.</remarks>
public sealed class XamlSourceInfoTable
{
    private readonly string _path;
    private readonly long _version;
    private readonly string[]? _records;
    private readonly string? _encodedRecords;
    private readonly (int Start, int Length)[]? _recordSpans;
    private readonly XamlSourceInfo?[] _values;

    public XamlSourceInfoTable(string path, long version, string[] records)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _version = version;
        _records = (string[])(records ?? throw new ArgumentNullException(nameof(records))).Clone();
        _values = new XamlSourceInfo?[records.Length];
    }

    private XamlSourceInfoTable(string path, long version, string records, (int Start, int Length)[] spans)
    {
        _path = path;
        _version = version;
        _encodedRecords = records;
        _recordSpans = spans;
        _values = new XamlSourceInfo?[spans.Length];
    }

    /// <summary>Creates a table from consecutive UTF-16 length-prefixed records.
    /// Individual metadata records remain lazily decoded and independently bounded.</summary>
    public static XamlSourceInfoTable FromEncoded(string path, long version, string records)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (records == null) throw new ArgumentNullException(nameof(records));
        var spans = new List<(int Start, int Length)>();
        var offset = 0;
        while (offset < records.Length)
        {
            var end = records.IndexOf(':', offset);
            if (end < 0 || !XamlMetadataNumbers.TryRead(records, offset, end, allowSign: false, out var length) ||
                length > records.Length - end - 1)
                throw new FormatException("Invalid source metadata record length.");
            offset = end + 1;
            spans.Add((offset, length));
            offset += length;
        }
        return new(path, version, records, spans.ToArray());
    }

    public XamlSourceInfo this[int index]
    {
        get
        {
            var value = Volatile.Read(ref _values[index]);
            if (value != null) return value;
            if (_records != null)
            {
                var record = _records[index];
                if (record == null) throw new FormatException("A source metadata record cannot be null.");
                value = Decode(record, 0, record.Length);
            }
            else
            {
                var span = _recordSpans![index];
                value = Decode(_encodedRecords!, span.Start, span.Length);
            }
            return Interlocked.CompareExchange(ref _values[index], value, null) ?? value;
        }
    }

    // start:length:identity-length:identity fingerprint-length:fingerprint count:key-length:key value-length:value ...
    // Lengths count UTF-16 code units. Only identity may have length -1, representing null.
    private XamlSourceInfo Decode(string record, int recordStart, int recordLength)
    {
        var offset = recordStart;
        var limit = recordStart + recordLength;
        int Number()
        {
            var end = record.IndexOf(':', offset, limit - offset);
            if (end < 0 || !XamlMetadataNumbers.TryRead(record, offset, end, allowSign: true, out var value))
                throw new FormatException("Invalid source metadata number.");
            offset = end + 1;
            return value;
        }
        string? Text(bool nullable = false)
        {
            var length = Number();
            if (length == -1 && nullable) return null;
            if (length < 0 || length > limit - offset) throw new FormatException("Invalid source metadata text length.");
            var value = record.Substring(offset, length);
            offset += length;
            return value;
        }
        var start = Number();
        var length = Number();
        if (start < 0 || length < 0 || start > int.MaxValue - length) throw new FormatException("Invalid source metadata span.");
        var identity = Text(nullable: true);
        var fingerprint = Text()!;
        var count = Number();
        // Each pair needs at least two empty length-prefixed strings ("0:0:").
        if (count < 0 || count > (limit - offset) / 4) throw new FormatException("Invalid source metadata declaration count.");
        var declarations = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = Text()!;
            var value = Text()!;
            if (declarations.ContainsKey(key)) throw new FormatException("Duplicate source metadata declaration.");
            declarations.Add(key, value);
        }
        if (offset != limit) throw new FormatException("Unexpected trailing source metadata.");
        return XamlSourceInfo.FromDecoded(_path, start, length, identity, fingerprint, declarations, _version);
    }
}
