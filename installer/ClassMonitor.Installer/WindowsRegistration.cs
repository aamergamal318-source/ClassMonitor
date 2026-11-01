using Microsoft.Win32;
using ClassMonitor.Installation;

internal sealed class WindowsRegistration : IInstallerRegistration
{
    private static string Key(InstallComponent component) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClassMonitor-" + component;
    public object? Capture(InstallComponent component)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key(component));
        return key is null ? null : key.GetValueNames().ToDictionary(n => n, n => (key.GetValue(n), key.GetValueKind(n)));
    }
    public void Register(InstallReceipt receipt, string root)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key(receipt.Component));
        key.SetValue("DisplayName", "ClassMonitor " + receipt.Component);
        key.SetValue("DisplayVersion", receipt.Version);
        key.SetValue("Publisher", "ClassMonitor open-source contributors");
        key.SetValue("InstallLocation", root);
        key.SetValue("DisplayIcon", Path.Combine(root, InstallerCore.ApplicationName(receipt.Component)));
        key.SetValue("UninstallString", '"' + Path.Combine(root, "ClassMonitorSetup.exe") + "\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
    public void Restore(InstallComponent component, object? snapshot)
    {
        Remove(component);
        if (snapshot is not Dictionary<string, (object?, RegistryValueKind)> values) return;
        using var key = Registry.CurrentUser.CreateSubKey(Key(component));
        foreach (var value in values) if (value.Value.Item1 is not null) key.SetValue(value.Key, value.Value.Item1, value.Value.Item2);
    }
    public void Remove(InstallComponent component) => Registry.CurrentUser.DeleteSubKey(Key(component), throwOnMissingSubKey: false);
}
