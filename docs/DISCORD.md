# Discord widget

The **Discord** widget shows your current voice channel and its members, your servers and their voice channels, and
Discord notifications. With **control** turned on it also mutes, deafens, leaves and joins.

PhoneBox talks to the Discord desktop app running on the same computer, through Discord's local connection. Nothing
goes through a PhoneBox server, and the phone never contacts Discord. Discord has no shared PhoneBox login, so you
create a small Discord application of your own once (about five minutes, free).

Requirements: the Discord **desktop app** (not the browser version) running and signed in on the computer that runs the
PhoneBox agent. Windows, macOS and Linux work through Discord's local socket; the setup below was tested on Windows.

## 1. Create a Discord application

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and sign in.
2. Click **New Application**, name it (for example `PhoneBox`), accept the terms and click **Create**.
3. In the left menu open **OAuth2**.
4. Under **Redirects** click **Add Redirect**, enter exactly `http://127.0.0.1` and click **Save Changes**. If the
   application has other redirects, `http://127.0.0.1` must be the first one: Discord's desktop app uses the first
   redirect for the login.
5. On the same page copy the **Client ID**. Click **Reset Secret** to show the **Client Secret** and copy it too.
   Discord shows the secret only once; if you lose it, reset it again.

## 2. Enter it in PhoneBox

1. Open the PhoneBox settings page, choose **General** and find the **Discord** card.
2. Turn on **Connect to the Discord app on this PC**.
3. Paste the **Client ID** and **Client secret**, then save.
4. Discord shows an approval window ("PhoneBox wants to access your Discord"). Click **Authorize**. This happens
   once; the login is kept in `discord-token.json` next to your settings and renewed automatically.

If Discord refuses the login, open your application in the Developer Portal, go to **App Testers** and add your own
Discord account (an application can be used by its owner and testers without verification).

## 3. Add the widget

Add a **Discord** widget to a page in the editor. Options:

| Option | Meaning |
|---|---|
| Channels | Show your servers as tiles; tap one to see and join its voice channels |
| Notifications | Show recent Discord notifications. PhoneBox only asks Discord for them, and only sends them to the phone, while a widget with this option on is on an enabled page |
| Control | Allow mute, deafen, switching between voice activity and push to talk, leave and join from the phone. **Off by default** |

With both the widget's **Control** option and `discord.allowControl` (below) off, the widget only displays state. Joining
works only for voice channels the agent already listed.

The input mode button (🗣️ voice activity, ✋ push to talk) switches Discord's **Input Mode** and keeps your push-to-talk
key, release delay and sensitivity as they are. When push to talk is on but no key is set in Discord, nobody can hear
you; the widget then shows a warning, and one more tap switches back to voice activity. It uses the voice permission
PhoneBox already asks for, so no new approval in Discord is needed.

### Controlling Discord without the widget

Shortcuts, scripts and custom widgets can use the same actions as the widget's buttons:
`POST /api/discord/mute`, `/deafen`, `/inputmode`, `/leave` and `/join?channel=<id>` (an id from `GET /api/discord`). By
default they work only while a Discord widget with **Control** on is on an enabled page. To use them without one, turn on
**Let shortcuts and custom widgets control Discord** in **Settings › General › Discord** (`discord.allowControl`, off
by default).

Who can reach these actions:

| From | How |
|---|---|
| Programs on this computer | `http://127.0.0.1:8765/api/discord/...` (the agent listens on the loopback address only) |
| The paired phone over USB | through the app's `adb reverse` tunnel, like the rest of the panel |
| The paired phone over Wi-Fi | `/api/discord` is on the Wi-Fi allow-list (`Lan.cs`), over TLS pinned to the paired certificate, with the paired key |

The switch doesn't add a path or a new way in: it only stops requiring the widget, and any of the callers above can then
act, not only the widget. With it off, nothing changes from before.

### Favourites and who is in a voice channel

Tap **★ Favourites** above the servers, star the servers you use (☆ next to a server) and, inside a big server, the
voice channels you want (☆ next to a channel), then **Done**. From then on the widget shows only those servers, and a
server with starred channels shows only those channels (plus the one you are in). With no favourites, every server
shows as before. The favourites are saved in the widget's settings on the computer, so each Discord widget has its own.

For favourites the agent also reads **who is in the voice channel**, every 10 seconds and for at most 20 channels: the
starred channels first, then the channels of favourite servers without a starred one. Their names show under the channel,
and the server shows how many people are in voice **in the channels that were read**, which may not be all of them. Other
channels are not asked, and none are while the widget's **Channels** option is off.

The phone only sends the id of a server or channel the agent itself listed, and the agent changes nothing but that
widget's two favourite lists (`favGuilds`, `favChannels`).

## What is requested

The login asks for these Discord permissions, all of them on your own account and only through the local app:
`rpc`, `rpc.voice.read`, `rpc.voice.write` and `rpc.notifications.read`. The client secret stays on the computer and is
never sent to the phone. See the Discord row in [PRIVACY.md](../PRIVACY.md) for the connections involved.

## Troubleshooting

| Problem | What to try |
|---|---|
| Widget says Discord is not connected | Start the Discord desktop app, then wait a few seconds; the agent retries on its own |
| "Invalid redirect" or similar login error | The redirect must be exactly `http://127.0.0.1` (no trailing slash, no port), and the first one in the list. Then turn **Connect to the Discord app** off and click **Save**, wait a few seconds, then turn it on and click **Save** again so PhoneBox asks again |
| Login refused with `Missing "redirect_uri"` | The application has no saved redirect. Add `http://127.0.0.1` under **OAuth2 › Redirects**, click **Save Changes**. Then, in PhoneBox, turn **Connect to the Discord app** off and click **Save**, wait a few seconds, then turn it on and click **Save** again |
| Discord refuses the login | Add your account under **App Testers** in the Developer Portal |
| Login keeps failing after changing the secret | Paste the new secret in Settings; delete `discord-token.json` next to `config.json` to force a fresh login |
| Only some servers appear | The widget lists voice channels of the first 60 servers Discord reports, so servers beyond that do not appear. For fewer than 60: the list comes from the Discord app and is retried every minute, so wait a little or restart Discord |
| The login is not asked again after Cancel, a timeout or a wrong secret | On purpose: PhoneBox stops instead of showing a new prompt every few seconds. Change the client ID or secret and click **Save**, or turn **Connect to the Discord app** off and click **Save**, wait a few seconds, then turn it on and click **Save** again |
| Buttons do nothing | Turn on **Control** in the widget's options |
