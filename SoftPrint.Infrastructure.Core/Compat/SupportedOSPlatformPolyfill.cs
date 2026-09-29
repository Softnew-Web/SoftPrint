#if !NET5_0_OR_GREATER
// Polyfill para [SupportedOSPlatform] introduzido no .NET 5 (System.Runtime.Versioning).
// Em netcoreapp3.1 o atributo não existe; esta stub permite que o código compile sem alteração.
namespace System.Runtime.Versioning
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    internal sealed class SupportedOSPlatformAttribute : Attribute
    {
        public SupportedOSPlatformAttribute(string platformName) { }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    internal sealed class UnsupportedOSPlatformAttribute : Attribute
    {
        public UnsupportedOSPlatformAttribute(string platformName) { }
    }
}
#endif
