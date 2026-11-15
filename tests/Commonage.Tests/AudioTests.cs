using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>The copy of a recording that visitors hear has none of the tags, and the sound is the original's, byte for byte.</summary>
public class AudioToolsTests
{
    private static readonly byte[] Secret = "SECRET-NAME-OF-THE-INTERVIEWEE"u8.ToArray();
    private static readonly byte[] Sound = Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 3)).ToArray();

    private static byte[] Stripped(string contentType, byte[] original)
    {
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        AudioTools.Strip(contentType, input, output);
        return output.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    private static byte[] Box(string type, params byte[][] content)
    {
        var body = content.SelectMany(c => c).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Mp3(bool tags)
    {
        var frame = new byte[] { 0xFF, 0xFB, 0x90, 0x00 }.Concat(Sound).ToArray();
        if (!tags)
            return frame;
        var tag = Secret.Concat(new byte[50]).ToArray();
        var header = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, (byte)(tag.Length >> 7), (byte)(tag.Length & 0x7F) };
        var v1 = new byte[128];
        "TAG"u8.CopyTo(v1);
        Secret.CopyTo(v1, 3);
        return header.Concat(tag).Concat(frame).Concat(v1).ToArray();
    }

    [Fact]
    public void An_MP3_loses_its_tags_at_both_ends_and_keeps_the_sound()
    {
        var original = Mp3(tags: true);
        Assert.Equal("audio/mpeg", AudioTools.DetectContentType(original));

        var copy = Stripped(AudioTools.Mp3, original);

        Assert.Equal(-1, IndexOf(copy, Secret));
        Assert.Equal(Mp3(tags: false), copy);
    }

    [Theory]
    [InlineData("LYRICSBEGIN")]
    [InlineData("TAG+")]
    [InlineData("3DI\u0004\u0000")]
    public void An_MP3_with_a_tag_kind_that_cannot_be_removed_is_refused(string marker)
    {
        var withTag = Mp3(tags: false).Concat(Encoding.ASCII.GetBytes(marker)).Concat(Secret).ToArray();
        Assert.Throws<InvalidDataException>(() => Stripped(AudioTools.Mp3, withTag));
    }

    [Fact]
    public void An_M4A_loses_user_data_and_dates_but_every_part_keeps_its_place()
    {
        var mvhd = Box("mvhd", [0, 0, 0, 0], [0xAA, 0xAA, 0xAA, 0xAA], [0xBB, 0xBB, 0xBB, 0xBB], new byte[80]);
        var udta = Box("udta", Box("meta", [0, 0, 0, 0], Secret));
        var ftyp = Box("ftyp", "M4A "u8.ToArray(), new byte[4], "M4A "u8.ToArray());
        var moov = Box("moov", mvhd, Box("trak", Box("udta", Secret)), udta);
        var xmp = Box("uuid", new byte[16], Secret);
        var mdat = Box("mdat", Sound);
        var original = ftyp.Concat(moov).Concat(xmp).Concat(mdat).ToArray();
        Assert.Equal("audio/mp4", AudioTools.DetectContentType(original));

        var copy = Stripped(AudioTools.Mp4, original);

        Assert.Equal(original.Length, copy.Length);
        Assert.Equal(-1, IndexOf(copy, Secret));
        Assert.Equal(-1, IndexOf(copy, [0xAA, 0xAA, 0xAA, 0xAA]));
        // The sound is where the file's own tables say it is.
        Assert.Equal(IndexOf(original, Sound), IndexOf(copy, Sound));
    }

    [Fact]
    public void A_WAV_keeps_only_what_a_player_needs()
    {
        static byte[] Chunk(string id, byte[] data)
        {
            var chunk = new byte[8 + data.Length + (data.Length & 1)];
            Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
            data.CopyTo(chunk, 8);
            return chunk;
        }
        var body = "WAVE"u8.ToArray().Concat(Chunk("fmt ", new byte[16])).Concat(Chunk("LIST", Secret)).Concat(Chunk("bext", Secret))
            .Concat(Chunk("data", Sound[..201])).ToArray();
        var original = "RIFF"u8.ToArray().Concat(BitConverter.GetBytes(body.Length)).Concat(body).ToArray();
        Assert.Equal("audio/wav", AudioTools.DetectContentType(original));

        var copy = Stripped(AudioTools.Wav, original);

        Assert.Equal(-1, IndexOf(copy, Secret));
        Assert.Equal(-1, IndexOf(copy, "LIST"u8.ToArray()));
        Assert.NotEqual(-1, IndexOf(copy, Sound[..201]));
        Assert.Equal(copy.Length - 8, BitConverter.ToInt32(copy, 4));
    }

    [Fact]
    public void A_FLAC_loses_comments_and_pictures_and_still_ends_its_description()
    {
        static byte[] Block(int type, bool last, byte[] data) =>
            new byte[] { (byte)(type | (last ? 0x80 : 0)), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }.Concat(data).ToArray();
        var original = "fLaC"u8.ToArray().Concat(Block(0, false, new byte[34])).Concat(Block(4, false, Secret)).Concat(Block(6, true, Secret)).Concat(Sound).ToArray();
        Assert.Equal("audio/flac", AudioTools.DetectContentType(original));

        var copy = Stripped(AudioTools.Flac, original);

        Assert.Equal(-1, IndexOf(copy, Secret));
        Assert.Equal(0x80, copy[4]);
        Assert.Equal(4 + 4 + 34 + Sound.Length, copy.Length);
    }

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("audio/mp4")]
    [InlineData("audio/wav")]
    [InlineData("audio/flac")]
    public void A_file_that_is_not_built_as_its_type_says_is_refused_not_passed_on(string contentType)
    {
        var junk = Secret.Concat(new byte[40]).ToArray();
        Assert.Throws<InvalidDataException>(() => Stripped(contentType, junk));
        // Cut short in the middle of its parts.
        var short_ = contentType == AudioTools.Flac ? "fLaC"u8.ToArray().Concat(new byte[] { 0, 0, 1, 0 }).ToArray()
            : contentType == AudioTools.Wav ? "RIFF\0\0\0\0WAVEfmt \xFF\0\0\0"u8.ToArray()
            : contentType == AudioTools.Mp4 ? new byte[] { 0, 0, 1, 0, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }
            : "ID3\x04\0\0\0\0\x7F\x7F"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => Stripped(contentType, short_));
    }

    [Fact]
    public void A_picture_is_not_taken_for_a_recording()
    {
        Assert.Null(AudioTools.DetectContentType([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }
}

[Collection(nameof(AppCollection))]
public class AudioTests(CommonageAppFactory app)
{
    private async Task<(int ItemId, MediaFile File)> UploadAsync(HttpClient curator, string name, byte[] bytes)
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", name, bytes);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var file = await app.WithDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.ArchiveItemId == item.Id));
            if (file.Status != ProcessingStatus.Pending || DateTime.UtcNow > deadline)
                return (item.Id, file);
            await Task.Delay(100);
        }
    }

    private static byte[] RecordingWithTags()
    {
        var tag = "SECRET-NAME-OF-THE-INTERVIEWEE"u8.ToArray();
        var header = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, (byte)tag.Length };
        var sound = new byte[] { 0xFF, 0xFB, 0x90, 0x00 }.Concat(Enumerable.Range(0, 2000).Select(i => (byte)(i % 251))).ToArray();
        return header.Concat(tag).Concat(sound).ToArray();
    }

    [Fact]
    public async Task Visitors_hear_a_published_recording_without_its_tags_and_never_the_original()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var original = RecordingWithTags();

        var (itemId, file) = await UploadAsync(curator, "interview.mp3", original);

        Assert.Equal(ProcessingStatus.Ready, file.Status);
        Assert.Equal("audio/mpeg", file.ContentType);
        Assert.Equal(original, await curator.GetByteArrayAsync($"/admin/media/{file.Id}/original"));

        using var visitor = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/audio")).StatusCode);
        // A curator can listen to a draft from the admin address.
        Assert.Equal(HttpStatusCode.OK, (await curator.GetAsync($"/admin/media/{file.Id}/audio")).StatusCode);

        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.SingleAsync(i => i.Id == itemId);
            stored.Publish(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });

        var heard = await visitor.GetAsync($"/media/{file.Id}/audio");
        Assert.Equal(HttpStatusCode.OK, heard.StatusCode);
        Assert.Equal("audio/mpeg", heard.Content.Headers.ContentType?.MediaType);
        var bytes = await heard.Content.ReadAsByteArrayAsync();
        Assert.Equal(-1, bytes.AsSpan().IndexOf("SECRET"u8));
        Assert.Equal(original.Length - 10 - 30, bytes.Length);

        // The player jumps about by asking for parts.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{file.Id}/audio") { Headers = { Range = new RangeHeaderValue(100, 199) } };
        var part = await visitor.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(bytes[100..200], await part.Content.ReadAsByteArrayAsync());

        // No pictures exist for a recording, and the original stays with curators.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/preview")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await visitor.GetAsync($"/admin/media/{file.Id}/original")).StatusCode);

        var page = await visitor.GetStringAsync($"/items/{itemId}");
        Assert.Contains($"src=\"/media/{file.Id}/audio\"", page);
        Assert.DoesNotContain($"/media/{file.Id}/thumbnail", page);

        // Back to a draft: the recording is gone again for visitors.
        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.SingleAsync(i => i.Id == itemId);
            stored.Status = ItemStatus.Draft;
            return await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/audio")).StatusCode);
    }

    [Fact]
    public async Task A_recording_that_is_not_built_as_it_says_fails_without_a_copy_for_anyone()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var broken = "ID3\x04\0\0\0\0\0\x05SECRET-not-followed-by-sound"u8.ToArray();

        var (itemId, file) = await UploadAsync(curator, "broken.mp3", broken);

        Assert.Equal(ProcessingStatus.Failed, file.Status);
        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.SingleAsync(i => i.Id == itemId);
            stored.Publish(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        using var visitor = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/audio")).StatusCode);
    }
}
