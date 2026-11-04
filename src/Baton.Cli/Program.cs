using System.Diagnostics;
using Baton.Cli;
using Baton.Core.Integration;

var exeFolder = AppContext.BaseDirectory.TrimEnd('\\', '/');
var pipe = PipeNames.For(exeFolder);
var version = typeof(CliApp).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0";

Task<ChannelReply?> Send(ChannelMessage message) => ChannelClient.SendAsync(pipe, message, TimeSpan.FromSeconds(2));

// Baton.exe sits next to batonctl.exe (installed, portable and Scoop all keep them together).
async Task<bool> StartApp()
{
    var exe = Path.Combine(exeFolder, "Baton.exe");
    if (!File.Exists(exe))
        return false;
    try
    {
        using var process = Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = false });
        if (process is null)
            return false;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return false;
    }
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(300);
        if (await ChannelClient.SendAsync(pipe, new ChannelMessage(Messages.Ping), TimeSpan.FromMilliseconds(300)) is { Ok: true })
            return true;
    }
    return false;
}

var app = new CliApp(Console.Out, Console.Error, Console.In, Send, StartApp, version, Environment.CurrentDirectory, CliText.WindowsIsItalian());
return await app.RunAsync(args);
