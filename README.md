# Spotibuds User API

ASP.NET Core 8 backend service for Spotibuds user profiles, social relationships, chat, feed activity, notifications, profile-picture uploads, and realtime SignalR updates.

The service stores application data in MongoDB, publishes optional friend events through RabbitMQ, uses Azure Blob Storage for user media, and exposes REST endpoints plus SignalR hubs for interactive clients.

## Features

- User profile CRUD with external identity-service IDs
- Profile-picture upload to Azure Blob Storage with SAS URLs
- Follow and friend-request workflows
- One-to-one and group chats with read receipts
- Realtime chat, friend, and notification updates over SignalR
- Notification inbox with read, handled, cleanup, and delete flows
- Feed slides built from recent listening history, weekly top artists, common artists, reactions, and now-playing state
- Weekly background job that recomputes each user's top artists
- Health and diagnostic endpoints for MongoDB, JWT, CORS, and SignalR
- Dockerfile for containerized deployment

## Tech Stack

- .NET 8 / ASP.NET Core Web API
- SignalR
- MongoDB
- RabbitMQ
- Azure Blob Storage
- JWT bearer authentication
- Swagger / OpenAPI in development

## Project Structure

```text
.
|-- Controllers/        REST API controllers
|-- Data/               MongoDB context and collection access
|-- Entities/           MongoDB-backed domain entities
|-- Hubs/               SignalR hubs for realtime clients
|-- Models/             User profile model and embedded value objects
|-- Services/           Blob storage, RabbitMQ, notifications, background jobs
|-- Program.cs          Application startup, dependency injection, middleware, routes
|-- User.csproj         .NET project file
|-- Dockerfile          Production container build
```

## Prerequisites

- .NET SDK 8.0+
- MongoDB instance or MongoDB Atlas connection string
- Azure Storage account, Azurite, or another connection string accepted by `Azure.Storage.Blobs`
- RabbitMQ if you want friend-event publishing
- A JWT issuer that signs tokens using the configured shared secret

## Configuration

Configuration can be supplied through `appsettings.json`, `appsettings.Development.json`, environment variables, or user secrets. For environment variables, use double underscores for nested keys.

| Setting | Required | Notes |
| --- | --- | --- |
| `ConnectionStrings:MongoDb` | Yes | MongoDB connection string. The database name is currently set to `spotibuds` in `Program.cs`. |
| `Jwt:Secret` | Required for auth | Symmetric signing secret used to validate JWTs. Use a long random value. |
| `Jwt:Issuer` | Required for auth | Expected JWT issuer. |
| `Jwt:Audience` | Required for auth | Expected JWT audience. |
| `Cors:AllowedOrigins` | Required for browser clients | Comma-separated origins, for example `http://localhost:3000`. `*` allows any origin without credentials. |
| `AzureStorage:ConnectionString` | Required for `UsersController` | Needed because user routes depend on `IAzureBlobService`. Use a real storage account or `UseDevelopmentStorage=true` with Azurite. |
| `AzureStorage:UsersContainer` | No | Blob container for user profile pictures. Defaults to `users`. |
| `RabbitMQ:HostName` | No | Defaults to `localhost` when not configured. |
| `RabbitMQ:Port` | No | Defaults to `5672`. |
| `RabbitMQ:UserName` | No | Defaults to `guest`. |
| `RabbitMQ:Password` | No | Defaults to `guest`. |
| `RabbitMQ:VirtualHost` | No | Defaults to `/`. |

