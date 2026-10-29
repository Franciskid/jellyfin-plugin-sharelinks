# ShareLinks for Jellyfin

Share a title, some music, photos, a book, a collection, a playlist, a folder or a Live TV
channel with a link. The person you send it to does not need an account and can only access what
you shared. You choose when the link expires. Or make it a watch party: everyone who opens the
link joins a SyncPlay group with you.

`Jellyfin 10.11 and 12` · `.NET 9` · `no account for the guest` · `access restricted on the server` · `automatic cleanup`

ShareLinks adds a **ShareLink** entry to the context menu of anything that can be shared.
Choose an expiry date and copy the link. Whoever opens it goes straight to the shared title,
already signed in with a temporary account. When the link expires,
the account and the temporary tag are removed.

<img width="1505" height="820" alt="Create guest link dialog, opened from the item menu" src="https://github.com/user-attachments/assets/27296f27-9a37-4870-90aa-df8b6d9e9f43" />

---

## Threat model

Guests receive a Jellyfin access token, which also works in mobile apps and other clients.
Hiding buttons in the browser would not stop someone from accessing the rest of your library.

The plugin restricts access on the server in two ways:

1. **Jellyfin's tag policy.** Every share creates a random tag, `sharelinks-<32 hex>`. The tag goes
   on the shared item and its children, such as the episodes in a season. The guest account can
   only access items with that tag. Other content is filtered out of library and search results.
2. **A request filter for plugin routes.** Jellyfin's own API stays available for playback,
   with access limited by the tag policy. Guests receive a 403 response from other plugins'
   routes unless an administrator explicitly allows access to that plugin.

On top of that, a few parts of Jellyfin's own API get extra rules for guests, because the tag policy
does not cover them:

- **SyncPlay.** Groups are shared between all users and the tag check only looks at what is queued,
  so an empty group is open to anyone. Guests only see and join their own share's groups.
- **Sessions and users.** Every viewer of a multi-use link is the same Jellyfin user, and Jellyfin
  lets a user list and control all of their own sessions. Guests only see their own session and
  their own account, and cannot send commands or messages to other sessions.
- **Live TV.** Jellyfin skips the tag check for items sitting directly under a view, which every
  channel does, so any account can open any channel by its id. Guests are kept to the one channel
  they were sent, can't open a tuner with a hand-made stream token, and can't read timers,
  recordings or tuner details.

The web client also hides the home, menu and search controls, disables cast and genre links,
and brings guests back to the shared title if they navigate away. This keeps the interface simple;
the server enforces the access restrictions even if someone disables the script.

Jellyfin has no server setting that keeps a user on one page. The plugin uses Jellyfin accounts
and tag permissions to control what guests can access, without a separate login system.

## Flow

```mermaid
sequenceDiagram
  participant A as Admin
  participant P as ShareLinks plugin
  participant J as Jellyfin core
  participant G as Guest browser
  A->>P: ShareLink on an item, pick expiry
  P->>P: 256-bit token, store HMAC hash only
  P->>J: Tag the item and its children
  P-->>A: URL (returned once, copied to clipboard)
  G->>P: GET /ShareLinks/Redeem?t=...
  P->>P: Hash the token, look the record up
  P->>J: Create guest user, policy = allow that tag
  P->>J: AuthenticateDirect, mint a session
  P-->>G: Bootstrap page, lands on the title
  Note over P,J: On expiry or revoke: delete the guest,<br/>delete its devices, strip the tag from the tree
```

1. **Create.** You choose an expiry from 1 hour to 7 days, or an exact date, capped by the
   configured maximum. You also choose single use, which is the default, or multi use.
2. **Tag.** The plugin tags the item and everything below it. Tagging never goes upwards: Jellyfin
   treats a parent's tags as belonging to all of its children, so tagging the series of a shared
   season would hand over every other season.
3. **Redeem.** Opening the link creates a temporary guest account, applies the access restrictions,
   and signs the visitor in. They go straight to the shared title.
