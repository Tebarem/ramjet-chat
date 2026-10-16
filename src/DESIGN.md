# Chat: design notes

Plugin id `chat`, namespace `RamjetChat`. A source plugin for the Ramjet server (deployed as `plugins/chat/`); `src/chat.csproj` only exists for an out-of-server compile check against the scripting API.

## Principles

- **Player messages stay signed.** The typed text (`Message`) is never changed. Restyling goes into the sender name (`SenderName`), the body (`Decoration`, unsigned content) and, for hidden words, the filter mask (`FilterKind`, `FilterBits`). The chat type stays `Chat`.
- **Clean room.** Written from the public behaviour of chat systems and the Ramjet API; no code from other chat plugins.
- **Pure core, thin engine layer.** Everything that does not touch the API is in `src/Core/` and `src/Config/`, with no `Mc.Scripting` types. The console tests in `tests/Chat.Tests/` compile those files directly, so the parsing and the rules are tested without a server.
- **Per-message work is bounded.** Templates are parsed and literal parts parsed into components at load. Per message: one recipient list, one markup build, a few component joins. No per-tick work.

## Layout

| Path | Contents |
|---|---|
| `src/Core/Template.cs` | Format templates: tokens, split at `{message}`, format selection by node |
| `src/Core/Markup.cs` | Markup gate (tags the sender may use), legacy `&` codes, mention finder |
| `src/Core/Filters.cs` | Word list, link detection, capitals, filter mask bits, filter actions |
| `src/Core/Throttle.cs` | Channel cooldown, message window, repeat count, channel kinds, radius |
| `src/Core/Support.cs` | Durations for mutes, the ignore relation, the chat log line |
| `src/Config/ChatConfig.cs` | `config.json` model and its JSON source-generated context |
| `src/Api/IPrivateMessagePolicy.cs` | The private message policy service for plugins that deliver messages themselves (chat also implements the engine's `IPrivateMessages`) |
| `src/Engine/ChatEngine.cs` | Compiled settings, per-player state, permission nodes, persisted lists |
| `src/Engine/Pipeline.cs` | The `PlayerChat` handler: cooldown, filters, recipients, formatting |
| `src/Engine/PrivateMessages.cs` | Private message policy, the `PrivateMessage` event, social spy, `/r` |
| `src/Engine/ChatCommands.cs` | Command tree |
| `src/Engine/ChatLog.cs` | Batched chat log writer, retention, bounded reads |
| `src/ChatPlugin.cs` | Plugin entry: wiring of config, events, commands and the service |

## Message path (`PlayerChat`)

Order, which is also the order of the checks in the code:

1. **Channel.** The active channel of the sender. A leading `!` (one character, then the text) sends this one message globally; the `!` is hidden in the decoration, the signed text keeps it. A staff channel the sender no longer holds falls back to global.
2. **Cooldown and window.** `Throttle.Admit`: a cooldown per channel, and at most N messages per window. Refused messages are not sent; the sender is told how long to wait. Bypass: `chat.bypass.spam`.
3. **Text rules** (`Screen`). Each matched rule has an action:
   - word list (whole words, any case): default `mask`;
   - link to a host that is not an allowed domain or a subdomain of one: default `block`;
   - capitals (enough letters, most of them capitals): default `log`;
   - repetition (the same normalised text N times in a row within the window): default `log`.
   `block` drops the message and tells the sender which rule it was. `mask` hides the matched characters from filtering recipients through the filter mask; the message is sent signed. `warn` tells the sender and sends. `log` only records. Bypass nodes: `chat.bypass.words|links|caps|repeat`. A mask on a rule other than the word list is treated as `log`.
4. **Chat log.** Every message reaching this point, and every blocked one, is queued for the log with its flags.
5. **Recipients.** Global: everybody online. Local: same world and within the radius (the edge counts; radius 0 means the whole world, never global). Staff: players with `chat.channel.staff`. Players who ignore the sender are always left out (unless the sender has `chat.bypass.ignore`). The sender is always included. When the list is not everybody, `ChatRoute.Restrict` limits the line to it.
6. **Formatting.** Described below. The body is decorated only when the gate, the channel label, the `!` or the template changed what is shown.
7. **Mentions.** `@name` for a player in the recipient list: the name is highlighted in the body and the player hears a sound, unless they turned mention sounds off. Needs `chat.mention` on the sender.

## Formats

- A format is a template: `{prefix}`, `{name}`, `{display}`, `{suffix}`, `{world}`, `{message}`. Unknown `{words}` stay literal. A template without `{message}` gets ` {message}` appended.
- The client draws `<name> body` from the vanilla chat type. So the text before `{message}` (trailing spaces dropped) becomes the sender component inside the brackets; the text after it is appended to the body as unsigned decoration.
- Format choice: the first format, in config order, whose node `chat.format.<name>` the sender holds; the last format is the default and needs no node.
- Prefix and suffix come from `IPlayerMeta` (the permissions plugin). They are parsed as markup (they are written by administrators). Without a provider they are empty.
- The name carries hover (the name) and click (suggest `/msg <name>`); insertion is the plain name.
- Literal text in a template is trusted config markup, parsed once at load.

## Markup (what a player may write)

Player text is never parsed as markup directly. `MiniMessageGate` keeps the tags the sender may use and removes the rest:

| Tag | Node | Notes |
|---|---|---|
| `<red>`, `<gold>` … (16 named colours) | `chat.color` | |
| `<#rrggbb>` | `chat.color.hex` | |
| `<bold>` `<b>`, `<italic>` `<i>` `<em>`, `<underlined>` `<u>`, `<strikethrough>` `<st>`, `<obfuscated>` `<obf>` | `chat.style.<name>` | |
| `<hover:show_text:…>` | `chat.hover` | the text is plain (no tags inside) |
| `<click:suggest_command:…>` | `chat.click.suggest` | |
| `<click:open_url:…>` | `chat.click.url` | the host must be an allowed domain |
| `<click:run_command:…>` | `chat.click.run` | staff only by default |
| `<reset>` | none | |

- A tag the sender may not use is removed; its text stays. A tag that is not known is shown as typed (`<` is escaped).
- Frames the player leaves open are closed at the end, so the style cannot leak.
- `&c`-style codes are read only when `LegacyCodes` is on; they become tags and pass the same gate.
- Shortcodes (`:smile:`) become glyphs from the `Emoji` table, in the decoration only.
- The result is parsed by the engine's MiniMessage parser (`Component.Parse`). The gate writes escapes the parser understands (`\<`).

## Item showing

`[item]` (node `chat.item`, default on) shows the item in the sender's main hand as a hover on the item's name. The text around it goes through the markup gate separately; mention highlighting is not applied to such lines.

## Private messages

- `/msg`, `/tell`, `/w` are the engine's executors. The engine fires `PrivateMessage` for each target; this plugin handles it:
  - before delivery (normal priority, awaited): the policy (`Check`) cancels a refused message and tells the sender; a message that goes out gets its decorated body (`Decorate`);
  - after delivery (monitor priority, only for messages that went out): `Delivered` records the reply partner of both players and the social spy copies.
- The policy (`IPrivateMessagePolicy.Check`): the sender needs `chat.msg`; the sender must not be muted (`ChatMute`, engine-owned); the target must not ignore the sender, unless the sender has `chat.bypass.ignore`.
- `/r <text>` replies to the last partner. It goes through the same policy and is delivered as unsigned chat lines (`Chat.SendTo`, incoming to the partner, outgoing to the sender). See "Known gaps".
- `/ignore`, `/unignore`, `/ignorelist`: one-way, persisted in `data/ignore.txt` (written off the tick). Staff with `chat.bypass.ignore` cannot be ignored.
- `/socialspy` (node `chat.socialspy`) turns copies on for the player; copies go to players with `chat.socialspy.see`. Not persisted.
- Reply partners are per session, cleared on quit.

## Mute

Mutes belong to the engine (`ChatMute`): the engine refuses a muted player's chat, `/say`, `/me` and private messages before a plugin sees them, and the table survives reloads and restarts. The plugin only adds the commands: `/mute <player> [duration] [reason]` (durations `30s`, `10m`, `2h`, `3d`, `1w`, sums such as `1h30m`, or `perm`), `/unmute`, `/mutelist`. A player with `chat.mute.exempt` can be muted only by someone with `chat.mute.force` (checked when the target is online).

## Channels and commands

- `/ch <global|local|staff>` switches the channel you talk in; `/g` and `/l` switch to global and local. The switch is per session.
- Staff channel needs `chat.channel.staff`.

## Chat log

- One line per message: `time (UTC, ms)`, `channel`, `player id`, `name`, `flags`, `text`, tab-separated. Tabs and line breaks in the text become spaces. Flags are `rule:action` pairs (for example `word list:mask`), or `-`.
- Files: `data/logs/yyyy-MM-dd.log` (UTC day). Lines are queued on the tick and written in batches by a thread-pool task every five seconds.
- Retention: files older than `LogRetentionDays` are removed at start-up.
- `/chatlog <player> [count]` (node `chat.chatlog`, count 1 to 200, default 20) reads the newest four files, scans at most 20 000 lines and 4 MB, and answers with the matching lines.

## Configuration

`config.json` in the data folder; written with defaults on first run. `/chat reload` (node `chat.reload`) re-reads it; a file that does not parse keeps the previous settings.

| Key | Default | Meaning |
|---|---|---|
| `Formats` | one `default` entry | `Name`, `Template`; the last entry is the default |
| `LocalRadius` | 100 | blocks; 0 = whole world |
| `MentionColor`, `MentionSound` | `#ffaa00`, `minecraft:entity.experience_orb.pickup` | mention highlight and sound |
| `LegacyCodes` | false | read `&c` style codes |
| `GlobalCooldownMs`, `LocalCooldownMs`, `StaffCooldownMs` | 0 | cooldown per channel |
| `MessagesPerWindow`, `WindowMs` | 6, 10000 | message window (0 = off) |
| `RepeatLimit`, `RepeatWindowMs` | 3, 30000 | repetition rule (0 = off) |
| `CapsMinLetters`, `CapsRatio` | 8, 0.75 | capitals rule |
| `WordAction`, `LinkAction`, `CapsAction`, `RepeatAction` | mask, block, log, log | `log`, `warn`, `mask` (words only), `block` |
| `Words` | empty | the word list |
| `AllowedDomains` | empty | link allowlist; also the hosts `open_url` may use |
| `Emoji` | five shortcodes | shortcode to glyph |
| `LogRetentionDays` | 14 | chat log retention |

Invalid action names fall back to the default of their rule.

## Permission nodes

Registered at enable (and when a reload adds formats). Defaults: player features are on; staff and bypass nodes are off.

- On by default: `chat.color`, `chat.color.hex`, `chat.style.*`, `chat.hover`, `chat.click.suggest`, `chat.click.url`, `chat.item`, `chat.mention`, `chat.msg`, `chat.reply`, `chat.ignore`.
- Off by default: `chat.format.<name>` (non-default formats), `chat.click.run`, `chat.socialspy`, `chat.socialspy.see`, `chat.bypass.{spam,repeat,caps,links,words,ignore}`, `chat.channel.staff`, `chat.mute`, `chat.mute.exempt`, `chat.mute.force`, `chat.chatlog`, `chat.reload`.

## Engine interaction

- Uses: `PlayerChatEvent` (sender name, decoration, filter mask), `PrivateMessageEvent`, `PlayerQuitEvent`, `ChatRoute.Restrict`, `Chat.SendTo`, `ChatMute`, `IPlayerMeta`, `OfflinePlayers`, `Server.Players`, the command registry, the scheduler, plugin config text files, permissions.
- The engine's chat pre-stage (mute before the event, text rules before the event) is not used: the text rules and cooldowns run in the plugin, after the engine has accepted the message (see "Known gaps").

## Known gaps

- **Enforcement runs in the plugin, not in the engine pre-stage.** Cooldowns and text rules run in the worker when `PlayerChat` is dispatched. A slow or stopped worker lets messages through unchecked; the awaited event then takes its default. The rules are otherwise the same. Moving them to the engine pre-stage needs an engine change (integration request).
- **`/r` is unsigned.** Delivering a signed private message needs the engine's `/msg` executor to run as the player, which the API does not offer. `/r` lines therefore show without a signature.
- Custom channels, `ChatChannels` service, cross-server chat: not started (phase 2 and 3 in the plan).
- Join and quit messages belong to the greeter plugin. Nothing here formats them. The quit message field (`PlayerQuitEvent`) is not used.
- Prefix hiding (`chat.prefix.hide`) and per-locale formats: not implemented.
- Placeholders (`IPlaceholders`) are not used for templates; the template tokens are fixed.
- Channel labels are shown only for local and staff lines. Global lines have no label.
- The staff log is the chat log file; there is no in-memory staff ring.
- Social spy and reply partners are not persisted.
- Command spam (commands per window) is not implemented here; the engine's command throttle covers it.
- Mute exemption is checked only when the target is online.
- Mentions are highlighted only for players in the recipient list and not ignoring the sender. A mention inside a `[item]` line is not highlighted.

## Checks that need a real client

- The decoration and the prefix of a signed line show with the signature intact (no "not secure" marker) for: a coloured body, a mention highlight, a glyph, a masked word, a local line and a line with a `!` removed.
- The vanilla layout of `/r` lines (incoming and outgoing chat types with the name and target arguments).
- The filter mask hides exactly the matched characters for a filtering recipient.
- Hover on the name and on `[item]` renders; suggest-command fills `/msg <name> `.

## Tests

`tests/Chat.Tests/` (console app, no packages): template parsing and selection, the markup gate (kept, stripped, escaped and unknown tags, hover and click rules, mentions, emoji, legacy codes), mention finding, the word list and its mask bits, link and capitals checks, cooldown, window and repeat, radius, durations, the ignore relation, the log line and the config round trip.

```bash
dotnet run --project tests/Chat.Tests
```

## Private message service contract

Chat registers two services.

`Mc.Scripting.Services.IPrivateMessages` (the engine API's contract, no reference to the chat assembly needed) is what the Essentials `/msg`
and `/r` override uses: `Send(sender, recipient, text)` applies the policy below, delivers unsigned lines in the vanilla `/msg` layout and
records the reply partners and spy copies. It returns `Ok`, `Offline`, `Ignored`, `Muted` or `Refused` (no `chat.msg`, empty text).

`RamjetChat.IPrivateMessagePolicy` is for a plugin that delivers private messages itself and wants the same rules and formatting:

```csharp
var chat = Services.TryGet<RamjetChat.IPrivateMessagePolicy>(); // null when chat is not enabled
var verdict = chat.Check(sender, target);                      // Allowed | NoPermission | Muted | Ignored
if (verdict != RamjetChat.PrivateMessageVerdict.Allowed) { /* tell the sender */ return; }
Component? body = chat.Decorate(sender, text);                 // null = show the typed text
// ... deliver ...
chat.Delivered(sender, target, text);                          // reply partners and social spy
```

The consumer references the chat assembly and lists `chat` in `depends` (or `softDepends`, falling back to its own delivery when the service is absent).
