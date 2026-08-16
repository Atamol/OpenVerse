### ![ja](https://flagcdn.com/20x15/jp.png) [日本語版](../unlock.md)

# Unlimited unlock

The guild button on the home screen is the switch for the Unlimited deckbuilding limits. Each press flips it, and while it is on:

- Resurgent cards are legal in Unlimited
- A deck may hold up to 40 copies of the same card
- The deck editor's card list carries every class

The 40-card deck size is unchanged and no other format is affected.

## Using it

Press guild on the home screen and the guild search list holds a single row reading `アンリミテッド解放: ON` or `OFF`. That is the current state, and going back home and opening guild again puts it back.

Only arriving from home flips it. The 申請中 tab re-issues `guild/info` from `GuildApply.OpenCategory`, so flipping on every one of those would undo the press the moment the user looked around.

The state is stored per player on the server, so nobody else on the same host is affected.

## When it takes effect

The client reads the card master and `load/index` once at login and never re-reads them, so the screen you flipped it on is still running the old rules and the row says `(未反映)`.

Return home and a dialog appears, and its back-to-title button lands the change when you come back in. The wording is the login bonus text, but the button runs `SoftwareReset.exec()`, which is the only route back through login the server can reach.

## The buttons inside the guild screen

Guild is not implemented, so nothing in there does anything. Create, join, invite, leave and chat all answer `result_code` 2054 (`GUILD_MAINTENANCE`) and the client puts up a maintenance dialog that closes. The list reads come back empty but successful, so moving between tabs raises no dialog.

Putting 2054 in `feature_maintenance_list` would grey the buttons out instead (`MaintenanceButton` calls `SetObjectToGrey`, which also disables the collider). But which buttons carry that component lives in the scene rather than the code, and if the home guild button has one the switch itself stops responding.

## Every class in one deck

The client holds two card masters, `CardMaster.CardMasterId.Default` and `NextCardMaster`. Every deckbuilding screen is pinned to `Default`, while which one a battle runs on comes from `card_master_id` in the matching reply, read by `DoMatchingBase.SettingCardMasterId`.

So while the switch is on, master 1 ships with its `clan` column zeroed and master 2 ships untouched. The editor's class filter is `(mask & 1 << card.Clan) != 0` and the mask always has neutral's bit 0 set, so every card lists under every deck.

`card_master_id` is always 2, switch on or off. Master 2 is the untouched CSV in either payload, so a locked player sees no difference. Sending it only to unlocked players leaves a window: turning the switch off does not reload the master, so that client would battle on the flattened one until it logged in again.

Side effects:

- the class filter button in the deck editor empties the list. Only the neutral button sets bit 0, so a class button leaves `1 << MainClass`, which no card with `clan` 0 matches. 全て puts it back
- the class tabs on the collection screen go empty for the same reason, and with no filter everything still shows
- outside battle, card class icons and frames render as neutral
- practice matches use master 1, because `PracticeDeckSelectConfirmDialog` resets the battle master to `Default`, so clan-dependent cards misbehave there

## How it works

| Limit | Where the client keeps it | Reachable from the server |
| --- | --- | --- |
| Resurgent ban | `IsResurgentCard` in the card master | yes |
| 3 copies | a constant in `UnlimitedFormatBehavior`, but overridden per card by `unlimited_restricted_base_card_id_list` in `load/index` | yes, through the override |
| Class restriction | `FilterController` builds it from the deck's class | not the mask, but zeroing `clan` gets through it |
| 40-card deck | `UnlimitedFormatBehavior.DeckCardNumMax` | no |

`unlimited_restricted_base_card_id_list` is the nerf route, for pushing a card's cap below 3. Nothing checks a lower bound, so it works upward too, and while the switch is on every base_card_id is sent at 40.

The owned count moves to 40 at the same time, because the editor will not let you add more copies than you own and raising the cap alone would still stop at 3.

## Deck codes

A deck code can be minted from a plain id list, which builds a deck without going through the editor.

```bash
curl -s -X POST http://localhost/openverse/deckcode -H 'Content-Type: application/json' -d '{"clan":1,"deck_format":2,"cardID":[100114010,100211010,100311010]}'
```

`clan` is the deck's class and picks the leader (1=Forest, 2=Sword, 3=Rune, 4=Dragon, 5=Shadow, 6=Blood, 7=Haven, 8=Portal). `deck_format` 2 is Unlimited. Class 0 is not an option: `DataMgr.SetClassPrm` drops anything outside 1..8, so resolving the leader throws.

The import side (`DeckCreateMenuUI`) only checks that each id exists in the master, never its class or how many times it appears.
