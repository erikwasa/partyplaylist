# Party Playlist

A small social web app for parties and gatherings where a host signs in with Spotify Premium, starts a room, and guests add songs to a shared queue.

The app does not stream music. Spotify handles playback through the host account and the selected Spotify Connect device. This repository starts with a local-first Azure Functions backend using .NET 10 isolated worker and Azure Table Storage/Azurite.

## Current status

Implemented so far:

- Azure Functions isolated worker backend targeting .NET 10
- `GET /api/health`
- Spotify OAuth login and callback
- Host token storage and refresh in Table Storage
- Spotify track search through the host token
- Room creation
- Queue item creation and listing
- Manual `queue-next` endpoint that sends the next waiting item to Spotify
- Local MVP frontend served from the Functions app at `/api/app`

## Local prerequisites

Install these on Windows:

- .NET 10 SDK
- Azure Functions Core Tools v4
- Azurite
- A Spotify Developer App
- Spotify Premium on the host account

## Local setup

```powershell
cd api
Copy-Item local.settings.example.json local.settings.json
notepad local.settings.json
```

Fill in your Spotify client id, client secret, and redirect URI in `local.settings.json`.

For local Spotify auth, use the loopback IP address rather than `localhost`:

```json
"Spotify__RedirectUri": "http://127.0.0.1:7071/api/auth/callback"
```

Start Azurite in a separate PowerShell window:

```powershell
azurite --location .azurite --debug .azurite/debug.log
```

Run the backend:

```powershell
dotnet restore
func start
```

Test health:

```powershell
Invoke-RestMethod http://127.0.0.1:7071/api/health
```

Start Spotify login in a browser:

```text
http://127.0.0.1:7071/api/auth/login
```

## Local demo UI

After Spotify login succeeds, open the local app:

```text
http://127.0.0.1:7071/api/app
```

Host flow:

1. Click **Create room**.
2. Copy the generated guest link.
3. Keep the page open to view the queue.
4. Click **Queue next** to send the next waiting song to Spotify.

Guest flow:

1. Open the guest link.
2. Enter a name or alias.
3. Search for a song.
4. Click **Add** on a search result.

The page polls the queue every few seconds. This is intentionally simple for the MVP; a later version can move the frontend to Azure Static Web Apps and replace polling with SignalR.

## Spotify redirect URI

Add this redirect URI in the Spotify Developer Dashboard for local development:

```text
http://127.0.0.1:7071/api/auth/callback
```

The value in Spotify Developer Dashboard and `Spotify__RedirectUri` must match exactly, including scheme, host, port, path, case, and trailing slash behavior.

After changing `local.settings.json`, restart the Functions host so the app reloads the new redirect URI.

## API endpoints

- `GET /api/health`
- `GET /api/app`
- `GET /api/auth/login`
- `GET /api/auth/callback`
- `POST /api/rooms`
- `GET /api/search?query=...`
- `GET /api/rooms/{roomId}/queue`
- `POST /api/rooms/{roomId}/queue`
- `POST /api/rooms/{roomId}/queue-next`

## Example queue request

```powershell
$body = @{
  trackUri = "spotify:track:..."
  trackName = "Song name"
  artistName = "Artist name"
  requestedBy = "Guest name"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:7071/api/rooms/<roomId>/queue" `
  -Body $body `
  -ContentType "application/json"
```

## Notes

`local.settings.json` is intentionally ignored because it contains local secrets. Use `local.settings.example.json` as the template.
