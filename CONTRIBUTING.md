# Contributing to DualWAN Speed & Gaming Balancer

Thank you for contributing!

## Development Requirements

- **OS:** Windows 10 or Windows 11 (x64)
- **Compiler:** Built-in `.NET Framework 4.x` C# 5 compiler at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- **Important C# Constraint:** Keep all C# source files in `DualWanApp/` strictly compatible with **C# 5** so anyone can compile with Windows's built-in `csc.exe` without installing Visual Studio or modern SDKs (avoid string interpolation `$""`, `?.`, `nameof`, inline `out` declarations, and expression-bodied members).
- **PowerShell Scripts:** Keep `dual-wan-manager.ps1` compatible with Windows PowerShell 5.1.

## Building & Testing Locally

1. Compile the desktop executable:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\DualWanApp\build.ps1
   ```
2. Validate PowerShell script syntax:
   ```powershell
   $errors = $null
   [System.Management.Automation.Language.Parser]::ParseFile('.\dual-wan-manager.ps1', [ref]$null, [ref]$errors)
   if ($errors) { $errors; exit 1 }
   ```
3. Verify Node.js proxy CLI (if Node.js is installed):
   ```powershell
   node .\dual-wan-proxy.js --help
   ```

## Pull Request Guidelines

- Do not commit compiled binaries (`*.exe`, `*.ico`) or local machine backups (`dual-wan-backup.json`).
- Keep functions focused, preserve existing safety backups (System Proxy and Registry backups), and test mode switching + clean proxy restoration before submitting.
