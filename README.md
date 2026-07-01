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
- Spotify device discovery
- Room creation with browser-local host control protection
- Guest link QR code generation
- Queue item creation and listing
- Duplicate track detection for active queue items
- Host moderation controls to remove and requeue songs
- Manual `queue-next` endpoint that sends the next waiting item to Spotify
- Manual `play-next` endpoint that starts the next waiting item immediately on Spotify
- Local MVP frontend served from the Functions app at `/api/app`
- Azure Bicep infrastructure and GitHub Actions deployment workflow

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

## Azure deployment

Infrastructure lives in `infra/` and is deployed by `.github/workflows/deploy-api.yml` on pushes to `main`.

Default production values:

- Resource group: `partyplaylist`
- Region: `swedencentral`
- Environment: `prod`
- Function App: `partyplaylist-ew`
- Azure URL: `https://partyplaylist-ew.azurewebsites.net/api/app`
- Spotify redirect URI: `https://partyplaylist-ew.azurewebsites.net/api/auth/callback`

```
az deployment sub validate `
  --location swedencentral `
  --template-file infra/main.bicep `
  --parameters `
    location=swedencentral `
    resourceGroupName=partyplaylist `
    environmentName=prod `
    functionAppName=partyplaylist-ew `
    spotifyClientId="xxx" `
    spotifyClientSecret="xxx" `
    spotifyRedirectUri="https://partyplaylist-ew.azurewebsites.net/api/auth/callback"
```

The deployment creates:

- Resource group
- Azure Storage account
- Blob container for Flex Consumption deployments
- Azure Functions Flex Consumption plan
- Linux Azure Function App
- Log Analytics workspace
- Application Insights
- Key Vault secret for the Spotify client secret

### Required GitHub secrets

Create these repository or environment secrets before merging the deployment workflow:

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
SPOTIFY_CLIENT_ID
SPOTIFY_CLIENT_SECRET
```

The Azure secrets are used by GitHub Actions OIDC login. The Spotify secret is stored in Key Vault and referenced by the Function App.

### Bootstrap Azure access for GitHub Actions

Run these once from a local shell where Azure CLI is logged in. Replace `<subscription-id>` and `<tenant-id>` with your values.

```powershell
$subscriptionId = "5957e955-1bb3-4fad-9b79-b2669eb0b734"
$tenantId = "04368cd7-79db-48c2-a243-1f6c2025dec8"
$githubOwner = "erikwasa"
$githubRepo = "partyplaylist"
$appName = "partyplaylist-github-actions"

az account set --subscription $subscriptionId

$app = az ad app create --display-name $appName | ConvertFrom-Json
$clientId = $app.appId

$sp = az ad sp create --id $clientId | ConvertFrom-Json

az role assignment create `
  --assignee $sp.id `
  --role Contributor `
  --scope "/subscriptions/$subscriptionId"

az ad app federated-credential create `
  --id $clientId `
  --parameters "{`"name`":`"github-main-prod`",`"issuer`":`"https://token.actions.githubusercontent.com`",`"subject`":`"repo:$githubOwner/$githubRepo:environment:prod`",`"audiences`":[`"api://AzureADTokenExchange`"]}"

Write-Host "AZURE_CLIENT_ID=$clientId"
Write-Host "AZURE_TENANT_ID=$tenantId"
Write-Host "AZURE_SUBSCRIPTION_ID=$subscriptionId"
```

Then add the printed values as GitHub secrets. Also create a GitHub environment named `prod`, because the workflow targets that environment.

### Register the production Spotify redirect URI

Add this exact URI in the Spotify Developer Dashboard:

```text
https://partyplaylist-ew.azurewebsites.net/api/auth/callback
```

## Local demo UI

After Spotify login succeeds, open the local app:

```text
http://127.0.0.1:7071/api/app
```

Host flow:

1. Open Spotify on the host computer, phone, or speaker.
2. Start playback manually once so Spotify exposes an active device.
3. In Party Playlist, click **Refresh devices**.
4. Select the playback device and click **Save device**.
5. Click **Create room**.
6. Share the generated guest link or QR code.
7. Keep the host browser tab open to view and moderate the queue.
8. Click **Queue next** to add the next waiting song to Spotify's queue.
9. Click **Play next now** to immediately start the next waiting song on Spotify.
10. Use **Remove** and **Requeue** for simple moderation.

Guest flow:

1. Open the guest link or scan the QR code.
2. Enter a name or alias.
3. Search for a song.
4. Click **Add** on a search result.

The page polls the queue every few seconds. This is intentionally simple for the MVP; a later version can move the frontend to Azure Static Web Apps and replace polling with SignalR.

## Host controls

Each room gets a generated host control value when it is created. The frontend stores it in `localStorage` for that browser and sends it as an `X-Host-Code` header for host-only actions.

Host-only endpoints currently include:

- saving the room playback device
- queueing the next song to Spotify
- starting the next song immediately
- removing queue items
- requeueing removed items

Guest links and QR codes do not include the host control value.

## Spotify redirect URI

Add this redirect URI in the Spotify Developer Dashboard for local development:

```text
http://127.0.0.1:7071/api/auth/callback
```

The value in Spotify Developer Dashboard and `Spotify__RedirectUri` must match exactly, including scheme, host, port, path, case, and trailing slash behavior.

After changing `local.settings.json`, restart the Functions host so the app reloads the new redirect URI.

## Spotify device troubleshooting

If **Queue next** says no active Spotify device was found:

1. Open the official Spotify app as the host user.
2. Start playing any track manually.
3. Return to Party Playlist and click **Refresh devices**.
4. Select the active, unrestricted device and click **Save device**.
5. Try **Queue next** again.

Restricted devices cannot be controlled through the Spotify Web API.

You can also inspect devices directly:

```powershell
Invoke-RestMethod http://127.0.0.1:7071/api/devices
```

## API endpoints

- `GET /api/health`
- `GET /api/app`
- `GET /api/auth/login`
- `GET /api/auth/callback`
- `GET /api/devices`
- `POST /api/rooms`
- `GET /api/rooms/{roomId}/qr`
- `POST /api/rooms/{roomId}/device`
- `GET /api/search?query=...`
- `GET /api/rooms/{roomId}/queue`
- `POST /api/rooms/{roomId}/queue`
- `POST /api/rooms/{roomId}/queue-next`
- `POST /api/rooms/{roomId}/play-next`
- `POST /api/rooms/{roomId}/queue/{itemId}/remove`
- `POST /api/rooms/{roomId}/queue/{itemId}/requeue`

Host-only endpoints require the `X-Host-Code` header.

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
