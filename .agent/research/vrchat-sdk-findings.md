# VRChat.API SDK — findings

- **Version:** 2.20.9 (latest stable; the `2.20.9-nightly.*` builds predate it, so there is no newer preview)
- **Found while:** building `explore/` (2026-09-12)

Upstream-fixable notes for `vrchatapi/vrchatapi-csharp`, which this project's maintainer also maintains.

---

## 1. `TryLoginAsync` inverts its own result — **bug**

`wrapper/VRChat.API/Client/VRChat.cs`:

```csharp
public async Task<VRChatLoginResult> TryLoginAsync(CancellationToken ct = default)
{
    CurrentUser user = null;
    try { user = await this.LoginAsync(ct); }
    catch (Exception exception) { return new VRChatLoginResult(false, exception); }

    return new VRChatLoginResult(user == null, null);
    //                           ^^^^^^^^^^^^ this is the `success` parameter
}
```

`VRChatLoginResult(bool success, Exception exception = null)`. So:

| Outcome | `user` | Reported `Success` | Correct? |
|---|---|---|---|
| Login succeeded | non-null | **`false`** | ✗ inverted |
| Login returned null | null | **`true`** | ✗ inverted |
| Login threw | — | `false` | ✓ |

**A successful login is reported as failure with a null exception**, which is indistinguishable from
a silent failure. Should be `user != null`.

Cost us roughly an hour of misdiagnosis: it presents as "login failed" with no error to investigate,
so the natural assumption is bad credentials.

**Workaround in `explore/Session.cs`:** use `LoginAsync` directly and catch, which is unambiguous.

## 2. `LoginAsync` returns `null` instead of throwing on a non-OK final response

```csharp
return response.StatusCode == HttpStatusCode.OK ? user : null;
```

The status is discarded. A caller gets `null` with no indication of *why* — 401, 403, 429 and a WAF
block are indistinguishable. Only the explicit `HttpStatusCode.Unauthorized` check earlier in the
method throws.

For Modbot this matters: §4.1 requires the gate to distinguish a 401 (re-login) from a 429 (cold
stop) from a WAF block (tell the operator). A bare `null` cannot drive that.

**Suggested upstream:** throw with the status and body, or return a result type carrying them.

**Not a workaround for Modbot -- a decision.** Confirmed with the SDK's maintainer: `LoginAsync` and
`TryLoginAsync` are convenience helpers for getting started quickly, and Modbot should not use them
at all. `IVRChatGate` drives `GetCurrentUserWithHttpInfoAsync` and `Verify2FAWithHttpInfoAsync`
directly so it keeps the status it needs to act on. See foundation section 4.1.1.

This generalises to the whole codebase: Modbot only ever calls the `...WithHttpInfoAsync` variants,
never the convenience overloads, because status codes, headers and raw bodies are what the gate
exists to react to.

## 3. Credential encoding is handled correctly

`AuthenticationApi` builds the header as:

```csharp
"Basic " + Base64Encode(HttpUtility.UrlEncode(Username) + ":" + HttpUtility.UrlEncode(Password))
```

Verified empirically against `/auth/user` with an email containing `+` and `@` and a password
containing `!`, `*` and `%`. **All three of raw, `HttpUtility.UrlEncode` and `Uri.EscapeDataString`
returned identical results**, so VRChat evidently normalises, and the SDK's choice is not a problem.

Worth recording because it is an easy thing to suspect and waste time on.

## 4. `Instance.Users` is privileged

`Instance.Users` is `List<LimitedUserInstance>`, each carrying `Id`, `DisplayName`, `Platform`,
`Bio` and `CurrentAvatarThumbnailImageUrl` — everything avatar tracking needs, in one call per
instance.

**VRChat only populates it for VRChat staff and the world's owner.** For an ordinary group-moderator
account it comes back empty. See M3 §7.2.1 — this is an attractive dead end and the model gives no
hint of the restriction.

## 5. Useful models confirmed

| Type | Notable fields |
|---|---|
| `Instance` | `Name`, `DisplayName` (the mid-2026 instance-naming feature), `Capacity`, `RecommendedCapacity`, `UserCount`, `Nonce`, `SecureName`, `ShortName` |
| `LimitedUserInstance` | `Id`, `DisplayName`, `Platform`, `Bio`, `StatusDescription`, `UserIcon`, `CurrentAvatarThumbnailImageUrl` |
| `User` | `CurrentAvatarImageUrl`, `CurrentAvatarThumbnailImageUrl`, `ProfilePicOverride`, `ProfileEffect` |
| `GroupInstance` | `InstanceId`, `Location`, `MemberCount`, `World` — **no occupant list** |

Avatar thumbnail URLs embed the file id:
`https://api.vrchat.cloud/api/1/file/file_<id>/1/file`

`Nonce`, `SecureName` and `ShortName` are instance secrets and must never be persisted.

## 6. `Configuration.BasePath` does not redirect a client — **landmine**

Setting `Configuration.BasePath` after construction has no effect. `ApiClient` captures its base URL
**at construction time** from `GlobalConfiguration.Instance`.

This was found the expensive way: a draft of the SDK contract tests set `BasePath` to a loopback stub
and silently sent four unauthenticated requests to the **real** `api.vrchat.cloud`, which answered
401. Nothing failed, nothing warned — the tests simply tested the wrong server.

Any test that believes it is talking to a stub must **construct `ApiClient` with the base path
directly** and then assert the stub actually received the request. A test-only `BasePath` knob was
removed rather than left in place, because a setter that looks like it works and does not is a loaded
gun pointed at production credentials.
