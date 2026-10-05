using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace XamlG.Build
{
    /// <summary>Metadata-only input ownership and stable build fingerprints. Never reads application source.</summary>
    public sealed class NormalizeXamlGInputs : Task
    {
        private static readonly HashSet<string> OwnedMetadata = new HashSet<string>(new[]
        { "XamlGDefaultItem", "XamlGLogicalPath", "XamlGCompile", "Link", "SourceItemGroup" }, StringComparer.OrdinalIgnoreCase);
        public ITaskItem[] AdditionalFiles { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] FrameworkSources { get; set; } = Array.Empty<ITaskItem>();
        [Required] public string ProjectDirectory { get; set; }
        public bool UseFrameworkDefaults { get; set; }
        public bool EnableDefaults { get; set; } = true;
        [Output] public ITaskItem[] NormalizedFiles { get; private set; }
        [Output] public string Fingerprint { get; private set; }

        public override bool Execute()
        {
            var windows = Path.DirectorySeparatorChar == '\\';
            var paths = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var groups = new Dictionary<string, List<ITaskItem>>(paths);
            var unrelated = new List<ITaskItem>();
            try
            {
                var root = Path.GetFullPath(ProjectDirectory);
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Func<ITaskItem, string> fullPath = item => Path.GetFullPath(Path.IsPathRooted(item.ItemSpec) ? item.ItemSpec : Path.Combine(root, item.ItemSpec));
                Func<ITaskItem, string> identity = item =>
                {
                    var path = fullPath(item);
                    var relative = path.StartsWith(prefix, comparison);
                    path = (relative ? path.Substring(prefix.Length) : path).Replace('\\', '/');
                    return (relative ? "project:" : "external:") + (windows ? path.ToUpperInvariant() : path);
                };
                var framework = new HashSet<string>(FrameworkSources.Where(IsXaml).Select(fullPath), paths);
                Action<ITaskItem> add = item =>
                {
                    var path = fullPath(item);
                    List<ITaskItem> group;
                    if (!groups.TryGetValue(path, out group)) groups.Add(path, group = new List<ITaskItem>());
                    group.Add(item);
                };
                foreach (var item in AdditionalFiles)
                    if (IsXaml(item)) add(item); else unrelated.Add(new TaskItem(item));
                foreach (var item in FrameworkSources.Where(IsXaml)) add(item);
                foreach (var item in Sources)
                {
                    if (!IsXaml(item)) { Error("XamlGSource must identify a .xaml, .axaml or .paml file: " + item.ItemSpec); continue; }
                    var isDefault = string.Equals(item.GetMetadata("XamlGDefaultItem"), "true", StringComparison.OrdinalIgnoreCase);
                    if (isDefault && (!EnableDefaults || UseFrameworkDefaults && !framework.Contains(fullPath(item)))) continue;
                    add(item);
                }
                var logicalOwners = new Dictionary<string, string>(StringComparer.Ordinal);
                var results = new List<ITaskItem>(unrelated);
                foreach (var pair in groups.OrderBy(p => p.Key, paths))
                {
                    string logical = null;
                    bool? compile = null;
                    var result = new TaskItem(pair.Key);
                    foreach (var item in pair.Value)
                    {
                        var metadata = item.CloneCustomMetadata();
                        foreach (string key in metadata.Keys)
                        {
                            var value = (string)metadata[key];
                            if (OwnedMetadata.Contains(key)) continue;
                            var previous = result.GetMetadata(key);
                            if (previous.Length != 0 && value.Length != 0 && previous != value)
                                Error("Conflicting metadata '" + key + "' for " + pair.Key);
                            else if (value.Length != 0) result.SetMetadata(key, value);
                        }
                        var path = item.GetMetadata("XamlGLogicalPath");
                        if (string.IsNullOrWhiteSpace(path)) path = item.GetMetadata("Link");
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            path = NormalizeLogical(path);
                            if (logical != null && logical != path) Error("Conflicting logical paths for " + pair.Key);
                            logical = path;
                        }
                        var configured = item.GetMetadata("XamlGCompile");
                        if (!string.IsNullOrWhiteSpace(configured))
                        {
                            bool value;
                            if (!bool.TryParse(configured, out value)) Error("XamlGCompile must be true or false for " + pair.Key);
                            else if (compile.HasValue && compile.Value != value) Error("Conflicting XamlGCompile values for " + pair.Key);
                            else compile = value;
                        }
                    }
                    if (logical == null)
                    {
                        if (!pair.Key.StartsWith(prefix, comparison))
                        { Error("External XAML requires Link or XamlGLogicalPath: " + pair.Key); continue; }
                        logical = NormalizeLogical(pair.Key.Substring(prefix.Length));
                    }
                    string owner;
                    if ((compile ?? true) && logicalOwners.TryGetValue(logical, out owner) && !paths.Equals(owner, pair.Key))
                        Error("Duplicate logical XAML path '" + logical + "' for " + owner + " and " + pair.Key);
                    else if (compile ?? true) logicalOwners[logical] = pair.Key;
                    result.SetMetadata("XamlGCompile", (compile ?? true) ? "true" : "false");
                    result.SetMetadata("XamlGLogicalPath", logical);
                    result.SetMetadata("Link", logical);
                    results.Add(result);
                }
                NormalizedFiles = results.ToArray();
                var text = new StringBuilder("XamlG.Inputs/2\n");
                foreach (var item in results.OrderBy(identity, StringComparer.Ordinal))
                {
                    Append(text, identity(item));
                    var metadata = item.CloneCustomMetadata();
                    // IDictionary.Keys works for both Hashtable and generic dictionary-backed
                    // MSBuild implementations; IEnumerable may yield incompatible entry types.
                    foreach (var key in metadata.Keys.Cast<string>().OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    {
                        Append(text, key.ToUpperInvariant());
                        Append(text, (string)metadata[key]);
                    }
                    text.Append('\n');
                }
                using (var hash = SHA256.Create()) Fingerprint = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
                return !Log.HasLoggedErrors;
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is NotSupportedException)
            { Error(error.Message); return false; }
        }
        private static void Append(StringBuilder text, string value) => text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        private static bool IsXaml(ITaskItem item)
        {
            var extension = Path.GetExtension(item.ItemSpec);
            return extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".paml", StringComparison.OrdinalIgnoreCase);
        }
        private static string NormalizeLogical(string value)
        {
            value = value.Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal)) value = value.Substring(2);
            if (value.StartsWith("/", StringComparison.Ordinal) || value.Contains(":") || value.Any(char.IsControl) || value.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new ArgumentException("Logical XAML paths must be normalized project-relative paths: " + value);
            return value;
        }
        private void Error(string message) => Log.LogError(null, "XG2001", null, null, 0, 0, 0, 0, message);
    }
}
