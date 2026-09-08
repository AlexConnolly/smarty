using System.Reflection;
using System.Runtime.Loader;

namespace Smarty.Api.Plugins;

/// <summary>
/// The isolated world one uploaded plugin is loaded into: its own folder is the search path, so it can ship
/// whatever libraries it needs without those versions reaching the rest of Smarty.
/// </summary>
/// <remarks>
/// Two decisions carry the whole design.
/// <para>
/// The contract assembly is NOT isolated. <c>Smarty.Plugins</c> — and anything from the shared framework — is
/// deliberately resolved from the host, because a second copy loaded here would be a second, unrelated
/// <c>IPlugin</c> type: the plugin would implement an interface the host has never heard of, and the only
/// symptom would be "no plugin DLL in the zip" on an assembly that plainly has one.
/// </para>
/// <para>
/// Nothing is loaded from a file handle. Every assembly is read into memory first, so no DLL in the plugin
/// folder is locked and a plugin can be replaced or removed while Smarty is running — on Windows a held handle
/// makes uninstall fail outright, and the only fix would be a restart.
/// </para>
/// </remarks>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _folder;

    public PluginLoadContext(string name, string folder) : base(name, isCollectible: true) => _folder = folder;

    /// <summary>Assemblies the plugin must share with the host rather than load its own copy of. The contract
    /// because type identity depends on it; the framework because two runtimes in one process is not a thing.</summary>
    private static bool SharedWithHost(string? name) =>
        name is not null &&
        (name.Equals("Smarty.Plugins", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("System.", StringComparison.OrdinalIgnoreCase));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (SharedWithHost(assemblyName.Name)) return null; // null → resolved from the default context

        var path = Path.Combine(_folder, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromBytes(path) : null;
    }

    /// <summary>A native library the plugin brought with it — probed beside its DLLs, and in the runtimes layout
    /// a <c>dotnet publish</c> produces.</summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in Probes(unmanagedDllName))
            if (File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);
        return IntPtr.Zero;
    }

    private IEnumerable<string> Probes(string name)
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        foreach (var file in new[] { name, name + ".dll", "lib" + name + ".so", "lib" + name + ".dylib" })
        {
            yield return Path.Combine(_folder, file);
            yield return Path.Combine(_folder, "runtimes", rid, "native", file);
        }
    }

    /// <summary>Load an assembly by value. Reads the debug symbols alongside it when they were packaged, so a
    /// stack trace out of a plugin names lines rather than offsets.</summary>
    public Assembly LoadFromBytes(string path)
    {
        var assembly = File.ReadAllBytes(path);
        var pdb = Path.ChangeExtension(path, ".pdb");
        using var image = new MemoryStream(assembly);
        if (File.Exists(pdb))
        {
            using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
            return LoadFromStream(image, symbols);
        }
        return LoadFromStream(image);
    }
}
