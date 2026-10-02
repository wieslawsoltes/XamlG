using XamlG.Roslyn;
namespace XamlG.Compiler;
public sealed record BoundPropertyValueSetter(BoundMember Member) : BoundValueSetter(Member.ValueType, Member.ValueType.AcceptsNull());
