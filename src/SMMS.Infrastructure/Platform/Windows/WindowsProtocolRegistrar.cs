using System.Runtime.Versioning;
using Microsoft.Win32;
using SMMS.Core.Ports;

namespace SMMS.Infrastructure.Platform.Windows;

/// <summary>
/// 通过 HKCU\Software\Classes 注册 <c>nxm://</c> 协议处理器。
/// 仅写当前用户分支，不触发 UAC。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProtocolRegistrar : IProtocolRegistrar
{
    private const string ClassesRoot = @"Software\Classes";

    public bool IsRegistered(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{scheme}\shell\open\command");
            return key?.GetValue(null) is string command && command.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Register(string scheme, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            using var schemeKey = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{scheme}");
            if (schemeKey is null)
            {
                return false;
            }

            schemeKey.SetValue(null, $"URL:{scheme} Protocol");
            schemeKey.SetValue("URL Protocol", string.Empty);

            using var commandKey = schemeKey.CreateSubKey(@"shell\open\command");
            if (commandKey is null)
            {
                return false;
            }

            commandKey.SetValue(null, $"\"{executablePath}\" \"%1\"");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Unregister(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return false;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{scheme}", throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
