namespace XamlG.Cli;

internal sealed record CliOptions(string Command, string? Project, string? File, string Output, string Framework,
    string? TargetFramework, bool Json, string? AssemblyOutput, IReadOnlyList<string> References, IReadOnlyList<string> CodeFiles)
{
    public static CliOptions Parse(string[] args)
    {
        var command = args.Length == 0 ? "help" : args[0];
        if (command is "-h" or "--help") command = "help";
        if (command is not ("help" or "compile" or "check" or "inspect")) throw new ArgumentException("Unknown command. Use 'xamlg --help'.");
        string? project = null, file = null, targetFramework = null, assembly = null;
        var output = Path.Combine("obj", "XamlG"); var framework = "Auto"; var json = false;
        var references = new List<string>(); var code = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            string Value()
            {
                if (++i >= args.Length) throw new ArgumentException("A command-line option is missing its value.");
                return args[i];
            }
            switch (args[i])
            {
                case "--project": project = Value(); break;
                case "--file": file = Value(); break;
                case "--output": output = Value(); break;
                case "--framework": framework = Value(); break;
                case "--target-framework": targetFramework = Value(); break;
                case "--reference": references.Add(Value()); break;
                case "--code": code.Add(Value()); break;
                case "--emit-assembly": assembly = Value(); break;
                case "--json": json = true; break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (command != "help" && project == null && file == null) throw new ArgumentException("Specify --project or --file.");
        return new(command, project, file, output, framework, targetFramework, json, assembly, references, code);
    }
}
