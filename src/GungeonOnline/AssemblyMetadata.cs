using System;

// Enter the Gungeon is a .NET 3.5 game, which has no AssemblyMetadataAttribute
// (.NET 4.5+). The type below exists so the next build records the same
// Thunderstore AI metadata as the AssemblyMetadata items in the csproj.
// Keep the keys and values identical to that ItemGroup.
namespace System.Reflection
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    sealed class AssemblyMetadataAttribute : Attribute
    {
        public AssemblyMetadataAttribute(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; private set; }
        public string Value { get; private set; }
    }
}

[assembly: System.Reflection.AssemblyMetadata(
    "AI_Assisted_Creation",
    "This assembly was partially or fully created with the assistance of Generative AI (code suggestions and edits under the author's direction).")]
[assembly: System.Reflection.AssemblyMetadata(
    "AI_Model_Vendor",
    "Anthropic (Claude Opus, Claude Fable)")]