4. **Clean up.** Expiry or revocation disables and deletes the guest account, removes its registered
   devices, and removes the tag from the shared items. Cleanup also runs on a schedule and at
   startup, so expired links are handled if the server was off.

## Design decisions

| Decision | Reason |
|---|---|
| Only the token HMAC hash is stored | The raw token is returned once and is never saved to disk. Lookups hash the presented token and compare with `FixedTimeEquals`. |
| The HMAC key is a per-server file, mode 0600 | The `sharelinks.json` file alone cannot be used to recover a token. The key is generated on first use. |
| Tags propagate down, never up | A bug fixed in 1.0.3: a shared season tagged its parent series, and Jellyfin's tag inheritance then exposed every other season of that series. |
| Guest accounts use a dedicated authentication provider | Guests cannot sign in through the login page with a password. If the plugin is disabled, Jellyfin also refuses those sign-ins. |
| The password is generated per redemption and thrown away | This prevents sign-in with a blank password. The browser receives only a session token. |
| Links are redeemed one request at a time | This prevents two simultaneous requests from both using the same single-use link. |
| Each multi-use viewer receives its own device id | Jellyfin logs out any session with the same user and device id, so a shared device id would kick out the previous viewer on every new arrival. |
| The guest session is minted as Jellyfin Web, with the device id the browser then uses | Jellyfin keys a session on app name plus device id. Any mismatch puts the guest's requests and WebSocket on two different sessions, and SyncPlay never tells the browser it joined. |
| The viewer limit is checked before changing anything | Handling the limit before Jellyfin raises an error lets the plugin turn away a new viewer without interrupting anyone already watching. |
| Other plugins are blocked by default | The filter distinguishes Jellyfin core from plugin code, so newly installed plugins are blocked too. |
| Guest devices are deleted before the user | Jellyfin does not remove devices when a user is deleted. Leftover devices can break the admin devices page. |
| Guest sessions are ended before the user is deleted | A session left in a SyncPlay group after its user is gone breaks the group's access check for everyone else in it. |
| Collections and playlists are walked in full | Their members are linked, not children, so nothing reaches them through tag inheritance. Each member gets the tag, and so does everything below it. |
| Removing a tag searches the whole library for it | Items dropped from a collection or playlist after it was shared still carry the tag. The search finds them, even when the collection itself was deleted. |
| Playlists get the guest added read only | Jellyfin decides who sees a playlist from its share list, not from tags. The guest is removed again on teardown. |
| A guest asking for the album of a track they can play gets a stand-in | The web client fetches a track's album for its loudness gain before playing it, and stops if that fails. Tagging the album would hand over its other tracks, so the guest gets the album's id, name and gain only. |
| Live TV share tags are put back | A guide refresh replaces a channel's tags with the tuner's. The cleanup pass and the guest's own page load put the share tag back. |

## What can be shared

| You share | The guest gets |
|---|---|
| Movie, episode, home video, music video, trailer, recording | That video |
| Series or season | Everything below it. Never the series above a season |
| Track or audiobook | That track |
| Album | The album and its tracks |
| Artist | Their own albums, and every track credited to them. Not compilations they only appear on |
| Photo or photo album | That photo, or the album's photos |
| Book | The book. The reader opens the file itself, so book guests can download it |
| Collection | The collection, every item in it, and everything below those (a series in a collection comes with its episodes) |
| Playlist | The playlist, read only, and its entries |
| Folder | A plain folder inside a library and everything in it |
| Live TV channel | That channel and its guide. Nothing else from Live TV |

Library roots, views, people, genres, studios, plugin channels and recordings still in progress
can't be shared. Neither can a library's own folder on disk, since that would hand over the whole
library.

## Watch parties

Tick **Watch together** in the create popup. ShareLinks opens a SyncPlay group in your browser
straight away, and everyone who opens the link is put in that group as soon as their page loads.
Press play when you're ready and they follow. Works for anything that plays: videos, music,
collections, playlists and folders.

