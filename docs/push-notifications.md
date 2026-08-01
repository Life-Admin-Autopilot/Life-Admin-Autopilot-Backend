# Push notifications

The backend sends notifications through **FCM HTTP v1**. One send covers both platforms:
Android devices get the message from FCM directly, iOS devices get it from FCM via APNs.
There is no separate APNs code path on the server — Firebase holds the APNs auth key and
does that hop itself.

> FCM accepting a message means it was queued for delivery, not that the handset showed it.
> Only a physical-device test proves the whole chain, which is why the checklist at the
> bottom is part of this story.

## 1. Firebase and APNs prerequisites (one-time, console work)

**Firebase project**

1. Create (or open) the Firebase project at <https://console.firebase.google.com>.
2. Add an **Android** app with the app's package id → download `google-services.json`.
3. Add an **iOS** app with the app's bundle id → download `GoogleService-Info.plist`.

**APNs (iOS only)** — without this, iOS sends fail with `THIRD_PARTY_AUTH_ERROR`:

1. Apple Developer portal → Certificates, Identifiers & Profiles → Keys → create a key
   with **Apple Push Notifications service (APNs)** enabled. Download the `.p8` **once**.
2. Enable **Push Notifications** on the App ID.
3. Firebase console → Project settings → **Cloud Messaging** → iOS app → upload the `.p8`
   with its Key ID and your Team ID.

**Server credentials**

Firebase console → Project settings → **Service accounts** → *Generate new private key*.
That JSON file is a private key: it is gitignored, never goes in `appsettings.json`, and
never reaches the client.

## 2. Backend configuration

Non-secret settings live in `appsettings.json`:

```json
"PushNotifications": {
  "ProjectId": "",
  "FcmBaseUrl": "https://fcm.googleapis.com/v1",
  "AndroidChannelId": "reminders",
  "TimeoutSeconds": 30,
  "MaxRetryAttempts": 3
}
```

`ProjectId` may stay empty — it is read from the service account key when omitted.

The credential comes from configuration, exactly like `SBG_API_KEY` does for Claude:

| Key | Use |
| --- | --- |
| `FCM_SERVICE_ACCOUNT_JSON` | The service account JSON *contents*. Preferred in deployed environments. |
| `FCM_SERVICE_ACCOUNT_FILE` | Path to the JSON file. Convenient locally; ignored when the JSON key is set. |

Local development:

```powershell
dotnet user-secrets set "FCM_SERVICE_ACCOUNT_FILE" "C:\keys\life-admin-firebase-adminsdk.json" --project Life-Admin-Autopilot-Backend
```

With neither key set the API still starts and every other feature works; push sends fail
fast with `PUSH_NOT_CONFIGURED` instead of calling FCM.

## 3. API

All endpoints require a bearer token. The device is always attached to the caller's own
user id from the JWT — a token can never be registered against someone else's account.

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/devices/register` | `{ "token": "...", "platform": "Android" \| "Ios", "deviceModel": "Pixel 8" }` — idempotent, call on every app start. |
| `GET` | `/api/devices` | The caller's active devices (tokens masked). |
| `DELETE` | `/api/devices` | `{ "token": "..." }` — call on logout. |
| `POST` | `/api/notificationstest/send-to-me` | `{ "title": "...", "body": "...", "data": { } }` — fans out to all the caller's devices. |
| `POST` | `/api/notificationstest/send-to-token` | `{ "deviceToken": "...", "title": "...", "body": "..." }` — targets one handset. |

`send-to-me` returns `404` when the user has no registered device and `502` when every
send failed, so a test that reached nobody cannot be mistaken for a pass.

From application code, inject `INotificationService` and call
`SendToUserAsync(userId, new PushMessage(title, body, data))` — it resolves the user's
devices, sends, logs failures and retires dead tokens. Do not call `IPushNotificationService`
(the FCM transport) directly.

## 4. Client setup (Capacitor)

```bash
npm install @capacitor/push-notifications
npx cap sync
```

**Android** — drop `google-services.json` into `android/app/`, and create the notification
channel whose id matches `PushNotifications:AndroidChannelId` (`reminders`), otherwise
Android 8+ shows reminders silently under the default channel.

**iOS** — drop `GoogleService-Info.plist` into the Xcode project, then in *Signing &
Capabilities* add **Push Notifications** and **Background Modes → Remote notifications**.
Push does not work in the iOS Simulator; a physical device is required.

Registration flow — the token from the `registration` event is what the backend stores:

```ts
import { PushNotifications } from '@capacitor/push-notifications';
import { Capacitor } from '@capacitor/core';

