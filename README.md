# ShareLinks for Jellyfin

Send someone a single movie, episode, season or whole series, without giving
them an account, without them seeing the rest of your library.

> *"here, watch this one film, the link dies tomorrow"*

ShareLinks adds a **ShareLink** button (with a little share icon) to the context
menu of any movie, episode, series or season in the Jellyfin web client. It sits
in its own section at the bottom of the menu. Click it, pick how long the link should live, and you get
a URL you can send to anyone. When they open it, they land straight on that title, already signed in,
and they cannot wander off into the rest of your server.

No account for them to create, no password for you to hand out, no permanent
guest user piling up. The link is temporary, the guest is temporary, and when it
expires everything is cleaned up on its own.

I built this for my own server (shared with family and a few friends), because I
kept wanting to show someone *one specific film* without either adding them as a
real user or handing over a login that sees everything.

---

<img width="1505" height="820" alt="image" src="https://github.com/user-attachments/assets/27296f27-9a37-4870-90aa-df8b6d9e9f43" />


## How it works

1. As an admin you open the context menu on a movie, episode, series or season
   and hit **ShareLink**. You choose an expiry (1 hour up to 7 days) and the
   plugin hands you a link, copied to your clipboard. You also choose there
   whether the link is single use, which is the default and stops working once
   the first person opens it, or multi-use, which lets everyone you send it to
   open it until it expires. A multi-use link has a ceiling on how many people can
   watch at the same time, ten by default, and the eleventh is asked to try again
   later rather than displacing anyone.
2. Behind the scenes the plugin tags the shared item with a unique, random tag
   and records the share. Share a series or a season and the tag goes on
   everything underneath it as well, so the guest can browse down through what
   you shared rather than seeing a single locked node. The tag never goes on
   anything above it: Jellyfin treats a parent's tags as belonging to all of its
   children, so tagging the series that a shared season sits in would hand over
   every other season too. Share one season and that is all the guest gets, and
   the series page is not theirs to open. Lookups only ever go through a keyed
   HMAC hash of the token, and the link itself is dropped from the record once
   it is revoked or expired.
3. Whoever opens the link gets a throwaway guest user created on the spot,
   restricted by that tag to the shared item and its tree, and is signed in
   automatically. They land on the title's page.
4. When the link expires (or you revoke it), a cleanup pass disables and deletes
   the guest user and strips the temporary tag from the whole tree again. A
   scheduled task and a startup pass make sure nothing lingers if the server was
   off at expiry time.

## What the guest sees

Just the shared title (and, for a series or season, its seasons and episodes),
and the ability to play them. The confinement is real and it is enforced on the
server, not only in the browser:

- The guest's Jellyfin policy only permits items carrying the share's tag, so
  every other movie, show, library and search comes back empty from the API.
  Even someone poking at the raw API cannot list your other content.
- Other plugins' endpoints refuse the guest, on the server, so hiding a plugin's
  button is not what keeps a guest out of it. See below.
- On top of that, the web client is tidied for the guest: the home, menu and
  search buttons are hidden, in-page links (cast, studio, genres) are made inert,
  and navigating outside the shared tree snaps back to the shared title.
  Navigating down within what you shared works normally: a shared series opens
  into its seasons and episodes, a shared season into its episodes. Going up does
  not, so a guest sent one season cannot reach the series it belongs to.

  Be clear about what that last part is: it runs in the browser. A guest who
  disables the script, or who uses their token from another client, can reach the
  home screen. They find it empty, because the tag policy answers those queries on
  the server. Which page you are on is the browser's doing, what you can pull is
  the server's. Only the second one is load bearing.

Playback works normally, including transcoding and remuxing if you allow it, and
the player's back button still returns them to the title's page.

One honest caveat: if you share a series or season and new episodes get added
to it later, those episodes only pick up the tag (and become visible to the
guest) the next time the link is redeemed : not the instant they are added. For
a one-use link that has already been redeemed, that never happens, so a
one-use link is a snapshot of the tree as it existed at redemption time.

## Managing links

The plugin's dashboard page lists every share with its status, the title, a
copyable link, the temporary guest name, and an expiry, and lets you revoke any
of them on the spot. Revoking runs the same teardown as expiry: guest gone and tag
gone.

## Other plugins and guests

A guest is a real Jellyfin account holding a real access token. That token works
anywhere a Jellyfin token works, including curl and the mobile apps, so anything
that decides who gets in has to decide it on the server.

**Block other plugins for guests** does that, and it is on by default. ShareLinks
registers a filter that runs on every API request in the server, so it covers
plugins you did not write and plugins you install later, without those plugins
needing to know ShareLinks exists. When a guest account calls another plugin's
endpoint, the request is refused with a 403. Jellyfin's own API is left alone:
the share tag already limits it to the shared title, and playback runs through it.

Some plugins genuinely need to answer guests. An intro skipper, for instance, is
called by the client during playback. The config page lists your installed
plugins with a checkbox each, so you can let those through one at a time.
Everything starts unticked, so a plugin you install next month is covered on the
day it lands rather than the day you remember it.

There is also a **cosmetic hidden selectors** box, a comma-separated list of CSS
selectors hidden in guest sessions. Add the class name or the id of the element
you want gone and it disappears for guests. It is for tidiness, so a guest is not
looking at another plugin's floating button. It runs in the browser and enforces
nothing: anyone who opens devtools or skips the web client sees straight past it.
Do not use it as a way to keep a guest out of something. The block above is that.

