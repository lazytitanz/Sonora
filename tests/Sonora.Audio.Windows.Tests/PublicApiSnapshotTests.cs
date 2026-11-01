using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Sonora.Audio.Windows;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Locks the Sonora.Audio.Windows public API for the v1.0 RC baseline.
/// Set SONORA_UPDATE_PUBLIC_API_BASELINE=1 to regenerate Baselines/Sonora.Audio.Windows.PublicApi.txt.
/// </summary>
public sealed class PublicApiSnapshotTests
{
    [Fact]
    public void SonoraAudioWindows_PublicApi_MatchesBaseline()
    {
        var actual = Dump(typeof(SonoraWindows).Assembly);
        var baselinePath = GetBaselinePath("Sonora.Audio.Windows.PublicApi.txt");

        if (ShouldUpdateBaseline())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, actual);
        }

        Assert.True(File.Exists(baselinePath), $"Missing baseline at {baselinePath}. Run with SONORA_UPDATE_PUBLIC_API_BASELINE=1.");
        var expected = File.ReadAllText(baselinePath).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    private static bool ShouldUpdateBaseline()
        => string.Equals(Environment.GetEnvironmentVariable("SONORA_UPDATE_PUBLIC_API_BASELINE"), "1", StringComparison.Ordinal);

    private static string GetBaselinePath(string fileName, [CallerFilePath] string sourceFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(sourceFile)!;
        return Path.Combine(testProjectDir, "Baselines", fileName);
    }

    private static string Dump(Assembly assembly)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Assembly: {assembly.GetName().Name}");
        sb.AppendLine("# v1.0 RC public API baseline for Sonora.Audio.Windows");

        foreach (var type in assembly.GetExportedTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine($"public {DescribeKind(type)} {type.FullName}");

            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var parms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));
                sb.AppendLine($"  {method.ReturnType.FullName} {method.Name}({parms})");
            }

            foreach (var prop in type.GetProperties(flags).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {prop.PropertyType.FullName} {prop.Name}");
            }
        }

        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string DescribeKind(Type type)
        => type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : type.IsValueType ? "struct"
            : type.IsAbstract && type.IsSealed ? "static class"
            : type.IsSealed ? "sealed class"
            : "class";
}
