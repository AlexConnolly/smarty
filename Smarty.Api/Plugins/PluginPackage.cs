using System.IO.Compression;
using System.Reflection;
using Smarty.Plugins;

namespace Smarty.Api.Plugins;

/// <summary>A plugin that is loaded and answering, together with the world it was loaded into. Disposing it
/// releases that world; nothing on disk is locked either way.</summary>
public sealed class LoadedPlugin : IDisposable
{
    private readonly PluginLoadContext _context;
    private bool _gone;

    internal LoadedPlugin(IPlugin instance, string assemblyFile, string typeName, PluginLoadContext context)
    {
        Instance = instance;
        AssemblyFile = assemblyFile;
        TypeName = typeName;
        _context = context;
    }

    public IPlugin Instance { get; }

    /// <summary>The DLL it came out of, relative to the plugin's folder.</summary>
    public string AssemblyFile { get; }

    public string TypeName { get; }

    /// <summary>Let the plugin's world go. Idempotent: the install path drops a probe the moment it has read
    /// what it needs, and the enclosing <c>using</c> then reaches the same object.</summary>
    public void Dispose()
    {
        if (_gone) return;
        _gone = true;
        _context.Unload();
    }
}

/// <summary>
/// Getting from an uploaded zip to a live <see cref="IPlugin"/>: unpack it somewhere safe, look through what
/// it contains for something that implements the contract, and say plainly when nothing does.
/// </summary>
public static class PluginPackage
{
    /// <summary>Bigger than any plausible plugin and small enough that a mis-drop can't fill the disk.</summary>
    public const long MaxPackageBytes = 64L * 1024 * 1024;

    /// <summary>The contract this build of Smarty offers plugins.</summary>
    internal static Version ContractVersion =>
        typeof(IPlugin).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// Whether a package was compiled against a newer contract than this Smarty has, and what to do about it.
    /// </summary>
    /// <remarks>
    /// Without this the symptom is a type that silently fails to load, and the honest-looking conclusion is
    /// "none of these DLLs implements IPlugin" — which blames the package for the host being out of date.
    /// An OLDER contract is fine and stays fine: nothing has been removed from it.
    /// </remarks>
    internal static string? BuiltForNewerContract(IEnumerable<AssemblyName> referenced, Version host)
    {
        var contract = referenced.FirstOrDefault(a =>
            string.Equals(a.Name, "Smarty.Plugins", StringComparison.OrdinalIgnoreCase));

        // Compared on major.minor only, which is exactly what the contract's versioning policy means: a build
        // or revision number carries no API change, and an unspecified component reads as -1, so comparing
        // whole Versions would call 1.1.0.0 newer than 1.1.
        if (contract?.Version is not { } wanted) return null;
        if ((wanted.Major, wanted.Minor).CompareTo((host.Major, host.Minor)) <= 0) return null;

        return $"it was built against Smarty.Plugins {wanted.ToString(2)}, and this Smarty has " +
               $"{host.ToString(2)}. Restart Smarty so it picks up the newer contract, then upload again.";
    }

    /// <summary>
    /// Why some of an assembly's types wouldn't load, said usefully.
    /// </summary>
    /// <remarks>
    /// One case is worth naming outright: a missing type from the CONTRACT assembly means the package was
    /// built against a newer <c>Smarty.Plugins</c> than this Smarty is running. The host is the stale half
    /// there, and the fix is to restart Smarty, not to repackage the plugin — which is the opposite of what
    /// every other loading failure means, so it cannot be left to be inferred.
    /// </remarks>
    private static IEnumerable<string> Reasons(string file, ReflectionTypeLoadException ex)
    {
        var reasons = new List<string>();
        foreach (var loader in (ex.LoaderExceptions ?? Array.Empty<Exception?>()).Where(e => e is not null))
        {
            if (loader is TypeLoadException { TypeName: { } missing } &&
                missing.StartsWith("Smarty.Plugins.", StringComparison.Ordinal))
            {
                reasons.Add(
                    $"{file} needs {missing}, which this build of Smarty doesn't have. The plugin was compiled " +
                    "against a newer Smarty.Plugins — restart Smarty so it picks the new one up, then upload again.");
                continue;
            }
            reasons.Add($"{file}: {loader!.Message}");
        }

        if (reasons.Count == 0) reasons.Add($"{file}: some of its types wouldn't load, with no reason given.");
        return reasons.Distinct();
    }

