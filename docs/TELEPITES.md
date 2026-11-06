# NodeWatch telepítése (Ubuntu 24.04)

Ez a leírás lépésről lépésre végigvezet a NodeWatch telepítésén egy Mosaic validátor node-on. A parancsokat a node-on
kell futtatni (SSH vagy PuTTY), egy `sudo` joggal rendelkező felhasználóval.

**Előfeltételek**

- Ubuntu 24.04, rajta futó Mosaic node (`mosaic-chain-mainnet.service`).
- A node RPC portjai a szokásos helyen vannak: relay chain `127.0.0.1:8844`, parachain `127.0.0.1:9944`.
- Internetkapcsolat az apt-hoz (a .NET 10 runtime telepítéséhez) és a riasztási célhoz (pl. Telegram).

## 1. A csomag letöltése a node-ra

A kiadási csomag neve `nodewatch-<verzió>.tar.gz`. A legegyszerűbb, ha közvetlenül a node-on töltöd le a GitHub
*Releases* oldaláról, és ellenőrzöd az ellenőrzőösszegét:

```bash
cd ~
wget https://github.com/Attila-maker/mosaic-live-watchdog/releases/download/v0.1.0/nodewatch-0.1.0.tar.gz
wget https://github.com/Attila-maker/mosaic-live-watchdog/releases/download/v0.1.0/nodewatch-0.1.0.tar.gz.sha256
sha256sum -c nodewatch-0.1.0.tar.gz.sha256
```

Az utolsó parancsnak `nodewatch-0.1.0.tar.gz: OK` választ kell adnia.

Ha a node-ról nem éred el a GitHubot, töltsd le Windowson, és másold fel például PuTTY `pscp`-vel (a `<felhasználó>` és
a `<node-ip>` a saját adataid), WinSCP-vel vagy Total Commander SFTP-kapcsolattal:

```
pscp nodewatch-0.1.0.tar.gz <felhasználó>@<node-ip>:
```

A csomagot a forrásból az `agent/build.sh` is elkészíti.

## 2. Telepítés

```bash
cd ~
tar -xzf nodewatch-0.1.0.tar.gz
cd nodewatch-0.1.0
sudo ./install.sh
```

A telepítő:

- feltelepíti a `dotnet-runtime-10.0` csomagot az Ubuntu tárolóból, ha még nincs fent;
- létrehozza a `nodewatch` rendszerfelhasználót (bejelentkezni nem lehet vele);
- a programot a `/opt/nodewatch` mappába másolja, és létrehozza a `nodewatch` parancsot;
- első telepítéskor létrehozza a `/etc/nodewatch/appsettings.json` beállításfájlt (később nem írja felül);
- telepíti a systemd szolgáltatást (`nodewatch.service`) és a polkit szabályt, amely a `nodewatch` felhasználónak
  **csak** a `mosaic-chain-mainnet.service` újraindítását engedi meg.

A szolgáltatás ekkor még nem indul el, előbb be kell állítani.

> Ha a chain szolgáltatásod neve más (például testneten), add meg telepítéskor:
> `sudo NODEWATCH_CHAIN_UNIT=mosaic-chain-testnet.service ./install.sh`, és írd át a beállításfájlban is.

## 3. Telegram bot létrehozása

1. Telegramon nyisd meg a **@BotFather**-t, küldd el neki: `/newbot`, adj nevet a botnak.
   A végén kapsz egy tokent, például `7012345678:AAH...`.
2. Mentsd el a tokent a node-on egy külön fájlba (a token **nem** kerül a beállításfájlba):

   ```bash
   sudo install -m 0640 -o root -g nodewatch /dev/null /etc/nodewatch/telegram.token
   sudo nano /etc/nodewatch/telegram.token
   ```

   Illeszd be a tokent egy sorba, mentsd (Ctrl+O, Enter), lépj ki (Ctrl+X).
3. Telegramon keresd meg a saját botodat, és küldd el neki: `/start`.
4. Kérdezd le a chat azonosítóját:

   ```bash
   sudo -u nodewatch nodewatch telegram-chatid
   ```

   Ez kiírja például: `ChatId: 123456789   (private: Attila)`. Ezt a számot írd be a beállításba (következő lépés).
   A Telegram a bot üzeneteit 24 óráig őrzi meg, ezért a `/start` után nem sokkal futtasd.

