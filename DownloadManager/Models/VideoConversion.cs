namespace DownloadManager.Models;

/// <summary>
/// Cosa fare del file a fine download (CB01 · Maxstream). Il valore è salvato nel download:
/// i numeri non vanno cambiati (Mp4Copy è stato aggiunto dopo, per questo vale 4).
/// </summary>
public enum VideoConversion
{
    /// <summary>File così come lo fornisce il sito (.mkv o .mp4), nessuna modifica.</summary>
    Original = 0,
    /// <summary>MP4 (copia senza ricodifica) + audio AAC stereo con volume regolato.</summary>
    AacAudio = 1,
    /// <summary>H.264 + AAC ricodificati con il processore (x264): compatibile con tutti i player web.</summary>
    H264Cpu = 2,
    /// <summary>H.264 + AAC ricodificati con la scheda video (NVENC/QSV/AMF): più veloce, file più grandi.</summary>
    H264Gpu = 3,
    /// <summary>.mkv copiato in .mp4 senza ricodifica, più controllo/riparazione dei .mp4 difettosi.</summary>
    Mp4Copy = 4,
}

/// <summary>Voce del menu "Conversione" con la spiegazione di cosa comporta.</summary>
public record VideoConversionOption(VideoConversion Mode, string Title, string Details)
{
    public static IReadOnlyList<VideoConversionOption> All { get; } = new[]
    {
        new VideoConversionOption(VideoConversion.Original, "Originale (nessuna elaborazione)",
            "Il file viene salvato esattamente come distribuito dalla fonte, nel suo formato (spesso MKV, a volte MP4), " +
            "senza controlli né modifiche a video, audio e sottotitoli. È pronto appena termina il download. " +
            "Indicato per VLC, PC e Smart TV; non adatto ai player web, e un MP4 difettoso all'origine resta tale."),
        new VideoConversionOption(VideoConversion.Mp4Copy, "Copia in MP4 (senza ricodifica)",
            "Video, audio e sottotitoli vengono trasferiti in un contenitore MP4 così come sono (remux): nessuna perdita di qualità, " +
            "dimensione invariata. Se il file è già MP4 viene controllato e, se necessario, riparato (es. video che non si apre). " +
            "Restano tutte le tracce audio e i sottotitoli testuali, con l'italiano come prima traccia audio; gli audio non ammessi " +
            "nell'MP4 (DTS, TrueHD, FLAC) diventano AAC. Nei browser il video HEVC (H.265) o l'audio Dolby (AC3/E-AC3) possono non " +
            "essere riprodotti. Tempo: circa 1 minuto per un film."),
        new VideoConversionOption(VideoConversion.AacAudio, "MP4 + audio AAC",
            "Come \"Copia in MP4\", ma ogni traccia audio viene ricodificata in AAC stereo a 192 kbps: downmix con i dialoghi in " +
            "primo piano e volume normalizzato a -16 LUFS (lo standard dello streaming, più alto dell'originale). " +
            "Il video non viene ricodificato e mantiene la qualità originale. Risolve l'audio assente nei browser; il video HEVC " +
            "è però riprodotto solo da Edge/Chrome con scheda video recente e da Safari. Tempo: circa 3-4 minuti per un film."),
        new VideoConversionOption(VideoConversion.H264Cpu, "Player web: H.264 + AAC (processore)",
            "Ricodifica completa di video e audio per la massima compatibilità: video in H.264 (profilo High, CRF 21) e ogni " +
            "traccia audio in AAC stereo a 192 kbps, con i dialoghi in primo piano e volume normalizzato a -16 LUFS. " +
            "Riproducibile in tutti i browser e player web (Plyr, Video.js, HTML5). Qualità visiva praticamente identica " +
            "all'originale e file più leggero (circa 2/3). Restano tutte le tracce audio e i sottotitoli testuali, italiano per primo. " +
            "Tempo: circa 10-11 minuti per un film di 3 ore, con il processore molto occupato durante la conversione."),
        new VideoConversionOption(VideoConversion.H264Gpu, "Player web: H.264 + AAC (scheda video)",
            "Stessa ricodifica di video e audio dell'opzione con il processore, ma il video viene codificato dalla scheda video " +
            "(NVIDIA NVENC, Intel Quick Sync o AMD AMF): più rapida e lascia libero il processore, con file più grandi " +
            "(simili all'originale) a parità di qualità. L'audio viene ricodificato in AAC come sopra. Se la scheda video non è " +
            "disponibile si passa automaticamente al processore. Tempo: circa 7 minuti per un film di 3 ore."),
    };

    /// <summary>Nota sotto le descrizioni: i tempi dipendono dall'hardware.</summary>
    public static string TimingNote =>
        "Tempi indicativi, misurati con Ryzen 7 3700X e GeForce RTX 3060 Ti su un film 1080p di 3 ore: " +
        "variano in base all'hardware del PC e alla durata del video.";

    public static VideoConversionOption For(VideoConversion mode) => All.FirstOrDefault(o => o.Mode == mode) ?? All[0];
    public override string ToString() => Title;
}
