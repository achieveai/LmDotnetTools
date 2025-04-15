using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public class UsageShadowPropertiesJsonConverter : ShadowPropertiesJsonConverter<Usage>
{
    protected override Usage CreateInstance()
    {
        return new Usage();
    }
} 