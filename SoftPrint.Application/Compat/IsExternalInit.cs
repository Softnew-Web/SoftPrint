// Polyfill para System.Runtime.CompilerServices.IsExternalInit
// Necessário para 'record' e 'init' setters no .NET Core 3.1
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
