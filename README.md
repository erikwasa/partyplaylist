# Party Playlist

A small social web app for parties and gatherings where a host signs in with Spotify Premium, starts a room, and guests add songs to a shared queue.

The app does not stream music. Spotify handles playback through the host account and the selected Spotify Connect device. This repository starts with a local-first Azure Functions backend using .NET 10 isolated worker and Azure Table Storage/Azurite.

## Backend status

Implemented in this PR:

- Azure Functions isolated worker backend targeting .NET 10
- `GET /api/health`
- Spotify OAuth login and callback
- Host token storage and refresh in Table Storage
- Spotify track search through the host token
- Room creation
- Queue item creation and listing
- Manual `queue-next` endpoint that sends the next waiting item to Spotify

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
Invoke-RestMethod http://localhost:7071/api/health
```

Start Spotify login in a browser:

```text
http://localhost:7071/api/auth/login
```

## Spotify redirect URI

Add this redirect URI in the Spotify Developer Dashboard for local development:

```text
http://localhost:7071/api/auth/callback
```

## API endpoints

- `GET /api/health`
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
  -Uri "http://localhost:7071/api/rooms/<roomId>/queue" `
  -Body $body `
  -ContentType "application/json"
```

## Notes

`local.settings.json` is intentionally ignored because it contains local secrets. Use `local.settings.example.json` as the template.
