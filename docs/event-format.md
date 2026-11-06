# Event format

NodeWatch sends every alert as a [CloudEvents 1.0](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md)
event in *structured* JSON mode:

```
Content-Type: application/cloudevents+json; charset=utf-8
```

Chat outputs (Telegram, Matrix) get a formatted text made from the same event. The relay and SafeChat outputs get the
event itself.

## Event types

| `type` | When |
|---|---|
| `eu.aisdev.nodewatch.alert.firing` | A problem started (or a reminder for a critical one that is still open) |
| `eu.aisdev.nodewatch.alert.resolved` | The problem is gone |
| `eu.aisdev.nodewatch.heartbeat` | "I am alive", every 60 s, sent only to the relay |

## Example

```json
{
  "specversion": "1.0",
  "id": "74fe8c29-8a3f-4751-8bb9-5aae25f62260",
  "source": "nodewatch://my-mosaic-node",
  "type": "eu.aisdev.nodewatch.alert.firing",
  "subject": "relay.block_progress",
  "time": "2026-11-06T10:26:40.93+00:00",
  "datacontenttype": "application/json",
  "channels": "telegram,safechat",
  "data": {
    "alertKey": "my-mosaic-node/relay.block_progress",
    "status": "firing",
    "severity": "critical",
    "title": "A relay chain nem halad",
    "text": "3 perce a #29481022 blokkon áll.",
    "node": "my-mosaic-node",
    "network": "mosaic-mainnet",
    "startsAt": "2026-11-06T10:23:40.11+00:00"
  }
}
```

## Attributes

| Attribute | Meaning |
|---|---|
| `id` | Unique per event (UUID). A receiver can use it to drop duplicates: the outbox may send the same event again after a timeout |
| `source` | `nodewatch://<Node.Name>` |
| `subject` | The check name (see below); missing for heartbeats |
| `time` | When the event was made |
| `channels` | *Extension, optional.* Comma-separated relay channel names that should deliver this event (from `Relay.Channels`). Missing: all channels of the node |

## `data`

| Field | Meaning |
|---|---|
| `alertKey` | `<node>/<check>`: the same for the firing, reminder and resolved events of one problem |
| `status` | `firing` or `resolved` |
| `severity` | `info`, `warning` or `critical`. On `resolved` it is the severity the alert had |
| `title` | One-line summary (Hungarian) |
| `text` | Details (Hungarian), may be empty |
| `node`, `network` | From the config |
| `startsAt` | When the problem started (it can be earlier than the first event: e.g. when the chain stopped moving) |
| `endsAt` | Only on `resolved` |
| `facts` | Optional key/value details. Heartbeats carry: `relayBest`, `fullPeers`, `totalPeers`, `paraBest`, `paraFinalized`, `validator`, `agentVersion` |

### Check names (`subject`)

`relay.rpc`, `relay.block_progress`, `relay.full_peers`, `para.rpc`, `para.finality`, `para.sync`, `validator.state`,
`host.disk`, `host.service.<unit>`, `restart` (the watchdog's own alerts), `agent` (NodeWatch cannot poll the node),
`test` (from `nodewatch test-alert`).

## HTTP contract of the receivers

Both receivers below take one event per `POST` request.

| Answer | NodeWatch does |
|---|---|
| `2xx` | Done, removes the event from the queue |
| `408`, `429`, `5xx`, network error, timeout (20 s) | Retries with backoff (5 s, 10 s, 20 s, … up to 10 min) for 24 hours |
| any other `4xx` | Drops the event and logs an error (retrying would not help) |

### SafeChat output

```
POST https://<safechat-server>/api/ops/alerts
Authorization: Bearer <integration token>
X-SafeChat: 1
Content-Type: application/cloudevents+json; charset=utf-8
```

The integration token is created by the SafeChat admin and is bound to one user; the alert appears for that user.

### Relay (planned)

```
POST https://<relay>/api/v1/events
Authorization: Bearer <node token>
Content-Type: application/cloudevents+json; charset=utf-8
```

The node token is issued by the relay admin for one node. Heartbeats go to the same endpoint, but are not queued: an old
heartbeat is worth nothing.