- **Only I can pause, seek or change what plays** is on by default. Guests still follow along and
  the group still waits for anyone who is buffering, but pause, seek, skip and queue changes from a
  guest are refused. A guest who presses stop just leaves the group.
- The link ends when the group closes, which happens when everyone has left it. A server restart
  closes every group, so it ends every party too. Expiry still applies on top of that.
- The group id is stored on the link and never taken from the guest, and party guests can't create
  or join any other group.
- SyncPlay won't let anyone queue something a member can't open, so while guests are in the group
  you can only play what you shared with them.

## What the guest can do

- Watch the shared title, and browse down into it. A shared series opens into its seasons and
  episodes. A shared season opens into its episodes.
- Access only the shared content. Other plugins are blocked unless you allow them in the settings.
- Playback works normally, with transcoding and remuxing if you allow them.

Going up does not work. A guest who receives one season cannot open the series that contains it.

## Multi-use links

A multi-use link works for everyone you send it to until it expires. Anyone with the link can use it, so only send it to people you want to give access to.

- Every viewer uses the same account, so every viewer sees the same single title. More viewers do
  not mean more content.
- The limit, 10 by default, caps how many people **start** watching at the same time. It is not a
  cap on how many people use the link in total. Sessions end, and each redemption issues its own
  token. Revoke the link if you need a hard stop.
- One account means shared playback position and shared watch state. Use single-use links if that
  matters.
- A viewer over the limit receives a "try again later" page. Nobody watching is disturbed.

## Managing links

The plugin dashboard page lists every share with its status, title, copyable link, guest name and
expiry. You can revoke any link on the spot, which removes the guest account and tags, just like expiry. A cleanup
button removes revoked, expired and failed records from the store.

## Install

Add the repository in **Dashboard → Plugins → Manage repositories**:

```
https://raw.githubusercontent.com/Franciskid/jellyfin-plugin-sharelinks/main/manifest.json
```

Install **ShareLinks**, then restart Jellyfin. Hard-refresh the web client once for the menu entry
to appear.

## Configuration

| Setting | Effect | Default |
|---|---|---|
| Default expiry | The expiry the create popup offers first | 24 h |
| Maximum expiry | The longest allowed lifetime for a link | 720 h |
| Public base URL override | Forces the host used to build links, instead of the request host | derived |
| Guest username prefix | Prefix for the temporary accounts | `share-` |
| Allow transcoding / remuxing | Whether guest playback may transcode or remux | on |
| Cleanup interval | How often the background cleanup runs | 60 min |
| Maximum viewers per multi-use link | Concurrent viewers on one multi-use link. 0 means no limit | 10 |
| Bitrate limit for guests | Caps guest streams from outside the local network, like a user's remote bitrate limit. 0 means no limit | 0 |
| Single use by default | How the create popup starts | on |
| Guest lockdown | Hides navigation controls in the guest interface | on |
| Allow watch parties | Offers "Watch together" in the create popup | on |
| Block other plugins for guests | Blocks guest access to other plugins on the server | on |
| Plugin access list | Plugins guests are allowed to access | empty |
| Cosmetic hidden selectors | Hides matching elements in the browser; does not restrict access | empty |

**On the plugin access list:** some plugins have to answer guests. An intro skipper, for example,
is called by the client during playback. Enable access for that plugin in the list. Plugins are blocked by default, including newly
installed ones.

**On intros and pre-rolls:** guests never get intros. Intro plugins like Local Intros hand out
videos outside the share, which the guest can't open, so they'd break playback. Any other video
or audio item a guest couldn't open is also dropped from what the server sends them, whichever
plugin or endpoint it came from.

**On the cosmetic selectors:** use these to hide elements such as another plugin's floating button.
This only changes what guests see in the browser. To restrict access, use the plugin access
settings above.

