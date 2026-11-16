# Enter The Gungeon Online Co-Op Mod

**Online co-op with independent cameras. Choose any character. Free and open source.**

Version 1.2.8 (beta). Two players are the normal game. Three to eight players are experimental. Steam, GOG and Epic copies play together. This is a BepInEx 5 plugin. It does not include any files from the game.

> **AI assistance.** Most of this mod's code was written with AI coding assistants from Anthropic, Claude Opus and Claude Fable, under the author's direction. It was not generated unattended and uploaded as-is. The repository's commit trailers name those models. The project file records the same fact as assembly metadata, `AI_Assisted_Creation` and `AI_Model_Vendor` (`Anthropic (Claude Opus, Claude Fable)`), and a build from that project emits the attributes. The DLL in this 1.2.8 package is the GitHub release build from 15 November 2026, which was compiled before those attributes were added, so they are not inside this particular file yet. This listing text was prepared with AI assistance as well (Grok, from xAI), from the public source and the previous listing. The source is public and MIT licensed. Please keep the listing in the **AI Generated** category.

![Your friend next to you, with their username above them](https://raw.githubusercontent.com/Hero88go/GungeonOnlineCoOpMod/main/docs/media/main-friend-name.png)

Gungeon's own co-op is couch co-op: one shared camera, and player two starts as the Cultist. This mod lets each player use their own PC.

**Your own camera.** Each player has their own screen, controller and HUD. Boss intros still play for everyone.

**Choose any character.** Pilot, Convict, Marine, Hunter, Robot, Bullet, or anyone you have unlocked. Two players can pick the same character.

**Two co-op styles** (the host picks in Settings):

- *Classic* (default): when one player starts a fight, the others are pulled into that room, like local co-op. Each player keeps their own camera.
- *Explore*: you can split up. Only one room can be in a fight at a time, so nobody starts a second fight alone and nobody is teleported.

**The host's game is the one that counts.** The other games follow it. About once a second the games compare enemies, rooms, chests, health, money and inventories, and a difference is corrected. Your own character still responds on the frame you press.

**A dropped player does not delete the run.**

- A short connection hiccup: play continues.
- Someone drops or their game closes: the run waits with no time limit. They reconnect with their character, guns, items, health and position.
- The host closes: the run is saved every floor and every minute. The host starts the game again and picks *Resume saved run*.
- Stopping on purpose: the host picks *Save run and quit*, or keeps the run in one of three slots with *Save run to a slot*.
- Stuck in a wall: *I'm stuck* in the online menu moves you to clear floor nearby.
- Quick restart is off during online play.

**Ways to join.**

- **Invite code.** The host's code is copied for them. The friend pastes it and picks *Join*. Works across Steam, GOG and Epic.
- **Steam invite.** On Steam, *Invite Steam friend* uses the Steam overlay. If a direct connection never opens and both players are on Steam, the join switches to Steam by itself.
- **IP address.** For a VPN (Radmin VPN, Hamachi, Tailscale or ZeroTier), the Host screen lists the host's VPN and home-network addresses with the port. *Copy IP* copies the first one. The friend pastes it into Join. `ip:port` still works. The default port is UDP 47624.

![The online menu](https://raw.githubusercontent.com/Hero88go/GungeonOnlineCoOpMod/main/docs/media/menu.png)

## Install (r2modman, Thunderstore Mod Manager, or Gale)

1. Select **Enter the Gungeon** in the mod manager and install **GungeonOnlineCoOp** (team GungeonOnline). **BepInExPack_EtG 5.4.2101** is installed with it. Do not pack your own copy of BepInEx into this mod.
2. Start the game with **Start modded**.
3. Pause. **PLAY ONLINE** should be in the pause menu.

Everyone in the session needs this same version and the same other mods. Sharing a mod-manager profile code is the reliable way to do that. Update through the mod manager. This mod does not download or install its own updates. If a newer version exists it only shows a message.

**Linux and Steam Deck (Proton).** The mod is for the Windows build of the game. In Steam, open the game's **Properties**. Under **Compatibility**, force a Proton version. Under **General**, set **Launch options** to:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

Without that launch option, Proton skips the mod's loader and **PLAY ONLINE** never appears.

## Play

1. Pause and pick **Play Online** (or hold **Back + Start**, or press **F8**).
2. One player picks **Host**. Send the invite code, a Steam invite, or the IP from the Host screen.
3. The friend picks **Join**.
4. Each player picks a character in their own Breach. The host enters the Gungeon when everyone is ready.

If the host's router blocks the first packet, the mod asks the host's game to open a path. That takes a few seconds. The details of what is sent are in the table below. If both routers are strict, use a VPN and join with the IP from the Host screen. The host's firewall has to allow Enter the Gungeon.

![Play Online in the pause menu](https://raw.githubusercontent.com/Hero88go/GungeonOnlineCoOpMod/main/docs/media/pause-menu-entry.png)

## Three to eight players (experimental)

On the Host screen, set **Players** from 2 to 8. Send the same invite to everyone.

- Each player has their own camera, controller and HUD. Players 3 and 4 get hearts beside the first two. Players 5 to 8 get a second row.
- While anyone is still up, going down is a knockout. The run ends when everyone is down. A revive chest or boss pedestal brings everyone back.
- Enemy health is 1.25x with 2 players (the game's own number), 1.5x with 3, and 1.75x with 4. Change it in the config if you want a different fight.
- **Your color** sets your name tag, outline and HUD face.

More than two players is experimental. Please report what breaks.

## Other mods

On join, the mod compares installed mods and lists any that differ, before the run starts. Mods that change how floors are built can still produce a different floor. When that happens the joining player is sent back to the Breach with a message, instead of playing inside the wrong walls.

Do not combine this with another online co-op mod. That includes GoTeamAlex's *Gungeon Online* and the *GungeonTogether* mods. Those are separate projects. They patch the same parts of the game and will conflict. This project is not a reupload of them, and it does not contain their code. See [CLEAN-ROOM.md](https://github.com/Hero88go/GungeonOnlineCoOpMod/blob/main/docs/CLEAN-ROOM.md).

ExpandTheGungeon 3.0.8 with MtG API 1.9.2 and BepInExPack_EtG 5.4.2101 has been played across several floors. That is not a promise that every mod pack works.

## Network connections and privacy

There is no analytics and no tracking. The mod does not download code, and it does not update itself. Updates come only from your mod manager. These are all the network connections in version 1.2.8:

| When | Where | What is sent | How to turn it off |
|---|---|---|---|
| Hosting or joining | The other players' games, direct UDP or Steam networking | The session's game data | Don't play online |
| Hosting or joining | Your router (UPnP, on your own network) | A request to open the host port (47624 unless you changed it) | `UseUpnp` in `BepInEx/config/gungeononline.cfg` |
| Hosting or joining | A public STUN server (`stun.l.google.com:19302` unless you changed it) | A standard request so the invite code can carry your public address | `StunServer` in the config |
| While hosting, and when joining by invite code or `ip:port` | The author's join relay, a Cloudflare Worker at `gungeon-report-relay.firescribe-share-worker.workers.dev/knock` | The joiner's and the host's public address and port. Also a token made from the host's local player id (a random id stored in the config, not an account name). When the host is running under Steam, the host's Steam id is sent too, so a Steam joiner can fall back to Steam if the direct path never opens. The mod is written to keep those knocks only long enough to open the path (about 30 seconds). | There is no in-game switch. It runs only while you host or join |
| Once per launch, about 16 seconds after startup | GitHub (`api.github.com`, this repository's latest release) | A read of the latest version number. The request's user agent is `GungeonOnlineCoOpMod`. Nothing about your account is added. If a newer version exists you get one message. The file is not downloaded. | *Check for updates* under Settings, Help and advanced (`CheckForUpdates` in the config). Default is on |
| Only when you press *Send report* | The same Worker, at `/report` | One text file: mod and game version, whether you are host or guest, ping and loss, the mod's settings file, the desync and metrics logs, and the last lines of `BepInEx/LogOutput.log`. Invite codes, IP addresses, Steam ids and the Windows user name in file paths are removed first. The report also includes the kinds of network adapter the host had (for example `Radmin-VPN` or `same-network`), never the addresses. A copy is saved in the game folder, in **Gungeon Online Reports**, so you can read it. *Open reports folder* opens that folder in Windows Explorer. It does not run anything else. | Don't press *Send report*. You can attach the saved file to a [GitHub issue](https://github.com/Hero88go/GungeonOnlineCoOpMod/issues) instead |

`LogOutput.log` can contain lines from your other mods. Read the saved copy before you send it if you would rather share it yourself.

## Settings

Settings are in the online menu and in `BepInEx/config/gungeononline.cfg` (in the mod manager: **Config editor**). Run backups are under `BepInEx/GungeonOnline/backups/` in the profile. The full setting list is in the [project README](https://github.com/Hero88go/GungeonOnlineCoOpMod/blob/main/README.md).

## Troubleshooting

- **No PLAY ONLINE:** launch with **Start modded**, with BepInExPack_EtG in the same profile. On Linux or Steam Deck, set the `WINEDLLOVERRIDES` launch option above.
- **"Your friend has a different version":** everyone needs 1.2.8. Update in the mod manager, or share a profile code.
- **Can't connect:** wait a few seconds for the path through the host's router. If you are both on Steam, the join can switch to Steam. Otherwise use the reply code (*Let in friend*) or a VPN IP from the Host screen. Swapping who hosts often works.
- Anything else: [open an issue](https://github.com/Hero88go/GungeonOnlineCoOpMod/issues) and, if you are willing, press *Send report*.

## FAQ

**Does it use rollback?** No. The host's game is authoritative, and your own character is predicted locally, so your inputs are not delayed by ping. Your friend's character, the enemies and the bullets are about half a round trip behind.

**Do I need to own the game?** Yes. Each player needs their own copy. This package contains no game files.

**Will it touch my save?** The normal save is used, and a run you finish online counts. Backups of the online run are kept separately in the profile.

**Is this affiliated with Dodge Roll or Devolver?** No. Enter the Gungeon is their game. This is an unofficial fan project.

## Credits and license

Made by Hero88go, with the AI assistance described at the top. Built on [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX). Source: [GitHub](https://github.com/Hero88go/GungeonOnlineCoOpMod). MIT license, included in this package as `LICENSE`.
