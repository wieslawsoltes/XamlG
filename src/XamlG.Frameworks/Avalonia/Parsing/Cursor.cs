using System;

#nullable enable

namespace XamlG.Frameworks.Avalonia.Parsing
{
    internal enum StandardCursorType
    {
        Arrow,
        Ibeam,
        Wait,
        Cross,
        UpArrow,
        SizeWestEast,
        SizeNorthSouth,
        SizeAll,
        No,
        Hand,
        AppStarting,
        Help,
        TopSide,
        BottomSide,
        LeftSide,
        RightSide,
        TopLeftCorner,
        TopRightCorner,
        BottomLeftCorner,
        BottomRightCorner,
        DragMove,
        DragCopy,
        DragLink,
        None,
        
        // Not available in GTK directly, see https://www.pixelbeat.org/programming/x_cursors/
        // We might enable them later, preferably, by loading pixmax directly from theme with fallback image
        // SizeNorthWestSouthEast,
        // SizeNorthEastSouthWest,
    }

    internal sealed class Cursor(StandardCursorType cursorType)
    {
        public StandardCursorType Type { get; } = cursorType;

        public static Cursor Parse(string s)
        {
            return Enum.TryParse<StandardCursorType>(s, true, out var t) ?
                new Cursor(t) :
                throw new ArgumentException($"Unrecognized cursor type '{s}'.");
        }

    }
}
