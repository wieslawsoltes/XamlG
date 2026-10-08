using System.Globalization;
using System.Threading;

namespace XamlG.Runtime;

/// <summary>Immutable, lazily decoded source metadata shared by instances of a compiled document.</summary>
/// <remarks>The compiler supplies length-prefixed metadata records, never executable code or XAML.</remarks>
public sealed class XamlSourceInfoTable
{
    private readonly string _path;
    private readonly long _version;
    private readonly string[] _records;
    private readonly XamlSourceInfo?[] _values;

    public XamlSourceInfoTable(string path, long version, string[] records)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _version = version;
        _records = (string[])(records ?? throw new ArgumentNullException(nameof(records))).Clone();
        _values = new XamlSourceInfo?[records.Length];
    }

    public XamlSourceInfo this[int index]
    {
        get
        {
            var value = Volatile.Read(ref _values[index]);
            if (value != null) return value;
            value = Decode(_records[index]);
            return Interlocked.CompareExchange(ref _values[index], value, null) ?? value;
        }
    }

    // start:length:identity-length:identity fingerprint-length:fingerprint count:key-length:key value-length:value ...
    // Lengths count UTF-16 code units. Only identity may have length -1, representing null.
    private XamlSourceInfo Decode(string record)
    {
        if (record == null) throw new FormatException("A source metadata record cannot be null.");
        var offset = 0;
        int Number()
        {
            var end = record.IndexOf(':', offset);
            if (end < 0 || !int.TryParse(record.Substring(offset, end - offset), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var value)) throw new FormatException("Invalid source metadata number.");
            offset = end + 1;
            return value;
        }
        string? Text(bool nullable = false)
        {
            var length = Number();
            if (length == -1 && nullable) return null;
            if (length < 0 || length > record.Length - offset) throw new FormatException("Invalid source metadata text length.");
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
        if (count < 0 || count > (record.Length - offset) / 4) throw new FormatException("Invalid source metadata declaration count.");
        var declarations = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = Text()!;
            var value = Text()!;
            if (declarations.ContainsKey(key)) throw new FormatException("Duplicate source metadata declaration.");
            declarations.Add(key, value);
        }
        if (offset != record.Length) throw new FormatException("Unexpected trailing source metadata.");
        return new(_path, start, length, identity, fingerprint, declarations, _version);
    }
}
