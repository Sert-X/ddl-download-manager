# Changelog

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