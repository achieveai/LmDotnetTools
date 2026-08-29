using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Tests;

/// <summary>
/// Holds the package's public surface to a baseline checked in beside this file.
/// </summary>
/// <remarks>
/// <para>
/// This package exists to be referenced by builds that are not rebuilt in lockstep with it — a
/// sandbox owner or an external service compiles against one version and keeps running against the
/// next. Nothing in the ordinary build notices when a signature moves out from under such a
/// consumer, because the consumer is not in this solution to fail. The baseline is that missing
/// compiler: every public type, member, and constant is written down, and a change to any of them
/// surfaces as a reviewable diff rather than as a <see cref="MissingMethodException"/> in a build
/// nobody here runs.
/// </para>
/// <para>
/// Constants are recorded with their values on purpose. A consumer compiled against
/// <c>ToolApprovalOutcomes.Allowed</c> carries the literal <c>"allowed"</c> in its own metadata, not
/// a reference to this field, so changing the value here changes nothing for that consumer until it
/// is rebuilt — and in the meantime the two disagree about what grants permission. That is the one
/// break in this package that is both silent and dangerous, so the gate watches it.
/// </para>
/// <para>
/// Additions are as reviewable as removals, and deliberately so. A new required member or a new
/// overload can break a precompiled consumer just as thoroughly as a deletion, and the point of a
/// baseline is that someone looks. To accept a change, run the test, then copy the emitted
/// <c>PublicApi.Received.txt</c> over <c>PublicApi.Shipped.txt</c> and include it in the commit.
/// </para>
/// </remarks>
public class LifecyclePublicApiTests
{
    private const string ShippedFileName = "PublicApi.Shipped.txt";
    private const string ReceivedFileName = "PublicApi.Received.txt";

    [Fact]
    public void The_public_surface_matches_the_shipped_baseline()
    {
        var shippedPath = Path.Combine(AppContext.BaseDirectory, ShippedFileName);
        var receivedPath = Path.Combine(AppContext.BaseDirectory, ReceivedFileName);

        File.Exists(shippedPath).Should().BeTrue("the baseline is the gate; without it this test proves nothing");

        var actual = DescribeSurface();
        File.WriteAllText(receivedPath, string.Join("\n", actual) + "\n");

        var shipped = File.ReadAllLines(shippedPath)
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

        var removed = shipped.Except(actual, StringComparer.Ordinal).ToList();
        var added = actual.Except(shipped, StringComparer.Ordinal).ToList();

        (removed.Count + added.Count)
            .Should()
            .Be(
                0,
                "the public surface changed.{0}{0}Removed (breaks a consumer compiled against the "
                    + "previous version):{0}{1}{0}{0}Added (review for required members and "
                    + "ambiguous overloads):{0}{2}{0}{0}If the change is intended, copy {3} over "
                    + "{4} and commit it.",
                Environment.NewLine,
                Format(removed),
                Format(added),
                receivedPath,
                shippedPath
            );
    }

    [Fact]
    public void The_contract_assembly_depends_on_no_other_assembly_from_this_repository()
    {
        var referenced = typeof(LifecycleEventEnvelope)
            .Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToList();

        referenced
            .Should()
            .NotContain(
                name => name.StartsWith("AchieveAi", StringComparison.Ordinal),
                "a consumer must be able to reference this contract without dragging in — and "
                    + "version-matching — the rest of the SDK"
            );
    }

    private static string Format(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "  (none)" : string.Join(Environment.NewLine, lines.Select(l => "  " + l));

    /// <summary>Renders the public surface as one sorted line per declaration.</summary>
    private static List<string> DescribeSurface()
    {
        var lines = new List<string>();

        foreach (var type in typeof(LifecycleEventEnvelope).Assembly.GetExportedTypes())
        {
            lines.Add(DescribeType(type));

            const BindingFlags Declared =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var member in type.GetMembers(Declared))
            {
                if (DescribeMember(type, member) is { } line)
                {
                    lines.Add(line);
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string DescribeType(Type type)
    {
        var kind =
            type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : type.IsValueType ? "struct"
            : type is { IsAbstract: true, IsSealed: true } ? "static class"
            : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class"
            : "class";

        return $"{kind} {TypeName(type)}";
    }

    private static string? DescribeMember(Type declaring, MemberInfo member)
    {
        var prefix = TypeName(declaring) + ".";

        switch (member)
        {
            // Nested types are enumerated in their own right by GetExportedTypes.
            case Type:
                return null;

            // Record boilerplate is derived from the properties, which are recorded already.
            case MethodInfo method
                when method.IsSpecialName
                    || method.Name.StartsWith('<')
                    || method.Name is nameof(Equals) or nameof(GetHashCode) or nameof(ToString) or "PrintMembers":
                return null;

            case MethodInfo method:
                return prefix
                    + $"{method.Name}{TypeParameters(method)}({Parameters(method.GetParameters())}) : {TypeName(method.ReturnType)}";

            case ConstructorInfo constructor:
                return prefix + $".ctor({Parameters(constructor.GetParameters())})";

            case PropertyInfo property:
                var accessors = string.Concat(
                    property.GetGetMethod() is not null ? "get; " : string.Empty,
                    property.GetSetMethod() is not null ? "set; " : string.Empty
                );
                return prefix
                    + $"{property.Name} : {TypeName(property.PropertyType)} {{ {accessors}}}{Required(property)}";

            case FieldInfo field when field.IsLiteral:
                return prefix + $"{field.Name} : {TypeName(field.FieldType)} = {Literal(field.GetRawConstantValue())}";

            case FieldInfo field:
                return prefix + $"{field.Name} : {TypeName(field.FieldType)}{Required(field)}";

            case EventInfo declaredEvent:
                return prefix + $"event {declaredEvent.Name} : {TypeName(declaredEvent.EventHandlerType!)}";

            default:
                return prefix + member.Name;
        }
    }

    private static string Required(MemberInfo member) =>
        member.IsDefined(typeof(RequiredMemberAttribute), inherit: false) ? " required" : string.Empty;

    private static string TypeParameters(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? "<" + string.Join(", ", method.GetGenericArguments().Select(TypeName)) + ">"
            : string.Empty;

    private static string Parameters(ParameterInfo[] parameters) =>
        string.Join(
            ", ",
            parameters.Select(p =>
                (
                    p.IsOut ? "out "
                    : p.ParameterType.IsByRef ? "ref "
                    : string.Empty
                )
                + TypeName(p.ParameterType)
                + (p.HasDefaultValue ? " = " + Literal(p.RawDefaultValue) : string.Empty)
            )
        );

    private static string Literal(object? value) =>
        value switch
        {
            null => "null",
            string text => "\"" + text + "\"",
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
        {
            return TypeName(type.GetElementType()!);
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return TypeName(underlying) + "?";
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition().FullName!;
            var bare = definition[..definition.IndexOf('`', StringComparison.Ordinal)];
            var arguments = string.Join(", ", type.GetGenericArguments().Select(TypeName));
            return $"{Shorten(bare)}<{arguments}>";
        }

        return Aliases.TryGetValue(type, out var alias) ? alias : Shorten(type.FullName ?? type.Name);
    }

    /// <summary>Drops this package's own namespace so the baseline stays readable.</summary>
    private static string Shorten(string fullName)
    {
        const string OwnNamespace = "AchieveAi.LmDotnetTools.LmLifecycle.";
        return fullName.StartsWith(OwnNamespace, StringComparison.Ordinal) ? fullName[OwnNamespace.Length..] : fullName;
    }

    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(int)] = "int",
        [typeof(long)] = "long",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
    };
}
