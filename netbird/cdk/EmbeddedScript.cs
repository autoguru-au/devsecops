using System.Reflection;

namespace Netbird.Cdk;

/// <summary>
/// Reads an EC2 user-data script that is embedded in this assembly (see the csproj
/// EmbeddedResource items). Embedding keeps the .sh files as real, LF-enforced files
/// while making them available at synth time without a working-directory dependency.
/// </summary>
internal static class EmbeddedScript
{
    public static string Read(string logicalName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded user-data script '{logicalName}' was not found in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
