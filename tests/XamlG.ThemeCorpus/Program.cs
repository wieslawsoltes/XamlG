using XamlG.ThemeCorpus;

try
{
    if (args.Length != 4)
        throw new ArgumentException("Usage: XamlG.ThemeCorpus <upstream-checkout> <Simple|Fluent> <expected-xaml-count> <evidence-directory>");
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
    ThemeCorpusRunner.Run(Path.GetFullPath(args[0]), args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
        Path.GetFullPath(args[3]), deadline.Token);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
