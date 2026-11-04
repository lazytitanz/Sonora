using System.Buffers.Binary;
using System.Text;
using ManagedCodecs;
using ManagedCodecs.Containers;

// Remux progressive AAC M4A → fragmented MP4 (ftyp iso6/dash + moov/mvex + moof/mdat).
// Produces a redistributable CI fixture from Sonora's committed tone-1s.m4a.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: GenerateFmp4Fixture <input.m4a> <output-fmp4.m4a>");
    return 1;
}

var input = args[0];
var output = args[1];

using var demuxer = Demuxer.Open(input);
var audio = demuxer.Audio ?? throw new InvalidOperationException("No audio");
var raw = new List<(long Dts, int Duration, byte[] Data)>();
foreach (var packet in demuxer.ReadPackets())
{
    if (packet.StreamIndex != audio.StreamIndex)
    {
        continue;
    }

    raw.Add((packet.Dts, packet.Duration > 0 ? (int)packet.Duration : 1024, packet.Data.ToArray()));
}

if (raw.Count == 0)
{
    throw new InvalidOperationException("No AAC packets");
}

// AAC encoder delay often yields a negative first DTS; shift so tfdt stays non-negative.
var dtsShift = raw.Min(p => p.Dts) < 0 ? -raw.Min(p => p.Dts) : 0L;
var packets = raw.Select(p => (Dts: p.Dts + dtsShift, p.Duration, p.Data)).ToList();

var timescale = audio.TimeBase is { Numerator: 1, Denominator: > 0 }
    ? audio.TimeBase.Denominator
    : audio.SampleRate;
var durationTicks = packets.Sum(p => (long)p.Duration);
var asc = audio.CodecPrivate.ToArray();

await using var fs = File.Create(output);
var w = new Mp4Writer(fs);

w.BeginBox("ftyp");
w.WriteFourCc("iso6");
w.WriteUInt32(0);
w.WriteFourCc("iso6");
w.WriteFourCc("dash");
w.WriteFourCc("mp41");
w.EndBox();

w.BeginBox("moov");
WriteMvhd(w, timescale, durationTicks);
w.BeginBox("trak");
WriteTkhd(w, durationTicks);
w.BeginBox("mdia");
WriteMdhd(w, timescale, durationTicks);
WriteHdlr(w);
w.BeginBox("minf");
WriteSmhd(w);
WriteDinf(w);
w.BeginBox("stbl");
WriteStsd(w, asc, audio.Channels, audio.SampleRate);
WriteEmptyFullBox(w, "stts");
WriteEmptyFullBox(w, "stsc");
WriteStszEmpty(w);
WriteEmptyFullBox(w, "stco");
w.EndBox();
w.EndBox();
w.EndBox();
w.EndBox();
w.BeginBox("mvex");
WriteTrex(w);
w.EndBox();
w.EndBox();

const int packetsPerFragment = 8;
uint seq = 1;
for (var index = 0; index < packets.Count; index += packetsPerFragment, seq++)
{
    var take = Math.Min(packetsPerFragment, packets.Count - index);
    var slice = packets.GetRange(index, take);
    var baseDts = slice[0].Dts;

    // Exact sizes (box header = 8 bytes each):
    // mfhd: 8+8=16; tfhd: 8+8=16; tfdt v1: 8+12=20;
    // trun: 8 + 4(ver) + 4(count) + 4(offset) + 8*N
    var trunSize = 8 + 12 + take * 8;
    var trafSize = 8 + 16 + 20 + trunSize;
    var moofSize = 8 + 16 + trafSize;
    var dataOffset = moofSize + 8; // first media byte after mdat header

    w.BeginBox("moof");
    w.BeginBox("mfhd");
    w.WriteUInt32(0);
    w.WriteUInt32(seq);
    w.EndBox();
    w.BeginBox("traf");
    w.BeginBox("tfhd");
    w.WriteUInt32(0x00020000); // default-base-is-moof
    w.WriteUInt32(1);
    w.EndBox();
    w.BeginBox("tfdt");
    w.WriteUInt32(0x01000000); // version 1
    w.WriteUInt64((ulong)baseDts);
    w.EndBox();
    w.BeginBox("trun");
    w.WriteUInt32(0x00000301); // data-offset | sample-duration | sample-size
    w.WriteUInt32((uint)take);
    w.WriteInt32(dataOffset);
    foreach (var p in slice)
    {
        w.WriteUInt32((uint)p.Duration);
        w.WriteUInt32((uint)p.Data.Length);
    }

    w.EndBox();
    w.EndBox();
    w.EndBox();

    w.BeginBox("mdat");
    foreach (var p in slice)
    {
        w.WriteBytes(p.Data);
    }

    w.EndBox();
}

Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length} bytes, {packets.Count} packets, {seq - 1} fragments)");
return 0;

