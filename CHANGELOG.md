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