- **Csoport:** add hozzá a botot a csoporthoz, a csoportban küldd el: `/start@<botod_neve>`, majd futtasd újra a
  `telegram-chatid` parancsot. A csoport azonosítója negatív szám (pl. `-1001234567890`).
- **Csatorna:** add hozzá a botot a csatornához adminisztrátorként, üzenetküldési joggal, írj valamit a csatornába,
  majd futtasd a `telegram-chatid` parancsot.
- A kritikus riasztások hanggal érkeznek, a figyelmeztetések és a „helyreállt” üzenetek csendben.

## 4. Beállítások

```bash
sudo nano /etc/nodewatch/appsettings.json
```

A fájl megjegyzésekkel van ellátva. A legfontosabbak:

| Beállítás | Mit írj be |
|---|---|
| `Node.Name` | A node neve, ahogy a riasztásokban látni szeretnéd |
| `Node.ValidatorAddress` | A validátor fiók SS58 címe. Üresen hagyva nincs validátorállapot-figyelés (Chilled / Faulted) |
| `Sinks` → `ChatId` | Az előző lépésben kapott szám (idézőjelek között) |
| `Sinks` → `MinSeverity` | `warning`: figyelmeztetés és kritikus; `critical`: csak a kritikus riasztások |

A többi érték alapból jó a mainnet node-hoz. Az újraindítási szabályok a `Restart` részben vannak:

| Beállítás | Alapérték | Jelentés |
|---|---|---|
| `Enabled` | `true` | Az automatikus újraindítás be van-e kapcsolva |
| `AfterMinutes` | `10` | Ennyi perc folyamatos hiba után indít újra |
| `MinHoursBetween` | `24` | Ennyi órán belül legfeljebb egyszer indít újra |
| `GraceMinutes` | `15` | Újraindítás után ennyi ideig hagyja a node-ot utolérni a láncot |

## 5. Ellenőrzés

```bash
sudo -u nodewatch nodewatch check
```

Valami ilyesmit kell látnod:

```
Node: my-mosaic-node (mosaic-mainnet)
Relay chain:  #29481022, peerek: 8 (FULL 1, AUTHORITY 3, NONE 4, LIGHT 0)
Parachain:    #1834567, véglegesítve #1834565, peerek: 12, szinkronizál: nem
Validátor:    Normal
Szolgáltatás: mosaic-chain-mainnet.service: active
Szolgáltatás: mosaic-indexer-mainnet.service: active
Szolgáltatás: update-mosaic.service: inactive
Lemez:        /var/lib/mosaic-mainnet: 62.4% szabad (290.1 GB)
```

Mire figyelj:

- **Relay chain FULL + AUTHORITY:** ebből a kettőből szinkronizál a node. Ha ez 0, a relay chain megáll.
  A `NONE` peerek (más Mosaic node-ok) ebből a szempontból nem számítanak.
- **Validátor: nem található** – rossz a `ValidatorAddress`.
- **Szolgáltatás: unknown** – a `systemctl` nem adott választ az állapotra.

## 6. Próbariasztás

```bash
sudo -u nodewatch nodewatch test-alert
```

Két üzenetet kell kapnod Telegramon: egy „Próbariasztás”-t és egy „Próbariasztás vége” üzenetet.
A parancs kiírja kimenetenként, hogy sikerült-e.

## 7. Indítás

```bash
sudo systemctl start nodewatch
systemctl status nodewatch
journalctl -u nodewatch -f        # napló élőben (kilépés: Ctrl+C)
```

A szolgáltatás a gép újraindítása után magától elindul.

## Mi történik, ha a node elakad?

1. A NodeWatch 30 másodpercenként ellenőriz. Két egymás utáni rossz eredmény után riaszt
   (például „A relay chain nem halad”).
2. Ha az újraindítást kiváltó hibák valamelyike 10 percig folyamatosan fennáll, a NodeWatch:
   - küld egy „Újraindítom a Mosaic node-ot” riasztást,
   - kiadja a `systemctl --no-block restart mosaic-chain-mainnet.service` parancsot,
   - 15 percig vár, hogy a node utolérje a láncot.
