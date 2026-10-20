# Changelog

## [1.1.3] — 2026-10-20

### 🎬 Film/Serie: scelta di lingue e sottotitoli (nuovo)
- Durante l'analisi del titolo compaiono le **tracce audio** e i **sottotitoli** disponibili, accanto alla qualità.
- **Più lingue di doppiaggio** nello stesso file (es. italiano + inglese): la prima scelta è quella predefinita; senza spunte viene usato l'italiano.
- **Sottotitoli** inclusi come tracce attivabili dal player, con la lingua impostata nei metadati.
- Opzione **"Imprimi nel video"**: il primo sottotitolo scelto viene disegnato nell'immagine (ricodifica del video, con avanzamento in percentuale).
- Le scelte vengono **ricordate** e preselezionate per i titoli successivi.
- L'app ora legge da sé la playlist master e scarica **esattamente** le tracce scelte, poi le unisce in locale con ffmpeg: prima yt-dlp poteva ripiegare su una traccia qualsiasi (es. film scaricato in inglese).
- Video e tracce audio vengono scaricati **in parallelo**.

### 📏 Film/Serie: dimensioni e velocità
- **Dimensione corretta fin dall'inizio**: stimata da banda × durata della playlist e poi dai segmenti scaricati; l'avanzamento misura i byte realmente scritti su disco. Prima la stima di yt-dlp con i segmenti paralleli poteva arrivare a decine di GB per un file da 800 MB.
- Nuovo cursore **"Segmenti paralleli"** (budget totale per download, diviso tra video e audio). Se una traccia fallisce (es. errore 503) viene riprovata da sola con meno segmenti, senza far ripartire il resto.
- **Accodamento istantaneo** di stagioni e serie intere: il link di ogni episodio viene estratto all'avvio del suo download (i link scadono comunque in pochi minuti).
- Nuovo cursore **"Download paralleli"** e pulsante **"Pausa tutti"** anche per Film/Serie.

### ⚡ Code di download separate
- **Anime e Film/Serie non si bloccano più a vicenda**: ogni tab ha i propri slot e il proprio limite di download simultanei. All'avvio la tab Film/Serie forzava a 1 il limite di **tutta** la coda.
- Accodare un anime non fa più partire i film rimasti in coda dalla sessione precedente (e viceversa).
- Accodamento anime **a blocchi**: i primi episodi partono subito mentre gli altri vengono preparati.
- Le righe degli episodi vengono create solo quando si espande il gruppo della serie: accodare centinaia di episodi non rallenta più l'interfaccia.

