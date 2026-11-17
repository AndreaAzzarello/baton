using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Baton.App.Services;

public sealed class ShellService : IShellService
{
    public void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public void Reveal(string? path, string folder)
    {
        var args = path is not null && File.Exists(path) ? $"/select,\"{path}\"" : $"\"{folder}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
    }

    public void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    public string? GetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (COMException)
        {
            return null; // clipboard busy in another program
        }
    }

    public void SetClipboardSecret(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text);
            // The formats Windows reads before it records a copy in the history, syncs it to the cloud or shows it to clipboard monitors.
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1, 0, 0, 0]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream([0, 0, 0, 0]));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (COMException)
        {
        }
    }

    public void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
        }
    }
}
