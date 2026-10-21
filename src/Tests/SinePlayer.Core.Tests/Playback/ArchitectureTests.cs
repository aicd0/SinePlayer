// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Reflection;

using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The rules of §4.2 are only worth as much as the check behind them. This is that check for the rule that matters
/// most: FFmpeg is a backend, and its types — the decoders beside the interop structs — may not appear in any
/// signature outside their own folder.
/// <para>
/// What it protects against is not a deliberate shortcut but slow drift: one field of a native frame kept "just for
/// now", one method that returns a decoder by its concrete type, and the design quietly becomes FFmpeg-shaped
/// without anyone deciding it. A test that walks the assembly says so on the commit that does it.
/// </para>
/// </summary>
[TestFixture]
public class ArchitectureTests
{
    private const string BackendNamespace = "SinePlayer.Core.Playback.Backends.FFmpeg";

    [Test]
    public void NoFFmpegTypeCrossesTheEngineBoundary()
    {
        Assembly engine = typeof(PlaybackPipeline).Assembly;
        Type[] types = engine.GetTypes();

        // A scan that finds nothing proves nothing, so the assembly is checked for the thing being looked for first.
        Assert.That(types.Any(IsBackend), Is.True, $"{engine.GetName().Name} holds no {BackendNamespace} type, so this test would pass vacuously");
        Assert.That(typeof(FFmpegVideoDecoder).Assembly, Is.SameAs(engine));

        List<string> leaks = [];
        foreach (Type type in types)
        {
            if (IsBackend(type))
            {
                continue;
            }

            Scan(type, leaks);
        }

        Assert.That(
            leaks,
            Is.Empty,
            $"Nothing outside {BackendNamespace} may name an FFmpeg type; the contracts are what everything else talks to."
            + Environment.NewLine + string.Join(Environment.NewLine, leaks));
    }

    private static void Scan(Type type, List<string> leaks)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        Collect(type.BaseType, $"{type.FullName} (base type)", leaks);

        foreach (Type contract in type.GetInterfaces())
        {
            Collect(contract, $"{type.FullName} (interface)", leaks);
        }

        foreach (FieldInfo field in type.GetFields(declared))
        {
            Collect(field.FieldType, $"{type.FullName}.{field.Name}", leaks);
        }

        foreach (PropertyInfo property in type.GetProperties(declared))
        {
            Collect(property.PropertyType, $"{type.FullName}.{property.Name}", leaks);
        }

        foreach (EventInfo raised in type.GetEvents(declared))
        {
            Collect(raised.EventHandlerType, $"{type.FullName}.{raised.Name}", leaks);
        }

        foreach (MethodInfo method in type.GetMethods(declared))
        {
            if (IsAccessor(method))
            {
                // Already covered by the property or event it belongs to.
                continue;
            }

            Collect(method.ReturnType, $"{type.FullName}.{method.Name} (return)", leaks);
            CollectParameters(method.GetParameters(), $"{type.FullName}.{method.Name}", leaks);

            foreach (Type argument in method.GetGenericArguments())
            {
                foreach (Type constraint in argument.GetGenericParameterConstraints())
                {
                    Collect(constraint, $"{type.FullName}.{method.Name} (constraint)", leaks);
                }
            }
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(declared))
        {
            CollectParameters(constructor.GetParameters(), $"{type.FullName} (constructor)", leaks);
        }
    }

    private static bool IsAccessor(MethodInfo method)
    {
        return method.IsSpecialName
            && (method.Name.StartsWith("get_", StringComparison.Ordinal)
                || method.Name.StartsWith("set_", StringComparison.Ordinal)
                || method.Name.StartsWith("add_", StringComparison.Ordinal)
                || method.Name.StartsWith("remove_", StringComparison.Ordinal));
    }

    private static void CollectParameters(ParameterInfo[] parameters, string source, List<string> leaks)
    {
        foreach (ParameterInfo parameter in parameters)
        {
            Collect(parameter.ParameterType, $"{source} ({parameter.Name})", leaks);
        }
    }

    /// <summary>
    /// Follows a type to everything it is made of — the element of an array or pointer, the arguments of a generic,
    /// the constraints of a type parameter — because <c>List&lt;AVFrame&gt;</c> and <c>AVFrame*</c> are as much a
    /// leak as <c>AVFrame</c> itself.
    /// </summary>
    private static void Collect(Type? referenced, string source, List<string> leaks)
    {
        if (referenced is null)
        {
            return;
        }

        if (referenced.HasElementType)
        {
            Collect(referenced.GetElementType(), source, leaks);
            return;
        }

        if (referenced.IsGenericParameter)
        {
            foreach (Type constraint in referenced.GetGenericParameterConstraints())
            {
                Collect(constraint, source, leaks);
            }

            return;
        }

        if (referenced.IsGenericType)
        {
            foreach (Type argument in referenced.GetGenericArguments())
            {
                Collect(argument, source, leaks);
            }
        }

        if (IsBackend(referenced))
        {
            leaks.Add($"{source} → {referenced.FullName}");
        }
    }

    private static bool IsBackend(Type type)
    {
        return type.Namespace is string space
            && (space == BackendNamespace || space.StartsWith(BackendNamespace + ".", StringComparison.Ordinal));
    }
}
