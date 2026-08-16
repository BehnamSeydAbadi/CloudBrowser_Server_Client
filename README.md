# Windows-Mobile-Cloud-Browser
"Run" chromium on your windows phone

**Currently a proof of concept** inspired by [browservice](https://github.com/ttalvitie/browservice).

This was hacked together in a few days, much of it is hardcoded & the code is pretty ugly (for now) but it works.

When it grows up it aims to be a usable modern browser for windows mobile devices that anyone can install on a PC (server) and have an up to date web browser on WP (client).

<img src="https://user-images.githubusercontent.com/1968543/235461724-d328459b-881c-4540-a086-4824e0c3aa1f.JPEG" height="500"><img src="https://user-images.githubusercontent.com/1968543/235461704-9f9ded26-ac71-4f79-9641-31fd9460d9ea.jpg" height="500">

### How to try
For now your phone and your server needs to be on the same network

1. Download the latest [release](https://github.com/PreyK/Windows-Mobile-Browser-Streaming/releases). 
2. Run the server app on your PC
3. Open the client app on your phone, enter the IP of the server (your PC's local IP in `ws://localip:8081` format) and click connect
4. Navigate to a page or search using google



### What works
- [x] 2 way communication with websockets and JSON
- [x] Render buffer forwarding to a UWP client
- [x] Navigation events from UWP client
- [x] Touch input events (multitouch) from UWP client
- [x] Add to Home (pin current page to Start)
- [X] Auto finding the server if on local connection (UDP discovery packets)
- [X] Easy&secure remote connections via tunnels (Ngrok, ZeroTier, serveo)
- [X] Auto scaling renderview based on screen resolution/rotation/UWP viewport
- [X] Multitouch
- [X] Text input
- [x] Auto save the last server address
- [x] Audio playback forwarding
- [X] Tabs
- [X] Back/Forward

### What's needed
- [ ] HiDPI
- [ ] Faster rendering (GPU?)
- [ ] Faster & smarter transport (chunking?, rawbytes?, SYN/ACK)
- [ ] Configurable streaming quality (ondemand rendering?)
- [ ] File uploads
- [ ] File downloads
- [ ] Camera & microphone
- [ ] Notifications
- [ ] In Private/Incognito
- [ ] General browser stuff
- [ ] Continuum support/verify
