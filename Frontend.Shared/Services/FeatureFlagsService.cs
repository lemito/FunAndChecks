using System.Net.Http.Json;

namespace Frontend.Shared.Services;

/// <summary>
/// Feature flags client. Source of truth is the backend API configuration.
/// </summary>
public class FeatureFlagsService(HttpClient http)
{
    /// <summary>
    /// Privacy Policy feature flag. Defaults to true until API response is received.
    /// </summary>
    public bool PrivacyPolicy { get; private set; } = true;

    public async Task LoadAsync()
    {
        try
        {
            var dto = await http.GetFromJsonAsync<FeaturesDto>("api/features");
            if (dto is not null)
                PrivacyPolicy = dto.PrivacyPolicy;
        }
        catch
        {
            // Keep default value on error
        }
    }
}

public record FeaturesDto(bool PrivacyPolicy);