Example PowerShell setup:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ConnectionStrings__MongoDb = "mongodb://localhost:27017"
$env:Jwt__Secret = "replace-with-a-long-random-secret-at-least-32-chars"
$env:Jwt__Issuer = "spotibuds-auth"
$env:Jwt__Audience = "spotibuds-api"
$env:Cors__AllowedOrigins = "http://localhost:3000"
$env:AzureStorage__ConnectionString = "UseDevelopmentStorage=true"
$env:AzureStorage__UsersContainer = "users"
$env:RabbitMQ__HostName = "localhost"
$env:RabbitMQ__Port = "5672"
$env:RabbitMQ__UserName = "guest"
$env:RabbitMQ__Password = "guest"
$env:RabbitMQ__VirtualHost = "/"
```

## Run Locally

Restore and run the API:

```powershell
dotnet restore
dotnet run --project User.csproj
```

The application currently binds to port `80` via `builder.WebHost.UseUrls("http://0.0.0.0:80")` in `Program.cs`.

Useful local URLs:

- `http://localhost/` - simple service status text
- `http://localhost/health` - application health
- `http://localhost/health/mongodb` - MongoDB health
- `http://localhost/swagger` - Swagger UI in `Development`

If port `80` is unavailable on your machine, update the `UseUrls` value in `Program.cs` or pass a different URL setting as part of your local hosting setup.

## Docker

Build the image:

```powershell
docker build -t spotibuds-user-api .
```

Run the container on host port `8080`:

```powershell
docker run --rm -p 8080:80 `
  -e ASPNETCORE_ENVIRONMENT=Development `
  -e ConnectionStrings__MongoDb="mongodb://host.docker.internal:27017" `
  -e Jwt__Secret="replace-with-a-long-random-secret-at-least-32-chars" `
  -e Jwt__Issuer="spotibuds-auth" `
  -e Jwt__Audience="spotibuds-api" `
  -e Cors__AllowedOrigins="http://localhost:3000" `
  -e AzureStorage__ConnectionString="UseDevelopmentStorage=true" `
  spotibuds-user-api
```

Then open `http://localhost:8080/health`.

## API Overview

The app uses controller routes under `/api/{controller}` plus a few root diagnostics in `Program.cs`.

### Root and Diagnostics

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/` | Basic service status |
| `GET` | `/health` | API health response |
| `GET` | `/health/mongodb` | MongoDB ping |
| `GET` | `/jwt-config` | Shows whether JWT settings are loaded |
| `GET` | `/test-jwt?token=...` | Parses a JWT for diagnostics |
| `GET` | `/diagnose-signalr` | Dumps request, auth, and WebSocket diagnostics |
| `GET` | `/cors-config` | Shows resolved CORS configuration |

### Users

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/users/health` | Users controller health |
| `GET` | `/api/users/all` | List users |
| `GET` | `/api/users/{id}` | Get a user by MongoDB ID |
| `GET` | `/api/users/identity/{identityUserId}` | Get a user by identity-service ID |
| `GET` | `/api/users/search?q=&page=&pageSize=` | Search users |
| `POST` | `/api/users` | Create a user profile |
| `PUT` | `/api/users/{id}` | Update by MongoDB ID |
| `PUT` | `/api/users/identity/{identityUserId}` | Update by identity-service ID |
| `DELETE` | `/api/users/{id}` | Delete a user |
| `POST` | `/api/users/{id}/profile-picture` | Upload profile picture by MongoDB ID |
| `POST` | `/api/users/identity/{identityUserId}/profile-picture` | Upload profile picture by identity-service ID |
| `POST` | `/api/users/{userId}/listening-history` | Add listening history |
| `POST` | `/api/users/identity/{identityUserId}/listening-history` | Add listening history by identity-service ID |
| `GET` | `/api/users/{userId}/listening-history` | Get listening history |
| `GET` | `/api/users/identity/{identityUserId}/listening-history` | Get listening history by identity-service ID |
| `GET` | `/api/users/identity/{identityUserId}/top-artists/week/current` | Get cached current-week top artists |

Create user example:

```json
{
  "identityUserId": "auth-user-123",
  "userName": "alex",
  "displayName": "Alex",
  "bio": "Listening loudly.",
  "avatarUrl": null,
  "isPrivate": false
}
```

