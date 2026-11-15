using System.Buffers.Binary;

namespace Commonage.Web.Media;

/// <summary>
/// Makes the copy of a recording that visitors may hear. Recordings carry names, places, dates, device serial numbers and
/// cover pictures in tags. These are cut out while the sound itself is copied byte for byte, so nothing is re-encoded and
/// no program outside the app is needed. A file whose structure is not understood is refused, never passed on as it is.
/// </summary>
public static class AudioTools
{
    public const string Mp3 = "audio/mpeg";
    public const string Mp4 = "audio/mp4";
    public const string Wav = "audio/wav";
    public const string Flac = "audio/flac";

    /// <summary>Largest sound header (the "moov" box of an M4A) the app reads into memory.</summary>
    private const int MaxHeaderBytes = 64 * 1024 * 1024;

    public static string? DetectContentType(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("ID3"u8))
            return Mp3;
        // A frame of MP3 data without tags. 0xFFD8 is a JPEG, which is checked before this.
        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) != 0)
            return Mp3;
        if (header.StartsWith("fLaC"u8))
            return Flac;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
            return Wav;
        // Voice recorders and phones. Other brands of this container may hold video, which is not accepted.
        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8)
            && (header[8..12].SequenceEqual("M4A "u8) || header[8..12].SequenceEqual("M4B "u8)))
            return Mp4;
        return null;
    }

    public static bool IsAudio(string contentType) => contentType.StartsWith("audio/", StringComparison.Ordinal);

    /// <summary>Writes the copy without tags. The input must be seekable.</summary>
    /// <exception cref="InvalidDataException">The file is not built the way its type says, so nothing can be said about what it holds.</exception>
    public static void Strip(string contentType, Stream input, Stream output)
    {
        switch (contentType)
        {
            case Mp3: StripMp3(input, output); break;
            case Mp4: StripMp4(input, output); break;
            case Wav: StripWav(input, output); break;
            case Flac: StripFlac(input, output); break;
            default: throw new InvalidDataException("Not a recording this archive can read.");
        }
    }

    // ---- MP3 ------------------------------------------------------------------------------------------------------

    private static void StripMp3(Stream input, Stream output)
    {
        var length = input.Length;
        long start = 0;
        var head = new byte[10];
        // Tags at the start, one after another if there are several.
        while (start + 10 <= length && ReadAt(input, start, head) && head.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            var size = SyncSafe(head.AsSpan(6, 4));
            if (size < 0)
                throw new InvalidDataException("The tag at the start of the recording is damaged.");
            start += 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
        }
        if (start >= length)
            throw new InvalidDataException("The recording holds no sound.");

        var end = length;
        // Tags at the end: ID3v1 (128 bytes), and APEv2 before or without it.
        var tail = new byte[128];
        if (end - start >= 128 && ReadAt(input, end - 128, tail) && tail.AsSpan(0, 3).SequenceEqual("TAG"u8))
            end -= 128;
        var footer = new byte[32];
        if (end - start >= 32 && ReadAt(input, end - 32, footer) && footer.AsSpan(0, 8).SequenceEqual("APETAGEX"u8))
        {
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12, 4));
            var hasHeader = (BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(20, 4)) & 0x80000000) != 0;
            var total = size + (hasHeader ? 32 : 0);
            if (total < 32 || total > end - start)
                throw new InvalidDataException("The tag at the end of the recording is damaged.");
            end -= total;
        }

        // Other tag kinds that sit at the end (a Lyrics3 block, an enhanced "TAG+", an ID3v2 footer) are not
        // understood here, so a file that carries one is refused rather than passed on with its text.
        var window = (int)Math.Min(end - start, 64 * 1024);
        if (window > 0)
        {
            var tailBytes = new byte[window];
            if (ReadAt(input, end - window, tailBytes) && HasForeignTag(tailBytes))
                throw new InvalidDataException("The recording carries a tag that cannot be removed.");
        }

        // The sound has to begin with a frame, or this is not what it says it is.
        var first = new byte[2];
        if (!ReadAt(input, start, first) || first[0] != 0xFF || (first[1] & 0xE0) != 0xE0)
            throw new InvalidDataException("The recording does not begin with MP3 sound.");

        Copy(input, start, end - start, output);
    }

    private static bool HasForeignTag(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf("LYRICSBEGIN"u8) >= 0 || bytes.IndexOf("LYRICS200"u8) >= 0
        || bytes.IndexOf("TAG+"u8) >= 0 || bytes.IndexOf("APETAGEX"u8) >= 0
        || bytes.IndexOf("3DI\u0004\u0000"u8) >= 0 || bytes.IndexOf("3DI\u0003\u0000"u8) >= 0;

    private static int SyncSafe(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        foreach (var b in bytes)
        {
            if ((b & 0x80) != 0)
                return -1;
            value = (value << 7) | b;
        }
        return value;
    }

    // ---- MP4 / M4A --------------------------------------------------------------------------------------------------

    private static void StripMp4(Stream input, Stream output)
    {
        var length = input.Length;
        long position = 0;
        var header = new byte[16];
        var sawFtyp = false;
        var sawMoov = false;
        var sawMdat = false;
        while (position < length)
        {
            if (length - position < 8 || !ReadAt(input, position, header.AsSpan(0, 8)))
                throw new InvalidDataException("The recording ends in the middle of a part.");
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = header.AsSpan(4, 4).ToArray();
            var headerSize = 8;
            if (size == 1)
            {
                if (length - position < 16 || !ReadAt(input, position, header))
                    throw new InvalidDataException("The recording ends in the middle of a part.");
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - position;
            }
            if (size < headerSize || size > length - position)
                throw new InvalidDataException("A part of the recording has an impossible size.");

            var name = System.Text.Encoding.Latin1.GetString(type);
            switch (name)
            {
                case "ftyp":
                    if (position != 0)
                        throw new InvalidDataException("The parts of the recording are in an unexpected order.");
                    sawFtyp = true;
                    Copy(input, position, size, output);
                    break;
                case "mdat":
                    sawMdat = true;
                    Copy(input, position, size, output);
                    break;
                case "moov":
                    if (size > MaxHeaderBytes)
                        throw new InvalidDataException("The description of the recording is too large.");
                    var moov = new byte[size];
                    ReadExactlyAt(input, position, moov);
                    CleanContainer(moov, headerSize, moov.Length, 0);
                    output.Write(moov);
                    sawMoov = true;
                    break;
                case "moof":
                case "mfra":
                case "sidx":
                    // Recordings cut into fragments are not handled. Better refused than served with their tags.
                    throw new InvalidDataException("Recordings made of fragments are not supported.");
                default:
                    // Padding, user data, XMP, anything unknown: the same size, but empty.
                    WriteFree(size, output);
                    break;
            }
            position += size;
        }
        if (!sawFtyp || !sawMoov || !sawMdat)
            throw new InvalidDataException("The recording lacks a part it needs.");
    }

    /// <summary>
    /// Turns what carries tags into empty "free" boxes of the same size, so the positions of the sound inside the file stay what
    /// the file's own tables say, and sets the dates in the headers to zero.
    /// </summary>
    private static void CleanContainer(byte[] buffer, int from, int to, int depth)
    {
        if (depth > 4)
            throw new InvalidDataException("The recording is nested too deeply.");
        var position = from;
        while (position < to)
        {
            if (to - position < 8)
                throw new InvalidDataException("A part of the recording is cut short.");
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(position, 4));
            if (size < 8 || size > to - position)
                throw new InvalidDataException("A part of the recording has an impossible size.");
            var type = System.Text.Encoding.Latin1.GetString(buffer, position + 4, 4);
            var boxEnd = position + (int)size;
            switch (type)
            {
                case "trak":
                case "mdia":
                    CleanContainer(buffer, position + 8, boxEnd, depth + 1);
                    break;
                case "mvhd":
                case "tkhd":
                case "mdhd":
                    ZeroDates(buffer, position + 8, boxEnd);
                    break;
                case "udta":
                case "meta":
                case "uuid":
                case "free":
                case "skip":
                    "free"u8.CopyTo(buffer.AsSpan(position + 4, 4));
                    buffer.AsSpan(position + 8, (int)size - 8).Clear();
                    break;
            }
            position = boxEnd;
        }
    }

    private static void ZeroDates(byte[] buffer, int contentStart, int boxEnd)
    {
        if (boxEnd - contentStart < 4)
            return;
        var wide = buffer[contentStart] == 1;
        var width = wide ? 8 : 4;
        // After version and flags come the time made and the time changed.
        if (boxEnd - contentStart < 4 + 2 * width)
            return;
        buffer.AsSpan(contentStart + 4, 2 * width).Clear();
    }

    private static void WriteFree(long size, Stream output)
    {
        if (size > uint.MaxValue)
            throw new InvalidDataException("A part of the recording is too large to be set aside.");
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
        "free"u8.CopyTo(header[4..]);
        output.Write(header);
        var zeros = new byte[Math.Min(size - 8, 81920)];
        var remaining = size - 8;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, zeros.Length);
            output.Write(zeros, 0, chunk);
            remaining -= chunk;
        }
    }

    // ---- WAV ------------------------------------------------------------------------------------------------------

    private static readonly string[] WavKept = ["fmt ", "fact", "data"];

    private static void StripWav(Stream input, Stream output)
    {
        var length = input.Length;
        long position = 12;
        var kept = new List<(long Start, long Size, string Id)>();
        var chunk = new byte[8];
        while (position + 8 <= length)
        {
            ReadExactlyAt(input, position, chunk);
            var id = System.Text.Encoding.Latin1.GetString(chunk, 0, 4);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4, 4));
            if (position + 8 + size > length)
                throw new InvalidDataException("A part of the recording runs past the end of the file.");
            if (WavKept.Contains(id))
                kept.Add((position + 8, size, id));
            position += 8 + size + (size & 1);
        }
        if (!kept.Any(k => k.Id == "fmt ") || !kept.Any(k => k.Id == "data"))
            throw new InvalidDataException("The recording lacks a part it needs.");

        long body = 4 + kept.Sum(k => 8 + k.Size + (k.Size & 1));
        if (body > uint.MaxValue)
            throw new InvalidDataException("The recording is too large for this format.");
        Span<byte> riff = stackalloc byte[12];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff[4..], (uint)body);
        "WAVE"u8.CopyTo(riff[8..]);
        output.Write(riff);
        foreach (var (start, size, id) in kept)
        {
            var head = new byte[8];
            System.Text.Encoding.Latin1.GetBytes(id, head);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4, 4), (uint)size);
            output.Write(head);
            Copy(input, start, size, output);
            if ((size & 1) != 0)
                output.WriteByte(0);
        }
    }

    // ---- FLAC -----------------------------------------------------------------------------------------------------

    private static void StripFlac(Stream input, Stream output)
    {
        var length = input.Length;
        long position = 4;
        var blocks = new List<(byte Type, long Start, int Size)>();
        var head = new byte[4];
        while (true)
        {
            if (position + 4 > length)
                throw new InvalidDataException("The recording ends in the middle of its description.");
            ReadExactlyAt(input, position, head);
            var type = (byte)(head[0] & 0x7F);
            var last = (head[0] & 0x80) != 0;
            var size = (head[1] << 16) | (head[2] << 8) | head[3];
            if (position + 4 + size > length)
                throw new InvalidDataException("A part of the recording runs past the end of the file.");
            // What the player needs, and the table for jumping about. Names, cover pictures, notes and all else stay out.
            if (type is 0 or 3)
                blocks.Add((type, position + 4, size));
            position += 4 + size;
            if (last)
                break;
        }
        if (blocks.Count == 0 || blocks[0].Type != 0 || blocks[0].Size != 34)
            throw new InvalidDataException("The recording does not begin with its stream information.");

        output.Write("fLaC"u8);
        for (var i = 0; i < blocks.Count; i++)
        {
            var (type, start, size) = blocks[i];
            var b = new byte[4];
            b[0] = (byte)(type | (i == blocks.Count - 1 ? 0x80 : 0));
            b[1] = (byte)(size >> 16);
            b[2] = (byte)(size >> 8);
            b[3] = (byte)size;
            output.Write(b);
            Copy(input, start, size, output);
        }
        Copy(input, position, length - position, output);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static bool ReadAt(Stream input, long position, Span<byte> buffer)
    {
        input.Position = position;
        return input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }

    private static void ReadExactlyAt(Stream input, long position, Span<byte> buffer)
    {
        if (!ReadAt(input, position, buffer))
            throw new InvalidDataException("The recording ends unexpectedly.");
    }

    private static void Copy(Stream input, long start, long count, Stream output)
    {
        input.Position = start;
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
                throw new InvalidDataException("The recording ends unexpectedly.");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }
}
