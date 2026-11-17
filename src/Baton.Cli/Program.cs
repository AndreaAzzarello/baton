using System.Diagnostics;
using Baton.Cli;
using Baton.Cli.Mcp;
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
        // Through the shell: Baton must not inherit batonctl's redirected output (a script waiting for the end of the output, or the
        // JSON-RPC stream of "batonctl mcp", would wait for Baton to exit), nor keep the terminal's folder locked as its own.
        using var process = Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true, WorkingDirectory = exeFolder });
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
        try
        {
            if (await ChannelClient.SendAsync(pipe, new ChannelMessage(Messages.Ping), TimeSpan.FromMilliseconds(300)) is { Ok: true })
                return true;
        }
        catch (ChannelNoReplyException)
        {
            // still starting up: ask again
        }
    }
    return false;
}

if (args.Length > 0 && args[0] == "mcp")
{
    // Standard output belongs to the JSON-RPC stream: everything we have to say goes to standard error.
    void Log(string line) => Console.Error.WriteLine($"[batonctl mcp] {line}");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var link = new PipeAppLink(pipe, StartApp, Log);
    return await McpHost.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), link, version, Log, stop.Token);
}

var app = new CliApp(Console.Out, Console.Error, Console.In, Send, StartApp, version, Environment.CurrentDirectory, CliText.WindowsIsItalian());
return await app.RunAsync(args);
