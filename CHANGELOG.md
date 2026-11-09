# Changelog

## 1.2.7

- Fixed: when a friend was the hero of their Past, their screen could pick the wrong hero, and friends' screens got a second, frozen copy of each Past boss.
- When the hero beats a Past's boss, only that boss falls on the other screens. The Bullet's Past has a second boss after the first.
- Fixed: the Bullet's final boss did not fall on friends' screens.
- Joining from a Steam invite keeps trying for another minute before giving up, and the message no longer mentions invite codes.

## 1.2.6

- Character Pasts work online. The player the clock is aimed at is the hero of the Past on every screen, and everyone else comes along as their partner, like couch co-op.
- In a Past every screen runs the story itself. When the hero beats the Past's boss it falls on every screen, and when the hero's ending starts, everyone's does.
- The Pilot's Past: the Pilot flies their ship and everyone else flies a co-op ship. Before, a friend's screen could turn them into the Pilot's ship and add an extra player nobody controlled.
- The Gunslinger's Past and the reset to final-fight guns and full health now reach every player.

## 1.2.5

- Fixed: with the door wait on (the default), the host's open doors were shown shut on a friend's screen until the next repair, and every fight room was logged as a repaired desync. The doors now open and close at the same moments on every screen.
- A boss intro that fails on a friend's screen no longer leaves that screen out of step with the host's (where a blank could be spent on one screen only); the failure is logged and play goes on without the cutscene.
- Joining by invite code: when the direct path to the host never opens and both players are on Steam, the join switches to Steam by itself.

## 1.2.4

- Reports are saved in a folder of their own in the game folder, **Gungeon Online Reports**, instead of on your Desktop. Reports already on your Desktop are moved there when the game starts.
- New: *Open reports folder* (Settings, Help and advanced) opens that folder.

## 1.2.3

- Barrels are in the same place on every screen, including ones that started a little apart, and a barrel shot to pieces is gone for everyone.
- A table a friend sees flipped is flipped for everyone. Before, a table standing slightly apart on one screen could stay upright there.
- Fixed a freeze after a lag spike: a burst of held-up messages made the game sort the same timings thousands of times.
- Fixed a freeze for a friend on the title screen or picking a character while the host played on.
- The Gorgun's stone gaze no longer skips everyone after a stutter.

## 1.2.2

- Secret floors (the Oubliette, the Abbey and the others) take everyone. Before, the host went alone, the friend was told their mods did not match and could not rejoin. A friend who finds the way in first brings everyone along.
- Each player's pause menu is their own. Before, the host's pause and menu buttons also opened and moved the friend's menu.
- A friend's map stays on their screen; yours shows a small live map of what they are looking at, with everyone on it.
- A friend's gun points exactly where they aim. Before, the host's game worked their aim out again with its own aim assist settings, so their gun (and shots) could swing toward enemies they were not aiming at.
- Up to 8 players (host setting, experimental). Players 5 to 8 get a second row of hearts under players 3 and 4.
- Faces beside the hearts are no longer cut off (on the right half of the screen they now sit on the inner side of the hearts).
- When a fight starts, its doors stay open while a teammate is still outside, so they can walk in instead of being pulled across the floor (Gameplay setting, 0 to 30 seconds, 6 by default). A pull now fades in and says whose fight you joined.
- "I'm stuck" in the online menu moves you to clear floor nearby.
- Runs are saved every minute and at every floor; after a crash or a drop the host picks which recent run to resume. Runs can also be kept in three save slots (online menu).
- New Gameplay setting: shared shells and keys (on by default, like local co-op) or a wallet for each player.
- Kicked barrels, flipped tables and moving platforms are in the same place on every screen, and a friend's kicks and flips always count. Before, a friend's flip could leave an upright table that blocked like a flipped one.
- Enemy bullets no longer hang in the air on a friend's screen after the host's bullet is gone.
- "Send report" shows whether the report was sent while the online menu is open.
- The message for a floor that differs from the host's no longer blames other mods when there are none.

## 1.2.1

