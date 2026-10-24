using System.Net.Http.Json;

namespace Frontend.Shared.Services;

/// <summary>
/// Feature flags client. Source of truth is the backend API configuration.
/// </summary>
public class FeatureFlagsService(IHttpClientFactory httpClientFactory)
{
    private HttpClient Http => httpClientFactory.CreateClient("ApiRaw");

    /// <summary>
    /// Privacy Policy feature flag. Значение приходит с API — источник истины там.
    /// Фолбэк до ответа и при ошибке — true: если сервер включил фичу, а ответ не дошёл,
    /// форма согласия всё равно нужна (сервер иначе отклонит регистрацию).
    /// </summary>
    public bool PrivacyPolicy { get; private set; } = true;

    public async Task LoadAsync()
    {
        try
        {
            var dto = await Http.GetFromJsonAsync<FeaturesDto>("api/features");
            if (dto is not null)
                PrivacyPolicy = dto.PrivacyPolicy;
        }
        catch
        {
            // Оставляем значение по умолчанию — см. поле выше.
        }
    }
}

public record FeaturesDto(bool PrivacyPolicy);