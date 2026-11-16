namespace Baton.Core.Rules;

/// <summary>Programs and scripts: files Baton never opens by itself on behalf of an agent or a rule.</summary>
public static class ExecutableTypes
{
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".lnk",
        ".hta", ".jar", ".reg", ".cpl", ".dll", ".appx", ".msix", ".url",
        ".pif", ".msc", ".chm", ".inf", ".scf", ".sct", ".ws", ".wsc", ".vb", ".gadget", ".application", ".appref-ms", ".xll", ".wll",
        ".diagcab", ".settingcontent-ms", ".theme", ".themepack", ".library-ms", ".search-ms", ".website", ".sys", ".ocx",
        ".iso", ".img", ".vhd", ".vhdx", ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".ppsm", ".sldm",
        ".msh", ".msh1", ".msh2", ".mshxml", ".ps1xml", ".psc1", ".cer", ".crt", ".der",
    };

    /// <summary>True for a path or an extension (with its dot) of a program or script.</summary>
    public static bool IsRunnable(string pathOrExtension)
    {
        var extension = pathOrExtension.StartsWith('.') && pathOrExtension.IndexOfAny(['\\', '/']) < 0 ? pathOrExtension : Path.GetExtension(pathOrExtension);
        return extension.Length > 0 && Runnable.Contains(extension);
    }
}
