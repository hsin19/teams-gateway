using System.Reflection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// 這支程式的版本，取自 build 時 SDK 壓進的 <see cref="AssemblyInformationalVersionAttribute"/>：
/// release 帶 <c>-p:Version</c>，後面接 Source Link 從 git 拿到的 commit。commit 縮成 7 碼，例如 <c>0.3.0+33be044</c>；
/// 本機沒帶版本時是 <c>1.0.0+…</c>。
/// </summary>
public static class AppVersion
{
    private const int ShortShaLength = 7;

    public static string Value { get; } = Shorten(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string Shorten(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
            return "unknown";
        var plus = informationalVersion.IndexOf('+');
        return plus >= 0 && informationalVersion.Length > plus + 1 + ShortShaLength
            ? informationalVersion[..(plus + 1 + ShortShaLength)]
            : informationalVersion;
    }
}
