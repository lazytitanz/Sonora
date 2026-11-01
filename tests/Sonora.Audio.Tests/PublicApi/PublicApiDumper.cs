using System.Reflection;
using System.Text;

namespace Sonora.Audio.Tests.PublicApi;

/// <summary>
/// Deterministic public API surface dump for snapshot tests.
/// </summary>
internal static class PublicApiDumper
{
    public static string Dump(Assembly assembly)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Assembly: {assembly.GetName().Name}");
        sb.AppendLine($"# Generated for Sonora public API snapshot tests");

        var types = assembly.GetExportedTypes()
            .Where(t => t.IsPublic)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            AppendType(sb, type);
        }

        return sb.ToString().Replace("\r\n", "\n");
    }

    private static void AppendType(StringBuilder sb, Type type)
    {
        sb.AppendLine();
        sb.Append(FormatTypeHeader(type));
        sb.AppendLine();

        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type).OrderBy(n => n, StringComparer.Ordinal))
            {
                var value = Convert.ToInt64(Enum.Parse(type, name));
                sb.AppendLine($"  {name} = {value}");
            }

            return;
        }

        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly;

        foreach (var ctor in type.GetConstructors(flags).OrderBy(FormatMethod, StringComparer.Ordinal))
        {
            sb.AppendLine($"  .ctor({FormatParameters(ctor.GetParameters())})");
        }

        foreach (var prop in type.GetProperties(flags).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var accessors = new List<string>();
            if (prop.GetMethod?.IsPublic == true)
            {
                accessors.Add("get");
            }

            if (prop.SetMethod?.IsPublic == true)
            {
                accessors.Add("set");
            }

            sb.AppendLine($"  {FormatTypeName(prop.PropertyType)} {prop.Name} {{ {string.Join("; ", accessors)} }}");
        }

        foreach (var evt in type.GetEvents(flags).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"  event {FormatTypeName(evt.EventHandlerType!)} {evt.Name}");
        }

        foreach (var method in type.GetMethods(flags)
                     .Where(m => !m.IsSpecialName)
                     .OrderBy(FormatMethod, StringComparer.Ordinal))
        {
            sb.AppendLine($"  {FormatTypeName(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})");
        }
    }

    private static string FormatTypeHeader(Type type)
    {
        var kind = type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : type.IsValueType ? "struct"
            : type.IsAbstract && type.IsSealed ? "static class"
            : type.IsAbstract ? "abstract class"
            : "class";

        var accessibility = "public";
        if (type.IsSealed && !type.IsAbstract && !type.IsValueType && !type.IsEnum && !type.IsInterface)
        {
            accessibility = "public sealed";
        }

        return $"{accessibility} {kind} {FormatTypeName(type)}";
    }

    private static string FormatMethod(MethodBase method)
    {
        var name = method is ConstructorInfo ? ".ctor" : method.Name;
        return $"{name}({FormatParameters(method.GetParameters())})";
    }

    private static string FormatParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}"));

    private static string FormatTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return FormatTypeName(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return FormatTypeName(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            return FormatTypeName(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition().FullName!;
            var tick = def.IndexOf('`');
            var root = tick >= 0 ? def[..tick] : def;
            var args = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{root}<{args}>";
        }

        return type.FullName ?? type.Name;
    }
}
