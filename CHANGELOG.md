# Changelog

## [1.4.1] — 2026-10-26

### 🗂 Videobox Corrector
- **Cambiare categoria non tocca più alberi e sorgenti**: prima ogni categoria aveva le sue spunte e, passando per esempio da Anime ITA ad Anime Sub ITA, gli alberi già caricati sparivano. Ora cambia solo l'elenco dei post; le spunte delle sorgenti sono una sola scelta per tutte le categorie.
- **Dopo "Applica" i link restano nella tabella**: la tabella si ricarica con i link appena salvati sul forum, già colorati. Prima le righe applicate sparivano.
- **Albero di una sorgente che non si caricava** ("non raggiungibile: Element session@0 already read to the end"): l'aggiornamento automatico, trovando una cartella appena eliminata, chiudeva la connessione che serviva al caricamento. Ora la connessione si chiude solo se è davvero bloccata, e mai mentre l'albero si carica.

### ⚡ Code di download
- **"Riscarica" non rallenta più l'app**: con serie lunghe e centinaia di episodi falliti l'app quasi si bloccava, perché i totali della serie venivano ricalcolati molte volte a ogni cambio di stato. Ora si calcolano in un solo passaggio e i cambi ravvicinati producono un solo aggiornamento (Anime e Film/Serie).

## [1.4.0] — 2026-10-26

### 🎬 Film/Serie Download · CB01
- **Conversione a scelta a fine download**, nel Dettaglio, con la spiegazione di cosa comporta ogni opzione:
  - **Originale (nessuna elaborazione)**: il file così come lo fornisce il sito (spesso .mkv);
  - **Copia in MP4 (senza ricodifica)**: come nelle versioni precedenti, ora anche con controllo e riparazione dei .mp4 difettosi;
  - **MP4 + audio AAC**: il video resta originale, l'audio diventa AAC stereo;
  - **Player web: H.264 + AAC**, con il processore o con la scheda video (NVIDIA, Intel, AMD): video e audio ricodificati, si riproducono in tutti i browser e player web.
