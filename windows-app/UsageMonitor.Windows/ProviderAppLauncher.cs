using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CodexUsageMonitor.Windows;

public enum ProviderAppTarget
{
    ChatGPT,
    Codex,
    Gemini,
    Antigravity,
    Claude
}

internal static class ProviderAppLauncher
{
    private const string PackageRepositoryPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    internal static bool TryLaunch(ProviderAppTarget target, out string error)
    {
        if (!TryBuildStartInfo(target, out var startInfo, out error) || startInfo is null)
            return false;

        try
        {
            using var process = Process.Start(startInfo);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            error = $"{GetDisplayName(target)} açılamadı: {ex.Message}";
            return false;
        }
    }

    internal static bool TryBuildStartInfo(
        ProviderAppTarget target,
        out ProcessStartInfo? startInfo,
        out string error)
    {
        startInfo = null;
        error = string.Empty;

        switch (target)
        {
            case ProviderAppTarget.ChatGPT:
                if (!TryFindPackagedApplication("OpenAI.Codex", "ChatGPT", out var chatGpt))
                {
                    error = "Yüklü Windows uygulamaları arasında ChatGPT masaüstü uygulaması bulunamadı.";
                    return false;
                }

                startInfo = BuildAppsFolderStartInfo(chatGpt.AppUserModelId);
                return true;

            case ProviderAppTarget.Codex:
                if (!TryFindPackagedApplication("OpenAI.Codex", "ChatGPT", out var codexHost) ||
                    !codexHost.SupportsCodexProtocol ||
                    !IsProtocolRegistered("codex"))
                {
                    error = "Codex modu açılamıyor; yüklü ChatGPT masaüstü uygulaması codex: protokolünü kaydetmemiş.";
                    return false;
                }

                startInfo = BuildShellStartInfo("codex://");
                return true;

            case ProviderAppTarget.Gemini:
                return TryBuildShortcutStartInfo("Gemini", out startInfo, out error);

            case ProviderAppTarget.Antigravity:
                return TryBuildShortcutStartInfo("Antigravity", out startInfo, out error);

            case ProviderAppTarget.Claude:
                if (!TryFindPackagedApplication("AnthropicPBC.Claude", "Claude", out var claude))
                {
                    error = "Yüklü Windows uygulamaları arasında Claude masaüstü uygulaması bulunamadı.";
                    return false;
                }

                startInfo = BuildAppsFolderStartInfo(claude.AppUserModelId);
                return true;

            default:
                error = "Bilinmeyen sağlayıcı uygulaması.";
                return false;
        }
    }

    private static bool TryBuildShortcutStartInfo(
        string appName,
        out ProcessStartInfo? startInfo,
        out string error)
    {
        var shortcutPath = FindStartMenuShortcut(appName);
        if (shortcutPath is null)
        {
            startInfo = null;
            error = $"{appName} uygulaması Windows Başlat menüsünde bulunamadı.";
            return false;
        }

        startInfo = BuildShellStartInfo(shortcutPath);
        error = string.Empty;
        return true;
    }

    private static ProcessStartInfo BuildAppsFolderStartInfo(string appUserModelId) =>
        new()
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            Arguments = $@"shell:AppsFolder\{appUserModelId}",
            UseShellExecute = true
        };

    private static ProcessStartInfo BuildShellStartInfo(string target) =>
        new()
        {
            FileName = target,
            UseShellExecute = true
        };

    private static bool TryFindPackagedApplication(
        string packageName,
        string displayName,
        out PackagedApplication application)
    {
        application = default!;

        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackageRepositoryPath);
            if (packages is null)
                return false;

            var packageNames = packages.GetSubKeyNames()
                .Where(name => name.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(GetPackageVersion);

            foreach (var packageFullName in packageNames)
            {
                using var package = packages.OpenSubKey(packageFullName);
                if (package?.GetValue("PackageRootFolder") is not string packageRoot ||
                    string.IsNullOrWhiteSpace(packageRoot))
                    continue;

                var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
                if (!File.Exists(manifestPath))
                    continue;

                using var reader = XmlReader.Create(manifestPath, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
                var manifest = XDocument.Load(reader);

                foreach (var candidate in manifest.Descendants()
                             .Where(element => element.Name.LocalName == "Application"))
                {
                    var appId = (string?)candidate.Attribute("Id");
                    var visualElements = candidate.Elements()
                        .FirstOrDefault(element => element.Name.LocalName == "VisualElements");
                    var appDisplayName = (string?)visualElements?.Attribute("DisplayName");
                    if (string.IsNullOrWhiteSpace(appId) ||
                        !string.Equals(appDisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var familyName = GetPackageFamilyName(packageFullName);
                    if (familyName is null)
                        continue;

                    var supportsCodexProtocol = candidate.Descendants()
                        .Any(element =>
                            element.Name.LocalName == "Protocol" &&
                            string.Equals((string?)element.Attribute("Name"), "codex", StringComparison.OrdinalIgnoreCase));

                    application = new PackagedApplication(
                        $"{familyName}!{appId}",
                        supportsCodexProtocol);
                    return true;
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException or XmlException or ArgumentException)
        {
            // Treat inaccessible or malformed package registration like an app that is unavailable.
        }

        return false;
    }

    private static bool IsProtocolRegistered(string scheme)
    {
        try
        {
            using var protocol = Registry.ClassesRoot.OpenSubKey(scheme);
            return protocol is not null &&
                   protocol.GetValueNames().Any(name =>
                       string.Equals(name, "URL Protocol", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static string? FindStartMenuShortcut(string appName)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };

        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directShortcut = Path.Combine(root, appName + ".lnk");
            if (File.Exists(directShortcut))
                return directShortcut;

            try
            {
                var matchingShortcut = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                    .Where(path => string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        appName,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path.Length)
                    .FirstOrDefault();
                if (matchingShortcut is not null)
                    return matchingShortcut;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Continue with the next Start Menu root if one cannot be searched.
            }
        }

        return null;
    }

    private static Version GetPackageVersion(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version)
            ? version
            : new Version(0, 0);
    }

    private static string? GetPackageFamilyName(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        if (parts.Length < 5 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[^1]))
            return null;

        return $"{parts[0]}_{parts[^1]}";
    }

    private static string GetDisplayName(ProviderAppTarget target) =>
        target switch
        {
            ProviderAppTarget.ChatGPT => "ChatGPT",
            ProviderAppTarget.Codex => "Codex",
            ProviderAppTarget.Gemini => "Gemini",
            ProviderAppTarget.Antigravity => "Antigravity",
            ProviderAppTarget.Claude => "Claude",
            _ => "provider app"
        };

    private sealed record PackagedApplication(string AppUserModelId, bool SupportsCodexProtocol);
}


