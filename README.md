# mosaic-live-watchdog

Watchdog and alerting agent for [Mosaic Chain](https://github.com/Mosaic-Chain) validator nodes on Linux.

**NodeWatch** runs next to the node as a small systemd service. It checks the node every 30 seconds through its local
RPC ports and sends an alert when something goes wrong. It can send alerts to Telegram, Matrix, a SafeChat server or a
relay service. When the node is stuck for 10 minutes, it restarts the chain service, at most once a day.

> **Status:** first release (0.1.0). Covered by unit tests and tried against mock servers, but not yet run on a live
> node for a longer time.
> Alert texts, log messages and the Hungarian install guide are in Hungarian; the code and this README are in English.

## Contents

| Folder | What is in it |
|---|---|
| [`agent/`](agent) | NodeWatch, the node agent (.NET 10, no third-party packages), its tests and install files |
| [`docs/`](docs) | [Event format](docs/event-format.md) and the [Hungarian install guide](docs/TELEPITES.md) |
| `relay/` | *Planned:* the relay service that forwards alerts and reports nodes that went silent |

## What it watches

| Check | Problem | Level |
|---|---|---|
| `relay.rpc` | The embedded relay chain node does not answer on its RPC port (default `127.0.0.1:8844`) | critical |
| `relay.block_progress` | The relay chain best block has not moved for 3 minutes | critical |
| `relay.full_peers` | No FULL or AUTHORITY peer on the relay chain (only those can be synced from) / only one / the peer list cannot be read | critical / warning / warning |
| `para.rpc` | The parachain RPC (default `127.0.0.1:9944`) does not answer | critical |
| `para.finality` | The parachain has not finalized a new block for 5 minutes | critical |
| `para.sync` | The parachain has no peers / is still syncing after 10 minutes | critical / warning |
| `validator.state` | The validator is `Chilled` / `Faulted` / not found in `NftStaking.ValidatorStates` / its state cannot be read | critical / warning |
| `host.service.<unit>` | A watched systemd unit is not running (the first one, the chain, is critical) | critical / warning |
| `host.disk` | Free space on the node's data disk is low (15% / 5%), or it cannot be read | warning / critical |
| `agent` | NodeWatch itself failed 5 polls in a row, so it cannot watch (or restart) the node | critical |

The `relay.full_peers` check exists because of a known stall: Mosaic nodes run the relay chain in warp-unsafe mode and
report the `NONE` role, so the sync engine cannot sync from them. A node whose relay peers are all `NONE` stops
following the relay chain without any error in its log.

A problem must be seen on two polls in a row before an alert goes out. Every alert gets a matching "resolved" message
when the problem is gone. Critical alerts are repeated every 30 minutes while they last.

## The watchdog: automatic restart

When one of these checks is critical for **10 minutes without a break** —
`relay.rpc`, `relay.block_progress`, `relay.full_peers`, `para.rpc`, `para.finality` —
NodeWatch restarts the chain service (`systemctl --no-block restart mosaic-chain-mainnet.service`) and sends an alert
about it.

Safety rules:

- **At most one restart in 24 hours.** The time of the restart is written to disk (with fsync) *before* the command runs,
  so a hanging or failing restart, a crash or a reboot can never lead to a second one. Within the same boot the 24 hours
  are also measured on the time since boot, so a wall-clock jump (for example a wrong RTC corrected by NTP) cannot
  shorten it. If the time cannot be saved,
  there is no restart. If the state file is unreadable, NodeWatch assumes a restart just happened. If the node fails
  again within 24 hours, NodeWatch sends one "manual action needed" alert instead of restarting again.
- **A freshly started node always gets the full 10 minutes.** The outage only counts while NodeWatch could fairly judge
  the node: the chain unit has been active for at least 10 minutes (systemd's own start time is checked, so a manual
  restart between two polls is noticed), `update-mosaic.service` (the Mosaic auto-update) is not running, and the polls
  were not interrupted (a suspended machine, a paused VM or a clock jump starts the measurement again).
- **No restart while the service is stopped or starting.** A unit you stopped on purpose stays stopped; you only get an
  alert that it is not running.
- **15 minutes grace** after a restart: the node may catch up before NodeWatch judges it again.
- **Only one NodeWatch** can run at a time (lock file), and `run` refuses to run as root.
- NodeWatch runs as an unprivileged user. A polkit rule allows this user to restart **only** the chain unit.

All of these numbers can be changed in the config (`Restart` section), and the watchdog can be switched off with
`"Enabled": false`.

## Where alerts go

| Mode | Behaviour |
|---|---|
| `direct` | NodeWatch sends alerts straight to the configured outputs (`Sinks`): Telegram, Matrix, SafeChat |
| `relay` | Everything goes to the relay service, which forwards it. The relay also alerts when the node stops sending heartbeats (for example when the node's internet connection is down) |
| `both` | Relay first; critical alerts the relay has not accepted within 2 minutes also go straight to the outputs |

Outgoing messages wait in an on-disk queue (`/var/lib/nodewatch/outbox`) and are retried with backoff for up to 24
hours, so an alert is not lost when the network or the chat server is down for a while.

Every message is a [CloudEvents 1.0](https://cloudevents.io) JSON event; see [docs/event-format.md](docs/event-format.md).
Telegram and Matrix get a formatted text message, SafeChat and the relay get the event itself.

## Install (Ubuntu 24.04)

You need a release package `nodewatch-<version>.tar.gz` (from the
[Releases](https://github.com/Attila-maker/mosaic-live-watchdog/releases) page, or build it with `agent/build.sh`).
On the node:

```bash
wget https://github.com/Attila-maker/mosaic-live-watchdog/releases/download/v0.1.0/nodewatch-0.1.0.tar.gz
wget https://github.com/Attila-maker/mosaic-live-watchdog/releases/download/v0.1.0/nodewatch-0.1.0.tar.gz.sha256
sha256sum -c nodewatch-0.1.0.tar.gz.sha256
tar -xzf nodewatch-0.1.0.tar.gz
cd nodewatch-0.1.0
sudo ./install.sh
```

`install.sh` installs `dotnet-runtime-10.0` from the Ubuntu archive if needed, creates the `nodewatch` system user,
copies the program to `/opt/nodewatch`, creates `/etc/nodewatch/appsettings.json` (only on first install), and installs
the systemd unit and the polkit rule. Running it again upgrades the program and keeps the config.

Then:

```bash
# 1. secrets go into separate files, readable by root and the nodewatch group only
sudo install -m 0640 -o root -g nodewatch /dev/null /etc/nodewatch/telegram.token
sudo nano /etc/nodewatch/telegram.token           # paste the bot token from @BotFather

# 2. settings: node name, validator address, chat id ...
sudo nano /etc/nodewatch/appsettings.json
sudo -u nodewatch nodewatch telegram-chatid       # after you sent /start to your bot

# 3. try it
sudo -u nodewatch nodewatch check                 # what NodeWatch sees on the node
sudo -u nodewatch nodewatch test-alert            # a test alert and its "resolved" pair

# 4. start
sudo systemctl start nodewatch
journalctl -u nodewatch -f
```

To remove it: `sudo ./uninstall.sh` (keeps config and state) or `sudo ./uninstall.sh --purge`.

## Configuration

The config file is JSON with comments: `/etc/nodewatch/appsettings.json`. The commented example is
[`agent/deploy/appsettings.example.json`](agent/deploy/appsettings.example.json). The most important settings:

| Setting | Default | Meaning |
|---|---|---|
| `Node.Name` | | Name of the node in alerts |
| `Node.ValidatorAddress` | empty | SS58 address (or `0x` public key) of the validator; empty: no validator check |
| `Node.RelayRpc` / `Node.ParaRpc` | `http://127.0.0.1:8844` / `:9944` | Local RPC endpoints |
| `Node.DataPath` | `/var/lib/mosaic-mainnet` | Folder whose disk is checked |
| `Node.Services` | chain + indexer unit | Units to watch; the first one is critical |
| `Checks.*` | see example | Poll interval and thresholds |
| `Restart.*` | 10 min, 24 h, 15 min grace | The watchdog (see above) |
| `Mode` | `direct` | `direct`, `relay` or `both` |
| `Sinks` | one Telegram output | Outputs, each with its own `MinSeverity` (`info`, `warning`, `critical`) |

Secrets (bot token, access token, node token) are never written into the config, only the path of the file holding
them (`BotTokenFile`, `AccessTokenFile`, `TokenFile`, `NodeTokenFile`).

### Outputs

`Sinks` is a list: you can use several outputs at once. Each output gets the alerts at or above its own
`MinSeverity` (`info`, `warning` or `critical`); the "resolved" message goes to every output that got the alert.
After every change, try it and restart the service:

```bash
sudo -u nodewatch nodewatch test-alert
sudo systemctl restart nodewatch
```

#### Telegram

1. In Telegram, open **@BotFather**, send `/newbot`, and choose a display name and a username ending in `bot`.
   BotFather answers with a token like `7012345678:AAH…`. Keep it secret: with it anyone can post as your bot.
2. Save the token on the node, alone on one line:

   ```bash
   sudo install -m 0640 -o root -g nodewatch /dev/null /etc/nodewatch/telegram.token
   sudo nano /etc/nodewatch/telegram.token
   ```

3. Find your bot in Telegram and send it `/start` (a bot cannot write to you first).
4. Add the output with a placeholder chat id, then ask NodeWatch for the real one:

   ```jsonc
   { "Type": "telegram", "BotTokenFile": "/etc/nodewatch/telegram.token", "ChatId": "IDE_JON_A_CHAT_ID", "MinSeverity": "warning" }
   ```

   ```bash
   sudo -u nodewatch nodewatch telegram-chatid
   # ChatId: 123456789   (private: Attila)
   ```

   Put the number into `ChatId` (as a string) and run `test-alert`.

- **Group:** add the bot to the group and send `/start@<your_bot>` in the group, then run `telegram-chatid` again.
  Group ids are negative, for example `-1001234567890`.
- **Channel:** add the bot to the channel as an administrator who may post messages, post something in the channel,
  then run `telegram-chatid`.
- `telegram-chatid` reads the bot's recent updates, which Telegram keeps for 24 hours: run it soon after the
  message.
- Critical alerts arrive with sound; warnings and "resolved" messages arrive silently.

#### Matrix

NodeWatch posts into one Matrix room as a separate bot user, as `m.notice` messages. It does not do Matrix end-to-end
encryption, so use a room without encryption.

1. **Create a bot account** on your homeserver, separate from your own account. For example on matrix.org: open
   <https://app.element.io> in a private browser window and register a user such as `mynode-alerts`.
2. **Create the room** with your own account (for example "Node alerts"), and switch **off** "Enable end-to-end
   encryption" when you create it (it cannot be switched off later). Invite the bot user
   (`@mynode-alerts:matrix.org`).
3. **Find the room id:** in Element, *Room settings → Advanced → Internal room ID*. It starts with `!`, for example
   `!AbCdEfGh:matrix.org`. The room address (`#name:server`) does not work here.
4. **Add the output** and create the (empty) token file:

   ```jsonc
   { "Type": "matrix", "Homeserver": "https://matrix-client.matrix.org", "RoomId": "!AbCdEfGh:matrix.org",
     "AccessTokenFile": "/etc/nodewatch/matrix.token", "MinSeverity": "warning" }
   ```

   ```bash
   sudo install -m 0640 -o root -g nodewatch /dev/null /etc/nodewatch/matrix.token
   ```

   `Homeserver` is the client API address of the server. For matrix.org it is `https://matrix-client.matrix.org`;
   for another server, look at `https://<server>/.well-known/matrix/client` (`m.homeserver.base_url`).
5. **Log the bot in:**

   ```bash
   sudo nodewatch matrix-login
   ```

   It asks for the bot's user name and password. It logs in, saves the access token into the token file and joins the
   room (this also accepts the invite). The password is not stored.
6. Run `sudo -u nodewatch nodewatch test-alert`.

Do not copy the access token from Element's settings: on servers that use the Matrix Authentication Service
(matrix.org since 2025) that token can expire within minutes. The token from `matrix-login` does not expire; it stays
valid until you sign out the "NodeWatch" session of the bot (for example in Element, *Settings → Sessions*).

#### SafeChat

```jsonc
{ "Type": "safechat", "Url": "https://chat.example.com/api/ops/alerts",
  "TokenFile": "/etc/nodewatch/safechat.token", "MinSeverity": "warning" }
```

The SafeChat output posts the CloudEvent with `Authorization: Bearer <token>` and `X-SafeChat: 1`. The token is an
integration token that only the SafeChat admin can create, bound to one user; the alerts appear for that user.

## Commands

```
nodewatch [command] [--config <file>] [--verbose]

  run              watch, alert and restart (default; systemd runs this)
  check            one-time query: prints what NodeWatch sees, sends nothing
  test-alert       sends a test alert and a "resolved" message to every configured target
  telegram-chatid  lists the chat ids that wrote to the Telegram bot
  matrix-login     logs the Matrix bot in, joins the room and saves the access token (run with sudo)
  version          prints the version
```

## Security

- Runs as the unprivileged `nodewatch` user under a hardened systemd unit (`ProtectSystem=strict`, `NoNewPrivileges`,
  no capabilities, private `/tmp`, …).
- The only privileged action is `systemctl restart <chain unit>`, allowed by a polkit rule for this one unit and verb.
- It never needs the validator keys: `/opt/mosaic/secrets` is made inaccessible to the service.
- It opens no port. It only makes outgoing HTTP calls: to the node's local RPC ports and to the configured outputs.
- It only reads: `chain_getHeader`, `chain_getFinalizedHead`, `system_health`, `state_getStorage` and `system_peers`.
  `system_peers` is an "unsafe" RPC method; on the local relay chain RPC it is allowed by default (`--rpc-methods auto`
  allows unsafe methods on localhost). Without it, only the peer check is skipped (with a warning), nothing else.
- Secrets live in separate `0640 root:nodewatch` files.

## Building from source

```bash
sudo apt install dotnet-sdk-10.0      # Ubuntu 24.04
cd agent
./build.sh                            # runs the tests, then makes out/nodewatch-<version>.tar.gz
```

The solution (`agent/NodeWatch.slnx`) also opens in Visual Studio 2026 or Rider. The test project is a small self-made
runner without NuGet packages: `dotnet run --project agent/tests/NodeWatch.Tests`.

## Roadmap

- **Relay service** (ASP.NET Core, Windows IIS or Linux): receives events from registered nodes with admin-issued node
  tokens, stores the output channel secrets encrypted, forwards alerts, and alerts by itself when a node stops sending
  heartbeats.
- SafeChat integration endpoint (`POST /api/ops/alerts`) on the SafeChat side.

## License

[Apache License 2.0](LICENSE)