- **Scelta predefinita**: "Usa sempre questa" nel Dettaglio, oppure "Conversione CB01" in "Nome file e cartella". Si può cambiare per ogni download e resta salvata anche dopo un riavvio.
- Le conversioni vengono eseguite **una alla volta** e nella coda si vede la fase ("Conversione 37%", "Copia in MP4...") al posto di "Downloading".
- **Audio più forte e dialoghi chiari** nelle conversioni AAC e H.264: il passaggio da 5.1 a stereo non abbassa più il canale dei dialoghi e il volume è normalizzato a -16 LUFS, come nei servizi di streaming.
- **Tutte le tracce audio e i sottotitoli** restano nel file, con l'**italiano sempre come prima traccia audio** (predefinita): i player web come Plyr partono in italiano.
- **Video che non si aprivano** (es. Avengers: Endgame, nessun lettore lo riproduceva): la configurazione del video HEVC viene ricostruita senza ricodificare.
- **Ricerca completa**: vengono lette tutte le pagine dei risultati (cercando "One Piece" ora compaiono tutti i 15 film e le 2 serie).
- Nelle serie compaiono anche **film, OAV e special** pubblicati nella stessa pagina (es. il film di Beyblade, l'OAV di One Piece).
- **Proxy e Cloudflare**: messaggi chiari quando Cloudflare blocca Maxstream, con il nome del proxy in uso. Nella tab Proxy, la lista CB01 avvisa che servono **proxy residenziali**.
- Download più affidabili: un download ripreso riparte da zero se il file parziale non è integro, e i pezzi incompleti vengono riscaricati invece di finire nel file.

### 🎨 Interfaccia
- **Messaggio di stato sempre visibile**: scorrendo la pagina resta fissato in alto, in tutte le tab, con lo stesso aspetto e la stessa distanza dalle tab.
- **Icone al posto delle emoji**:
  - il messaggio di stato mostra spunta verde, triangolo arancione o X rossa in base all'esito;
  - nuovi badge in Account (Premium, Free, Non connesso, Da verificare);
  - pallini colorati in Domini;
  - icone di livello nel Log globale;
  - icone e frecce di ordinamento in SFTP.
- **Novità nell'app**: il badge della versione nell'intestazione apre il changelog completo.
- Frasi iniziali uniformi in Anime Download e Film/Serie Download.
- **Guide rapide aggiornate**:
  - Film e Serie, con le sezioni StreamingCommunity e CB01;
  - Anime;
  - Proxy;
  - Domini;
  - Account.

### 🗂 Videobox Corrector
- Caricando gli alberi di due o più sorgenti, una sorgente poteva restare senza spunta nel menu: risolto. Togliendo la spunta a una sorgente, la sua radice sparisce dall'albero.

### 🧹 Varie
- Esportando o copiando il Log globale, il livello è scritto in testo (INFO, AVVISO, ERRORE).

## [1.3.0] — 2026-10-25

### 🌐 Proxy (tab rinnovata)
- **Una lista per ogni provider** (AnimeWorld, AnimeSaturn, StreamingCommunity, CB01 · Maxstream, Download diretti) più l'**Archivio** con tutti i proxy: lo stesso proxy può funzionare su un sito e non su un altro.
- **Importa** incollando una lista o da file, in tutte le liste o solo in quelle scelte. I doppioni vengono saltati e i proxy esclusi restano esclusi.
- **Esclusi**: i proxy che falliscono il test escono solo dalla lista di quel sito (si può disattivare) e non vengono più ritestati. "Ripristina" li rimette in lista; "Pulisci archivio" elimina quelli inutilizzabili ovunque.
- **Riquadro "Proxy attivo"** per ogni provider: indirizzo, tempo di risposta, interruttore "Proxy in uso / Connessione diretta" e i pulsanti:
  - **Attiva il più veloce**: misura insieme i migliori e attiva quello che risponde più in fretta in quel momento;
  - **Attiva selezionato**: verifica il proxy prima di attivarlo e mostra chiaramente l'esito;
  - Ruota al prossimo e Disattiva.
- **Testa tutti i provider**: ritesta le liste una dopo l'altra, con avanzamento e riepilogo finale.
- Righe con IP e porta, protocollo, utente, stato, tempo di risposta e data del test, con il proxy attivo in cima. Nuovi badge colorati al posto delle vecchie icone.
- **Test CB01 · Maxstream reale**: prova home e login di Maxstream (con credenziali di prova, non il tuo account) e CB01, e dice chi blocca. Maxstream rifiuta gli IP di datacenter e VPN.

### 🎬 Film/Serie Download
- **Riquadro Dettaglio** per film e serie: locandina, anno, provider, qualità e audio scelti, trama (se disponibile), cartella e nome del file che verrà creato.
- **CB01 · Sub ITA ben visibile**: etichetta "SUB ITA" sulle locandine dei film e nel Dettaglio.
  - Le stagioni ITA e Sub ITA sono **voci separate**, per esempio "Stagione 1 · ITA (120 ep.)" e "Stagione 1 · Sub ITA (293 ep.)".
  - Nuova colonna "Lingua" negli episodi.
  - Nuovi pulsanti **"Scarica tutta la serie ITA"** e **"Scarica tutta la serie Sub ITA"**.
- **CB01 · pagine con più serie** (es. Beyblade, GTO): ogni spoiler diventa la sua serie, per esempio "Beyblade V-Force · Stagione 1", con cartella propria.
- CB01: le cartelle con file numerati senza stagione (es. Boruto Sub ITA) non risultano più vuote; dal titolo delle serie è tolto l'ultimo aggiornamento ("Bleach – 17×44 – ITA" → "Bleach"), che finiva anche nel nome della cartella.
- **StreamingCommunity**: con il pattern `{Series} S{Season:D2}E{Episode:D2}` il file era "I Simpson S01 S01E01.mp4". Ora è "I Simpson S01E01.mp4" e la cartella è "I Simpson".

### ⚡ Anime Download
- **AnimeWorld molto più veloce**: gli episodi di serie lunghe (es. 1.180 di One Piece) si caricano in circa 1 secondo invece di 6-8; anche la ricerca è più rapida.
- **AnimeSaturn trova tutti i risultati**: prima leggeva solo la prima pagina (cercando "one piece" la serie principale non c'era). Le pagine si caricano insieme e, ripetendo una ricerca, le locandine non spariscono più.
- Il browser interno si prepara in anticipo: la prima ricerca non aspetta l'avvio.
- Il riquadro "Episodi" si chiama **"Dettaglio"** e mostra locandina, provider, numero di episodi e selezionati, cartella e nome del file.

### 🖼 Anime e Film/Serie
- **Pulsante "Analizza"** sulle locandine al passaggio del mouse: fa come il doppio clic.
- Pattern e cartella base sono nel riquadro richiudibile **"Nome file e cartella"**, con un riassunto quando è chiuso: più spazio per gli episodi.

### 🗂 Organizza file
- Nei pattern la **"/" crea davvero una sottocartella** (es. `{Series}/{Series} - {Episode:D3}`): prima diventava "_" nel nome del file. Vale per Riordina, Unisci e Dividi, su PC e via SFTP; l'Annulla rimuove anche le cartelle create.
- Spiegazione della "/" e di `{Series}` sotto i pattern; esempi aggiornati.

### 🌍 Domini
- I domini vengono **ricontrollati anche mentre l'app è aperta** (di default ogni 10 minuti, si può cambiare o lasciare solo all'avvio): se uno smette di rispondere il pallino della tab diventa rosso.

### 👤 Account
- Messaggio chiaro quando Maxstream mette in verifica il login dell'account (troppi accessi ravvicinati o da IP diversi): basta accedere una volta dal browser.

### 🧹 Varie
- Il **Log globale** parte chiuso.

## [1.2.0] — 2026-10-25

### 🎬 Film/Serie Download
- **Nuovo provider CB01** (film e serie TV), scaricati da Maxstream con account Premium: si sceglie dal menu accanto alla ricerca.
- **File originale in alta qualità**: si scarica il file originale (es. 2,4 GB) invece della versione ridotta dello streaming, e alla fine diventa sempre un **.mp4**. La conversione copia video e audio senza perdita di qualità; l'audio non adatto all'MP4 (DTS, TrueHD, Vorbis...) diventa AAC.
- **Scelta del link a prova di errore**: se nel post HD e SD o Maxstream e Mixdrop sono invertiti, l'app prova i link e tiene il file migliore (risoluzione e dimensione reali).
- Serie con un link per episodio o con la cartella di tutte le stagioni: stagioni ed episodi letti correttamente; la versione ITA ha la precedenza su quella SUB-ITA.
- **Pattern dei nomi separati per CB01**: `{OriginalFileName}` è il nome del file su Maxstream. I pattern di StreamingCommunity ora sono indicati come tali.
- I download CB01 rimasti in coda ripartono anche dopo un riavvio dell'app.
- Tolto l'avviso che chiedeva un proxy obbligatorio per StreamingCommunity.

### ⚡ Anime e Film/Serie
- **Selezione multipla con Ctrl e Shift** negli episodi, sia sulle righe sia sulle caselle: Ctrl aggiunge o toglie, Shift seleziona l'intervallo.
- **Apri il post nel browser**: icona sul poster dei risultati, oppure clic destro → "Apri nel browser" / "Copia link".

### 👤 Account
- Sezione rinnovata: una sola guida e due blocchi identici per StreamingCommunity e CB01 · Maxstream.
- **Maxstream**: login Premium con scadenza e giorni rimanenti; la sessione si rinnova da sola durante i download se scade.
- Entrambi gli account vengono **verificati a ogni avvio**; StreamingCommunity mostra la data di scadenza invece della data grezza.
- FlareSolverr è facoltativo: con la connessione di casa non serve.

### 🌍 Domini e 🌐 Proxy
- **CB01 e Maxstream nella tab Domini**: quando cambiano dominio basta aggiornarlo lì.
- Proxy opzionale per CB01/Maxstream, con test dedicato nella tab Proxy.

### ☁ SFTP
- **Barra di scorrimento**: trascinandola non parte più per errore il drag & drop dei file (il cursore con il divieto e la lista che non scorreva).
- **Ricerca per lettera** come in Esplora risorse: premendo una lettera si va al primo file o cartella che inizia così; ripremendola, al successivo.

### 📦 Aggiornamenti
- L'aggiornamento si installa **senza la finestra di sistema** di Velopack: l'app si chiude per qualche secondo e si riapre con la schermata **"Aggiornamento completato"** e le novità. Vale dagli aggiornamenti successivi a questo.
- La schermata di Setup.exe mostra la versione installata.

## [1.1.8] — 2026-10-25

### ⚡ Anime Download
- **Accodamento istantaneo anche di centinaia di episodi** (AnimeWorld e AnimeSaturn): gli episodi entrano subito in coda e il link di ognuno si chiede al sito solo quando parte il suo download. Prima le richieste partivano tutte insieme, il sito rispondeva 403 e l'accodamento si fermava dopo 50-60 episodi.
- **Errori 403/429 gestiti da soli**: le richieste al sito sono distanziate; se il sito chiede di rallentare, l'app aspetta (sempre un po' di più) e riprova fino a 4 volte prima di segnare l'errore.
- **Cambio proxy senza blocchi**: niente più "Servizio non inizializzato", il browser interno si riapre da solo e il lavoro continua.
- AnimeSaturn usa il proprio proxy (prima usava per errore quello di AnimeWorld).
- Gli episodi in coda non ancora partiti funzionano anche dopo un riavvio dell'app.
- **Ricerche veloci una dopo l'altra**: vale sempre l'ultima; quella vecchia viene annullata e i suoi risultati ignorati. Lo stesso per gli episodi quando cambi anime al volo. Se il sito dà un errore passeggero, la ricerca riprova da sola.