- Fixed a rare frozen loading screen when moving to a new floor. It happened when a friend kept taking hits while a floor loaded: every hit made the game search for the health display, which does not exist during a load, so the load never got to finish.

## 1.2.0

- Bosses look and move the same on a friend's screen as on the host's. The Dragun's head follows the players again (so it can be hit), the Treadnaught's gun turns to aim, the Gatling Gull's shadow shows before it lands, and bosses play their attack animations instead of standing in one pose (the Trigger Twins no longer slide around). Boss health bars follow the host.
- Enemy lasers (the Beholster and laser enemies) show for a friend, with their charge glow, and hurt them.
- The Gatling Gull's rockets and their landing marks show for a friend.
- The Cannonbalrog's dark attack darkens the room for everyone, so its eyes show where it fires from.
- Puddles enemies leave (the Gorgun's poison, fire, water) show for a friend and affect them.
- The Gorgun's stone gaze also turns a friend to stone.
- The warning mark where an enemy is about to drop in shows for a friend too.
- Switching guns as a friend (mouse wheel, number keys, quick switch) always switches on the host too. Before, the host could keep you holding an emptied gun, and your shots did no damage.
- Linux and Steam Deck: the README explains the Proton launch option the mod needs.

## 1.1.9

- Map pings: with the full map open, right click a spot, or press X / Square on a controller to ping the spot under the map crosshair. Everyone sees a ring in your color on their minimap, full map and screen for 8 seconds, with a short sound. While the full map is open, right click no longer rolls and X no longer reloads.
- A friend no longer gets stuck in the arrival elevator (unable to move for the rest of the run) when their connection drops while the elevator is opening.
- Rooms held shut during a fight stay shut on every screen. Before, a friend could rarely see one of them open for a few seconds.

## 1.1.8

- A player who is on fire shows as burning on every screen, and only while they really are. Before, the other screens never showed it (or showed a copy burning from flames on the floor there).
- Alternate costumes show on every screen. Before, the other players saw the regular outfit.
- After a character swap, the host's player finishes arriving on every new floor again (the free blank refill, reload and item effects at the start of a floor). Before, that step stopped half way on every later floor.

## 1.1.7

- The mouse pointer shows on top of the online menu. Before, it was hidden behind the menu, so the menu could only be used with keys or a controller.
- Pop-up messages no longer draw over the online menu (they covered its title and the invite code). The menu shows the same information itself.
- Long hints in the online menu wrap inside its box instead of running past its edges.
- Long online menu pages (Your friends) fit on the screen. Before, their title and bottom row were cut off.

## 1.1.6

- The Door Lord (the boss door mimic) dies on a guest's screen too. Before, its copy errored and stayed alive for the guest.
- A friend whose new character reaches the host during a floor load is swapped cleanly once the floor is loaded. Before, the host could keep the old character as an extra player for the rest of the run.
- A friend who changes character in the Breach just as the host starts the run follows the host onto the first floor. Before, their game could fail to load it and end up on a different floor, which ended the session.
- Kill Pillars and other bosses made of several parts show up for a friend who finishes loading a floor after the host. Before, their parts could be missing until the game noticed and sent the whole floor again.
- Floors with wall mimics: a guest's copy of a wall mimic no longer fails to appear (it used to error and be sent again every few seconds).
- A floor always finishes loading even when one player's arrival on it fails (for example a friend who drops or reconnects during the load). Before, that stopped the load for everyone on that PC and left them stuck on a frozen screen.

## 1.1.5

- Guests were sometimes sent back to the Breach with a message about different mods, even with no other mods installed. The game reads each player's save while it builds a floor (story progress, what you have seen before, stats), so two players with different saves could build different floors. Guests now build every floor with the host's save state, so floors match.
- Guests now see enemies that appear during a fight: slimes when they split, ghosts and other enemies that fade in, and summoned enemies. Before, they stayed invisible on the guest and the guest's shots passed through them.
- Guests see enemies aim their guns and see the Sniper Shell's aiming laser before it fires.
- Kill Pillars (the statue boss) is visible for guests. Before, guests could not see it for the whole fight.
- Talking to an NPC or a shrine no longer freezes anyone. A friend's conversation used to block your own, which could leave you unable to move until you died. Conversations now finish on every screen with the answers each player chose.
- After the host's game closes and the saved run is resumed, a friend still on the old copy of the floor no longer starts a boss intro on the host (which froze the host), and a friend who reconnects while a floor loads no longer stops that floor from loading.
- Arriving on a new floor no longer starts a boss intro on anyone's screen because a friend's copy briefly stood where the boss room is.

## 1.1.4

- Content mods (ExpandTheGungeon, Planetside of Gunymede, Modular, GungeonCraft and other enemy and room mods): a guest no longer gets stuck loading every floor after the first. Joining used to remove the mods' own enemy and boss templates along with the Breach's enemies, which broke the mods' enemy lookups and stopped the guest's floors from generating.
- Breach mods that remove or switch off the game's Breach object no longer stop guests from picking a character.
- A HUD error that repeated every frame with some modpacks is fixed.

## 1.1.3

- Classic co-op: a player pulled into a fight lands on clear floor inside the room instead of on the exact spot of the player who walked in, which was often the doorway and left them in the wall once the door sealed.
- On joining, everyone's other mods are compared and any that differ are listed on both screens, before the run starts.
- New r2modman package (GungeonOnline-<version>-r2modman.zip, Import local mod) that uses the mod manager's BepInEx pack.
- Guests see Jammed enemies as Jammed and see the Lord of the Jammed. The host's jammed state and the Lord of the Jammed's position are sent to every guest; a guest's own curse roll no longer decides.
- A guest still inside a run of their own (for example after the host picked Save run and quit) who joins a host in the Breach is taken to the Breach to pick a character, instead of the host waiting forever.
- Guests see the host's boss reward pedestals and their exact items. Collecting a reward updates every screen.
- Replacing guest shop stock reuses its sprite, preventing black pictures and duplicate sprites without deleting the shop item.
- In Explore, a teammate who reaches a normal sealed combat door can enter the ongoing fight. Empty rooms held shut to prevent a second fight stay locked.
- Clone restarts the run for everyone, including ghosts and the last player to die, whether the host or a guest owns it.
- Revive chests, rainbow-chest type and locks follow the host. Boss-pedestal revives reach every machine, and scattered ground loot follows the host until it settles.
- Dropped guns are synchronized as pickups, including guns from rainbow chests, instead of being missed inside their debris containers or treated as enemy projectiles. Rat theft is decided by the host.
- Returning to the Breach and Quick Restart clear the old run on every machine, including the death-screen pause, instead of leaving guests loading forever.
- A guest reconnecting during a floor change follows the host's newest floor as soon as it is ready.
- Boss-reward messages no longer conflict with player-progress messages.
- Custom enemies created at runtime by a content mod can be copied from the guest's generated actors, even when no prefab exists in the base-game enemy catalogue.
- Floor generation resets both game random generators. If another mod still produces a different layout, the guest returns safely to the Breach with an explanation instead of playing in mismatched walls and rooms.
- Tested with ExpandTheGungeon 3.0.8, MtG API 1.9.2 and the r2modman Gungeon BepInEx pack. The DLL targets BepInEx 5.4.21 so it also loads on that older mod-manager setup. Both players still need matching mods; this does not establish compatibility with every modpack.

## 1.1.2

**Faster friends**
- Your friends' characters show up 14 to 21 ms sooner on your screen (measured: 44 to 30 ms on fiber, 67 to 47 ms on a jittery 50 ms link, 82 to 61 ms on hotel Wi-Fi), and closer to where they really are. Instead of waiting for late input, the mod keeps your friend moving and puts a late update where they are by then, never past a stop, turn or roll. For more smoothing instead of less delay, raise *Smoothing of your friend* in Settings.

**Fixes**
- Swapping character in the Breach no longer teleports the guest next to the host (sometimes into a wall).
- A guest who crashed and came back while the host was changing floors could end up a floor behind and stuck.
- On some floors, a guest could end up on a different floor layout from the host after arriving (rooms and enemies stopped matching). One enemy failing to clear on arrival stopped the guest from telling the host it was ready, and the host then made it load the floor a second time.
- A hit on a guest could fail to count on the host when one of the guest's items reacted to it.
- If your floor is different from the host's (usually another mod that changes floors, or different mods on each PC), you now get a message saying so, with your other mods listed, instead of rooms and enemies that silently don't match.

## 1.1.1

**Fixes**
- Teammate HUD set to hearts only (the default with 3 or 4 players): your teammates' gun and item pictures no longer float on screen without their boxes.
- Teammate HUD set to off now hides your teammates' hearts and blanks too.

## 1.1

**3 and 4 players (experimental)**
- Online co-op for up to 4 players. The host picks *Players: 2, 3 or 4* on the Hosting screen; everyone else sees the host's choice.
- Each player keeps their own camera, controller and HUD. Players 3 and 4 get their own HUD panels, made from the game's own, beside the first two so they take no extra room at the top.
- Off-screen players show as just their character icon at the screen edge. New options: *Locator text* (icon only, name, or name and distance) and *Reload text* (everyone, only mine, or off).
- A slow connection to more than one friend shows as one short line.
- A death is a knock-out while anyone is still standing; the run ends only when everyone is down. A revive chest or boss pedestal brings every ghost back.
- Classic style pulls everyone into the fight; Explore stays one fight at a time.
- Rooms stay lit and active for every player, room-clear rewards reach everyone, and players 3 and 4 get credit for their own kills, damage and synergies.
- Enemy health scales with the player count: 1.25x for 2 (the game's own), 1.5x for 3, 1.75x for 4 (host settings).
- *Your color* (8 choices) is shared with everyone: your name tag, outline and HUD face use it.
- *HUD faces*: each player's character face beside their hearts. *Outlines*: off, teammates, or everyone, in each player's color.
- *Teammate HUD*: everything, hearts only, or off. Name tags are solid, close above the head, with a *Name height* option.
- With 3 or 4 players, the game's own Save and Quit only remembers 2 players; the mod saves the run for everyone and picks it back up when someone reconnects. Couch co-op (two people on one PC) and the boss intro cards stay 2 players.

**Smoother motion on uneven connections**
- When a friend's input arrives late, their character slides into place instead of hopping; on the guest's screen, enemies, enemy bullets and empty mine carts do the same when an update from the host lands. Only the look changes: where things really are, and what they hit, is exact. Measured on jittery connections: 2 to 4 times less on-screen jerk, no added delay. Option: *SmoothFriendMotion* (on).
- Steam connections now time packets by when they really arrive, so a slow frame on your PC is no longer mistaken for a bad connection.
- Network timing stays exact however long the game has been open (the game's own clock gets coarser after a few hours).
- Fixed: some enemy bullets (from enemy guns, and from enemies the game placed itself) showed with the wrong look on the guest's screen.

## 1.0.4

**Fixes**
- After a network dropout of roughly 10 to 20 seconds (a Wi-Fi hiccup, say), the game could stay connected but stop sharing the world: enemies, pickups and doors stopped updating for the friend for the rest of the session. Late packets from before the dropout made the host think lost messages had arrived. The connection now catches up by itself within a few seconds, and if it ever does get stuck, it reconnects and restores the run on its own.

## 1.0.3

**Fixes**
- Friends can now get in when the host's router or firewall was blocking the join. The joining game asks the host's game, through the mod's small relay, to open a path, and the next try goes through. Only the two public addresses are sent, and the relay forgets them after 30 seconds.
- If the host still can't be reached, the joining game keeps trying for another minute and copies your reply code for you, so the host can pick Let in friend. Before, the reply code was never shown, and it stopped working as soon as the join gave up.
- The host now gets a message when someone tries to join with a different version of the mod or the game, or when the game is full.
- Send report now includes what the connection setup saw, so join problems are easier to track down.

## 1.0.2

**New**
- Couch co-op with any character: on one PC, player two joins at the Cultist as usual, then talks to any Gungeoneer in the Breach to become them, just like player one. The Gungeoneers stay out in the Breach during couch co-op instead of disappearing.

**Fixes**
- If the guest's game crashed while the host was loading the next floor, the host could get stuck on the loading screen.
- After a crash, the friend who came back could stay frozen: leftover network traffic from the crashed game was mixed into the new connection. Every packet now carries its session, and old ones are ignored.
- Falling into a pit while your friend rides a minecart no longer leaves you hanging mid-fall until they get off.

## 1.0.1

**Fixes**
- The guest now sees every enemy bullet. Walking into a boss room after the fight had started cleared the boss's bullets on the guest's screen while they kept flying for the host. Bullets already in the air when the guest finishes loading a floor are sent too.
- Rejoining as the Paradox or the Gunslinger no longer charges Hegemony Credits again.
- A screen that stays black while you can still move now fades back in by itself within a few seconds.
- When both players go down, both games now end the run (the guest could keep walking around as a ghost).
- The ping shown in the menu and the connection warning is steady now (it used to jump with every frame hitch).

**New**
- *Send report* (was Save report) sends the report to me as well as saving it to your Desktop. Invite codes, IP addresses, your Windows user name and Steam ids are removed first.
- The game tells you when a new version is out: one check at launch, nothing about you is sent. Turn it off in Settings, Help and advanced.

**Updating from 1.0:** run the installer again (or copy the files over). Your settings and saved runs are kept.

## 1.0 (first public release)

**Play**
- Online co-op for 2 players, each on their own PC with their own controller, camera and HUD.
- Any Gungeoneer for both players, duplicates allowed.
- Steam, GOG and Epic copies play together.
- Host-authoritative netcode with local prediction: zero added input delay on your own character, and a once-a-second sync check that repairs any difference on the spot.

**Easy to start**
- One-click installer that finds the game on Steam (every library), GOG and Epic.
- *Play Online* in the game's pause menu, plus Back + Start on a controller, plus F8.
- Controller-first menu. Invite codes are copied to the clipboard; joining reads the code from the clipboard.
- UPnP, hole punching, a reply-code fallback and `ip:port` for VPNs. Steam friends can be invited from the menu.
- The Breach door waits until everyone has picked a character.

**A run is never lost**
- Short drops (up to 20 s) are bridged. Long loads no longer count as silence.
- A dropped or crashed player is waited for with no time limit and gets their character, items and spot back.
- The host backs up the run at every floor and every 2 minutes; *Resume saved run* after a host crash.
- Quick restart is off online.

**Fair and consistent**
- Hits and pit falls are decided by the player they happen to.
- The exit elevator leaves on every PC together.
- Minecarts: the cart you ride responds instantly on your screen.
- Each player's own aim assist setting is used for their character everywhere.
- Hegemony credits, kills and unlocks earned in the run go into both players' saves.
- One fight at a time (optional), local pause, NPC conversations only take over the talking player's screen.

**Fixes**
- A character pick sent before the connection was fully up could get lost and leave both players in the Breach: the pick is now re-sent until the host has it.
- Reliable messages can no longer stall: a message that arrived but could not be used is sent again.
- Minecarts: the cart you drive is yours on every machine (no more drift while riding).
- Sync checks no longer raise false alarms at very high ping or right after a drop.
- A friend on a slow PC gets more time to load a floor before being sent it again.
- Safety net: if your character is ever left without control or stuck mid-fall for no reason, control comes back on its own.
- Locator only shows once your friend is fully off screen (no overlap with the name tag).
- Lower cost per frame (health and money sync no longer rebuilds text every frame).

**Known issue**
- In some fights the guest does not see a few of the enemies' bullets. Those bullets can't hit the guest (hits are decided on the guest's own screen), and everything else stays in sync. Fixed in 1.0.1.

**Tools**
- *Save report* writes a report to your Desktop. `metrics.jsonl` records connection numbers every 5 seconds.
