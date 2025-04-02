using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public class UsageJsonConverter : ShadowPropertiesJsonConverter<Usage>
{
    protected override Usage CreateInstance()
    {
        return new Usage();
    }
}