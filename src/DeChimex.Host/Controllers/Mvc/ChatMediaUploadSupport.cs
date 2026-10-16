using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>Server-validated storage result shared by the two chat composers. Each
/// caller maps this neutral transport shape into its own bounded persistence model.</summary>
internal sealed record UploadedChatAttachment(
    string Kind,
    string OriginalFileName,
    string StoredFileName,
    string Url,
    string ContentType,
    long SizeBytes,
    int? DurationSeconds);

/// <summary>Shared upload mechanics only. Order and internal messages retain separate
/// entities, tables, URLs, authorization rules, and notification lifecycles.</summary>
internal static class ChatMediaUploadSupport
{
    public static async Task<IReadOnlyList<UploadedChatAttachment>> UploadAsync(
        IFileStorageService storage,
        IList<IFormFile>? files,
        IList<string>? kinds,
        IList<string>? durations,
        FileCategory category,
        CancellationToken ct)
    {
        if (category is not (FileCategory.ChatMedia or FileCategory.TeamChatMedia))
            throw new ArgumentOutOfRangeException(nameof(category), "Only private chat media categories may use this upload helper.");
        if (files is null || files.Count == 0) return [];
        if (files.Count > 6)
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["Files"] = ["A message carries at most 6 attachments."]
            });

        var result = new List<UploadedChatAttachment>(files.Count);
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file.Length <= 0) continue;

                var declared = kinds is not null && i < kinds.Count ? kinds[i]?.Trim().ToLowerInvariant() : null;
                var (kind, contentType) = Classify(file.FileName, file.ContentType, declared);
                int? duration = null;
                if (kind is "audio" or "voice" && durations is not null && i < durations.Count &&
                    int.TryParse(durations[i], out var seconds) && seconds is >= 1 and <= 3600)
                    duration = seconds;

                using var stream = file.OpenReadStream();
                var uploaded = await storage.UploadAsync(new FileUploadRequest(
                    stream, file.FileName, contentType, file.Length, category), ct);
                result.Add(new UploadedChatAttachment(kind, file.FileName, uploaded.StoredFileName,
                    uploaded.Url, contentType, uploaded.Size, duration));
            }
            return result;
        }
        catch
        {
            await DeleteBestEffortAsync(storage, result, ct);
            throw;
        }
    }

    public static async Task DeleteBestEffortAsync(
        IFileStorageService storage, IEnumerable<UploadedChatAttachment> attachments, CancellationToken ct)
    {
        foreach (var attachment in attachments)
        {
            try { await storage.DeleteAsync(attachment.StoredFileName, ct); }
            catch { /* only clean-up after a failed database save; original failure wins */ }
        }
    }

    /// <summary>The allow-listed extension, not the user-supplied MIME header, selects
    /// the persisted render kind. WebM is shared by video and recorded audio, so its
    /// MIME is used only to distinguish those two already-allowed container uses.</summary>
    private static (string Kind, string ContentType) Classify(string fileName, string? claimedContentType, string? declared)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var claimed = claimedContentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
        return extension switch
        {
            ".jpg" or ".jpeg" => ("image", "image/jpeg"),
            ".png" => ("image", "image/png"),
            ".webp" => ("image", "image/webp"),
            ".gif" => ("image", "image/gif"),
            ".mp4" => ("video", "video/mp4"),
            ".webm" when claimed.StartsWith("audio/", StringComparison.Ordinal) =>
                (declared == "voice" ? "voice" : "audio", "audio/webm"),
            ".webm" => ("video", "video/webm"),
            ".mp3" => (declared == "voice" ? "voice" : "audio", "audio/mpeg"),
            ".ogg" or ".oga" => (declared == "voice" ? "voice" : "audio", "audio/ogg"),
            ".wav" => (declared == "voice" ? "voice" : "audio", "audio/wav"),
            ".m4a" => (declared == "voice" ? "voice" : "audio", "audio/mp4"),
            ".pdf" => ("file", "application/pdf"),
            ".doc" => ("file", "application/msword"),
            ".docx" => ("file", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            _ => ("file", "application/octet-stream")
        };
    }
}
