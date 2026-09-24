using SoftPrint.Application.Abstractions;

namespace SoftPrint.Application.Abstractions;

public interface IApiKeyProvider
{
    string ApiKey { get; }

    /// <summary>Gera nova chave, grava em disco e invalida a anterior.</summary>
    string Rotate();
}
