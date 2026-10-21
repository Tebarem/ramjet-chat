# Chat

> **Requires `Ramjet.Api` 1.0.0-preview.3 or newer, and a server that runs it.** This plugin uses the private-message service (`IPrivateMessages`), which is new in that version; it does not compile against or run on older versions.

Ramjet's chat plugin (id `chat`): formats with prefix, suffix and group templates, gated MiniMessage-style formatting with hover and click, global, local and staff channels, private messages with ignore and social spy, `@mentions`, word, link, capitals and repetition rules, cooldowns, chat mute commands, a daily chat log and emoji shortcodes. Messages stay signed: the typed text is never changed, the styling goes into the name, the unsigned decoration and the filter mask.

Clean-room: written from the public behaviour of chat systems and the Ramjet API only.

- `src/`: the plugin, a Ramjet source plugin. Deploy it as `plugins/chat/` (the server compiles it). `src/chat.csproj` exists for an out-of-server compile check only.
- `src/DESIGN.md`: the message path, the formats and markup rules, the private message service contract, the permission nodes, the configuration and the known gaps.
- `tests/Chat.Tests/`: console tests for the pure core (templates, markup gate, mentions, filters, limits, durations, ignores, log lines, config). No packages, no server.

## Build and test

The plugin builds against the public [`Ramjet.Api`](https://www.nuget.org/packages/Ramjet.Api) package (`1.0.0-preview.4` or newer) from nuget.org; no engine build is needed.

```bash
dotnet build src/chat.csproj
dotnet run --project tests/Chat.Tests
```

The tests need no engine build.

## Commands

| Command | Node | Purpose |
|---|---|---|
| `/ch <global\|local\|staff>`, `/g`, `/l` | `chat.channel.staff` for staff | switch the channel you talk in |
| `!text` | none | one global message while in another channel |
| `/r <message>` | `chat.reply` | reply to your last private message |
| `/ignore <player>`, `/unignore <player>`, `/ignorelist` | `chat.ignore` | one-way ignore |
| `/socialspy` | `chat.socialspy` | copies of private messages to you |
| `/mentions` | `chat.mention` | turn mention sounds on or off |
| `/mute <player> [duration] [reason]`, `/unmute <player>`, `/mutelist` | `chat.mute` | chat mutes (engine-owned) |
| `/chatlog <player> [count]` | `chat.chatlog` | recent chat lines of a player |
| `/chat reload` | `chat.reload` | re-read `config.json` |

`/msg`, `/tell` and `/w` are the engine's; the chat plugin's rules apply to them through the private message event.

## License

MIT, see `LICENSE`.
