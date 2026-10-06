# Desktop overlay: the Users list

The desktop overlay's people rows, drawn the way VRChat's own Here > Users list is, with the
pictures of the people in a group's instance. Applies to the window over VRChat in VRChat's look
(`OverlayLook.FromColours`) and nowhere else: the headset's rows are as they were.

## 1. One width

The panel was as wide as what was on it, so the Instance list and the Audit Log, whose rows were
built from different parts, came out different widths. Now:

- the window's frame has a fixed width (`DesktopOverlayWindow.PaintGround`): the window's own,
  less the room the shadow falls into;
- the screen's root (`OverlayView.FillWidth`) is exactly as wide as it is given and cuts what it
  holds to that;
- the rows lay out in a grid whose middle column takes what is left, so a long name, a long flag or
  `(here before you) ~1hr 4m+` is cut with an ellipsis inside its card.

## 2. Rows

A card is 68 px tall with a 7 px gap: a 48 px rounded-square picture, the name in bold with the
rank, 18+, flags and heads-up marks at the end of the line, and a small line under it (when they came,
`(here before you) ~Xm+`, or the **Left** tag and the seconds). The **+** is a square raised button at
the right. The Audit Log's cards carry the same, with the event's words under the name and the time at
the right. Every colour is the look's; the banner and the heading colour are worked out from the
palette in `VRChatLook` (`Banner`, `BannerText`, `Heading`) and never hard-coded.

Only words that are new: **Users** and **Other users**, VRChat's own labels. The count under
**Users** is the same present-only number the old "N here" bar said, and the freshness the bar carried
is at the heading's far end.

The moderator's own card is first, in a banner in the highlights colour, and has no **+**. The rows
scrolled past are the others; the banner stays. The driver's scroll count leaves the moderator's row
out while the window pins it (`OverlayDriver.PinsModeratorRow`, set from the window in VRChat's look),
so the last row is reachable without a wheel step that does nothing; the headset's list pins nothing
and counts every row. With no moderator known, or filtered out, there is
no banner and no label.

## 3. Pictures

**What the data is.** The server's roster carries, for each person whose stored picture the server's
cache already holds, a path on itself (`pictureUrl`, `/api/v1/companion/picture/{id}?v=…`): never
VRChat's address. VRChat's stored addresses need the service account's session cookie (see the VRChat
files design), so a PC cannot fetch one directly; the path is the server's own route for devices.

**The route** (`ContextHandler.PictureAsync`) takes a device token like every route in the folder,
names a person and never an address, and sends what the web app's own route (`/api/files/vrchat`)
would send for that person's stored address, from the same cache, with the same type and ETag.
**It sends what the cache holds and fetches nothing.** It has no VRChat gate, so it makes no request
to VRChat of any kind, and a miss is a 404. The cache is filled by the web app's route, when somebody
has a face shown there (the user's choice, 2026-10-05: serve cached pictures only; the first version
fetched a miss through the gate, and that was taken out). A path that would 404 is no use, and the
companion keeps a failed picture for as long as its address is wanted, so the roster only names a
person's path once the cache holds their picture, from the first roster read after that. Both the
route and the roster follow the operator's **Proxy VRChat images through Modbot** switch, which is off
by default: with it off the route answers 404 and the roster names no path, because Modbot does not
serve VRChat pictures on a server that turned that off, and sending the device on to VRChat would not
work.

**The companion** makes the path whole against the pairing's own address and takes only the form the
route has (`ServerPairing.PictureAddress`), so the picture is only ever fetched from the server that
named it. `GroupPictures` sends that server's token with it, to that server only, and reads the
picture at 160 px wide. Pictures are in memory, kept while a screen shows them
(`DesktopOverlayWindow.PictureAddresses`, asked for in `MainWindow.LetGoOfPicturesNothingShows`) and
let go of after. A person whose picture has not arrived, has none, or is in an instance no group owns
shows the head and shoulders.

**Choices not taken.** The stored VRChat address is not sent to the companion: it would not load.
Looking people up, or fetching a picture to fill the cache, is not done: the roster only names what the server already
holds. In an instance no group owns there are no pictures (the log has none) and nothing is asked.
