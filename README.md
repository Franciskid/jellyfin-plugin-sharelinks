# ShareLinks for Jellyfin

Send someone a single movie or episode, without giving them an account, without
them seeing the rest of your library.

> *"here, watch this one film, the link dies tomorrow"*

ShareLinks adds a **ShareLink** button (with a little share icon) to the context
menu of any movie or episode in the Jellyfin web client. Click it, pick how long
the link should live, and you get a URL you can send to anyone. When they open
it, they land straight on that one title, already signed in, and they cannot
wander off into the rest of your server.

No account for them to create, no password for you to hand out, no permanent
guest user piling up. The link is temporary, the guest is temporary, and when it
expires everything is cleaned up on its own.

I built this for my own server (shared with family and a few friends), because I
kept wanting to show someone *one specific film* without either adding them as a
real user or handing over a login that sees everything.

---

<img width="1476" height="784" alt="image" src="https://github.com/user-attachments/assets/841f24f8-8b0f-4fd0-a8f8-890ff80faa2f" />


## How it works

1. As an admin you open the context menu on a movie or episode and hit
   **ShareLink**. You choose an expiry (1 hour up to 30 days) and the plugin
   hands you a link, copied to your clipboard.
2. Behind the scenes the plugin tags that one item with a unique, random tag and
   records the share. The raw link token is shown to you once and never stored,
   only a keyed HMAC hash of it is kept.
3. Whoever opens the link gets a throwaway guest user created on the spot,
   restricted by that tag to the single shared item, and is signed in
   automatically. They land on the title's page.
4. When the link expires (or you revoke it), a cleanup pass disables and deletes
   the guest user and strips the temporary tag. A scheduled task and a startup
   pass make sure nothing lingers if the server was off at expiry time.

## What the guest sees

Just the one title, and the ability to play it. The confinement is real and it
is enforced on the server, not only in the browser:

- The guest's Jellyfin policy only permits the single shared item, so every
  other movie, show, library and search comes back empty from the API. Even
  someone poking at the raw API cannot list your other content.
- On top of that, the web client is locked down for the guest: the home,
  menu and search buttons are hidden, in-page links (cast, studio, genres) are
  made inert, "add to playlist" is removed, and any attempt to navigate away
  snaps back to the shared title.

Playback works normally, including transcoding and remuxing if you allow it, and
the player's back button still returns them to the title's page.

## Managing links

The plugin's dashboard page lists every share with its status, the title, a
copyable link, the temporary guest name, and an expiry, and lets you revoke any
of them on the spot. Revoking runs the same teardown as expiry: guest gone, tag
gone.

## Hiding other plugins from guests

If you run other plugins that inject their own UI into the web client (a search
bar, a floating button), you probably do not want a guest to see them. I had
exactly that problem with a different plugin of mine, so the **Guest hidden
selectors** setting is a comma-separated list of CSS selectors that get hidden
in guest sessions. It ships with a default that hides that plugin's floating
button and panel; add any other plugin's selector and it disappears for guests
too, no code change needed.

## Security stance

The design goal is simple: a raw share token exists only at the moment it is
issued, is returned to you once, and is then forgotten. Persistent storage keeps
only a keyed HMAC hash of the token plus the metadata needed to audit and clean
up the link. So:

1. raw tokens are never logged
2. raw tokens are never written to disk
3. the token is only returned in the creation response
4. token validation is a hash comparison
5. guest-user creation and teardown live behind explicit service calls
6. the real access boundary is the server-side tag policy; the web-client
   lockdown is convenience on top of it

## Configuration

All of these live on the plugin's dashboard page:

| Setting | What it does |
|---|---|
| Default / maximum expiry | The default the menu offers, and the ceiling a link may be set to |
| Public base URL override | Force the host used when building links (otherwise derived from the request) |
| Guest username prefix | Prefix for the throwaway guest accounts (default `share-`) |
| Allow transcoding / remuxing | Whether guest playback may transcode or remux |
| Cleanup interval | How often the background cleanup runs |
| One-use default | Whether new links default to single redemption |
| Guest lockdown | The web-client confinement described above (on by default) |
| Guest hidden selectors | CSS selectors hidden from guests, to suppress other plugins' UI |

## Known limitation: cast and crew

Jellyfin has a core bug ([jellyfin/jellyfin#14926](https://github.com/jellyfin/jellyfin/issues/14926))
where a user restricted by tags loses the Cast & Crew section entirely, because
the tag filter is applied to people as well as to media. Since a ShareLinks
guest is tag-restricted, they hit this: the shared title's page shows no
actors, director or writer. This is a server-side Jellyfin issue, not something
the plugin can style around. A workaround inside the plugin is possible and on
the list.

## Compatibility

- Jellyfin **10.11** (targetAbi `10.11.0.0`), .NET 9. Tested on 10.11.8.
- The UI injection targets the standard Jellyfin web client, and works with both
  the English and French interface.

## Credits and license

Developed by [Franciskid](https://github.com/Franciskid).

Licensed under the [GPL-3.0](LICENSE), like most Jellyfin plugins.


## Images

<img width="590" height="427" alt="image" src="https://github.com/user-attachments/assets/1ae3c28a-644e-4c9f-824a-07b800aa5eff" />

