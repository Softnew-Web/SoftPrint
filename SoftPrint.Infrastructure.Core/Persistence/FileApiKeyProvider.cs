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
    /// Dono (conta de usuário) da pasta de dados ou da primeira pasta acima dela que tenha um.
    /// Pastas criadas pelo serviço pertencem ao SYSTEM; %LocalAppData% pertence ao usuário.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static SecurityIdentifier? FindProfileOwner(string? directory)
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