3. Ha a node rendbe jön, „helyreállt” üzenet jön.
4. Ha 24 órán belül újra elakad, **nem** indítja újra, hanem egyszer szól: „Nem indítom újra a node-ot … Kézi
   beavatkozás kell.”

Újraindítást kiváltó hibák: a relay chain RPC nem válaszol, a relay chain nem halad, nincs full peer, a parachain RPC
nem válaszol, a parachain nem véglegesít.

Újraindítás előtt a NodeWatch nem chillel: ilyenkor a node már legalább 10 perce nem végzi a dolgát, az újraindítás
csak rövidíti a kiesést.

A NodeWatch nem indítja újra a node-ot:

- ha a chain szolgáltatás le van állítva vagy éppen indul (pl. karbantartáskor `systemctl stop`-pal leállítottad –
  ilyenkor csak riaszt, hogy nem fut);
- ha éppen a Mosaic automatikus frissítése (`update-mosaic.service`) fut;
- ha a chain szolgáltatás még nem fut 10 perce (például kézzel újraindítottad, vagy a frissítés indította újra): a
  frissen indult node mindig megkapja a teljes 10 percet;
- ha szünet volt az ellenőrzésekben (felfüggesztett gép, óraállítás): ilyenkor a mérés elölről kezdődik.

Az utolsó újraindítás idejét a `/var/lib/nodewatch/state.json` őrzi, és még a parancs kiadása előtt lemezre írja, így
a NodeWatch vagy a gép újraindítása nem nullázza a napi korlátot. Ha az állapotfájl nem menthető, nincs újraindítás;
ha nem olvasható, a NodeWatch úgy veszi, hogy épp most volt egy. A `check` parancs ki is írja („Utolsó
újraindítás: …”).

## Frissítés

Másold fel az új csomagot, és futtasd újra a telepítőt:

```bash
tar -xzf nodewatch-0.2.0.tar.gz
cd nodewatch-0.2.0
sudo ./install.sh
```

A beállításokat és az állapotot megtartja, a szolgáltatást újraindítja. Az új alapértelmezések a
`/etc/nodewatch/appsettings.example.json` fájlban vannak, ezzel összevetheted a sajátodat.

## Eltávolítás

```bash
sudo ./uninstall.sh            # a beállítások és az állapot megmaradnak
sudo ./uninstall.sh --purge    # mindent töröl, a nodewatch felhasználót is
```

## További kimenetek

### Matrix

A NodeWatch egy külön bot felhasználóként ír egy Matrix szobába (`m.notice` üzenetként). Matrix végponttól végpontig
titkosítást nem végez, ezért titkosítás nélküli szobát használj.

1. **Bot fiók:** hozz létre egy külön felhasználót a saját fiókodtól függetlenül. matrix.org-on például egy privát
   böngészőablakban a <https://app.element.io> oldalon regisztrálj egy `mynode-alerts` nevű felhasználót.
2. **Szoba:** a saját fiókoddal hozz létre egy szobát (pl. „Node riasztások”), és létrehozáskor kapcsold **ki** a
   „Végpontok közötti titkosítás engedélyezése” (Enable end-to-end encryption) kapcsolót – később már nem lehet.
   Hívd meg a bot felhasználót (`@mynode-alerts:matrix.org`).
3. **Szobaazonosító:** Elementben *Szoba beállításai → Speciális → Belső szobaazonosító* (Room settings → Advanced →
   Internal room ID). `!`-lel kezdődik, pl. `!AbCdEfGh:matrix.org`. A szoba címe (`#nev:szerver`) itt nem jó.
4. **Beállítás:** vedd fel a kimenetet a `Sinks` listába, és hozd létre az üres tokenfájlt:

   ```jsonc
   { "Type": "matrix", "Homeserver": "https://matrix-client.matrix.org", "RoomId": "!AbCdEfGh:matrix.org",
     "AccessTokenFile": "/etc/nodewatch/matrix.token", "MinSeverity": "warning" }
   ```

   ```bash
   sudo install -m 0640 -o root -g nodewatch /dev/null /etc/nodewatch/matrix.token
   ```

   A `Homeserver` a szerver kliens-API címe: matrix.org esetén `https://matrix-client.matrix.org`. Más szervernél a
   `https://<szerver>/.well-known/matrix/client` címen, az `m.homeserver.base_url` mezőben találod.