export async function initPush(apiFetch: typeof fetch) {
  const permission = await PushNotifications.requestPermissions();
  if (permission.receive !== 'granted') return;

  // Fires again whenever FCM rotates the token, so the backend upserts rather than inserts.
  PushNotifications.addListener('registration', async ({ value }) => {
    await apiFetch('/api/devices/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        token: value,
        platform: Capacitor.getPlatform() === 'ios' ? 'Ios' : 'Android',
      }),
    });
  });

  PushNotifications.addListener('registrationError', err => console.error('push registration failed', err));

  await PushNotifications.register();
}
```

On logout, `DELETE /api/devices` with the same token so a handed-down handset stops
receiving the previous owner's reminders.

## 5. Physical-device test checklist

Run this once per platform on real hardware. Both must pass for the story's acceptance
criteria.

1. Install the app on the device and sign in.
2. Accept the notification permission prompt.
3. `GET /api/devices` → the device is listed with the expected platform.
4. `POST /api/notificationstest/send-to-me` with a title and body.
5. **Expected:** `200` with `sentCount: 1`, and the notification appears on the device —
   with the app backgrounded (banner) and foregrounded (`pushNotificationReceived` fires).
6. Server log shows `Push accepted by FCM for device ... as projects/.../messages/... in NNms`.

Then verify the failure path (acceptance criterion 2):

7. `POST /api/notificationstest/send-to-token` with a deliberately corrupted token.
8. **Expected:** `502` with `errorCode: PUSH_TOKEN_INVALID` (or `PUSH_INVALID_ARGUMENT` for
   a malformed one), and a matching `warning` in the server log. Nothing is swallowed.
9. Uninstall the app, then `send-to-me` again → the send fails with `PUSH_TOKEN_INVALID`,
   the log records it, and `GET /api/devices` no longer lists that device: the dead token
   was retired rather than retried forever.

## 6. Failure handling

Every failed send is logged as a warning at two levels — the FCM transport (with the raw
FCM error) and the notification service (with the user and whether the token was retired).
Device tokens are always masked in logs and API responses (`e8Xq7T...9Zk4 (len 150)`).

| Code | Meaning | What happens |
| --- | --- | --- |
| `PUSH_TOKEN_INVALID` | `UNREGISTERED` / `SENDER_ID_MISMATCH` — app uninstalled, token rotated, or wrong Firebase project | Logged, and the token is deactivated |
| `PUSH_INVALID_ARGUMENT` | FCM rejected the request | Logged; the token is **kept** — a payload bug of ours must not wipe every user's devices |
| `PUSH_NOT_AUTHORIZED` | Our service account is bad, or Firebase could not authenticate with APNs | Logged; needs an operator — nothing will deliver until it is fixed |
| `PUSH_RATE_LIMITED` | `QUOTA_EXCEEDED` | Logged |
| `PUSH_UNAVAILABLE` | FCM 5xx | Logged; retried automatically (3 attempts, exponential backoff) |
| `PUSH_NETWORK_ERROR` | FCM unreachable or timed out | Logged; retried automatically |
| `PUSH_NOT_CONFIGURED` | No service account in this environment | Logged; FCM is never called |

A user with no registered device is logged as a warning too — otherwise a reminder that
reached nobody would look like a successful send.
