using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

/// <summary>
/// Ripara gli MP4 con video HEVC la cui configurazione del codec (box "hvcC") è vuota: capita con
/// alcuni file originali di Maxstream. I parametri del codec (VPS/SPS/PPS) ci sono, ma solo dentro
/// il primo fotogramma, e così VLC, il lettore di Windows e ffmpeg rifiutano il file.
///
/// La riparazione non riscrive il video: costruisce l'hvcC da quei parametri, trasforma il vecchio
/// indice ("moov") in spazio libero e accoda in fondo l'indice corretto. Gli offset dei dati restano
/// identici, quindi l'operazione è veloce anche su file da diversi GB.
/// </summary>
public static class HevcConfigRepair
{
    private const int NalVps = 32, NalSps = 33, NalPps = 34;

    /// <summary>True se il file è stato riparato; false se non serviva o non era possibile (file intatto).</summary>
    public static bool TryRepair(string path, ILogger? logger = null)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var (moovPos, moovSize, moovHeader) = FindTopLevel(fs, "moov");
            if (moovPos < 0 || moovSize > 512L * 1024 * 1024) return false;

            var moov = new byte[moovSize];
            fs.Position = moovPos;
            fs.ReadExactly(moov);

            var fixes = new List<(int HvcCPos, byte[] NewBox)>();
            foreach (var trak in Children(moov, moovHeader, moov.Length).Where(b => b.Type == "trak"))
            {
                var stbl = Path(moov, trak, "mdia", "minf", "stbl");
                if (stbl == null) continue;
                var stsd = Child(moov, stbl.Value, "stsd");
                if (stsd == null) continue;

                // stsd: versione/flag (4) + numero voci (4), poi le voci
                foreach (var entry in Children(moov, stsd.Value.Body + 8, stsd.Value.End))
                {
                    if (entry.Type is not ("hvc1" or "hev1")) continue;
                    // VisualSampleEntry: 78 byte fissi prima dei box figli
                    var hvcC = Children(moov, entry.Body + 78, entry.End).FirstOrDefault(b => b.Type == "hvcC");
                    if (hvcC.Type == null || hvcC.End - hvcC.Body >= 23) continue;   // configurazione già presente

                    var nals = FirstSampleNals(fs, moov, stbl.Value);
                    if (nals == null) { logger?.LogWarning("[HEVC] hvcC vuoto ma parametri non trovati in {File}", path); continue; }

                    fixes.Add((hvcC.Start, BuildHvcCBox(nals.Value.Vps, nals.Value.Sps, nals.Value.Pps)));
                }
            }
            if (fixes.Count == 0) return false;

            var newMoov = Rebuild(moov, fixes);

            // Il vecchio indice diventa "free" (resta al suo posto), il nuovo va in fondo al file
            fs.Position = moovPos + 4;
            fs.Write(Encoding.ASCII.GetBytes("free"));
            fs.Position = fs.Length;
            fs.Write(newMoov);
            fs.Flush(true);

