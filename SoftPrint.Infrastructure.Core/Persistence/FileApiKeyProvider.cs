using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SoftPrint.Application.Abstractions;
#if !NET5_0_OR_GREATER
using SoftPrint.Infrastructure.Compat;
#endif

namespace SoftPrint.Infrastructure.Persistence;

public sealed class FileApiKeyProvider : IApiKeyProvider
{
    private readonly object _gate = new();
    private readonly string _keyPath;
    private string _apiKey;

    public FileApiKeyProvider(IAppPaths paths)
    {
        var directory = paths.DataRoot;
        Directory.CreateDirectory(directory);
        _keyPath = Path.Combine(directory, "api-key.txt");
        if (!File.Exists(_keyPath))
            WriteNewKeyUnlocked();

        _apiKey = ReadKeyFile();
        if (_apiKey.Length < 32)
            throw new InvalidOperationException("Chave de API inválida.");

        TryHardenAcl(_keyPath);
    }

    public string ApiKey
    {
        get { lock (_gate) return _apiKey; }
    }

    public string Rotate()
    {
        lock (_gate)
        {
            WriteNewKeyUnlocked();
            _apiKey = ReadKeyFile();
            TryHardenAcl(_keyPath);
            return _apiKey;
        }
    }

    private void WriteNewKeyUnlocked()
    {
#if NET6_0_OR_GREATER
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
#else
        var keyBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(keyBytes);
        var raw = BitConverter.ToString(keyBytes).Replace("-", "");
#endif
        File.WriteAllText(_keyPath, raw + Environment.NewLine);
    }

    private string ReadKeyFile() => File.ReadAllText(_keyPath).Trim();

    private static void TryHardenAcl(string path)
    {
        try
        {
#if NET5_0_OR_GREATER
            if (OperatingSystem.IsWindows())
#else
            if (OsHelper.IsWindows())
#endif
                HardenWindowsAcl(path);
#if NET7_0_OR_GREATER
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
        }
        catch
        {
            /* ignore — melhor esforço */
        }
    }

    [SupportedOSPlatform("windows")]
    private static void HardenWindowsAcl(string path)
    {
        var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null) return;

        var file = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        // SYSTEM precisa ler em alguns cenários de serviço/update.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        // No serviço quem roda é o SYSTEM, mas a chave fica na pasta do usuário (--config-root):
        // sem isto o usuário perde a leitura e o SoftPrint dele não abre quando o serviço é desligado.
        if (FindProfileOwner(file.DirectoryName) is { } owner && owner != identity.User)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }
        file.SetAccessControl(security);
    }

    /// <summary>
    /// Usuário dono do perfil onde está a pasta de dados (C:\Users\fulano\... → fulano), pela lista
    /// de perfis do Windows. O dono NTFS da pasta não serve sozinho: o que um administrador cria
    /// costuma pertencer ao grupo Administradores, e o que o serviço cria, ao SYSTEM.
    /// Fora de um perfil, usa o dono da primeira pasta acima que seja uma conta de usuário.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static SecurityIdentifier? FindProfileOwner(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        return FindProfileUser(directory!) ?? FindAccountOwner(directory);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? FindProfileUser(string directory)
    {
        try
        {
            var full = LongPath(directory).TrimEnd('\\') + "\\";
            using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list is null) return null;

            SecurityIdentifier? best = null;
            var bestLength = 0;
            foreach (var sidText in list.GetSubKeyNames())
            {
                using var profile = list.OpenSubKey(sidText);
                if (profile?.GetValue("ProfileImagePath") is not string imagePath || imagePath.Length == 0)
                    continue;
                var root = LongPath(Environment.ExpandEnvironmentVariables(imagePath)).TrimEnd('\\') + "\\";
                if (root.Length <= bestLength || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var sid = new SecurityIdentifier(sidText);
                    if (!sid.IsAccountSid()) continue; // perfis de SYSTEM/LocalService/NetworkService
                    best = sid;
                    bestLength = root.Length;
                }
                catch (ArgumentException)
                {
                    /* subchave que não é um SID (ex.: *.bak) */
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Caminho completo e sem nomes curtos 8.3 (ex.: C:\Users\RUNNER~1 → C:\Users\runneradmin).</summary>
    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        var buffer = new System.Text.StringBuilder(1024);
        var length = GetLongPathName(full, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : full;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint bufferSize);

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? FindAccountOwner(string? directory)
    {
        for (var dir = directory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists) continue;
                if (info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))
                        is SecurityIdentifier sid && sid.IsAccountSid())
                    return sid;
            }
            catch
            {
                /* sem permissão para ler o dono: tenta a pasta de cima */
            }
        }
        return null;
    }
}
