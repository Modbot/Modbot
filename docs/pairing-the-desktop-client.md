# Pairing the desktop client

The Modbot desktop client is a small Windows program that tells your group's Modbot which of the
group's instances you are standing in, so presence can be recorded and the headset overlay can
show you who is around. Before it can report to a server it has to be **paired** with it: given a
token for that one server, and nothing else.

Pairing takes about ten seconds and happens in your browser. You do not type a server address, a
code, or a name for your computer.

## Before you start

- Install the client ([how](installing-the-desktop-client.md)) and run it once. It sits in the
  tray (the icons near the clock). Running it once is what tells Windows that Modbot pairing links
  open this program.
- Have a staff account on your group's Modbot. Pairing is done signed in as you, and the server
  remembers that this client is yours.

## The one-click way

1. In the client window, on the **Servers** page, press **Pair with a server**. Your browser
   opens Modbot's pairing page. If your group gave you the address of their own Modbot, you can
   also go straight to `https://<your group's modbot>/pair`.
2. Sign in if you are asked to.
3. Press **Open in Modbot**. Your browser asks whether to open Modbot — say yes. (Most browsers
   offer a "remember this choice" box; ticking it means no question next time.)
4. The client window comes to the front and shows **Paired with <server>**. That server now
   appears on the Servers page and the client starts reporting for that group's instances.

The link only works once and stops working after five minutes. If you leave the page open longer
than that, press **Get a new link**.

## If the button did nothing

Some browsers refuse to open a link that goes to another program, or send it to the wrong one.
The pairing page has a second way that carries exactly the same thing:

1. On the pairing page, press **Copy pairing token**.
2. In the client window, on the Servers page, paste it into the **Pairing token** box and press
   **Use pairing token**.

That is it. The token is the same one the button would have sent; nothing about it is weaker.

## Pairing with more than one group

A moderator who staffs two groups pairs the same client twice, once from each group's Modbot. Each
server gets its own token and its own place on the Servers page, and neither group's operators
learn about the other. Pairing the same server a second time replaces the old pairing rather than
adding a duplicate.

## Unpairing

Press **Unpair** on the server's card. The token, anything queued for that server and the
connection are all removed together. The group's operator does not have to do anything, and the
client stops reporting to that server immediately. An operator can also revoke a client from their
side; when that happens the card shows **Stopped** and says so.

## What the messages mean

| The client says | What happened | What to do |
|---|---|---|
| *This pairing link has expired or was already used.* | Links work once, for five minutes. | Open the pairing page again and use the new link. |
| *That is not a Modbot pairing token.* | Something other than the token was pasted. | Press **Copy pairing token** again and paste the whole thing. |
| *This pairing token points at http://…, which is not a secure address.* | The link named a server that is not using HTTPS. | Ask your group's operator; Modbot only pairs over HTTPS. |
| *… refused this pairing. If you have been removed from that group's staff…* | The server would not accept the code because your account no longer can. | Nothing, unless you think that is wrong — then ask the group. |
| *… did not answer like a Modbot server.* | The address in the link is not a Modbot, or is a Modbot behind something that answered instead. | Open the pairing page at the address you normally use for Modbot in a browser and try again. |
| *Could not reach …* | The server did not answer at all. | Check your connection and try again. Nothing has been changed. |

## What is and is not sent

Pairing sends three things to the server: the one-time code from the link, the client's own
version number, and the word `windows`. It does not send your computer's name, your Windows
account name, your VRChat account, anything from VRChat's log, or which other servers you are
paired with.

The pairing link carries the one-time code and the server's address, and nothing that lasts. Links
end up in browser history; a code found there later is worthless, because it has already been
used or has expired.

What comes back is a token for that one server. It can submit presence and read that group's
roster for the overlay, and it cannot do anything else — not ban, not kick, not read the member
list. It is stored encrypted to your Windows account in `%APPDATA%\Modbot\pairings.json`; the rest
of that file is left readable on purpose so you can see exactly which servers the client talks to.

## For testers and self-hosters: pointing the button at your own server

**Pair with a server** opens `https://my.modbot.co/go?redir=/pair` by default, which sends a signed-in
moderator on to their own group's Modbot. If you would rather it opened your server's page
directly, create `%APPDATA%\Modbot\settings.json` containing:

```json
{ "pairingPage": "https://modbot.example/pair" }
```

Restart the client. The address must be HTTPS, or plain HTTP to `localhost` only — anything else
is ignored and the default is used. You can always skip the button and open the `/pair` page in
your browser yourself; the client does not need to know where it is.

## What the client changed on your machine to make links work

One registry key under your own Windows account, `HKEY_CURRENT_USER\Software\Classes\modbot-client`,
naming the client's program file as what opens `modbot-client://` links. It needs no administrator
rights and affects nobody else's account. Uninstalling the client leaves the key pointing at a file
that no longer exists, which is harmless; deleting the key is safe at any time.
