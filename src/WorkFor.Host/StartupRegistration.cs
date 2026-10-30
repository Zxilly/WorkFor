using System.ComponentModel;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace WorkFor.Host;

internal interface IStartupRegistry
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class CurrentUserStartupRegistry : IStartupRegistry
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Value = "WorkFor";
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: false);
        var value = key?.GetValue(Value, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch { null => null, string text => text, _ => throw new IOException("登录启动项的数据类型不正确。") };
    }
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        key.SetValue(Value, command, RegistryValueKind.String);
        key.Flush();
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        key?.DeleteValue(Value, throwOnMissingValue: false);
        key?.Flush();
    }
}

/// <summary>Owns only HKCU Run\WorkFor; the executable and arguments come from trusted startup code.</summary>
internal sealed class StartupRegistration
{
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly IStartupRegistry _registry;
    public string Command => string.Join(" ", new[] { Quote(_executable) }.Concat(_arguments.Select(Quote)));

    public StartupRegistration(string executablePath, IReadOnlyList<string>? arguments = null, IStartupRegistry? registry = null)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("登录启动入口必须是绝对路径。");
        _executable = Path.GetFullPath(executablePath);
        if (!new[] { "WorkFor.exe", "WorkFor.Host.exe", "WorkFor.Launcher.exe", "launcher.exe" }
            .Contains(Path.GetFileName(_executable), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("登录启动入口必须是 WorkFor 自有启动器或宿主。");
        _arguments = arguments?.ToArray() ?? [];
        if (_arguments.Any(x => x is null || x.Contains('\0') || x.Contains('\r') || x.Contains('\n')))
            throw new ArgumentException("登录启动参数不合法。");
        _registry = registry ?? new CurrentUserStartupRegistry();
    }

    public string? Capture() => _registry.Read();
    public bool IsEnabled => string.Equals(_registry.Read(), Command, StringComparison.Ordinal);

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (!File.Exists(_executable)) throw new IOException("登录启动入口不存在。");
                if (new FileInfo(_executable).LinkTarget is not null) throw new IOException("登录启动入口不能是符号链接。");
                if (Command.Length > 260) throw new IOException("登录启动命令超过 Windows Run 项的260字符限制，请选择更短的安装路径。");
                if (!string.Equals(_registry.Read(), Command, StringComparison.Ordinal)) _registry.Write(Command);
                if (!string.Equals(_registry.Read(), Command, StringComparison.Ordinal)) throw new IOException("登录启动项写入后验证失败。");
            }
            else
            {
                if (_registry.Read() is not null) _registry.Delete();
                if (_registry.Read() is not null) throw new IOException("登录启动项移除后验证失败。");
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        { throw new ConfigException("STARTUP_REGISTRATION_FAILED", "无法更新当前用户的登录启动项：" + ex.Message); }
    }

    public void Restore(string? previous)
    {
        if (previous is null) _registry.Delete();
        else _registry.Write(previous);
        if (!string.Equals(_registry.Read(), previous, StringComparison.Ordinal)) throw new IOException("登录启动项回滚验证失败。");
    }

    private static bool IsRegistryFailure(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception;

    // CommandLineToArgvW / Microsoft C runtime escaping, without invoking a shell.
    internal static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') text.Append('\\', backslashes * 2 + 1).Append('"');
            else text.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
