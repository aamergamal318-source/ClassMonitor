using Microsoft.Win32;
using ClassMonitor.Installation;

/// <summary>Per-user Apps entry, shortcuts and opt-in sign-in start. Capture and Restore make all of it reversible.</summary>
internal sealed class WindowsRegistration(bool desktopShortcut = false, bool autostart = false) : IInstallerRegistration
{
    private sealed record Snapshot(Dictionary<string, (object? Value, RegistryValueKind Kind)>? Apps, Dictionary<string, byte[]> Shortcuts, string? RunValue);

    private static string Key(InstallComponent component) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClassMonitor-" + component;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string[] ShortcutPaths(InstallComponent component) => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), InstallerCore.ShortcutName(component)),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), InstallerCore.ShortcutName(component)),
    };

    public object? Capture(InstallComponent component)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key(component));
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return new Snapshot(key?.GetValueNames().ToDictionary(n => n, n => (key.GetValue(n), key.GetValueKind(n))),
            ShortcutPaths(component).Where(File.Exists).ToDictionary(p => p, File.ReadAllBytes),
            component == InstallComponent.Agent ? run?.GetValue(InstallerCore.RunValueName) as string : null);
    }

    public void Register(InstallReceipt receipt, string root)
    {
        string application = Path.Combine(root, InstallerCore.ApplicationName(receipt.Component));
        using (var key = Registry.CurrentUser.CreateSubKey(Key(receipt.Component)))
        {
            key.SetValue("DisplayName", "ClassMonitor " + receipt.Component);
            key.SetValue("DisplayVersion", receipt.Version);
            key.SetValue("Publisher", "ClassMonitor open-source contributors");
            key.SetValue("InstallLocation", root);
            key.SetValue("DisplayIcon", application);
            key.SetValue("UninstallString", InstallerCore.UninstallCommand(Environment.GetFolderPath(Environment.SpecialFolder.System), root));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        var shortcuts = ShortcutPaths(receipt.Component);
        CreateShortcut(shortcuts[0], application, root);
        if (desktopShortcut) CreateShortcut(shortcuts[1], application, root);
        else if (File.Exists(shortcuts[1])) File.Delete(shortcuts[1]);
        if (receipt.Component == InstallComponent.Agent)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (autostart) run.SetValue(InstallerCore.RunValueName, '"' + application + '"');
            else run.DeleteValue(InstallerCore.RunValueName, throwOnMissingValue: false);
        }
    }

    public void Restore(InstallComponent component, object? snapshot)
    {
        Remove(component);
        if (snapshot is not Snapshot before) return;
        if (before.Apps is not null)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key(component));
            foreach (var value in before.Apps) if (value.Value.Value is not null) key.SetValue(value.Key, value.Value.Value, value.Value.Kind);
        }
        foreach (var shortcut in before.Shortcuts) File.WriteAllBytes(shortcut.Key, shortcut.Value);
        if (before.RunValue is not null)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(InstallerCore.RunValueName, before.RunValue);
        }
    }

    public void Remove(InstallComponent component)
    {
        Registry.CurrentUser.DeleteSubKey(Key(component), throwOnMissingSubKey: false);
        foreach (string path in ShortcutPaths(component)) if (File.Exists(path)) File.Delete(path);
        if (component == InstallComponent.Agent)
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(InstallerCore.RunValueName, throwOnMissingValue: false);
        }
    }

    private static void CreateShortcut(string path, string target, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcuts are unavailable."))!;
        try
        {
            dynamic link = ((dynamic)shell).CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = target;
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
