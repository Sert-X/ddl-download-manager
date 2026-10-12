# Changelog

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