using System.IO;
using System.Reflection;

namespace NetScopePLC;

internal static class NativeToolHost
{
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path is not null) return _path;

            var sideBySide = System.IO.Path.Combine(AppContext.BaseDirectory, "NetScopeNative.exe");
            if (File.Exists(sideBySide))
            {
                _path = sideBySide;
                return _path;
            }

            _path = ExtractEmbedded();
            return _path;
        }
    }

    private static string ExtractEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("NetScopeNative.exe")
            ?? throw new FileNotFoundException("内嵌扫描核心 NetScopeNative.exe 未找到，请重新构建。");
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetScopePLC");
        Directory.CreateDirectory(directory);
        var target = System.IO.Path.Combine(directory, "NetScopeNative.exe");
        using var file = File.Open(target, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.CopyTo(file);
        return target;
    }
}
