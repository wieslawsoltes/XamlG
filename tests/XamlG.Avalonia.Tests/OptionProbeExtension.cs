using Avalonia.Metadata;
namespace XamlG.Avalonia.Tests;

public sealed class OptionProbeExtension
{
    public static int Constructions;
    public static int Predicates;
    public OptionProbeExtension() => Constructions++;
    [MarkupExtensionOption("selected")] public object? Selected { get; set; }
    [MarkupExtensionOption("other")] public object? Other { get; set; }
    [MarkupExtensionDefaultOption] public object? Default { get; set; }
    public bool ShouldProvideOption(string option) { Predicates++; return option == "selected"; }
    public object ProvideValue() => throw new InvalidOperationException("The compiler placeholder must not execute.");
}
