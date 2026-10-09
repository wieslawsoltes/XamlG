using System;

namespace XamlG.Frameworks.Avalonia.Parsing;

/// <summary>
/// Represents cached content modes for graphics acceleration features.
/// </summary>
internal abstract class CacheMode
{

    public static CacheMode Parse(string s)
    {
        if(s == "BitmapCache")
            return new BitmapCache();
        throw new ArgumentException("Unknown CacheMode: " + s);
    }
}
internal sealed class BitmapCache : CacheMode { }
