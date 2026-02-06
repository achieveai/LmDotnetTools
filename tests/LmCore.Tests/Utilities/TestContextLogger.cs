using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;

public static class TestContextLogger
{
    private static readonly ILogger Logger = TestLoggingConfiguration.CreateLogger("LmCore.Tests.Diagnostics");

    public static void LogDebug(string messageTemplate, params object?[] args)
    {
        var (testClassName, testCaseName) = ResolveCallerContext();

        // Use ambient diagnostic context so sinks/renderers can project these fields.
        using var classContext = LogContext.PushProperty("testClassName", testClassName);
        using var caseContext = LogContext.PushProperty("testCaseName", testCaseName);

        Logger.LogDebug(messageTemplate, args);
    }

    public static void LogDebugMessage(string message)
    {
        LogDebug("{Message}", message);
    }

    private static (string TestClassName, string TestCaseName) ResolveCallerContext()
    {
        var stackTrace = new StackTrace();
        foreach (var frame in stackTrace.GetFrames() ?? [])
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType == null)
            {
                continue;
            }

            if (method.DeclaringType == typeof(TestContextLogger))
            {
                continue;
            }

            var type = method.DeclaringType;
            var typeName = type.Name;
            var methodName = method.Name;

            // Normalize async state machine frames:
            // typeName like "<MyTest>d__12" and methodName "MoveNext".
            if (typeName.StartsWith("<", StringComparison.Ordinal) && typeName.Contains(">d__", StringComparison.Ordinal))
            {
                var closeIndex = typeName.IndexOf('>');
                if (closeIndex > 1)
                {
                    methodName = typeName[1..closeIndex];
                }

                if (type.DeclaringType != null)
                {
                    typeName = type.DeclaringType.Name;
                }
            }

            return (typeName, methodName);
        }

        return ("UnknownTestClass", "UnknownTestCase");
    }
}
