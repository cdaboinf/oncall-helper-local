using System.Text.Json;
using System.Text.Json.Serialization;
using OnCallHelperLocal.Data;
using OnCallHelperLocal.Services;

namespace OnCallHelperLocal;

/// <summary>
/// `backup [folder] [--keep N]`: writes every incident to one dated JSON file (default folder /backups). The file has the
/// same shape as the hosted export, so `import --file` restores it. Vectors are left out (they are rebuilt on import), so
/// the file is small. Prints counts only. With --keep N, older backup files in the folder are deleted, keeping the newest N.
/// </summary>
public static class Backup
{
    private const string Prefix = "incidents-backup-";

    public static async Task<int> RunAsync(LocalSettings settings, string[] args)
    {
        var positional = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
        var folder = positional.FirstOrDefault() ?? Environment.GetEnvironmentVariable("BACKUP_DIR") ?? "/backups";
        var keepArg = Array.IndexOf(args, "--keep");
        int? keep = null;
        if (keepArg >= 0)
        {
            if (keepArg + 1 >= args.Length || !int.TryParse(args[keepArg + 1], out var k) || k < 1)
            { Console.Error.WriteLine("--keep needs a whole number of 1 or more."); return 1; }
            keep = k;
            positional.Remove(args[keepArg + 1]);
            folder = positional.FirstOrDefault() ?? Environment.GetEnvironmentVariable("BACKUP_DIR") ?? "/backups";
        }

        var store = new IncidentStore(settings);
        var incidents = (await store.GetAllAsync(CancellationToken.None)).OrderByDescending(i => i.CreatedAt).ToList();
        if (incidents.Count == 0)
        {
            Console.WriteLine($"Database '{settings.MongoDatabase}' has no incidents; no backup file written.");
            return 0;
        }

        var views = incidents.Select(i => new IncidentView(
            i.Id, i.Title, i.Metadata.ServiceName, i.Metadata.Environment, i.Metadata.Severity, i.Metadata.Description,
            DateTime.SpecifyKind(i.CreatedAt, DateTimeKind.Utc),
            i.Resolution is null ? null : new ResolutionView(i.Resolution.RootCause, i.Resolution.Summary, i.Resolution.StepsTaken ?? [], i.Resolution.ResolvedBy),
            null)).ToList();

        try
        {
            Directory.CreateDirectory(folder);
            var name = $"{Prefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(folder, name);
            var temp = path + ".tmp";
            // Write to a temporary name first, so an interrupted backup never leaves a half-written file that looks valid.
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(views, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            }));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* not supported on this file system */ }
            }
            File.Move(temp, path, overwrite: true);
            Console.WriteLine($"backed up {incidents.Count} incidents from '{settings.MongoDatabase}' to {path} ({new FileInfo(path).Length / 1024} KB)");

            if (keep is { } n)
            {
                var old = Directory.GetFiles(folder, Prefix + "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(n).ToList();
                foreach (var f in old) File.Delete(f);
                if (old.Count > 0) Console.WriteLine($"deleted {old.Count} older backup file(s); kept the newest {n}");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write the backup to '{folder}': {ex.Message}");
            return 1;
        }
    }
}