            logger?.LogInformation("[HEVC] {File}: configurazione del video ricostruita ({N} tracce)", System.IO.Path.GetFileName(path), fixes.Count);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[HEVC] Riparazione non riuscita per {File}", path);
            return false;
        }
    }

    // ------------------------------------------------------------------ box MP4

    private readonly record struct Box(string Type, int Start, int Body, int End);

    private static IEnumerable<Box> Children(byte[] buf, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p));
            var type = Encoding.ASCII.GetString(buf, p + 4, 4);
            int header = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(p + 8)); header = 16; }
            else if (size == 0) size = end - p;
            if (size < header || p + size > end) yield break;
            yield return new Box(type, p, p + header, (int)(p + size));
            p += (int)size;
        }
    }

    private static Box? Child(byte[] buf, Box parent, string type)
    {
        foreach (var b in Children(buf, parent.Body, parent.End)) if (b.Type == type) return b;
        return null;
    }

    private static Box? Path(byte[] buf, Box from, params string[] types)
    {
        Box? cur = from;
        foreach (var t in types) { cur = Child(buf, cur.Value, t); if (cur == null) return null; }
        return cur;
    }

    private static (long Pos, long Size, int Header) FindTopLevel(FileStream fs, string type)
    {
        var hdr = new byte[16];
        long p = 0;
        while (p + 8 <= fs.Length)
        {
            fs.Position = p;
            fs.ReadExactly(hdr, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var t = Encoding.ASCII.GetString(hdr, 4, 4);
            int header = 8;
            if (size == 1) { fs.ReadExactly(hdr, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8)); header = 16; }
            else if (size == 0) size = fs.Length - p;
            if (size < header) break;
            if (t == type) return (p, size, header);
            p += size;
        }
        return (-1, 0, 0);
    }

    /// <summary>VPS, SPS e PPS letti dal primo campione video (NAL con lunghezza a 4 byte).</summary>
    private static (List<byte[]> Vps, List<byte[]> Sps, List<byte[]> Pps)? FirstSampleNals(FileStream fs, byte[] moov, Box stbl)
    {
        long offset;
        var co64 = Child(moov, stbl, "co64");
        var stco = Child(moov, stbl, "stco");
        if (co64 != null) offset = (long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(co64.Value.Body + 8));
        else if (stco != null) offset = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stco.Value.Body + 8));
        else return null;

        var stsz = Child(moov, stbl, "stsz");
        if (stsz == null) return null;
        long sampleSize = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stsz.Value.Body + 4));
        if (sampleSize == 0) sampleSize = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stsz.Value.Body + 12));
        if (sampleSize <= 0 || sampleSize > 64 * 1024 * 1024) return null;

        var sample = new byte[sampleSize];
        fs.Position = offset;
        fs.ReadExactly(sample);

        var vps = new List<byte[]>(); var sps = new List<byte[]>(); var pps = new List<byte[]>();
        int p = 0;
        while (p + 4 < sample.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(sample.AsSpan(p));
            if (len <= 0 || p + 4 + len > sample.Length) break;
            var nal = sample.AsSpan(p + 4, len).ToArray();
            switch ((nal[0] >> 1) & 0x3F)
            {
                case NalVps: vps.Add(nal); break;
                case NalSps: sps.Add(nal); break;
                case NalPps: pps.Add(nal); break;
            }
            p += 4 + len;
        }
        return vps.Count > 0 && sps.Count > 0 && pps.Count > 0 ? (vps, sps, pps) : null;
    }

    // ------------------------------------------------------------------ hvcC

    private static byte[] BuildHvcCBox(List<byte[]> vps, List<byte[]> sps, List<byte[]> pps)
    {
        var info = SpsInfo.Parse(sps[0]);
        var body = new List<byte>
        {
            1,                                                                  // configurationVersion
            (byte)((info.ProfileSpace << 6) | (info.Tier << 5) | info.ProfileIdc)
        };
        body.AddRange(info.Compatibility);                                      // 4 byte
        body.AddRange(info.Constraints);                                        // 6 byte
        body.Add(info.LevelIdc);
        body.Add(0xF0); body.Add(0x00);                                         // min_spatial_segmentation_idc = 0
        body.Add(0xFC);                                                         // parallelismType = 0
        body.Add((byte)(0xFC | (info.ChromaFormat & 3)));
        body.Add((byte)(0xF8 | (info.BitDepthLuma8 & 7)));
        body.Add((byte)(0xF8 | (info.BitDepthChroma8 & 7)));
        body.Add(0); body.Add(0);                                               // avgFrameRate
        body.Add((byte)((info.MaxSubLayers & 7) << 3 | (info.TemporalIdNested ? 1 : 0) << 2 | 3)); // lengthSizeMinusOne = 3
        body.Add(3);                                                            // numOfArrays
        foreach (var (type, list) in new[] { (NalVps, vps), (NalSps, sps), (NalPps, pps) })
        {
            body.Add((byte)(0x80 | type));                                      // array_completeness = 1
            body.Add((byte)(list.Count >> 8)); body.Add((byte)list.Count);
            foreach (var nal in list)
            {
                body.Add((byte)(nal.Length >> 8)); body.Add((byte)nal.Length);
                body.AddRange(nal);
            }
        }

        var box = new byte[8 + body.Count];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes("hvcC").CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    /// <summary>Copia del moov con gli hvcC sostituiti e le dimensioni dei box che li contengono aggiornate.</summary>
    private static byte[] Rebuild(byte[] moov, List<(int HvcCPos, byte[] NewBox)> fixes)
    {
        var result = moov.ToArray();
        foreach (var (pos, newBox) in fixes.OrderByDescending(f => f.HvcCPos))
        {
            int oldSize = (int)BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(pos));
            int delta = newBox.Length - oldSize;

            // Aggiorna la dimensione di ogni box che contiene questa posizione (moov, trak, ..., hvc1)
            void Grow(int start, int end)
            {
                foreach (var b in Children(result, start, end))
                {
                    if (pos < b.Start || pos >= b.End || b.Start == pos) continue;
                    int bodyStart = b.Type switch { "stsd" => b.Body + 8, "hvc1" or "hev1" => b.Body + 78, _ => b.Body };
                    Grow(bodyStart, b.End);
                    uint size = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(b.Start));
                    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(b.Start), (uint)(size + delta));
                    return;
                }
            }
            // il moov stesso
            var top = Children(result, 0, result.Length).First();
            Grow(top.Body, top.End);
            BinaryPrimitives.WriteUInt32BigEndian(result, (uint)(BinaryPrimitives.ReadUInt32BigEndian(result) + delta));

            result = result[..pos].Concat(newBox).Concat(result[(pos + oldSize)..]).ToArray();
        }
        return result;
    }

    /// <summary>I campi dell'SPS che servono all'hvcC.</summary>
    private sealed class SpsInfo
    {
        public int ProfileSpace, Tier, ProfileIdc, MaxSubLayers, ChromaFormat, BitDepthLuma8, BitDepthChroma8;
        public byte LevelIdc;
        public bool TemporalIdNested;
        public byte[] Compatibility = new byte[4], Constraints = new byte[6];

        public static SpsInfo Parse(byte[] nal)
        {
            var rbsp = RemoveEmulation(nal.AsSpan(2));        // salta l'header NAL di 2 byte
            var r = new BitReader(rbsp);
            var s = new SpsInfo();
            r.Read(4);                                       // sps_video_parameter_set_id
            int maxSubLayersMinus1 = (int)r.Read(3);
            s.MaxSubLayers = maxSubLayersMinus1 + 1;
            s.TemporalIdNested = r.Read(1) == 1;

            s.ProfileSpace = (int)r.Read(2);
            s.Tier = (int)r.Read(1);
            s.ProfileIdc = (int)r.Read(5);
            for (int i = 0; i < 4; i++) s.Compatibility[i] = (byte)r.Read(8);
            for (int i = 0; i < 6; i++) s.Constraints[i] = (byte)r.Read(8);
            s.LevelIdc = (byte)r.Read(8);

            var profilePresent = new bool[8]; var levelPresent = new bool[8];
            for (int i = 0; i < maxSubLayersMinus1; i++) { profilePresent[i] = r.Read(1) == 1; levelPresent[i] = r.Read(1) == 1; }
            if (maxSubLayersMinus1 > 0) for (int i = maxSubLayersMinus1; i < 8; i++) r.Read(2);
            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i]) { r.Read(32); r.Read(32); r.Read(24); }   // 88 bit
                if (levelPresent[i]) r.Read(8);
            }

            r.Ue();                                          // sps_seq_parameter_set_id
            s.ChromaFormat = (int)r.Ue();
            if (s.ChromaFormat == 3) r.Read(1);
            r.Ue(); r.Ue();                                  // larghezza, altezza
            if (r.Read(1) == 1) { r.Ue(); r.Ue(); r.Ue(); r.Ue(); }   // conformance window
            s.BitDepthLuma8 = (int)r.Ue();
            s.BitDepthChroma8 = (int)r.Ue();
            return s;
        }

        private static byte[] RemoveEmulation(ReadOnlySpan<byte> data)
        {
            var output = new List<byte>(data.Length);
            int zeros = 0;
            foreach (var b in data)
            {
                if (zeros >= 2 && b == 3) { zeros = 0; continue; }
                output.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }
            return output.ToArray();
        }
    }

    private sealed class BitReader(byte[] data)
    {
        private int _bit;

        public ulong Read(int count)
        {
            ulong v = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = _bit >> 3;
                int bit = byteIndex < data.Length ? (data[byteIndex] >> (7 - (_bit & 7))) & 1 : 0;
                v = (v << 1) | (uint)bit;
                _bit++;
            }
            return v;
        }

        public ulong Ue()
        {
            int zeros = 0;
            while (Read(1) == 0 && zeros < 32) zeros++;
            return (1UL << zeros) - 1 + Read(zeros);
        }
    }
}
