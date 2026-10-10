namespace XamlG.Frameworks.Avalonia.Parsing
{
    /// <summary>
    /// Determines how <see cref="FlexBasis"/> affects the size of the flex item
    /// </summary>
    internal enum FlexBasisKind
    {
        /// <summary>
        /// Uses the measured Width and Height of the flex panel to determine the initial size of the item.
        /// </summary>
        Auto,

        /// <summary>
        /// The initial size of the item is set to the <see cref="FlexBasis"/> value.
        /// </summary>
        Absolute,

        /// <summary>
        /// Indicates the <see cref="FlexBasis"/> value is a percentage, and the size of the flex item is scaled by it.
        /// </summary>
        Relative,
    }
}