Listening-history example:

```json
{
  "songId": "track-123",
  "songTitle": "Song Title",
  "artist": "Artist Name",
  "coverUrl": "https://example.com/cover.jpg",
  "duration": 180
}
```

### Follows and Friends

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/follows` | Follow a user |
| `DELETE` | `/api/follows` | Unfollow a user |
| `GET` | `/api/follows/{userId}/followers` | List followers |
| `GET` | `/api/follows/{userId}/following` | List following |
| `GET` | `/api/follows/check?followerId=&followedId=` | Check follow relationship |
| `GET` | `/api/follows/{userId}/stats` | Follower and following counts |
| `POST` | `/api/friends/request` | Send friend request |
| `POST` | `/api/friends/{friendshipId}/accept` | Accept friend request |
| `POST` | `/api/friends/{friendshipId}/decline` | Decline friend request |
| `GET` | `/api/friends/pending/{userId}` | Pending friend requests |
| `GET` | `/api/friends/{userId}` | Friend IDs for a user |
| `GET` | `/api/friends/status?userId1=&userId2=` | Friendship status |
| `DELETE` | `/api/friends/{friendshipId}` | Remove friend |

Follow request body:

```json
{
  "followerId": "auth-user-123",
  "followedId": "auth-user-456"
}
```

Friend request body:

```json
{
  "toUserId": "auth-user-456"
}
```

### Chats

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/chats/create-or-get` | Create or retrieve a chat |
| `GET` | `/api/chats/{id}` | Get chat details |
| `GET` | `/api/chats/user/{userId}` | List chats for a user |
| `GET` | `/api/chats/{chatId}/messages?page=&pageSize=` | Get paged chat messages |
| `POST` | `/api/chats/{chatId}/messages` | Send a message |
| `GET` | `/api/chats/messages/{messageId}` | Get a single message |
| `POST` | `/api/chats/messages/{messageId}/read` | Mark one message as read |
| `DELETE` | `/api/chats/{chatId}` | Delete a chat |
| `GET` | `/api/chats/unread-counts` | Get unread counts by chat |
| `GET` | `/api/chats/{chatId}/unread-count` | Get unread count for one chat |
| `POST` | `/api/chats/{chatId}/mark-all-read` | Mark all messages read |

Create chat body:

```json
{
  "participantIds": ["auth-user-123", "auth-user-456"],
  "isGroup": false,
  "name": ""
}
```

Send message body:

```json
{
  "content": "Hey, what are you listening to?",
  "type": "Text",
  "replyToId": null
}
```

### Feed and Notifications

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/feed/slides?identityUserId=&limit=&skip=` | Get shuffled feed slides |
| `POST` | `/api/feed/nowplaying?ttlSec=90` | Set in-memory now-playing state |
| `DELETE` | `/api/feed/nowplaying/{identityUserId}` | Clear now-playing state |
| `POST` | `/api/feed/nowplaying/batch` | Get now-playing state for many users |
| `POST` | `/api/feed/reactions` | Create or update a reaction |
| `GET` | `/api/feed/reactions/latest?identityUserId=&limit=&skip=` | Latest reactions for a user |
| `GET` | `/api/feed/reactions/by-post?postId=&currentUserId=` | Reactions for a feed post |
| `GET` | `/api/feed/post?id=` | Get one feed post |
| `GET` | `/api/notifications/{userId}?limit=&skip=` | List notifications |
| `POST` | `/api/notifications/{notificationId}/read` | Mark one notification read |
| `POST` | `/api/notifications/{notificationId}/handle` | Mark one notification handled |
| `POST` | `/api/notifications/{userId}/read-all` | Mark all notifications read |
| `DELETE` | `/api/notifications/{userId}/cleanup?daysOld=30` | Delete old notifications |
| `DELETE` | `/api/notifications/{notificationId}?userId=` | Delete one notification |
| `DELETE` | `/api/notifications/{userId}/all` | Delete all notifications for a user |

Now-playing body:

```json
{
  "identityUserId": "auth-user-123",
  "songId": "track-123",
  "songTitle": "Song Title",
  "artist": "Artist Name",
  "coverUrl": "https://example.com/cover.jpg",
  "positionSec": 42,
  "isPlaying": true
}
```

## SignalR

SignalR hubs are mapped at:

| Hub | Path | Purpose |
| --- | --- | --- |
| `FriendHub` | `/friend-hub` | Friend requests, friend status, simple chat helpers, online friends |
| `NotificationHub` | `/notification-hub` | Notification inbox updates and unread counts |
| `ChatHub` | `/chat-hub` | Chat rooms, messages, typing indicators, read receipts |

Clients should provide the JWT as an access token when connecting. SignalR JavaScript clients can do that with `accessTokenFactory`:

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("http://localhost/chat-hub", {
    accessTokenFactory: () => token
  })
  .withAutomaticReconnect()
  .build();

await connection.start();
```

