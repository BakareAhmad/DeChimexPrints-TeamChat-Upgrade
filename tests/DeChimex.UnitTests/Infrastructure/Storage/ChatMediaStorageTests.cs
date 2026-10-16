using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using DeChimex.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Infrastructure.Storage;

public sealed class ChatMediaStorageTests
{
    [Theory]
    [InlineData(FileCategory.ChatMedia)]
    [InlineData(FileCategory.TeamChatMedia)]
    public async Task Audio_webm_is_not_sent_through_the_video_encoder(FileCategory category)
    {
        var root = Path.Combine(Path.GetTempPath(), "dechimex-chat-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var encoder = Substitute.For<IVideoEncodeService>();
        var storage = new LocalFileStorageService(Options.Create(new StorageOptions
        {
            LocalRootPath = Path.Combine(root, "public"),
            PrivateRootPath = Path.Combine(root, "private"),
            MaxFileSizeBytes = 1024
        }), encoder);
        var bytes = new byte[] { 1, 2, 3, 4 };

        try
        {
            using var stream = new MemoryStream(bytes);
            var uploaded = await storage.UploadAsync(new FileUploadRequest(
                stream, "voice-note.webm", "audio/webm", bytes.Length, category));

            var path = Path.Combine(root, "private", uploaded.StoredFileName);
            File.ReadAllBytes(path).Should().Equal(bytes);
            await encoder.DidNotReceive().MakeBrowserPlayableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await storage.DeleteAsync(uploaded.StoredFileName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Video_webm_remains_eligible_for_existing_browser_playability_processing()
    {
        var root = Path.Combine(Path.GetTempPath(), "dechimex-chat-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var encoder = Substitute.For<IVideoEncodeService>();
        encoder.MakeBrowserPlayableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var storage = new LocalFileStorageService(Options.Create(new StorageOptions
        {
            LocalRootPath = Path.Combine(root, "public"),
            PrivateRootPath = Path.Combine(root, "private"),
            MaxFileSizeBytes = 1024
        }), encoder);

        try
        {
            using var stream = new MemoryStream([1, 2, 3, 4]);
            var uploaded = await storage.UploadAsync(new FileUploadRequest(
                stream, "clip.webm", "video/webm", 4, FileCategory.ChatMedia));

            await encoder.Received(1).MakeBrowserPlayableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await storage.DeleteAsync(uploaded.StoredFileName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