## HTTP API

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /ShareLinks/Admin/Create` | Admin | Create a link. Returns the raw URL once. `watchParty` and `hostOnlyControl` make it a party |
| `GET /ShareLinks/Admin/List` | Admin | All records with status and expiry |
| `POST /ShareLinks/Admin/Revoke/{id}` | Admin | Revoke a link and remove its guest account and tags |
| `POST /ShareLinks/Admin/Cleanup` | Admin | Remove revoked, expired and failed records |
| `GET /ShareLinks/Admin/Plugins` | Admin | Installed plugins and their guest access state |
| `GET /ShareLinks/GuestState` | Session | Whether the caller is a guest, and what to lock down |
| `POST /ShareLinks/WatchParty/Join` | Session | Put the calling guest's session in its party's group |
| `GET /ShareLinks/Redeem?t=...` | none | Redeem a token, return the bootstrap page |
| `GET /ShareLinks/ClientScript` | none | The injected web-client script |

Records live in `sharelinks/sharelinks.json` under Jellyfin's data folder. The HMAC key lives beside
it in `token-secret.key`.

## Known limits

- **Cast and crew are missing on a shared page.** Jellyfin core bug
  [jellyfin/jellyfin#14926](https://github.com/jellyfin/jellyfin/issues/14926): a tag-restricted user
  loses the Cast & Crew section, because the tag filter is applied to people as well as to media. A
  ShareLinks guest is tag-restricted, so guests are affected too.
- **The token travels in the query string.** It appears in reverse-proxy access logs and in browser
  history.
- **Redemption is public and has no rate limit.** Tokens are 256-bit random, so guessing one is not
  realistic, but the endpoint answers anyone.
- **Episodes added after a share** receive the tag on the next redemption, not the moment they are
  added. A one-use link that was already redeemed is limited to the items present when it was redeemed.
  The same goes for tracks added to a shared playlist and movies added to a shared collection.
- **Watch party guests can still stall the group.** Buffering makes SyncPlay wait for that guest,
  even with host-only control. Revoke the link if someone abuses it.
- **Party guests see the usernames in the group.** SyncPlay lists participants by name, so guests
  see yours and anyone else's who joins.
- **A party ends on server restart.** SyncPlay groups live in memory.
- **Starting a new party closes your previous one** if nobody is left in it, since you can only be
  in one SyncPlay group at a time.
- **Live TV guests tie up a tuner** while they watch, like any viewer.
- **Records are kept after they expire**, for audit. Remove them with the cleanup button.
- **The `sharelinks-` tag is hidden from non-admins in the web client only.** It stays in the API
  response, because Jellyfin uses that tag to restrict guest access.
- **The guest session token is a real Jellyfin token.** It can be misused in the ways any Jellyfin
  token can. Access is still limited to the shared content for the duration you set.

## Compatibility

Jellyfin **10.11** and **12** from a single build (`targetAbi 10.11.0.0`, .NET 9, which Jellyfin 12's
.NET 10 runtime loads as is). Tested on 10.11.0, 10.11.5, 10.11.8, 10.11.9, 10.11.11, 12.0, 12.1 and 12.4.
The web-client injection targets the shipped client, in the Legacy and Modern layouts, in English
and in French.

## Workflow

**1. Open the context menu on an item**

<img width="566" height="240" alt="ShareLink in the item menu" src="https://github.com/user-attachments/assets/25cfaa99-eed2-4bc6-a14b-bc00bc629d5e" />

**2. Choose an expiry and the single-use option**

<img width="566" height="521" alt="Expiry picker" src="https://github.com/user-attachments/assets/754f3daa-80ee-4209-9d09-467940140f81" />

**3. Copy the link**

<img width="566" height="313" alt="Generated link" src="https://github.com/user-attachments/assets/d9e581eb-d654-4c73-8730-0b2b19fbbe25" />

## Tests

```
dotnet test Jellyfin.Plugin.ShareLinks.Tests
```

## License

Developed by [Franciskid](https://github.com/Franciskid). Licensed under the [GPL-3.0](LICENSE),
like most Jellyfin plugins.