### 🛡️ Download più affidabili
- **Una sola istanza dell'app**: due finestre aperte scaricavano lo stesso episodio negli stessi file temporanei ("file utilizzato da un altro processo").
- yt-dlp e ffmpeg vengono **chiusi insieme all'app** anche in caso di crash o chiusura forzata (Job Object di Windows); all'avvio vengono chiusi quelli rimasti da sessioni precedenti.
- Lo stesso download non può mai girare due volte in contemporanea; "Riprova" aspetta che il tentativo precedente sia chiuso.
- Se fallisce solo la **rinomina finale** del file (tipicamente l'antivirus che lo sta scansionando), l'app la completa da sola invece di buttare via il download.
- Pulizia completa dei frammenti di tentativi precedenti e delle cartelle temporanee di download non più in coda.
- Il controllo "nessun progresso" di yt-dlp ora funziona davvero (veniva disattivato subito dopo l'avvio).

### 🧰 Videobox Corrector (nuovo)
- Nuova tab per correggere i link video dei post del forum (Invision Community) confrontandoli con i file sul server SFTP.
- Albero SFTP colorato (verde / arancione / rosso / viola), mappatura dei file per post e invio dei nuovi link.
- L'**albero SFTP si aggiorna da solo**: subito e solo nella cartella interessata per le modifiche fatte dall'app (upload, eliminazioni, rinomine), ogni minuto per quelle esterne, mantenendo le cartelle espanse.
- Sezione **Impostazioni e Domini** ridisegnata, con anteprima live del link generato.

### 🌐 Proxy
- **Attivazione di un proxy senza lag**: vengono salvati solo i proxy cambiati, in un'unica transazione, e i browser dei provider vengono riavviati in background.
- I proxy **appena creati o importati** ora vengono usati subito dai provider (prima solo dopo un riavvio).
- Eliminazioni ed import in blocco con una sola query.

### 📋 Log globale
- **Filtri** per livello (tutti / avvisi ed errori / solo errori) e per sezione.
- Categorie più precise (AnimeWorld, AnimeSaturn, Streaming, SFTP, Proxy, Domini, Videobox, Database...) e messaggi di stato di ogni tab registrati nel log.
- Errori in rosso e avvisi in giallo; scorrimento automatico sulle voci nuove.
- Lista virtualizzata: molto più leggera con migliaia di righe.
- "Copia" non rovina più le lettere accentate.
- Gli errori non gestiti finiscono nel log invece di chiudere l'app (e in `crash.log` se fatali).

### 🎨 UI / UX
- **Titolo dell'app ridisegnato**, con versione letta automaticamente dal programma.
- Pulsanti principali in **blu soft** (sfondo trasparente) invece del blu pieno.
- **Icone centrate** nei pulsanti tondi (play, pausa e tutte le icone non quadrate erano spostate a sinistra).
- **Cambiando tab, liste e tabelle restano dove le avevi lasciate** (anche nelle sotto-tab di Organizza File e SFTP).
- Ridisegnati Info, Conferma, Input, selezione cartella remota, Aggiornamento, Preparazione e player video.
- Organizza File: messaggio di stato sopra la guida.
- Pallino SFTP **grigio** quando è disconnesso.
- Guide di tutte le tab riscritte in modo più dettagliato; aggiunti tooltip a 85 pulsanti e caselle.

### 🐛 Bug fix
- Totale del gruppo serie mostrato a GB interi ("0,00 / 2,00 GB" invece di "0,19 / 2,43 GB").
- Dopo "Pausa tutti" / "Annulla tutti", "Avvia tutti" non riaccodava alcuni download, che restavano bloccati.
- Selettore cartella remota con il titolo corretto per ogni operazione.
- Rimosso codice inutilizzato, tra cui un comando "elimina tutto" che avrebbe cancellato anche i download di Film/Serie.

## [1.1.2] — 2026-10-14

### 🎬 Anteprima video SFTP (nuovo)
- **Player interno** per i video presenti sull'SFTP: click destro su un file → "▶ Anteprima video".
- Lettura in **streaming** dal server SFTP: non viene scaricato tutto il file, si vede subito e si può fare seek avanti/indietro.
- Basato su **LibVLCSharp** (VLC integrato nell'app — nessuna installazione esterna richiesta).
- Controlli: play/pausa, ±10s, barra di avanzamento, volume, schermo intero.
- Scorciatoie: Spazio (play/pausa), ←/→ (±10s), ↑/↓ (volume), F (fullscreen), ESC (chiudi).
- Supporto formati: MP4, MKV, AVI, MOV, WEBM, FLV, WMV, M4V, TS, M2TS + audio MP3, FLAC, AAC, M4A, OGG, OPUS, WAV, WMA.

### 🚀 Performance
- **Accodamento Anime molto più veloce**: da 2-3 secondi a ~200-300ms per batch di 20-30 episodi.
  - Nuovo `DownloadRepository.InsertManyAsync`: un solo INSERT SQLite in una transazione invece di N INSERT separati.
  - Nuovo `DownloadQueueService.EnqueueManyAsync`: una sola `InvokeAsync` UI + un solo `QueueChanged` per tutto il batch.
  - Nuovo `SnapshotExistingEpisodeKeys`: una sola scansione della coda invece di N chiamate a `ContainsEpisode`.
- Eliminata la dipendenza da `AvaloniaResource` per i binari ffmpeg/yt-dlp (evita duplicazione dentro l'exe).

### 🎨 UI / UX
- **Tema più scuro** con accento **blu** (via il viola, meno "violet", più "professionale").
- **Log globale ridisegnato**: righe separate da bordo sottile, badge categoria più evidente, font monospace per il timestamp, testo del messaggio a 12px.
- **Log globale non più trascinabile** con GridSplitter (altezza fissa 150px aperto / 36px chiuso) e con **angoli superiori stondati**.
- Pulsante **"Download"** in SFTP non più troncato a "Downloa".
- **Indicatori colorati** negli header di Anime Download / Film/Serie / SFTP:
  - 🔴 **Rosso** se ci sono download falliti
  - 🟡 **Giallo** se ci sono download attivi
  - 🟢 **Verde** se la coda è vuota o tutto completato
- **Pallino Account** rosso/verde per stato connessione.
- **Pallino Domini** verde solo se tutti i domini rispondono `HTTP 200 OK`.

### 🐛 Bug fix
- Fix encoding: la stringa "Questa azione non può essere annullata." mostrava `puÃ²` invece di `può` (locale Windows-1252 vs UTF-8).
- Fix risoluzione path plugin SFTP di LibVLC su Windows (passato come `--plugin-path` esplicito).
- Fix `SystemDecorations` deprecato in Avalonia 12 (warning).

## [1.1.1] — 2026-10-12

### 🌐 Proxy: test domini e login ora lo usano
- I **test domini** (tab 🌍 Domini) ora usano il proxy attivo per il provider testato. Se hai un IP bannato, il test non fallisce più con timeout: passa attraverso il proxy di StreamingCommunity / AnimeWorld / AnimeSaturn.
- Il **login a StreamingCommunity** (tab 👤 Account) ora usa il proxy attivo per SC. Non fallisce più con "credenziali non valide" quando Cloudflare blocca l'IP diretto.
- Nuovo metodo `DomainTester.InvalidateCache()` chiamato prima di ogni test, per garantire che i cambi di proxy siano applicati immediatamente senza riavviare l'app.

### 🟢🔴 Indicatori di stato colorati
- Tab **🏠 Anime Download**: pallino **giallo** se ci sono download attivi / in coda / in pausa / falliti, **verde** se la coda è vuota o tutto completato.
- Tab **🎬 Film/Serie Download**: stesso comportamento, pallino **giallo** durante download attivi, **verde** altrimenti.
- Tab **☁ SFTP**: pallino **giallo** durante upload/download attivi, **verde** altrimenti.
- Gli indicatori si aggiornano automaticamente al cambio di stato della coda, senza refresh manuale.

### 🐛 Bug fix
- Fix encoding: la stringa "Questa azione non può essere annullata." mostrava `puÃ²` invece di `può` nei dialog di conferma eliminazione (locale Windows-1252 vs UTF-8).

## [1.1.0] — 2026-10-12

### 🎬 Novità: supporto StreamingCommunity
- **Nuovo provider** StreamingCommunity (film e serie TV) con ricerca, login premium, selezioni stagioni/episodi e download funzionante.
- Download HLS via **yt-dlp** con `--impersonate chrome`: bypassa Cloudflare Managed Challenge che bloccava FFmpeg e N_m3u8DL-RE.
- Selezione automatica dell'audio **italiano** (`ba[language=ita]`) con fallback ad altre lingue.
- Estrazione del **master playlist** (non la rendition 480p/720p/1080p) per avere video e audio insieme nello stesso download.
- Login programmatico via API + riuso dei cookie di sessione per yt-dlp.
- Cache dei cookie Netscape (5 minuti) per velocizzare download consecutivi.

### 🌐 Sistema Proxy multi-provider
- Ogni provider (AnimeWorld / AnimeSaturn / StreamingCommunity / Download diretti) ha una **lista proxy indipendente**.
- 3 liste filtrate per provider + rotazione sequenziale con wrap-around.
- Auto-select post-test che rispetta le deselezioni manuali dell'utente.
- Preemptive Proxy-Authorization per fix degli errori 407.
- Retry automatico con **rotazione proxy** su errori di rete (403, 407, 429, 5xx, timeout).

### 🏠 Anime Download
- **Poster nelle tile dei risultati** per AnimeWorld e AnimeSaturn (scaricati in background, cache condivisa).
- Ricerca AnimeSaturn ottimizzata: `DOMContentLoaded` + `WaitForSelectorAsync` nativo invece di `NetworkIdle`.
- Cache di ricerca (5 minuti) per evitare richieste duplicate.
- Eliminata la ricerca via API di AnimeWorld (non restituiva poster, causava merge incoerente) → solo scraping HTML.

### 📊 Progress bar e download
- **Barra di progresso fluida** 0-100% che non si resetta tra video e audio.
- Tracciamento cumulativo di più file (video + audio) con valore unico.
- Ripresa al riavvio: i download interrotti ripartono correttamente.
- Fix loop infinito su **Pausa/Play** (cancellazione `.part` per vixcloud, che non supporta resume su HLS).

### 🎨 UI / UX
- Tab **Account**: pallino rosso (non connesso) / verde (connesso) nell'header, come in Domini.
- Tab **Domini**: pallino verde **solo per `HTTP 200 OK`** (prima bastava qualsiasi risposta).
- **Auto-salvataggio** di pattern, cartella base e creazione sottocartella in Anime Download e Film/Serie Download.
- **Pattern separati** per Film (`{Series} ({Anno})`) e Serie (`{Episode}`) in Film/Serie Download, con auto-switch in base alla selezione.
- **Cartella base separata** tra Anime Download e Film/Serie Download.
- Log globale: testo selezionabile, wrappabile, con pulsanti **Copia tutto** ed **Esporta** (dialog "Salva con nome").
- Aggiunti pulsanti **Forza priorità / Rimuovi priorità** in Film/Serie Download.
- Rimosso pulsante **Pausa** in Film/Serie Download (non ha senso con yt-dlp).
- Pulsanti di download disabilitati finché l'analisi qualità non è completata.

### ⚙️ Bootstrap e installer
- **BootstrapDialog al primo avvio**: finestra di progresso che scarica Chromium (~150 MB) con log e barra di avanzamento.
- ffmpeg, ffprobe, yt-dlp, WinSCP **già inclusi nell'installer** (nessun download extra per l'utente).
- Fix `.csproj`: `CopyToPublishDirectory` sui binari esterni per garantire la presenza in publish.
- Esclusione binari da `AvaloniaResource` per evitare duplicazione dentro l'exe.
- GitHub Actions: download yt-dlp + step di verifica che i binari siano nel pacchetto.

### 🐛 Bug fix
- Fix `VideoQuality.DisplayName` che mostrava un bitrate assurdo (`9223372036854,8 Mbps`) per "Auto".
- Fix `HasAccountIssue` non aggiornato al cambio stato login (pallino in Account).
- Fix `MovieSearchResult` ora `ObservableObject` per supportare poster asincroni.
- Fix `StreamingCommunityService.CdnBase` ora derivato dinamicamente dal dominio attuale.
- Fix `Logout` che cancellava le credenziali dal DPAPI (ora le mantiene, nuovo pulsante separato "Elimina credenziali").
- Fix retry proxy che non considerava `ct.IsCancellationRequested` (loop su pausa).
- Fix race condition sul `LogService` con contenuto molto grande.

## v1.0.9 — 2026-10-06

### Nuove funzionalità
- **Retry item/serie**: pulsanti ▶ ⚡ ⏸ ✕ 🔄 🗑 a fianco di ogni episodio + pulsanti serie "🔄 Failed" / "🔄 Cancelled"
- **Header serie**: ora mostra anche paused, failed, cancelled
- **Organizer**: campo "Episodio iniziale" per Singola cartella e Unisci cartelle (locale e remoto)
- **Download tab**: nome serie/anime mostrato nell'header del pannello Episodi

### Fix e miglioramenti
- **Fix check 0-byte**: non si blocca più su cartelle con molti file (es. Mushoku Tensei). Causa: `GetFileInfo` seriale su ogni file con size 0. Ora chiamato solo per size sconosciuta, con timeout 45s e watchdog 90s per cartella
- **Fix evidenziazione post-upload**: la cartella padre si aggiorna correttamente dopo il completamento di tutti gli upload (usa `HashSet` di job ID invece di contatore, immune a Clear Completed)
- **Fix lag con code upload grandi**: rimossi refresh SFTP dopo ogni upload, update in-place delle entry visibili
- **Fix log che saturavano la UI**: log del check 0-byte per-cartella ora solo su Debug, in UI solo START/FINE/TIMEOUT/errori
- **Tema chiaro**: pulsanti "warning" in stile soft (bg ambra chiaro, testo ambra scuro), selezione testo leggibile (bianco su blu), evidenziazione 0-byte in rosso chiaro invece che scuro
- **Thread-safety** di `ZeroByteCheckerService`: cache protette da lock, nessuna corruzione durante download paralleli
- **SFTP**: rimosso `ContinueWith` che poteva lasciare il `_sessionGate` acquisito dopo cancellazione

### Rimosso
- CheckBox "Crea una sottocartella per ogni serie" in Organizer (Singola cartella + Unisci cartelle)

## v1.0.8 — Novità e miglioramenti

### 🆕 Nuovo provider: AnimeSaturn
- Supporto completo per AnimeSaturn (ricerca, episodi, download)
- Selettore provider nella barra di ricerca del tab Download
- Architettura `IAnimeProvider` predisposta per futuri provider

### 🎬 Download HLS/streaming
- Supporto download da stream HLS (`.m3u8`) tramite FFmpeg integrato
- FFmpeg bundlato nell'app: nessuna installazione manuale richiesta
- Progress bar funzionante anche sui download HLS

### 🌍 Nuova tab "Domini"
- Modifica dell'URL base dei provider senza aggiornare l'app
- Test raggiungibilità dominio con badge verde/rosso
- Test automatico all'avvio
- Reset rapido al dominio di default
- Pallino di avviso sull'header della tab se un dominio è KO

### 🏗️ Architettura (refactor importante)
- Suddivisione del MainViewModel in sub-ViewModel per tab (più manutenibile)
- `SharedState` per lo stato condiviso tra i tab
- `TransferJobBase` e `SftpQueueServiceBase<TJob>` per ridurre duplicazione
- `SftpSessionLimiter` per limitare le sessioni SFTP simultanee
- Eliminazione della God class (~2700 → ~250 righe)
- `ViewLocator` rimosso (dead code)

### 🎨 UI / UX
- **Tab Download**:
  - Contatore serie trovate in "Risultati ricerca"
  - Contatore episodi totali + selezionati in "Episodi"
  - Checkbox episodi cliccabile direttamente
  - "Inverti" selezione ora funziona correttamente
- **Tab SFTP**:
  - Colonne DataGrid ridimensionabili e riordinabili
  - Freccia ↑/↓ per direzione ordinamento (icona dinamica)
  - Riordino liste più veloce (senza refresh da disco)
  - FileSystemWatcher per il browser locale (aggiornamento automatico)
  - Context menu "Apri / Rinomina / Elimina" corretti per i file locali
  - Force reload corretto (non resta più bloccato)

### 🐛 Bug fix
- `settings.json` ora salvato atomicamente (no corruzione in caso di crash)
- Cache 0-byte: evidenziazione ora solo dopo check manuale 🔍
- Ripristino coda download non più bloccante all'avvio
- Vari fix minori su race condition e thread safety