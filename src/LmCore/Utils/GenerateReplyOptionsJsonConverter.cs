using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public class GenerateReplyOptionsJsonConverter : ShadowPropertiesJsonConverter<GenerateReplyOptions>
{
    protected override GenerateReplyOptions CreateInstance()
    {
        return new GenerateReplyOptions();
    }
}