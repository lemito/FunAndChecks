namespace FunAndChecks.Application.Common;

/// <summary>
/// Feature flags configuration section.
/// </summary>
public class FeatureFlags
{
    public const string SectionName = "FeatureFlags";

    /// <summary>
    /// Privacy Policy feature: consent checkbox, policy page, consent endpoints.
    /// </summary>
    public bool PrivacyPolicy { get; set; } = true;
}

/// <summary>
/// DTO for exposing feature flags to the frontend.
/// </summary>
public record FeaturesDto(bool PrivacyPolicy);