5. **A bot bejelentkeztetése:**

   ```bash
   sudo nodewatch matrix-login
   ```

   Bekéri a bot felhasználónevét és jelszavát, bejelentkezik, az access tokent a tokenfájlba menti, és belép a
   szobába (ezzel a meghívást is elfogadja). A jelszót nem tárolja.
6. **Próba:** `sudo -u nodewatch nodewatch test-alert`, majd `sudo systemctl restart nodewatch`.

Az access tokent ne az Element beállításaiból másold ki: a Matrix Authentication Service-t használó szervereken
(a matrix.org 2025 óta ilyen) az néhány perc alatt lejárhat. A `matrix-login` által kért token nem jár le; addig
érvényes, amíg a bot „NodeWatch” munkamenetét ki nem jelentkezteted (Elementben: *Beállítások → Munkamenetek*).

### SafeChat

A SafeChat admin felületén az admin hoz létre integrációs tokent egy felhasználóhoz rendelve; a riasztások ennél a
felhasználónál jelennek meg. A tokent a `/etc/nodewatch/safechat.token` fájlba mentsd:

```jsonc
{ "Type": "safechat", "Url": "https://<safechat-szerver>/api/ops/alerts",
  "TokenFile": "/etc/nodewatch/safechat.token", "MinSeverity": "warning" }
```

Több kimenet is megadható egyszerre; mindegyik a saját `MinSeverity` szintje szerint kapja a riasztásokat, a
„helyreállt” üzenetet pedig minden kimenet megkapja, amelyik a riasztást megkapta.

## Hibaelhárítás

| Jelenség | Teendő |
|---|---|
| A szolgáltatás nem indul (`systemctl status nodewatch`: failed) | Beállítási hiba: `journalctl -u nodewatch -n 20` megmutatja, melyik beállítás rossz. Javítás után: `sudo systemctl restart nodewatch` |
| Az újraindítás nem sikerült: `Interactive authentication required` vagy `Access denied` | Hiányzik vagy rossz a polkit szabály: `cat /etc/polkit-1/rules.d/50-nodewatch.rules`. A benne lévő unitnévnek egyeznie kell a `Restart.Unit` értékkel. Futtasd újra a telepítőt |
| `Lemez: HIBA – … mappa nem létezik` | Állítsd a `Node.DataPath` értékét a node adatmappájára |
| `Validátor: nem található` | Rossz a `Node.ValidatorAddress` |
| Nem jön Telegram üzenet | `sudo -u nodewatch nodewatch test-alert` kiírja a hibát. Gyakori ok: rossz token, rossz ChatId, vagy nem küldtél `/start`-ot a botnak |
| Nem jön Matrix üzenet (`M_FORBIDDEN` vagy `M_UNKNOWN_TOKEN` a naplóban) | `M_FORBIDDEN`: a bot nincs bent a szobában – hívd meg, és futtasd újra: `sudo nodewatch matrix-login`. `M_UNKNOWN_TOKEN`: a bot munkamenetét kijelentkeztették – futtasd újra a `matrix-login` parancsot |
| `Már fut egy NodeWatch ezzel az állapotmappával` | Kézzel indítottad a `run` parancsot, miközben a szolgáltatás is fut. Kézzel csak a `check` és a `test-alert` kell |
| „A relay chain peerjei nem kérdezhetők le” figyelmeztetés | A relay chain RPC nem engedi a `system_peers` hívást. Helyi (127.0.0.1) kapcsolaton ez alapból engedélyezett; nézd meg, nincs-e `--rpc-methods safe` a relay chain kapcsolói között |
| „A NodeWatch nem tudja ellenőrizni a node-ot” | Öt ellenőrzés egymás után hibával állt le; ilyenkor újraindítás sincs. A hiba oka a naplóban: `journalctl -u nodewatch -n 50` |
| Szeretnéd, hogy 24 órán belül újra indíthasson | `sudo systemctl stop nodewatch && sudo rm /var/lib/nodewatch/state.json && sudo systemctl start nodewatch` (a nyitott riasztások állapota is törlődik) |

Részletesebb napló: a beállításfájlban `"VerboseLog": true`, majd `sudo systemctl restart nodewatch`.
