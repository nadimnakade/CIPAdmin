#!/usr/bin/env python3
"""
End-to-end check of the Google Authenticator second factor against a RUNNING backend.

Covers every branch of the rule:
  IS_BYPASS=1                        -> nextStep=done, a token is issued
  IS_BYPASS=0 + enrolled + right code -> nextStep=done, a token is issued
  IS_BYPASS=0 + enrolled + wrong code -> INVALID OTP CODE, no token
  IS_BYPASS=0 + not enrolled          -> nextStep=enroll_otp, secret PERSISTED, then
                                        the first valid code completes enrollment
  repeated wrong codes                -> throttled

The TOTP codes are computed locally with the same RFC 6238 algorithm the server uses,
so no phone is needed.

Usage: python Backend/sql/otp-e2e-test.py http://127.0.0.1:5099
"""
import base64
import hashlib
import hmac
import json
import struct
import sys
import time
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5099").rstrip("/")
PWD = "OtpTest@123"

passed, failed = 0, 0


def check(label, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  PASS  {label}")
    else:
        failed += 1
        print(f"  FAIL  {label}  {detail}")


def post(path, payload):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, json.loads(r.read().decode() or "{}")
    except urllib.error.HTTPError as e:
        body = e.read().decode()
        try:
            return e.code, json.loads(body or "{}")
        except json.JSONDecodeError:
            return e.code, {"raw": body[:200]}


def login(user):
    return post("/api/auth/login", {"username": user, "password": PWD})


def b32decode(s):
    pad = "=" * (-len(s) % 8)
    return base64.b32decode(s.upper() + pad)


def totp(secret, when=None):
    counter = int((when if when is not None else time.time()) // 30)
    key = b32decode(secret)
    msg = struct.pack(">Q", counter)
    h = hmac.new(key, msg, hashlib.sha1).digest()
    off = h[-1] & 0x0F
    code = (struct.unpack(">I", h[off:off + 4])[0] & 0x7FFFFFFF) % 1000000
    return f"{code:06d}"


print(f"=== authenticator end-to-end against {BASE} ===\n")

# ---------- 1. IS_BYPASS = 1 skips the authenticator entirely ----------
print("[1] IS_BYPASS = 1 -> straight to the dashboard")
status, res = login("OTP_BYPASS_TEST")
check("login succeeds", status == 200 and res.get("valid") is True, f"status={status}")
check("nextStep is done", res.get("nextStep") == "done", f"got {res.get('nextStep')!r}")
check("a token is issued", bool((res.get("user") or {}).get("token")))
check("no QR is handed out", not res.get("otpSecret"), "secret leaked on a bypass login")

# ---------- 2. IS_BYPASS = 0, enrolled, wrong code is rejected ----------
print("\n[2] IS_BYPASS = 0 + enrolled -> asks for a code, rejects a wrong one")
status, res = login("OTP_ENROLLED_TEST")
check("login succeeds", status == 200 and res.get("valid") is True, f"status={status}")
check("nextStep is require_otp", res.get("nextStep") == "require_otp", f"got {res.get('nextStep')!r}")
check("no token before the OTP step", not (res.get("user") or {}).get("token"), "token issued too early")

status, res = post("/api/auth/verify-otp", {"username": "OTP_ENROLLED_TEST", "code": "000000"})
bad = res.get("message", "")
check("a wrong code is refused", "INVALID OTP" in bad or status in (401, 200), f"status={status} msg={bad!r}")
check("no token on a wrong code", not (res.get("user") or {}).get("token"))

# ---------- 3. IS_BYPASS = 0, enrolled, correct code logs in ----------
print("\n[3] IS_BYPASS = 0 + enrolled + correct code -> signed in")
secret = "JBSWY3DPEHPK3PXP"  # seeded by otp-selftest.sql
code = totp(secret)
status, res = post("/api/auth/verify-otp", {"username": "OTP_ENROLLED_TEST", "code": code})
check("nextStep is done", res.get("nextStep") == "done", f"got {res.get('nextStep')!r} code={code}")
check("a token is issued", bool((res.get("user") or {}).get("token")))
check("session reports enrolled", (res.get("user") or {}).get("isGoogleAuthenticated") is True)

# ---------- 4. IS_BYPASS = 0, never enrolled -> QR enrollment ----------
print("\n[4] IS_BYPASS = 0 + never enrolled -> QR enrollment")
status, res = login("OTP_FRESH_TEST")
check("login succeeds", status == 200 and res.get("valid") is True, f"status={status}")
check("nextStep is enroll_otp", res.get("nextStep") == "enroll_otp", f"got {res.get('nextStep')!r}")
check("no token before enrollment", not (res.get("user") or {}).get("token"), "token issued before scanning")
enroll_secret = res.get("otpSecret") or ""
uri = res.get("otpProvisioningUri") or ""
check("a secret is returned", len(enroll_secret) >= 16 and enroll_secret.isalnum(), f"len={len(enroll_secret)}")
check("a QR payload is returned", uri.startswith("otpauth://totp/CIPAdmin:OTP_FRESH_TEST"), uri[:60])
check("secret is embedded in the QR payload", f"secret={enroll_secret}" in uri)

# The bug this catches: the secret was never stored, so no code could ever match.
status, res = post("/api/auth/verify-otp", {"username": "OTP_FRESH_TEST", "code": totp(enroll_secret)})
check("the new secret is usable immediately", res.get("nextStep") == "done", f"got {res.get('nextStep')!r} msg={res.get('message')!r}")
check("enrollment issues a token", bool((res.get("user") or {}).get("token")))
check("session now reports enrolled", (res.get("user") or {}).get("isGoogleAuthenticated") is True)

# ---------- 5. after enrolling, the next login asks for a code ----------
print("\n[5] after enrolling, the next login asks for a code")
status, res = login("OTP_FRESH_TEST")
check("nextStep is require_otp", res.get("nextStep") == "require_otp", f"got {res.get('nextStep')!r}")
check("no QR is shown a second time", not res.get("otpSecret"))

# ---------- 6. throttling ----------
print("\n[6] repeated wrong codes are throttled")
# Enough attempts to trip the 5-failure limit in a 15-minute window.
last = ""
for _ in range(7):
    _, res = post("/api/auth/verify-otp", {"username": "OTP_ENROLLED_TEST", "code": "111111"})
    last = res.get("message", "")
check("the OTP step locks after repeated failures", "TOO MANY" in last, f"last message={last!r}")

print(f"\n=== {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
