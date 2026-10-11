namespace FunAndChecks.Application.Profiles;

public interface IProfileService
{
    Task<UserProfileDto> GetAsync(Guid viewerId, Guid userId, CancellationToken ct = default);
    Task<UserProfileDto> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default);
    Task<MediaAssetDto> SetAvatarAsync(Guid userId, UpdateAvatarRequest request, CancellationToken ct = default);
    Task RemoveAvatarAsync(Guid userId, CancellationToken ct = default);
    Task<MediaFileDto> GetMediaAsync(Guid mediaId, bool poster, CancellationToken ct = default);
}
