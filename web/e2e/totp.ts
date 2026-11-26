import { createHmac } from 'node:crypto'

/**
 * A time-based one-time code (RFC 6238: HMAC-SHA1, 6 digits, 30 seconds), as
 * an authenticator app would show it. Keycloak keys the HMAC with the secret's
 * own bytes (the app's QR code carries their base32), so the fixture secret in
 * the dev realm is used as it is written there.
 */
export function totp(secret: string, at: number = Date.now()): string {
  const counter = Buffer.alloc(8)
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)))
  const hmac = createHmac('sha1', Buffer.from(secret, 'utf8')).update(counter).digest()
  const offset = hmac[hmac.length - 1] & 0x0f
  return String((hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0')
}
