# PreToolUse (Bash, PowerShell): blocks commands that would change the real registration of the app or delete files
# on the Desktop. It only blocks, with a reason; it never changes or deletes anything itself.
# Allowed: reading the registry, tests, sandboxed runs (BATON_DATA), anything inside the project folder.
# Fail-closed: an input it cannot read or any error of its own blocks the command (exit 2), and settings.json has
# "onFailure": "block" for a timeout or a script that cannot start.
$ErrorActionPreference = "Stop"
trap {
  [Console]::Error.WriteLine("BLOCCATO dal guard del progetto: errore interno del controllo ($_). Nel dubbio il comando non viene eseguito.")
  exit 2
}
[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Block([string]$reason) {
  [Console]::Error.WriteLine("BLOCCATO dal guard del progetto: $reason")
  exit 2
}

try { $command = ([Console]::In.ReadToEnd() | ConvertFrom-Json).tool_input.command } catch { Block "richiesta dell'hook non leggibile, quindi non controllabile." }
if (-not $command) { exit 0 }

# Commands that write to the registry (reg.exe, PowerShell cmdlets, .NET RegistryKey calls).
$writesRegistry = '(?i)\breg(\.exe)?\s+(add|delete|import|copy|restore|load|unload)\b|\b(Set|New|Remove|Rename|Clear|Copy|Move)-ItemProperty\b|\b(New|Set|Remove)-Item\b|\.(SetValue|DeleteValue|DeleteSubKey|DeleteSubKeyTree|CreateSubKey)\('

if ($command -match $writesRegistry) {
  if ($command -match '(?i)CurrentVersion\\+Run(Once)?\b') {
    Block "modifica della chiave Run (avvio con Windows). La voce si gestisce solo dall'app (Impostazioni) tramite SystemRegistration; per le prove usa BATON_DATA, che tiene il registro in memoria. Leggere la chiave (reg query, Get-ItemProperty) è consentito."
  }
  if ($command -match '(?i)NativeMessagingHosts') {
    Block "modifica di NativeMessagingHosts. L'host nativo lo registra l'app all'avvio (SystemRegistration.RegisterBrowserHost) o il link baton:register; per le prove usa BATON_DATA. La lettura è consentita."
  }
}

# Deleting on the Desktop, except inside the project folder (Desktop\DownloadManager\...).
$deletes = '(?i)(^|[\s;&|(])(Remove-Item|ri|rm|rmdir|rd|del|erase)\b|\[(System\.)?IO\.(File|Directory)\]::Delete'
$desktopOutsideProject = '(?i)(Desktop|GetFolderPath\(\s*.Desktop.\s*\))(?![\\/]+DownloadManager([\\/"''\s]|$))'
if ($command -match $deletes -and $command -match $desktopOutsideProject) {
  Block "cancellazione sul Desktop dell'utente. Le prove scrivono solo nella cartella di lavoro (%TEMP%\dm-trials); i file sul Desktop vanno rimossi solo dall'utente, o dopo una sua conferma esplicita."
}
exit 0