static void WriteMvhd(Mp4Writer w, int timescale, long duration)
{
    w.BeginBox("mvhd");
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32((uint)timescale);
    w.WriteUInt32((uint)Math.Min(uint.MaxValue, duration));
    w.WriteUInt32(0x00010000);
    w.WriteUInt16(0x0100);
    w.WriteUInt16(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    WriteUnityMatrix(w);
    for (var i = 0; i < 6; i++)
    {
        w.WriteUInt32(0);
    }

    w.WriteUInt32(2);
    w.EndBox();
}

static void WriteTkhd(Mp4Writer w, long duration)
{
    w.BeginBox("tkhd");
    w.WriteUInt32(0x00000003);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(1);
    w.WriteUInt32(0);
    w.WriteUInt32((uint)Math.Min(uint.MaxValue, duration));
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt16(0);
    w.WriteUInt16(0);
    w.WriteUInt16(0);
    w.WriteUInt16(0);
    WriteUnityMatrix(w);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.EndBox();
}

static void WriteMdhd(Mp4Writer w, int timescale, long duration)
{
    w.BeginBox("mdhd");
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32((uint)timescale);
    w.WriteUInt32((uint)Math.Min(uint.MaxValue, duration));
    w.WriteUInt16(0x55C4);
    w.WriteUInt16(0);
    w.EndBox();
}

static void WriteHdlr(Mp4Writer w)
{
    w.BeginBox("hdlr");
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteFourCc("soun");
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteBytes("SoundHandler\0"u8);
    w.EndBox();
}

static void WriteSmhd(Mp4Writer w)
{
    w.BeginBox("smhd");
    w.WriteUInt32(0);
    w.WriteUInt16(0);
    w.WriteUInt16(0);
    w.EndBox();
}

static void WriteDinf(Mp4Writer w)
{
    w.BeginBox("dinf");
    w.BeginBox("dref");
    w.WriteUInt32(0);
    w.WriteUInt32(1);
    w.BeginBox("url ");
    w.WriteUInt32(0x00000001);
    w.EndBox();
    w.EndBox();
    w.EndBox();
}

static void WriteStsd(Mp4Writer w, byte[] asc, int channels, int sampleRate)
{
    w.BeginBox("stsd");
    w.WriteUInt32(0);
    w.WriteUInt32(1);
    w.BeginBox("mp4a");
    w.WriteBytes(new byte[6]);
    w.WriteUInt16(1);
    w.WriteUInt16(0);
    w.WriteUInt16(0);
    w.WriteUInt32(0);
    w.WriteUInt16((ushort)channels);
    w.WriteUInt16(16);
    w.WriteUInt16(0);
    w.WriteUInt16(0);
    w.WriteUInt32((uint)sampleRate << 16);
    w.BeginBox("esds");
    w.WriteUInt32(0);
    WriteDescr(w, 0x03, es =>
    {
        es.WriteUInt16(1);
        es.WriteByte(0);
        WriteDescr(es, 0x04, dec =>
        {
            dec.WriteByte(0x40);
            dec.WriteByte(0x15);
            dec.WriteByte(0);
            dec.WriteByte(0);
            dec.WriteByte(0);
            dec.WriteUInt32(128000);
            dec.WriteUInt32(128000);
            WriteDescr(dec, 0x05, cfg => cfg.WriteBytes(asc));
        });
        WriteDescr(es, 0x06, sl => sl.WriteByte(0x02));
    });
    w.EndBox();
    w.EndBox();
    w.EndBox();
}

static void WriteDescr(Mp4Writer w, byte tag, Action<Mp4Writer> body)
{
    using var ms = new MemoryStream();
    var inner = new Mp4Writer(ms);
    body(inner);
    var payload = ms.ToArray();
    w.WriteByte(tag);
    if (payload.Length < 128)
    {
        w.WriteByte((byte)payload.Length);
    }
    else
    {
        w.WriteByte(0x81);
        w.WriteByte((byte)(payload.Length & 0x7F));
    }

    w.WriteBytes(payload);
}

static void WriteEmptyFullBox(Mp4Writer w, string fourCc)
{
    w.BeginBox(fourCc);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.EndBox();
}

static void WriteStszEmpty(Mp4Writer w)
{
    w.BeginBox("stsz");
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.EndBox();
}

static void WriteTrex(Mp4Writer w)
{
    w.BeginBox("trex");
    w.WriteUInt32(0);
    w.WriteUInt32(1);
    w.WriteUInt32(1);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.EndBox();
}

static void WriteUnityMatrix(Mp4Writer w)
{
    w.WriteUInt32(0x00010000);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0x00010000);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0);
    w.WriteUInt32(0x40000000);
}

sealed class Mp4Writer
{
    private readonly Stream _stream;
    private readonly Stack<long> _starts = new();

    public Mp4Writer(Stream stream) => _stream = stream;

    public void BeginBox(string fourCc)
    {
        _starts.Push(_stream.Position);
        WriteUInt32(0);
        WriteFourCc(fourCc);
    }

    public void EndBox()
    {
        var start = _starts.Pop();
        var end = _stream.Position;
        var here = end;
        _stream.Position = start;
        WriteUInt32((uint)(end - start));
        _stream.Position = here;
    }

    public void WriteFourCc(string fourCc)
        => WriteBytes(Encoding.ASCII.GetBytes(fourCc));

    public void WriteBytes(ReadOnlySpan<byte> data) => _stream.Write(data);

    public void WriteByte(byte value) => _stream.WriteByte(value);

    public void WriteUInt16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteUInt32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        _stream.Write(b);
    }
}
