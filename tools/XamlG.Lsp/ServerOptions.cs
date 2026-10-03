namespace XamlG.Lsp;

internal sealed record ServerOptions(string? Project, bool TrustProject, string Framework,
    string? TargetFramework, IReadOnlyList<string> References, IReadOnlyList<string> CodeFiles, bool Help, bool Watch)
{
    public static ServerOptions Parse(string[] args)
    {
        string? project = null, targetFramework = null;
        var framework = "Auto"; var trust = false; var help = false; var watch = true;
        var references = new List<string>(); var codeFiles = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            string Value()
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])) throw new ArgumentException("Missing value for " + option + ".");
                return args[index];
            }
            switch (option)
            {
                case "--project": project = Value(); break;
                case "--trust-project": trust = true; break;
                case "--framework": framework = Value(); break;
                case "--target-framework": targetFramework = Value(); break;
                case "--reference": references.Add(Value()); break;
                case "--code": codeFiles.Add(Value()); break;
                case "--no-watch": watch = false; break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException("Unknown option: " + option);
            }
        }
        if (!help)
        {
            if (project != null && !trust) throw new ArgumentException("--project requires --trust-project because MSBuild and source generators can execute project-defined code.");
            if (project != null && (references.Count != 0 || codeFiles.Count != 0)) throw new ArgumentException("Use either --project or metadata-only --code/--reference inputs, not both.");
            if (project == null && (trust || targetFramework != null)) throw new ArgumentException("--trust-project and --target-framework require --project.");
            if (framework is not ("Auto" or "Portable" or "Avalonia")) throw new ArgumentException("--framework must be Auto, Portable, or Avalonia.");
        }
        return new(project, trust, framework, targetFramework, references, codeFiles, help, watch);
    }
}
