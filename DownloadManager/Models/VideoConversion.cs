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
    /// <summary>.mkv copiato in .mp4 senza ricodifica (nessuna traccia ricodificata), più controllo/riparazione dei .mp4 difettosi.</summary>
    Mp4Copy = 4,
    /// <summary>Variante "High Definition" di Maxstream (la migliore dopo l'originale), poi come Mp4Copy.</summary>
    MaxstreamHd = 5,
    /// <summary>Variante "High Definition" di Maxstream, poi audio AAC con volume normalizzato (come AacAudio).</summary>
    MaxstreamHdLoud = 6,
}

public static class VideoConversionExtensions
{
    /// <summary>Si scarica la variante HD di Maxstream invece dell'originale.</summary>
    public static bool PrefersMaxstreamHd(this VideoConversion mode) =>
        mode is VideoConversion.MaxstreamHd or VideoConversion.MaxstreamHdLoud;

    /// <summary>Lavoro da fare sul file a fine download (le varianti HD si trattano come le scelte equivalenti).</summary>
    public static VideoConversion FileProcessing(this VideoConversion mode) => mode switch
    {
        VideoConversion.MaxstreamHd => VideoConversion.Mp4Copy,
        VideoConversion.MaxstreamHdLoud => VideoConversion.AacAudio,
        _ => mode
    };
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
            "Video, audio e sottotitoli vengono trasferiti in un contenitore MP4 così come sono (remux): nessuna ricodifica e " +
            "nessuna perdita di qualità. Se il file è già MP4 viene controllato e, se necessario, riparato (es. video che non si apre). " +
            "Restano tutte le tracce audio e i sottotitoli testuali, con l'italiano come prima traccia audio. L'audio resta quello " +
            "originale: se è Dolby AC3/E-AC3 o DTS, nei player web il video può restare muto (per quello c'è \"MP4 + audio AAC\"). " +
            "Se l'MP4 non può contenere l'audio il file resta nel formato originale. Tempo: circa 1 minuto per un film."),
        new VideoConversionOption(VideoConversion.AacAudio, "MP4 + audio AAC",
            "Come \"Copia in MP4\", ma ogni traccia audio viene ricodificata in AAC stereo a 192 kbps: downmix con i dialoghi in " +
            "primo piano e volume normalizzato a -16 LUFS (lo standard dello streaming, più alto dell'originale). " +
            "Il video non viene ricodificato e mantiene la qualità originale. Risolve l'audio assente nei browser; il video HEVC " +
            "è però riprodotto solo da Edge/Chrome con scheda video recente e da Safari. Tempo: circa 3-4 minuti per un film."),
        new VideoConversionOption(VideoConversion.MaxstreamHd, "Maxstream HD (qualità massima dopo l'originale)",
            "Invece del file originale si scarica la variante \"High Definition\" preparata da Maxstream: la qualità più alta dopo " +
            "l'originale, spesso un file più leggero. Poi si lavora come \"Copia in MP4\": controllo del file e italiano come prima " +
            "traccia, senza ricodificare l'audio. Se la variante HD non c'è si scarica la \"Standard Quality\" (e l'originale solo se mancano entrambe). " +
            "Tempo: circa 1 minuto per un film."),
        new VideoConversionOption(VideoConversion.MaxstreamHdLoud, "Maxstream HD + audio aumentato",
            "Come \"Maxstream HD\", poi ogni traccia audio viene ricodificata in AAC stereo a 192 kbps con i dialoghi in primo piano " +
            "e volume normalizzato a -16 LUFS (più alto dell'originale), come \"MP4 + audio AAC\". Il video non viene ricodificato. " +
            "Tempo: circa 3-4 minuti per un film."),
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