    /// <summary>
    /// What stops a plugin being installable, or null when nothing does. Both of these are refusals rather
    /// than warnings: without a name it can't be identified, stored or configured, and without a description
    /// it can't be CHOSEN — the description is what the orchestrator reads to decide a job belongs to this
    /// plugin and what the worker is told the plugin is for, so one without it is dead weight nothing routes to.
    /// </summary>
    internal static string? Unusable(IPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Name))
            return "its Name is empty, so it can't be identified or configured.";
        if (string.IsNullOrWhiteSpace(plugin.Description))
            return "its Description is empty. Say in one line what the plugin is for — it becomes the " +
                   "description of the persona Smarty delegates to, and nothing will route work to a plugin " +
                   "that doesn't say what it does.";
        return null;
    }

    /// <summary>
    /// Unpack <paramref name="zip"/> into <paramref name="destination"/>, refusing any entry that would land
    /// outside it. A zip is a file someone else wrote: an entry named <c>../../appsettings.json</c> is a
    /// perfectly legal zip and an overwritten host config, so the resolved path is checked rather than trusted.
    /// </summary>
    public static void Extract(Stream zip, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // a directory entry
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The zip tries to write outside its own folder ({entry.FullName}).");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Load the plugin out of an unpacked folder. Every DLL in it is a candidate — the entry point isn't
    /// declared anywhere, so a package is simply "the one that implements <see cref="IPlugin"/> plus whatever
    /// it needs". Returns null with a reason when none does, which is the message the upload form shows.
    /// </summary>
    public static LoadedPlugin? Load(string folder, string contextName, out string? problem)
    {
        problem = null;
        var dlls = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();

        if (dlls.Count == 0)
        {
            problem = "The zip has no .dll in it — a plugin package is the plugin's built DLL plus any libraries it needs.";
            return null;
        }

        var context = new PluginLoadContext(contextName, folder);
        var rejected = new List<string>();

        foreach (var dll in dlls)
        {
            // The contract itself is frequently packaged by accident (a plain `dotnet build` copies it into the
            // output). Loading it here would find IPlugin and no implementation, which is true but unhelpful.
            if (Path.GetFileNameWithoutExtension(dll).Equals("Smarty.Plugins", StringComparison.OrdinalIgnoreCase))
                continue;

            Assembly assembly;
            try { assembly = context.LoadFromBytes(dll); }
            catch (Exception ex) { rejected.Add($"{Path.GetFileName(dll)}: {ex.Message}"); continue; }

            // Asked before anything is scanned, because the answer changes what the person should DO: every
            // other loading failure means fix the package, this one means the running Smarty is behind.
            if (BuiltForNewerContract(assembly.GetReferencedAssemblies(), ContractVersion) is { } behind)
            {
                rejected.Add($"{Path.GetFileName(dll)}: {behind}");
                continue;
            }

            Type[] types;
            // A library whose own dependencies are missing still yields the types that did resolve, which is
            // enough to find the plugin — so a partial load is worth continuing with rather than discarding.
            //
            // But the types that DIDN'T load are the whole story when the plugin is one of them, and throwing
            // their reasons away is how "the plugin was built against a newer Smarty.Plugins than this Smarty
            // has" came out as "none of these DLLs implements IPlugin" — which is not true, is not actionable,
            // and points at the package when the stale thing is the host.
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
                foreach (var reason in Reasons(Path.GetFileName(dll), ex)) rejected.Add(reason);
            }
            catch (Exception ex) { rejected.Add($"{Path.GetFileName(dll)}: {ex.Message}"); continue; }

            var type = types.FirstOrDefault(t =>
                t is { IsAbstract: false, IsInterface: false } &&
                typeof(IPlugin).IsAssignableFrom(t) &&
                t.GetConstructor(Type.EmptyTypes) is not null);

            if (type is null) continue;

            try
            {
                var instance = (IPlugin)Activator.CreateInstance(type)!;
                if (Unusable(instance) is { } why) { rejected.Add($"{type.FullName}: {why}"); continue; }
                return new LoadedPlugin(instance, Path.GetRelativePath(folder, dll), type.FullName ?? type.Name, context);
            }
            catch (Exception ex)
            {
                rejected.Add($"{type.FullName}: {ex.Message}");
            }
        }

        context.Unload();
        problem = rejected.Count > 0
            ? "Couldn't load the plugin — " + string.Join("; ", rejected.Distinct())
            : $"None of the {dlls.Count} DLL(s) implements IPlugin with a parameterless constructor. " +
              "Reference Smarty.Plugins, implement IPlugin, and put the built DLL in the zip.";
        return null;
    }
}
