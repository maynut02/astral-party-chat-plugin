namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public void LogInfo(object value) { }
        public void LogWarning(object value) { }
        public void LogDebug(object value) { }
    }
}

namespace AstralParty.Chat
{
    public static class AstralPartyChatPlugin
    {
        public const string PluginVersion = "tests";
    }
}
