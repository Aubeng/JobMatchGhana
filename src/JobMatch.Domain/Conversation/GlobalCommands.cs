using JobMatch.Domain.Enums;

namespace JobMatch.Domain.Conversation;

/// <summary>
/// Recognises global commands available in any conversation state. Matching is on the
/// whole (trimmed, lower-cased) message so profile answers like "I can stop leaks" are
/// NOT mistaken for the "stop" command.
/// </summary>
public static class GlobalCommands
{
    public static Intent? TryParse(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t switch
        {
            "help" or "menu" or "?" => Intent.Help,
            "stop" or "pause" or "cancel" or "quit" or "unsubscribe" => Intent.Stop,
            "skills" or "update skills" or "update my skills" => Intent.UpdateSkills,
            // FR-15: update the profile at any time.
            "update" or "update profile" or "update my profile" or "edit profile" or "change profile"
                or "my profile" or "profile" => Intent.UpdateProfile,
            "matches" or "my matches" or "list matches" => Intent.ListMatches,
            // FR-16: delete my data (always confirmed first).
            "delete" or "delete my data" or "delete my profile" or "delete me" or "remove my data"
                or "delete my details" => Intent.DeleteData,
            _ => null
        };
    }
}