## Security stance

The design goal is simple: a raw share token exists only at the moment it is
issued, is returned to you once, and is then forgotten. Persistent storage keeps
only a keyed HMAC hash of the token plus the metadata needed to audit and clean
up the link. So:

1. raw tokens are never logged
2. only the token's HMAC hash is used to look a link up
3. the finished share URL is kept on the record while the link is live, so the
   dashboard can re-copy it, and is dropped again the moment the link is revoked
   or expires
4. token validation is a hash comparison
5. guest-user creation and teardown live behind explicit service calls
6. the real access boundary is the server-side tag policy; the web-client
   lockdown is convenience on top of it. So even if someone somehow managed
   to connect with the guest account normally, they would only see the shared
   content through its tags. So no risk that anyone sees your entire library.

The same applies to the guest's login. The plugin mints the guest session itself
on the server, using Jellyfin's own session manager. No password is ever stored
anywhere, not even encrypted, and no password ever appears in the page sent to
the guest. The only thing the guest's browser receives is a session token
scoped to that one guest account, and that token dies the moment the guest
account is cleaned up. On top of that, the guest account is assigned an authentication provider that
refuses every interactive sign-in, so the normal login page cannot be used to get
into a guest account at all, password or not. If the plugin is disabled Jellyfin
falls back to its own invalid-provider handling, which refuses too.

### What a multi-use link does and does not protect

A multi-use link is by design usable by anyone you send it to, so treat the URL
itself as the secret. Within that:

- The tag policy is per account and the account is the same one, so every viewer
  still sees exactly the shared title and nothing else. Letting more people in
  does not widen what any of them can reach.
- The viewer ceiling caps how many people can *start* watching at once. It is not
  a hard cap on how many people ever get in: sessions end, and each redemption
  issues its own session token which keeps working until the link is revoked or
  expires. If you need a hard stop, revoke the link.
- Everyone shares one temporary account, so they share playback position and
  watched state on that title, and they can see each other's sessions in Jellyfin.
  If that matters to you, use single-use links.
- Reaching the ceiling turns the new arrival away with a "try again" page. It does
  not disturb anyone already watching.

### Known limits

- The share token travels in the link's query string, so it will appear in your
  reverse proxy's access log and in browser history.
- Redeeming is a public endpoint with no rate limit. Tokens are 256-bit random, so
  guessing one is not realistic, but the endpoint is reachable by anyone.
- Records are kept after they expire, for audit, and are never pruned automatically (you can do so manually though.
- The `sharelinks-` tag is hidden from non-admins in the web client only. It is
  still present in the API response for anyone who looks, because that tag is what
  confines the guest and it cannot be removed without removing the confinement.

## Configuration

All of these live on the plugin's dashboard page:

| Setting | What it does |
|---|---|
| Default / maximum expiry | The default the menu offers, and the ceiling a link may be set to |
| Public base URL override | Force the host used when building links (otherwise derived from the request) |
| Guest username prefix | Prefix for the throwaway guest accounts (default `share-`) |
| Allow transcoding / remuxing | Whether guest playback may transcode or remux |
| Cleanup interval | How often the background cleanup runs |
| Maximum viewers per multi-use link | How many people may watch one multi-use link at the same time (default 10, 0 means no limit) |
| Single use by default | How the single-use box starts out in the create popup; it is a per-link choice |
| Guest lockdown | The web-client tidying described above (on by default) |
| Block other plugins for guests | Refuses guests on other plugins' API endpoints, server side (on by default) |
| Plugin access list | Plugins you tick stay reachable by guests despite the block |
| Cosmetic hidden selectors | CSS selectors hidden from guests. Appearance only, enforces nothing |

## Known limitation: cast and crew

Jellyfin has a core bug ([jellyfin/jellyfin#14926](https://github.com/jellyfin/jellyfin/issues/14926))
where a user restricted by tags loses the Cast & Crew section entirely, because
the tag filter is applied to people as well as to media. Since a ShareLinks
guest is tag-restricted, they hit this: the shared title's page shows no
actors, director or writer. This is a server-side Jellyfin issue, not something
the plugin can style around. A workaround inside the plugin might be possible by
adding the tag to all of the crew/cast of the specific media shared.

## Compatibility

- Jellyfin **10.11** (targetAbi `10.11.0.0`), .NET 9. Tested on 10.11.8.
- The UI injection targets the standard Jellyfin web client, and works with both
  the English and French interface.

## Install

Dashboard => Plugins => Manage repositories => New repository => https://raw.githubusercontent.com/Franciskid/jellyfin-plugin-sharelinks/main/manifest.json

**You may need to hard refresh the page for the button to appear**

## Credits and license

Developed by [Franciskid](https://github.com/Franciskid).

Licensed under the [GPL-3.0](LICENSE), like most Jellyfin plugins.


## **Workflow**

### Step 1
<img width="566" height="240" alt="image" src="https://github.com/user-attachments/assets/25cfaa99-eed2-4bc6-a14b-bc00bc629d5e" />

### Step 2
<img width="566" height="521" alt="image" src="https://github.com/user-attachments/assets/754f3daa-80ee-4209-9d09-467940140f81" />

### Step 3
<img width="566" height="313" alt="image" src="https://github.com/user-attachments/assets/d9e581eb-d654-4c73-8730-0b2b19fbbe25" />


