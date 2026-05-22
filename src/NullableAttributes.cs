// Polyfill: Il2Cppmscorlib does not contain NullableAttribute / NullableContextAttribute.
// These are required by the C# compiler when building with nullable annotations against
// an IL2CPP-replaced BCL. Standard pattern for BepInEx IL2CPP mod projects.
// Reference: https://github.com/dotnet/roslyn/blob/main/docs/features/nullable-reference-types.md
#pragma warning disable CS0436  // type conflicts with imported type -- intentional polyfill

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag)        { NullableFlags = new[] { flag }; }
        public NullableAttribute(byte[] flags)     { NullableFlags = flags; }
    }

    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Event |
        AttributeTargets.Field | AttributeTargets.GenericParameter | AttributeTargets.Module |
        AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) { Flag = flag; }
    }

    // Required by the C# compiler for "where T : unmanaged" generic constraints
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class IsUnmanagedAttribute : Attribute
    {
        public IsUnmanagedAttribute() { }
    }
}