### 🎬 Film/Serie Download
- Stessa protezione per ricerca, stagioni, episodi e qualità: contano solo i risultati dell'ultima scelta.
- Cambiando titolo mentre caricava le qualità, restavano quelle del titolo precedente: corretto.

## [1.1.7] — 2026-10-24

### ⚡ Anime Download
- **AnimeSaturn non salta più episodi** accodandone tanti insieme: il sito rifiutava le richieste troppo ravvicinate ("troppe richieste") e quegli episodi venivano persi. Ora le richieste sono poche alla volta e distanziate; se il sito chiede di rallentare, l'app aspetta e riprova da sola.
- Se il link di qualche episodio non si trova, il messaggio dice quali (es. "Link non trovato per 2 episodi (12, 37)") e solo quelli restano selezionati: basta premere di nuovo "Scarica selezionati" per riprovarli. Vale anche per AnimeWorld.

### 🧰 Videobox Corrector
- **Post modificati ed eliminati sul forum**: la lista si riallinea ogni 30 secondi (prima 90, e saltava il controllo mentre l'app era occupata); i post eliminati o spostati in un'altra categoria vengono tolti. Aprendo un post che non esiste più, viene tolto dalla lista.
- Gli aggiornamenti automatici usano la categoria con cui hai caricato i post, anche se nel frattempo cambi categoria nel menu.
- **Sorgenti senza ricaricare tutto**:
  - "Carica" legge solo le sorgenti nuove e tiene quelle già caricate (rilegge tutto solo se sono già tutte caricate, o se cambi la configurazione di una sorgente).
  - Togliendo la spunta a una sorgente dal menu "Sorgenti", sparisce subito dall'albero.
  - Nuova voce **"Togli dall'albero"** nel clic destro sulle radici delle sorgenti: la toglie e le leva la spunta nel menu.

## [1.1.6] — 2026-10-22

### 📦 Installazione e aggiornamenti
- **Installer rinnovato**: Setup.exe mostra una schermata grafica con logo e barra di avanzamento al posto della finestra predefinita.
- Nuovo installer **.msi con procedura guidata** (Benvenuto → Installazione → Fine) con grafica e testi in italiano; installa per l'utente corrente come Setup.exe.
- **Icona** dell'app in alta definizione (fino a 256×256): non è più sgranata su Setup.exe e collegamenti.
- **Aggiornamento dall'app con le dimensioni**: prima di scaricare mostra quanto c'è da scaricare (anche "solo le parti cambiate"); durante il download indica MB scaricati su totali, MB rimanenti con tempo stimato e velocità, misurati sui file realmente scaricati.
- "Annulla download" ora ferma davvero il download (prima chiudeva solo la finestra).

### ☁ SFTP
- **Duplica…** nel clic destro (locale, server principale e Server 2): chiede il nome della copia proponendo "Nome - copia"; sul server la copia la fa il server stesso, senza passare dal PC.
- **Rinomina senza estensione**: è selezionato solo il nome, scrivendo l'estensione resta (anche in Duplica).
- **Anteprima video anche in locale** e sul secondo server.
- Menu del **Server 2** completo: Anteprima video, Rinomina, Duplica, Sposta in…
- Menu più puliti: "Apri" solo sulle cartelle, "Anteprima video" solo sui file video.
- **Messaggi di avanzamento** per le operazioni lunghe: "Spostamento in corso (12/40)…", "Eliminazione in corso…", "Rinomina in corso…", preparazione upload/download. Eliminare o spostare cartelle locali grandi non blocca più la finestra.
- La cartella aperta **si aggiorna da sola dopo upload e copie tra server**, anche con decine di file in parallelo (al massimo una lettura ogni 1,5 secondi).
- Sostituendo un file con uno dello stesso nome, **dimensione e data si aggiornano** senza uscire dalla cartella.
- **Zero byte**: il rosso resta finché nella cartella c'è ancora un file vuoto (prima spariva dopo aver sostituito anche solo alcuni file); ogni lettura di una cartella aggiorna l'evidenziazione sua e delle cartelle superiori, senza rifare il check.

### 🧰 Videobox Corrector
- **Post dal più nuovo**: in cima ci sono i post più recenti ("Carica Pag." carica i 50 più recenti).
- **Nuovi post del forum** aggiunti da soli in cima alla lista (controllo ogni 90 secondi), senza perdere selezione e mappatura.
- **Caricamento albero con totale**: "250 cartelle lette su 1357 (18%)" con barra di avanzamento reale.
- L'**albero si aggiorna** con le modifiche fatte da qualunque pannello della tab SFTP (anche dal Server 2 e dalle copie tra server) e quando premi **Aggiorna** dopo modifiche fatte fuori dall'app (es. WinSCP).
- **Connessioni più robuste**: se una sorgente va in timeout la connessione viene riaperta da sola e un errore su una sorgente non blocca le altre; i colori vengono sempre ricalcolati (prima la radice poteva restare gialla dopo aver eliminato i file in più).
- **Post con tantissimi link** (es. Detective Conan): avviso prima dell'invio e controllo dopo; se il forum ne salva meno (limite PHP `max_input_vars`, di solito 1000) lo segnala con la soluzione invece di "completato".

### ⚡ Download
- **AnimeSaturn: accodamento molto più veloce**: il link del video si ottiene con due richieste leggere invece di aprire due pagine nel browser per ogni episodio (resta il metodo precedente come riserva).
- **Film di StreamingCommunity accodati all'istante**: il link viene estratto all'avvio del download, come per gli episodi.

## [1.1.5] — 2026-10-22

### ☁ SFTP: copia da server a server (nuovo)
- Il pannello di sinistra del Browser passa da **Locale** a **Server 2**: scegli un profilo e premi Connetti. È una connessione separata, quella principale (destra) resta com'è.
- Pulsanti **Copia →** e **← Copia** al centro (e voci nel clic destro) per copiare file e cartelle intere nella cartella aperta dall'altra parte.
- I dati passano dal PC **in streaming**, letti da un server e scritti sull'altro nello stesso momento: nessun file temporaneo su disco.
- Nuova coda **"Server ↔ Server"** con avanzamento, velocità, copie parallele, pausa, ripresa e annulla. Una copia messa in pausa riparte dall'inizio del file.
- Sul secondo server si può navigare, creare cartelle ed eliminare (anche col tasto Canc); i pannelli si aggiornano da soli a copia finita.

### 🧰 Videobox Corrector
- **Più sorgenti SFTP insieme**, anche su server diversi: in "Impostazioni e Domini" ogni sorgente ha nome, connessione (un profilo della tab SFTP o la connessione della tab), cartella sul server, dominio e percorso pubblico, con anteprima del link. La configurazione precedente diventa da sola la prima sorgente.
- Menu **Sorgenti** sopra l'albero per scegliere quali caricare; la scelta è ricordata per ogni categoria. Ogni sorgente è una radice dell'albero; se un server non risponde lo dice sulla sua radice e le altre si caricano lo stesso.
- Le sorgenti con profilo dedicato usano una **connessione propria**: la tab SFTP non deve più essere connessa (tolto l'avviso rosso "Tab SFTP disconnessa").
- L'albero **si aggiorna subito** anche per le sorgenti con un profilo dedicato, quando la tab SFTP è connessa allo stesso server (eliminazioni, rinomine, nuove cartelle, upload).
- **Filtro per colore** dei post al posto di "Nascondi OK": verde, giallo, rosso, viola e grigio si mostrano o nascondono a piacere, ognuno con il numero di post; scorciatoie **"Da sistemare"** e **"Tutti"**.
- **Post aggiornati in tempo reale**: i post visibili si riallineano col forum ogni 90 secondi, quando li selezioni e dopo "Applica", senza ricaricare tutto.
- **Mappatura di un singolo file** (film): seleziona il file nell'albero invece della cartella.
- **Copia col clic destro**: titolo, indirizzo e link dei post; nome, percorso e link pubblico (anche di tutti i file) dall'albero; URL e nomi dalla tabella di mappatura.
- "Carica" non duplica più le cartelle dell'albero se un aggiornamento automatico era in corso.
- Il campo "Connessione SFTP" delle sorgenti non resta più vuoto quando l'elenco dei profili si ricarica.

### 📝 Testi lunghi e pattern
- **Testo che scorre**: passando il mouse su un testo tagliato con "…" il testo scorre fino alla fine e poi ricomincia; resta anche il tooltip con il testo completo. Vale in tutta l'app.
- I nomi lunghi nell'albero di Videobox ora si accorciano con "…" invece di uscire dal pannello.
- **Legenda dei pattern**: un pulsante **"?"** accanto a ogni campo pattern (Anime, Film/Serie, cartella stagione, Organizza file) spiega ogni segnaposto con un esempio; cliccandolo viene aggiunto al pattern.
- Il segnaposto **{Season}** ora funziona: "S{Season:D2}E{Episode:D2}" diventa ad esempio S01E05 (prima restava scritto così nel nome del file).

### ⚡ Anime Download
- **Accodamento più veloce** con AnimeWorld: la pagina del sito usata per ottenere i link resta aperta e viene riusata, senza scaricare immagini e pubblicità; prima ogni accodamento, anche di un solo episodio, ricaricava tutta la home e aspettava qualche secondo in più. Anche il download del singolo episodio usa questa via.

### 🗂 Organizza file
- **Unisci cartelle**: accanto a ogni cartella compare il numero di video (prima era sempre 0), sia in locale che sul server; si aggiorna dopo l'unione.

### 🧹 Varie
- Copia negli appunti senza caratteri invisibili all'inizio del testo.

## [1.1.4] — 2026-10-20

### ☁ SFTP
- Nuova funzione **"Sposta in..."** nel menu del clic destro, per file locali e remoti: scegli la cartella di destinazione, funziona anche con più elementi selezionati.
- **Scorrimento più fluido** nelle tabelle (file locali, remoti, code di upload e download): il menu del clic destro ora è uno per tabella invece di essere ricreato per ogni riga mentre si scorre, causa dei blocchi scorrendo velocemente.
- Il clic destro seleziona la riga sotto il mouse; se fa parte di una selezione multipla, il menu agisce su tutta la selezione.
- **Zero-Byte Checker di nuovo visibile**: file a 0 byte e cartelle che li contengono tornano evidenziati in rosso (lo stile della riga era andato perso), anche al passaggio del mouse e da selezionati.
- Il pallino della tab non resta più **rosso** dopo aver eliminato i trasferimenti falliti.
- La colonna "Contenuto" delle cartelle si aggiorna correttamente quando si ricarica la lista.

### 🧰 Videobox Corrector
- Post **gialli** (alcuni link fuori dai domini impostati): ora diventano gialle anche la loro cartella, le sottocartelle e le cartelle superiori nell'albero SFTP, per un confronto immediato.
- Il **viola** (mismatch tra numero di link e numero di file) arriva fino alla cartella principale della serie: es. con "MAO/1" diventano viola sia "1" che "MAO". Ogni cartella mostra il problema più grave tra le sue sottocartelle (verde < giallo < rosso < viola).
- Caricamento di post e albero SFTP con **avanzamento e pulsante Stop**: durante il caricamento i pulsanti "Carica" lasciano il posto a barra di avanzamento, conteggio (pagine e post / cartelle lette) e Stop, poi tornano. Fermando "Carica Tutti" i post già scaricati restano.
- "Tutti i media" e le categorie hanno icone vettoriali al posto dell'emoji.

### 🎬 Film/Serie Download
- Menu **Audio** e **Sottotitoli** ridisegnati nello stile delle tendine dell'app: pannello arrotondato con titolo e contatore delle selezioni, righe evidenziate al passaggio del mouse, codice lingua in un badge (e "forzati" dove serve), pulsanti **Tutte / Nessuna**, elenco scorrevole per i sottotitoli.

### 🏠 Anime Download
- **Ricerca AnimeWorld corretta**: con una parola inesistente non compaiono più risultati a caso. Prima venivano presi gli anime dei riquadri laterali della pagina (classifica, consigliati); ora si leggono solo i risultati veri e si riconosce l'avviso "nessun risultato".

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