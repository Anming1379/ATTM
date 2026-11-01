using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ATTM.Plugins;

/// <summary>
/// 沙箱插件的静态体检：在**加载之前**读一遍程序集元数据，
/// 找出被禁止的 API（文件、目录、进程、网络、注册表、P/Invoke）。
///
/// 说明：.NET 进程内没有硬安全边界，这里做的是「元数据级准入」——
/// 引用了禁止 API 的沙箱插件直接拒绝装载，并提示作者申请无沙箱权限。
/// </summary>
public static class PluginSecurityScanner
{
    /// <summary>沙箱下禁止引用的类型全名。</summary>
    private static readonly ImmutableHashSet<string> ForbiddenTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.FileStream",
        "System.IO.StreamWriter",
        "System.IO.StreamReader",
        "System.IO.FileSystemWatcher",
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "System.Net.Http.HttpClient",
        "System.Net.WebClient",
        "System.Net.Sockets.Socket",
        "System.Net.Sockets.TcpClient",
        "System.Net.Sockets.UdpClient",
        "System.Net.Dns",
        "Microsoft.Win32.Registry",
        "Microsoft.Win32.RegistryKey",
        "System.Runtime.InteropServices.Marshal",
        "System.Runtime.InteropServices.NativeLibrary",
        "System.Reflection.Emit.AssemblyBuilder");

    /// <summary>返回被命中的禁止项（空表示干净）。路径非法时抛异常。</summary>
    public static IReadOnlyList<string> Scan(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var hits = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            var typeName = metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);

            if (ForbiddenTypes.Contains(typeName)) hits.Add(typeName);
        }

        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);

            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;

            var methodName = metadata.GetString(method.Name);
            hits.Add("P/Invoke:" + methodName);
        }

        return hits.ToList();
    }

    /// <summary>把命中项整理成给用户看的一句话。</summary>
    public static string Describe(IReadOnlyList<string> hits)
        => hits.Count == 0 ? string.Empty : string.Join("、", hits);
}
