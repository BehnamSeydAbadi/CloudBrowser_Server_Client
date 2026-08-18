# Windows-Mobile-Cloud-Browser
"Run" chromium on your windows phone

**Currently a proof of concept** inspired by [browservice](https://github.com/ttalvitie/browservice).

This was hacked together in a few days, much of it is hardcoded & the code is pretty ugly (for now) but it works.

When it grows up it aims to be a usable modern browser for windows mobile devices that anyone can install on a PC (server) and have an up to date web browser on WP (client).

<div style="display: flex; gap: 10px; align-items: flex-start;">
<img src="https://private-user-images.githubusercontent.com/39994282/636598230-683b6d73-cc55-471a-aa7c-42a4b642d365.png?jwt=eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJnaXRodWIuY29tIiwiYXVkIjoicmF3LmdpdGh1YnVzZXJjb250ZW50LmNvbSIsImtleSI6ImtleTUiLCJleHAiOjE3ODY4NDc0MjgsIm5iZiI6MTc4Njg0NzEyOCwicGF0aCI6Ii8zOTk5NDI4Mi82MzY1OTgyMzAtNjgzYjZkNzMtY2M1NS00NzFhLWFhN2MtNDJhNGI2NDJkMzY1LnBuZz9YLUFtei1BbGdvcml0aG09QVdTNC1ITUFDLVNIQTI1NiZYLUFtei1DcmVkZW50aWFsPUFLSUFWQ09EWUxTQTUzUFFLNFpBJTJGMjAyNjA4MTYlMkZ1cy1lYXN0LTElMkZzMyUyRmF3czRfcmVxdWVzdCZYLUFtei1EYXRlPTIwMjYwODE2VDAyMjUyOFomWC1BbXotRXhwaXJlcz0zMDAmWC1BbXotU2lnbmF0dXJlPTJmYWRiNDhhZGQ3NWZlZjE0YjUwMWEzYjljYTUyNzIwODQwYTBjYzE5NDUwNjAxMGI1ZDY1NjUxNjY3ZWY0NzEmWC1BbXotU2lnbmVkSGVhZGVycz1ob3N0JnJlc3BvbnNlLWNvbnRlbnQtdHlwZT1pbWFnZSUyRnBuZyJ9.O-g3s20YtBIgiGf5I_Jg7UiXdiuuioBdfFcUIrvX6eM" height="500">
<img src="https://private-user-images.githubusercontent.com/39994282/636598293-e632b558-1600-43a8-bff9-a0aadd6d821d.png?jwt=eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJnaXRodWIuY29tIiwiYXVkIjoicmF3LmdpdGh1YnVzZXJjb250ZW50LmNvbSIsImtleSI6ImtleTUiLCJleHAiOjE3ODY4NDgwMDAsIm5iZiI6MTc4Njg0NzcwMCwicGF0aCI6Ii8zOTk5NDI4Mi82MzY1OTgyOTMtZTYzMmI1NTgtMTYwMC00M2E4LWJmZjktYTBhYWRkNmQ4MjFkLnBuZz9YLUFtei1BbGdvcml0aG09QVdTNC1ITUFDLVNIQTI1NiZYLUFtei1DcmVkZW50aWFsPUFLSUFWQ09EWUxTQTUzUFFLNFpBJTJGMjAyNjA4MTYlMkZ1cy1lYXN0LTElMkZzMyUyRmF3czRfcmVxdWVzdCZYLUFtei1EYXRlPTIwMjYwODE2VDAyMzUwMFomWC1BbXotRXhwaXJlcz0zMDAmWC1BbXotU2lnbmF0dXJlPTZmYzliMTJjNmZiNzEwZjdiZDk3ZTNiNWFhYzlhYWM3MTJjZjA3NjQ5OTg3ZTg2ZTU2N2VhZjcwYTM4MGU3MjEmWC1BbXotU2lnbmVkSGVhZGVycz1ob3N0JnJlc3BvbnNlLWNvbnRlbnQtdHlwZT1pbWFnZSUyRnBuZyJ9.mIpENm3WIqsCxJ4DYYHejwZ1eFZHWCdY4p44ZTVMI1M" height="500">
  <img src="https://private-user-images.githubusercontent.com/39994282/636598257-41e0f3cc-59ca-4a6e-83b3-87051da86298.png?jwt=eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJnaXRodWIuY29tIiwiYXVkIjoicmF3LmdpdGh1YnVzZXJjb250ZW50LmNvbSIsImtleSI6ImtleTUiLCJleHAiOjE3ODY4NDc3NTMsIm5iZiI6MTc4Njg0NzQ1MywicGF0aCI6Ii8zOTk5NDI4Mi82MzY1OTgyNTctNDFlMGYzY2MtNTljYS00YTZlLTgzYjMtODcwNTFkYTg2Mjk4LnBuZz9YLUFtei1BbGdvcml0aG09QVdTNC1ITUFDLVNIQTI1NiZYLUFtei1DcmVkZW50aWFsPUFLSUFWQ09EWUxTQTUzUFFLNFpBJTJGMjAyNjA4MTYlMkZ1cy1lYXN0LTElMkZzMyUyRmF3czRfcmVxdWVzdCZYLUFtei1EYXRlPTIwMjYwODE2VDAyMzA1M1omWC1BbXotRXhwaXJlcz0zMDAmWC1BbXotU2lnbmF0dXJlPWI4M2VlODhlMGI5YjE1ODJhN2JkZmY5MDIxOGQyMTY1NzkxZDk3YmU1MTRjMTQ5MzQzYWE4NDRlNWRjZWQ3Y2QmWC1BbXotU2lnbmVkSGVhZGVycz1ob3N0JnJlc3BvbnNlLWNvbnRlbnQtdHlwZT1pbWFnZSUyRnBuZyJ9.-ZyzfoOn-cc_Lpj5xhzdOESEqeCpCEZC2-7kA5645qk" height="500">
</div>

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
- [X] File downloads (Till 200MB is funtional)
- [X] Camera + QRCode scanning 
- [X] Improve audio playback quality
- [X] Faster & smarter transport (chunking?, rawbytes?, SYN/ACK)

### What's needed
- [ ] Notifications
- [ ] Microphone
- [ ] Faster rendering (GPU?)
- [ ] File uploads
- [ ] Large File downloads
- [ ] In Private/Incognito
- [ ] General browser stuff