Common client-invoked methods:

- `FriendHub`: `SendFriendRequest`, `AcceptFriendRequest`, `RemoveFriend`, `SendMessage`, `MarkMessageAsRead`, `CreateChat`, `GetOnlineFriends`
- `NotificationHub`: `GetNotifications`, `MarkAsRead`, `MarkAsHandled`, `MarkAllAsRead`, `SendNotificationToUser`
- `ChatHub`: `JoinChat`, `LeaveChat`, `SendMessage`, `MarkAsRead`, `StartTyping`, `StopTyping`

Common server-sent events:

- `FriendRequestReceived`, `FriendRequestSent`, `FriendRequestAccepted`, `FriendRemoved`
- `ChatCreated`, `ReceiveMessage`, `MessageReceived`, `MessageSent`, `MessageRead`
- `UserStartedTyping`, `UserStoppedTyping`, `OnlineFriends`, `FriendStatusChanged`
- `NewNotification`, `UnreadCountUpdate`, `NotificationsLoaded`
- `Error`

## Background Services

- `WeeklyTopArtistsService` runs continuously and recomputes each user's top three artists after the next Sunday 00:00 UTC boundary.
- `RabbitMqConsumerService` is registered, but consumer setup is currently disabled in code because direct SignalR notifications are used for the active flows.
- `NowPlayingStore` uses in-memory cache with short TTLs, so now-playing data is process-local and resets when the API restarts.

## Data Model Notes

- `User.IdentityUserId` is the external identity ID used across most social APIs.
- MongoDB collections are accessed through `MongoDbContext`: `users`, `friends`, `chats`, `messages`, `reactions`, `feed`, and `notifications`.
- Chat participants, friend IDs, follower IDs, and notification target/source IDs generally use identity-service user IDs.
- Profile pictures are stored under `{userId}/profile_pic/{guid}.{extension}` in the configured Azure Blob container.

## Troubleshooting

- If auth fails for API calls or hubs, check `/jwt-config` and verify `Jwt:Secret`, `Jwt:Issuer`, and `Jwt:Audience` match the token issuer.
- If browser requests fail, check `/cors-config` and make sure the frontend origin is listed in `Cors:AllowedOrigins`.
- If SignalR fails during negotiation or WebSocket upgrade, call `/diagnose-signalr` from the same client context and inspect whether the access token is being sent.
- If MongoDB-backed endpoints return `503`, call `/health/mongodb` and verify `ConnectionStrings:MongoDb`.
- If user endpoints fail during controller activation, verify `AzureStorage:ConnectionString` is configured. For local development with Azurite, `UseDevelopmentStorage=true` is enough to construct the blob client.

## Development Commands

```powershell
dotnet restore
dotnet build
dotnet run --project User.csproj
```

There is no test project in this repository yet. Add focused tests around controllers and services before making behavior changes with a wide blast radius.
