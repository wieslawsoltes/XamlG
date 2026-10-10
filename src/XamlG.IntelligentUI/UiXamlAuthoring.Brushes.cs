using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

internal sealed partial class UiXamlAuthoring
{
    private string Brush(XElement node, Scope scope)
    {
        var kind = node.Name.LocalName switch { "SolidColorBrush" => "solid", "LinearGradientBrush" => "linear", _ => "radial" };
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind };
        var allowed = kind switch
        {
            "solid" => new[] { "Color", "Opacity", "Transform", "TransformOrigin" },
            "linear" => ["StartPoint", "EndPoint", "SpreadMethod", "Opacity", "Transform", "TransformOrigin"],
            _ => ["Center", "GradientOrigin", "RadiusX", "RadiusY", "SpreadMethod", "Opacity", "Transform", "TransformOrigin"]
        };
        CheckAttributes(node, allowed); CheckText(node);
        void Assign(string name, string value)
        {
            value = Resolve(value, scope); var key = char.ToLowerInvariant(name[0]) + name[1..];
            object literal = value;
            if (name == "Opacity")
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw Error("invalid_brush", "Invalid brush opacity.");
                literal = number;
            }
            if (!values.TryAdd(key, literal)) throw Error("invalid_brush", "Duplicate brush property.");
        }
        foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration)) Assign(attribute.Name.LocalName, attribute.Value);
        var stops = new List<object>(); var hadCollection = false; var hadImplicit = false;
        void Stop(XElement stop)
        {
            if (stop.Name != Ns + "GradientStop" || stop.HasElements) throw Error("invalid_brush", "Expected an inert GradientStop.");
            CheckAttributes(stop, "Color", "Offset"); CheckText(stop);
            if (stops.Count >= UiBrushValues.MaximumStops || !double.TryParse(Resolve((string?)stop.Attribute("Offset") ?? "0", scope), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) || !double.IsFinite(offset))
                throw Error("invalid_brush", "Invalid or excessive gradient stops.");
            stops.Add(new { color = Resolve((string?)stop.Attribute("Color") ?? "Transparent", scope), offset });
        }
        foreach (var child in node.Elements())
        {
            if (child.Name == Ns + "GradientStop")
            {
                if (kind == "solid" || hadCollection) throw Error("invalid_brush", "Conflicting gradient stop collection.");
                hadImplicit = true; Stop(child); continue;
            }
            var name = PropertyName(node, child); CheckAttributes(child);
            if (name == "GradientStops")
            {
                if (kind == "solid" || hadCollection || hadImplicit) throw Error("invalid_brush", "Conflicting gradient stop collection.");
                hadCollection = true; CheckText(child); foreach (var stop in child.Elements()) Stop(stop);
            }
            else
            {
                if (!allowed.Contains(name, StringComparer.Ordinal)) throw Error("invalid_brush", "Unknown brush property element.");
                Assign(name, ScalarProperty(child, scope));
            }
        }
        if (kind != "solid") values.Add("stops", stops);
        var result = JsonSerializer.SerializeToElement(values); UiBrushValues.Read(result);
        return "{}" + result.GetRawText();
    }
}
