using System.Security.Cryptography;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Persistence;

public sealed class FileApiKeyProvider : IApiKeyProvider
{
    public string ApiKey { get; }

    public FileApiKeyProvider(IAppPaths paths)
    {
        var directory = paths.DataRoot;
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "api-key.txt");
        if (!File.Exists(keyPath))
            File.WriteAllText(keyPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

        ApiKey = File.ReadAllText(keyPath).Trim();
        if (ApiKey.Length < 32)
            throw new InvalidOperationException("Chave de API inválida.");
    }
}
