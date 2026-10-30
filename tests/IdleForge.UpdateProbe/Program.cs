using System.Reflection;

var marker = Path.Combine(AppContext.BaseDirectory, "probe-started.txt");
await File.WriteAllTextAsync(marker, Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown");
var tokenIndex = Array.IndexOf(args, "--update-health-token");
var fileIndex = Array.IndexOf(args, "--update-health-file");
if (tokenIndex >= 0 && fileIndex >= 0 && tokenIndex + 1 < args.Length && fileIndex + 1 < args.Length)
    await File.WriteAllTextAsync(args[fileIndex + 1], args[tokenIndex + 1]);
