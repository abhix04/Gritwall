namespace Umbra.Core.Tracking;

/// <summary>
/// Broad activity categories for application tracking visualization and budgeting.
/// </summary>
public enum ActivityCategory
{
    Unclassified = 0,
    Productive = 1,
    Social = 2,
    Games = 3
}

/// <summary>
/// Category classifier. All application activity defaults to Unclassified until
/// an authoritative application categorization engine is implemented.
/// </summary>
public static class ActivityCategoryClassifier
{
    /// <summary>
    /// Classifies an application activity record.
    /// In Phase 1, all application activities default to <see cref="ActivityCategory.Unclassified"/>.
    /// </summary>
    public static ActivityCategory Classify(string? processName, string? executablePath = null)
    {
        return ActivityCategory.Unclassified;
    }

    /// <summary>
    /// Maps an activity category to its approved UI hex color.
    /// Productive = Green, Social = Amber, Games = Purple, Unclassified = Grey.
    /// </summary>
    public static string GetCategoryColorHex(ActivityCategory category) => category switch
    {
        ActivityCategory.Productive => "#4CAF50",
        ActivityCategory.Social => "#F7A21B",
        ActivityCategory.Games => "#B98BE0",
        _ => "#7E8B99"
    };
}
