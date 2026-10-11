using FluentValidation;
using FunAndChecks.Application.Common.Exceptions;
using FunAndChecks.Application.Common.Interfaces;
using FunAndChecks.Domain.Constants;
using FunAndChecks.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FunAndChecks.Application.Profiles;

public class ProfileService(IApplicationDbContext db, IIdentityService identity, IProfileMediaProcessor processor,
    IValidator<UpdateProfileRequest> validator) : IProfileService
{
    public async Task<UserProfileDto> GetAsync(Guid viewerId, Guid userId, CancellationToken ct = default)
    {
        var account = await RequireAccountAsync(userId, ct);
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var avatar = await MediaMetadata(db.ProfileMediaAssets.Where(a => a.UserProfileId == userId)).SingleOrDefaultAsync(ct);
        var awards = await db.AchievementAwards.AsNoTracking().Include(a => a.Achievement)
            .Where(a => a.UserId == userId && a.RevokedAtUtc == null)
            .OrderByDescending(a => a.GrantedAtUtc).ThenByDescending(a => a.Id).ToListAsync(ct);
        var achievementIds = awards.Select(a => a.AchievementId).Distinct().ToArray();
        var images = await db.ProfileMediaAssets.AsNoTracking().Where(a => a.AchievementDefinitionId.HasValue &&
                achievementIds.Contains(a.AchievementDefinitionId.Value))
            .Select(a => new { AchievementId = a.AchievementDefinitionId!.Value,
                Image = new MediaAssetDto(a.Id, a.ContentType, a.Width, a.Height, a.DurationSeconds, a.IsAnimated, a.PosterData.Length > 0) })
            .ToDictionaryAsync(a => a.AchievementId, a => a.Image, ct);
        var pins = await db.PinnedAchievements.AsNoTracking().Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.AchievementAwardId, p => p.Position, ct);
        var issuers = await identity.GetProfileAccountsAsync(awards.Where(a => a.IssuedByUserId.HasValue)
            .Select(a => a.IssuedByUserId!.Value), ct);
        var roles = await identity.GetCurrentRolesAsync(viewerId, ct);
        var canManage = roles.Contains(Roles.Admin) || roles.Contains(Roles.SuperAdmin);
        var isSuper = roles.Contains(Roles.SuperAdmin);
        return new(userId, account.FullName, account.GroupName, account.IsAdmin,
            profile?.AboutMe ?? "", profile?.GitHubUrl, profile?.PortfolioUrl, avatar,
            awards.Select(a => new AchievementAwardDto(a.Id, a.AchievementId, a.Achievement.Name,
                a.Achievement.Description, images.GetValueOrDefault(a.AchievementId), a.GrantedAtUtc,
                a.IssuedByUserId, a.IssuedByUserId.HasValue && issuers.TryGetValue(a.IssuedByUserId.Value, out var issuer)
                    ? issuer.FullName : a.IssuedByName, pins.TryGetValue(a.Id, out var position) ? position : null,
                canManage && (isSuper || a.IssuedByUserId == viewerId))).ToList());
    }

    public async Task<UserProfileDto> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await db.ExecuteSerializableAsync(async token =>
        {
            await RequireAccountAsync(userId, token);
            var ids = request.PinnedAwardIds?.ToArray() ?? [];
            if (await db.AchievementAwards.CountAsync(a => ids.Contains(a.Id) && a.UserId == userId && a.RevokedAtUtc == null, token) != ids.Length)
                throw new ConflictException("Only your current awards can be pinned.", "profile.pins_invalid");
            var profile = await EnsureProfileAsync(userId, token);
            profile.AboutMe = request.AboutMe?.Trim() ?? "";
            profile.GitHubUrl = CleanUrl(request.GitHubUrl);
            profile.PortfolioUrl = CleanUrl(request.PortfolioUrl);
            profile.UpdatedAtUtc = DateTime.UtcNow;
            var previous = await db.PinnedAchievements.Where(p => p.UserId == userId).ToListAsync(token);
            db.PinnedAchievements.RemoveRange(previous);
            // Save removal before inserting positions, so reorderings cannot collide with old keys.
            await db.SaveChangesAsync(token);
            db.PinnedAchievements.AddRange(ids.Select((id, index) => new PinnedAchievement
                { UserId = userId, AchievementAwardId = id, Position = index + 1 }));
            await db.SaveChangesAsync(token);
        }, ct);
        return await GetAsync(userId, userId, ct);
    }

    public async Task<MediaAssetDto> SetAvatarAsync(Guid userId, UpdateAvatarRequest request, CancellationToken ct = default)
    {
        await RequireAccountAsync(userId, ct);
        if (request.MediaSessionId == Guid.Empty || request.Media is null)
            throw new ValidationException("Choose a media session and its edit settings.");
        await using var lease = await processor.RenderAsync(userId, request.MediaSessionId, request.Media, 256, ct);
        var assetId = Guid.NewGuid();
        await db.ExecuteSerializableAsync(async token =>
        {
            await RequireAccountAsync(userId, token);
            await EnsureProfileAsync(userId, token);
            var previous = await db.ProfileMediaAssets.Where(a => a.UserProfileId == userId).ToListAsync(token);
            db.ProfileMediaAssets.RemoveRange(previous);
            await db.SaveChangesAsync(token);
            var asset = FromProcessed(assetId, lease.Media);
            asset.UserProfileId = userId;
            db.ProfileMediaAssets.Add(asset);
            await db.SaveChangesAsync(token);
        }, ct);
        await lease.CommitAsync(ct);
        return Metadata(assetId, lease.Media);
    }

    public async Task RemoveAvatarAsync(Guid userId, CancellationToken ct = default)
    {
        await RequireAccountAsync(userId, ct);
        var assets = await db.ProfileMediaAssets.Where(a => a.UserProfileId == userId).ToListAsync(ct);
        db.ProfileMediaAssets.RemoveRange(assets);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MediaFileDto> GetMediaAsync(Guid mediaId, bool poster, CancellationToken ct = default)
    {
        var file = await db.ProfileMediaAssets.AsNoTracking().Where(a => a.Id == mediaId)
            .Select(a => new MediaFileDto(poster ? a.PosterData : a.Data, poster ? a.PosterContentType : a.ContentType))
            .SingleOrDefaultAsync(ct);
        return file ?? throw new NotFoundException("Media not found.", "media.not_found");
    }

    private async Task<ProfileAccountInfo> RequireAccountAsync(Guid userId, CancellationToken ct) =>
        await identity.GetProfileAccountAsync(userId, ct) ?? throw new NotFoundException("Profile not found.", "profile.not_found");

    private async Task<UserProfile> EnsureProfileAsync(Guid userId, CancellationToken ct)
    {
        var profile = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is not null) return profile;
        profile = new UserProfile { UserId = userId, UpdatedAtUtc = DateTime.UtcNow };
        db.UserProfiles.Add(profile);
        return profile;
    }

    private static string? CleanUrl(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static IQueryable<MediaAssetDto> MediaMetadata(IQueryable<ProfileMediaAsset> assets) => assets.AsNoTracking()
        .Select(a => new MediaAssetDto(a.Id, a.ContentType, a.Width, a.Height, a.DurationSeconds, a.IsAnimated, a.PosterData.Length > 0));

    internal static MediaAssetDto Metadata(Guid id, ProcessedProfileMedia media) =>
        new(id, media.ContentType, media.Width, media.Height, media.DurationSeconds, media.IsAnimated, media.PosterData.Length > 0);

    internal static ProfileMediaAsset FromProcessed(Guid id, ProcessedProfileMedia media) => new()
    {
        Id = id, Data = media.Data, ContentType = media.ContentType,
        PosterData = media.PosterData, PosterContentType = media.PosterContentType,
        Width = media.Width, Height = media.Height, DurationSeconds = media.DurationSeconds, IsAnimated = media.IsAnimated
    };
}
